/*
 * Fetches only the octree nodes of a remote COPC file that overlap an AOI envelope (via HTTP
 * Range requests), and reconstructs a byte-faithful *partial* local copy of the original file --
 * same absolute byte offsets throughout, just with everything not fetched left as a hole -- so
 * PDAL's readers.copc reads it exactly as it would the full file. No LASzip decompression or file
 * format rewriting happens here at all; see the design notes in the project plan for why that's
 * both sufficient and safe (LASzip's chunked mode makes each node independently decodable).
 *
 * Validated against a real 106MB Kentucky COPC tile before this was ported to C#: a Python
 * prototype using this exact technique produced a point count that matched a full-file crop
 * exactly (813,676 points, same AOI) -- see the kylidar-addin plan history for the spike.
 *
 * Anything unexpected throws CopcPartialFetchException; callers should catch it and fall back to
 * a full download for that tile. This path is strictly a bandwidth optimization, never a
 * correctness requirement.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace KylidarAddin.Services.Copc
{
    public class CopcPartialFetchException : Exception
    {
        public CopcPartialFetchException(string message, Exception inner = null) : base(message, inner) { }
    }

    public static class CopcPartialFetchService
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        private const int HeaderProbeBytes = 8192;
        private const long CoalesceGapThreshold = 64 * 1024; // merge ranges within 64KB of each other
        private const int MaxConcurrentRangeRequests = 8;

        /// <summary>
        /// Fetch just the parts of a remote COPC file needed to cover the AOI, writing a sparse
        /// partial local copy to <paramref name="destinationPath"/>. <paramref name="resolveAoiEnvelope"/>
        /// is called with the tile's own SRS WKT (parsed from its header) and must return the AOI's
        /// envelope reprojected into that CRS -- this class has no ArcGIS geometry dependency of its
        /// own, so callers (which do) own that reprojection. Throws CopcPartialFetchException on any
        /// unexpected condition -- callers should fall back to a full download.
        /// </summary>
        public static async Task FetchAsync(string href, string destinationPath,
            Func<string, Task<(double minX, double minY, double maxX, double maxY)>> resolveAoiEnvelope,
            IProgress<string> progress, CancellationToken ct)
        {
            try
            {
                var headerBytes = await GetRangeAsync(href, 0, HeaderProbeBytes, ct).ConfigureAwait(false);
                if (!CopcHeaderReader.TryParse(headerBytes, out var fileInfo, out var bytesNeeded))
                {
                    if (bytesNeeded > headerBytes.Length)
                    {
                        headerBytes = await GetRangeAsync(href, 0, bytesNeeded + 256, ct).ConfigureAwait(false);
                        if (!CopcHeaderReader.TryParse(headerBytes, out fileInfo, out _))
                            throw new CopcPartialFetchException("Could not parse COPC header even after re-fetching a larger header region.");
                    }
                    else
                    {
                        throw new CopcPartialFetchException("Not a COPC file, or COPC info VLR not found in the header region.");
                    }
                }

                if (string.IsNullOrEmpty(fileInfo.SrsWkt))
                    throw new CopcPartialFetchException("Tile has no SRS WKT VLR; can't safely compute the AOI envelope in its native CRS.");

                var (aoiMinX, aoiMinY, aoiMaxX, aoiMaxY) = await resolveAoiEnvelope(fileInfo.SrsWkt).ConfigureAwait(false);

                var copcInfo = fileInfo.CopcInfo;
                var neededRanges = new List<(long offset, long size)> { (0, fileInfo.OffsetToPointData) };
                long maxExtent = fileInfo.OffsetToPointData;

                // Walk the hierarchy (recursing into child pages), collecting leaf chunks that
                // overlap the AOI's X/Y envelope. Conservative over-inclusion is fine -- the
                // existing readers.copc polygon crop still runs afterward and discards extras.
                var keptChunks = new List<(long offset, long size)>();
                var pagesToVisit = new Queue<(ulong offset, ulong size)>();
                pagesToVisit.Enqueue((copcInfo.RootHierOffset, copcInfo.RootHierSize));

                while (pagesToVisit.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var (pageOffset, pageSize) = pagesToVisit.Dequeue();
                    neededRanges.Add(((long)pageOffset, (long)pageSize));
                    maxExtent = Math.Max(maxExtent, (long)(pageOffset + pageSize));

                    var pageBytes = await GetRangeAsync(href, (long)pageOffset, (long)pageSize, ct).ConfigureAwait(false);
                    foreach (var entry in CopcHierarchy.ParsePage(pageBytes))
                    {
                        if (entry.IsPagePointer)
                        {
                            pagesToVisit.Enqueue((entry.Offset, (ulong)entry.ByteSize));
                            continue;
                        }
                        if (entry.IsEmpty) continue;

                        var (minX, minY, maxX, maxY) = entry.GetBoundsXY(copcInfo);
                        bool overlaps = !(maxX < aoiMinX || minX > aoiMaxX || maxY < aoiMinY || minY > aoiMaxY);
                        if (!overlaps) continue;

                        keptChunks.Add(((long)entry.Offset, entry.ByteSize));
                        maxExtent = Math.Max(maxExtent, (long)entry.Offset + entry.ByteSize);
                    }
                }

                if (keptChunks.Count == 0)
                {
                    throw new CopcPartialFetchException("AOI didn't overlap any octree node -- unexpected given the STAC search already matched this tile.");
                }

                var allRanges = neededRanges.Concat(keptChunks).ToList();
                var merged = CoalesceRanges(allRanges);

                progress?.Report($"Partial fetch: {merged.Count} range request(s), {merged.Sum(r => r.size) / 1024 / 1024.0:F1} MB of {maxExtent / 1024 / 1024.0:F1} MB tile.");

                await WriteSparseFileAsync(href, destinationPath, maxExtent, merged, ct).ConfigureAwait(false);
            }
            catch (CopcPartialFetchException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new CopcPartialFetchException($"Partial COPC fetch failed: {ex.Message}", ex);
            }
        }

        private static List<(long offset, long size)> CoalesceRanges(List<(long offset, long size)> ranges)
        {
            var sorted = ranges.OrderBy(r => r.offset).ToList();
            var merged = new List<(long offset, long size)>();
            foreach (var r in sorted)
            {
                if (merged.Count > 0)
                {
                    var last = merged[^1];
                    long lastEnd = last.offset + last.size;
                    if (r.offset <= lastEnd + CoalesceGapThreshold)
                    {
                        long newEnd = Math.Max(lastEnd, r.offset + r.size);
                        merged[^1] = (last.offset, newEnd - last.offset);
                        continue;
                    }
                }
                merged.Add(r);
            }
            return merged;
        }

        private static async Task WriteSparseFileAsync(string href, string destinationPath, long totalLength,
            List<(long offset, long size)> ranges, CancellationToken ct)
        {
            var destDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

            using var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            fs.SetLength(totalLength);
            TryMarkSparse(fs);

            using var throttle = new SemaphoreSlim(MaxConcurrentRangeRequests);
            var writeLock = new object();

            var tasks = ranges.Select(async range =>
            {
                await throttle.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var data = await GetRangeAsync(href, range.offset, range.size, ct).ConfigureAwait(false);
                    lock (writeLock)
                    {
                        fs.Position = range.offset;
                        fs.Write(data, 0, data.Length);
                    }
                }
                finally
                {
                    throttle.Release();
                }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private static async Task<byte[]> GetRangeAsync(string href, long offset, long size, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, href);
            req.Headers.Range = new RangeHeaderValue(offset, offset + size - 1);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                throw new CopcPartialFetchException($"Server did not honor a Range request (status {(int)resp.StatusCode}); can't do partial reads against this host.");
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }

        #region Sparse file P/Invoke (no managed API for FSCTL_SET_SPARSE)

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeHandle hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        private const uint FsctlSetSparse = 0x000900c4;

        /// <summary>Best-effort: mark the file sparse so unwritten regions don't consume real disk
        /// space. If this fails (non-NTFS volume, etc.) the file just ends up fully allocated --
        /// network savings, the actual goal, are unaffected either way.</summary>
        private static void TryMarkSparse(FileStream fs)
        {
            try
            {
                DeviceIoControl(fs.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            }
            catch { /* ignore -- best effort */ }
        }

        #endregion
    }
}
