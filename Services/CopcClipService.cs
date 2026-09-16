/*
 * Given an AOI geometry (map CRS) and the LiDAR phase(s) to search, three output modes are
 * supported (see the dock pane's Output section -- fastest to most disk-hungry):
 *   - DownloadTilesOnlyAsync: search the STAC catalog and download each matching tile's raw file
 *     (COPC or plain LAZ) as-is -- no PDAL involved at all. Fastest, least disk. Never clips.
 *   - ConvertToLasAsync(keepRawCopc: false): search, fully download each tile locally, convert
 *     each to its own full-tile .las, discard the raw tile. One file kept per tile, in
 *     outputFolder.
 *   - ConvertToLasAsync(keepRawCopc: true): same conversion, but the raw tile is kept in
 *     "outputFolder\COPC" alongside the .las in "outputFolder\LAS" -- most disk, but no re-download
 *     needed if the raw tile is wanted later.
 * The AOI decides which tiles are found/downloaded (via the STAC "intersects" search); optionally
 * (see "Clip to area of interest" in the dock pane) each tile can also be cropped down to the AOI
 * (plus a required buffer -- see PrepareAoi) during conversion, via PDAL. Clipping only applies to
 * the two convert modes -- DownloadTilesOnlyAsync never runs PDAL, so there's nothing to crop.
 * Every conversion converts each local tile to its own .las in its OWN PDAL process, run
 * concurrently -- one shared pipeline covering every tile (an earlier approach here) runs as a
 * single Python/PDAL process, so it can't use more than one core no matter how many tiles there
 * are; a separate process per tile gets genuine OS-level parallelism instead. Concurrency is capped
 * at 60% of the core count for this CPU-bound step (see MaxTileConvertConcurrency -- leaves the
 * rest of the system, Pro's own UI thread included, some headroom) and throttled back further under
 * memory pressure (see RunWithAdaptiveConcurrencyAsync), since process count -- not point count --
 * is what drives memory footprint here. The earlier, network-bound tile-fetch step uses a separate,
 * looser cap (see MaxConcurrentTileFetches: the smaller of logical cores - 2 and 75% of cores).
 * SearchTileCountAsync is a preview-only STAC search used by the dock pane's "Search Catalog" button.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArcGIS.Core.Geometry;
using KylidarAddin.Stac;

namespace KylidarAddin.Services
{
    public class ClipResult
    {
        public bool Success { get; set; }
        public IReadOnlyList<string> OutputPaths { get; set; }
        public string Error { get; set; }
    }

    /// <summary>Result of PrepareAoi: the GeoJSON used for the STAC search, plus (only when
    /// clipping is enabled) one WKT polygon string per AOI part, used to crop tiles via PDAL.</summary>
    public class AoiSearchInfo
    {
        public string GeoJson { get; set; }
        public IReadOnlyList<string> CropWktParts { get; set; }
    }

    public static class CopcClipService
    {
        /// <summary>
        /// How many tiles to download at once: whichever is SMALLER of (logical cores - 2) and 75%
        /// of logical cores -- the -2 keeps a couple of cores free on small machines (where 75% alone
        /// would allow using all-but-one), while the 75% cap keeps this from creeping up toward
        /// "every core but 2" on big machines (e.g. 62 of 64) where that stops leaving meaningful
        /// headroom. This step is network-bound, not CPU-bound (barely touches the CPU) -- a looser
        /// cap than MaxTileConvertConcurrency's 60%, which governs the actually CPU-bound convert
        /// step below. A large value here means that many simultaneous full-file HTTP downloads
        /// against the same host -- worth watching for if that host starts throttling/erroring
        /// under load.
        /// </summary>
        private static int MaxConcurrentTileFetches =>
            Math.Max(1, Math.Min(Environment.ProcessorCount - 2, (int)(Environment.ProcessorCount * 0.75)));

        /// <summary>
        /// Geometry-engine step: project the AOI to WGS84 for the STAC "intersects" search, and
        /// (when clipping is enabled) buffer it first and split the buffered polygon into per-part
        /// WKT strings for PDAL. Must be called from the MCT thread (inside QueuedTask.Run) --
        /// unlike ConvertToLasAsync below, which does no ArcGIS.Core.Geometry work and must NOT be
        /// run on/blocked against the MCT thread, since it awaits network I/O and an external
        /// process for potentially a long time.
        ///
        /// A buffer is required when clipping a point or line AOI (enforced by the dock pane before
        /// Run/Export Script are even enabled) -- neither has any area to crop by without one. A
        /// polygon AOI already has an area, so its buffer is optional: when clipping is on but no
        /// buffer was given, the polygon's own boundary is used as-is (bufferFeet is 0 or unparsed).
        /// </summary>
        public static AoiSearchInfo PrepareAoi(Geometry aoi, bool clipToAoi, double bufferFeet)
        {
            var searchGeometry = aoi;
            if (clipToAoi && bufferFeet > 0)
            {
                double bufferMapUnits = bufferFeet;
                if (aoi.SpatialReference?.Unit is LinearUnit lu && lu.ConversionFactor > 0)
                {
                    // ConversionFactor is meters per one unit of this SR's linear unit; convert the
                    // requested feet distance into that unit so "buffer feet" means feet regardless
                    // of the map's own units.
                    bufferMapUnits = (bufferFeet * 0.3048) / lu.ConversionFactor;
                }
                searchGeometry = GeometryEngine.Instance.Buffer(aoi, bufferMapUnits);
            }

            var searchWgs84 = (Geometry)GeometryEngine.Instance.Project(searchGeometry, SpatialReferences.WGS84);
            var info = new AoiSearchInfo { GeoJson = GeoJsonConverter.ToGeoJsonGeometry(searchWgs84) };
            if (clipToAoi)
                info.CropWktParts = GeoJsonConverter.ToWktParts(searchWgs84);
            return info;
        }

        /// <summary>Preview-only STAC search: how many LiDAR tiles intersect this AOI/phase selection,
        /// without fetching or processing anything. Backs the dock pane's "Search Catalog" button.</summary>
        public static async Task<int> SearchTileCountAsync(string geoJson, IReadOnlyCollection<string> collections, CancellationToken ct = default)
        {
            var tiles = await SearchTilesAsync(geoJson, collections, null, ct).ConfigureAwait(false);
            return tiles.Count;
        }

        /// <summary>Matching tile hrefs, for the dock pane's "Export Script" feature -- like
        /// SearchTileCountAsync but hands back what's needed to build a portable download script.</summary>
        public static async Task<IReadOnlyList<string>> SearchTileUrlsAsync(string geoJson, IReadOnlyCollection<string> collections, CancellationToken ct = default)
        {
            var tiles = await SearchTilesAsync(geoJson, collections, null, ct).ConfigureAwait(false);
            return tiles.Select(t => t.asset.Href).ToList();
        }

        /// <summary>Download each matching tile's raw file (COPC or plain LAZ) as-is into
        /// outputFolder -- no PDAL involved, no format conversion or clipping.</summary>
        public static async Task<ClipResult> DownloadTilesOnlyAsync(
            string geoJson, IReadOnlyCollection<string> collections, string outputFolder,
            IProgress<string> progress, CancellationToken ct = default)
        {
            var tiles = await SearchTilesAsync(geoJson, collections, progress, ct).ConfigureAwait(false);
            if (tiles.Count == 0)
                return new ClipResult { Success = false, Error = "No LiDAR coverage found for this AOI in the selected phase(s). Try a different phase or a new AOI." };

            progress?.Report($"Found {tiles.Count} LiDAR tile(s) intersecting the AOI.");
            Directory.CreateDirectory(outputFolder);

            var outputPaths = new string[tiles.Count];
            using (var throttle = new SemaphoreSlim(MaxConcurrentTileFetches))
            {
                var downloadTasks = tiles.Select(async (t, i) =>
                {
                    await throttle.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        var fileName = Path.GetFileName(new Uri(t.asset.Href).LocalPath);
                        var localPath = Path.Combine(outputFolder, fileName);
                        await TileDownloadService.DownloadAsync(t.asset.Href, localPath, progress, ct).ConfigureAwait(false);
                        outputPaths[i] = localPath;
                    }
                    finally
                    {
                        throttle.Release();
                    }
                });
                await Task.WhenAll(downloadTasks).ConfigureAwait(false);
            }

            return new ClipResult { Success = true, OutputPaths = outputPaths.ToList() };
        }

        /// <summary>Search the STAC catalog and flatten matching items to their LiDAR assets.</summary>
        private static async Task<List<(StacItem item, StacAsset asset)>> SearchTilesAsync(
            string geoJson, IReadOnlyCollection<string> collections, IProgress<string> progress, CancellationToken ct)
        {
            progress?.Report("Searching STAC catalog for LiDAR coverage...");
            using var stac = new StacClient();
            var results = await stac.SearchIntersectsAsync(geoJson, collections, limit: 200, ct: ct).ConfigureAwait(false);

            var tiles = new List<(StacItem item, StacAsset asset)>();
            foreach (var item in results?.Features ?? Enumerable.Empty<StacItem>())
                foreach (var asset in item.GetLidarAssets())
                    tiles.Add((item, asset));
            return tiles;
        }

        /// <param name="keepRawCopc">
        /// True: the raw COPC/LAZ tile(s) are kept, in "<paramref name="outputFolder"/>\COPC", and
        /// converted .las output goes to "<paramref name="outputFolder"/>\LAS". False: raw tiles are
        /// fetched into a scratch temp folder and deleted once conversion finishes; .las output goes
        /// straight into outputFolder.
        /// </param>
        /// <param name="cropWktParts">
        /// Null/empty: every tile is converted in full, no cropping. Non-empty: each tile is cropped
        /// to these AOI part(s) (already buffered -- see PrepareAoi) during conversion.
        /// </param>
        public static async Task<ClipResult> ConvertToLasAsync(
            string geoJson, IReadOnlyCollection<string> collections,
            bool keepRawCopc, string outputFolder, IReadOnlyList<string> cropWktParts,
            IProgress<string> progress, CancellationToken ct = default)
        {
            string tempDir = null;
            try
            {
                var tiles = await SearchTilesAsync(geoJson, collections, progress, ct).ConfigureAwait(false);
                if (tiles.Count == 0)
                    return new ClipResult { Success = false, Error = "No LiDAR coverage found for this AOI in the selected phase(s). Try a different phase or a new AOI." };

                progress?.Report($"Found {tiles.Count} LiDAR tile(s) intersecting the AOI.");

                string rawTilesDir;
                if (keepRawCopc)
                {
                    rawTilesDir = Path.Combine(outputFolder, "COPC");
                    Directory.CreateDirectory(rawTilesDir);
                }
                else
                {
                    tempDir = Path.Combine(Path.GetTempPath(), $"kylidar_{Guid.NewGuid():N}");
                    Directory.CreateDirectory(tempDir);
                    rawTilesDir = tempDir;
                }

                // Fetch each tile locally, concurrently (bounded by MaxConcurrentTileFetches) rather
                // than one at a time, since this step is dominated by network I/O; results are
                // written into a slot per tile so the output list built afterward stays stable
                // regardless of fetch order.
                var localTileSlots = new (string path, bool isCopc)?[tiles.Count];
                using (var throttle = new SemaphoreSlim(MaxConcurrentTileFetches))
                {
                    var fetchTasks = tiles.Select(async (t, i) =>
                    {
                        var (item, asset) = t;
                        await throttle.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            ct.ThrowIfCancellationRequested();
                            var fileName = Path.GetFileName(new Uri(asset.Href).LocalPath);
                            var localPath = Path.Combine(rawTilesDir, fileName);

                            await TileDownloadService.DownloadAsync(asset.Href, localPath, progress, ct).ConfigureAwait(false);
                            localTileSlots[i] = (localPath, asset.IsCopc);
                        }
                        finally
                        {
                            throttle.Release();
                        }
                    });
                    await Task.WhenAll(fetchTasks).ConfigureAwait(false);
                }
                var localTiles = localTileSlots.Select(t => t.Value).ToList();

                if (keepRawCopc)
                    progress?.Report($"Kept {localTiles.Count} raw tile(s) in {rawTilesDir}.");

                // Convert each tile via its own concurrent PDAL process.
                var lasFolder = keepRawCopc ? Path.Combine(outputFolder, "LAS") : outputFolder;
                Directory.CreateDirectory(lasFolder);

                bool clipping = cropWktParts != null && cropWktParts.Count > 0;
                progress?.Report(clipping
                    ? $"Converting and clipping {localTiles.Count} point cloud tile(s) to the AOI (up to {MaxTileConvertConcurrency} of {Environment.ProcessorCount} cores)..."
                    : $"Converting {localTiles.Count} point cloud tile(s) (up to {MaxTileConvertConcurrency} of {Environment.ProcessorCount} cores)...");

                var outputPaths = localTiles.Select(t => Path.Combine(lasFolder, TileBaseName(t.path) + ".las")).ToList();
                try
                {
                    await RunPerTileConversionsAsync(localTiles, outputPaths, cropWktParts, progress, ct).ConfigureAwait(false);
                }
                catch (PdalStageFailedException ex)
                {
                    return new ClipResult { Success = false, Error = ex.Message };
                }

                return new ClipResult { Success = true, OutputPaths = outputPaths };
            }
            finally
            {
                if (tempDir != null)
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
                }
            }
        }

        /// <summary>
        /// Individual-output-file base name for a tile: its filename with the LiDAR extension
        /// stripped, including the compound ".copc.laz" (Path.GetFileNameWithoutExtension only
        /// strips the last ".laz", which would otherwise leave e.g. "tile_123.copc.las").
        /// </summary>
        private static string TileBaseName(string tilePath)
        {
            var name = Path.GetFileName(tilePath);
            foreach (var ext in new[] { ".copc.laz", ".laz", ".las" })
            {
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return name.Substring(0, name.Length - ext.Length);
            }
            return Path.GetFileNameWithoutExtension(name);
        }

        /// <summary>Thrown by a per-tile conversion job on PDAL failure; caught around the call into
        /// RunPerTileConversionsAsync/ConvertOneTileAsync and turned into a failed ClipResult,
        /// matching how every other PDAL failure in this class is surfaced (not as a thrown
        /// exception).</summary>
        private class PdalStageFailedException : Exception
        {
            public PdalStageFailedException(string message) : base(message) { }
        }

        /// <summary>
        /// Cap per-tile PDAL process concurrency at 60% of logical cores rather than 100%: each
        /// process is CPU-bound (LAZ decompression, format conversion, and -- when clipping --
        /// cropping/merging), so saturating every core leaves nothing for the rest of the system --
        /// ArcGIS Pro's own UI thread included -- to stay responsive while a big run is going.
        /// </summary>
        private static int MaxTileConvertConcurrency => Math.Max(1, (int)(Environment.ProcessorCount * 0.60));

        /// <summary>Run one PDAL process per tile, concurrently (see RunWithAdaptiveConcurrencyAsync
        /// for the concurrency/memory policy). <paramref name="outputPaths"/> is parallel to
        /// <paramref name="localTiles"/>.</summary>
        private static Task RunPerTileConversionsAsync(
            IReadOnlyList<(string path, bool isCopc)> localTiles, IReadOnlyList<string> outputPaths,
            IReadOnlyList<string> cropWktParts, IProgress<string> progress, CancellationToken ct)
        {
            var jobs = localTiles.Select((tile, i) =>
                (Func<Task>)(() => ConvertOneTileAsync(tile, outputPaths[i], cropWktParts, progress, ct)));
            return RunWithAdaptiveConcurrencyAsync(jobs, MaxTileConvertConcurrency, ct);
        }

        /// <summary>Run a single tile through its own PDAL process: read (COPC or LAS), optionally
        /// crop to the AOI, and write to outputPath. Throws PdalStageFailedException on failure.</summary>
        private static async Task ConvertOneTileAsync(
            (string path, bool isCopc) tile, string outputPath, IReadOnlyList<string> cropWktParts,
            IProgress<string> progress, CancellationToken ct)
        {
            var pipelineJson = BuildSingleTileConvertPipelineJson(tile.path, tile.isCopc, outputPath, cropWktParts);
            var result = await PdalRunner.RunPipelineAsync(pipelineJson, progress, ct).ConfigureAwait(false);
            if (!result.Success)
                throw new PdalStageFailedException(result.Error ?? $"PDAL failed converting {Path.GetFileName(tile.path)}.");
            progress?.Report($"Converted {Path.GetFileName(tile.path)}.");
        }

        /// <summary>
        /// Run each job with as much OS-level parallelism as the machine has cores for
        /// (<paramref name="maxConcurrency"/>), but throttle back -- stop starting new ones, let
        /// running ones drain -- whenever system memory usage is at/above MaxMemoryLoadFraction.
        /// Each job here is a separate PDAL process, so process count (not point/data size) is what
        /// drives memory footprint, making "how many processes are in flight" the right knob. Always
        /// keeps at least one job running so a persistently-high memory reading can't wedge things
        /// indefinitely; memory is only re-checked when about to start an additional job, so this
        /// never busy-polls.
        /// </summary>
        private static async Task RunWithAdaptiveConcurrencyAsync(IEnumerable<Func<Task>> jobFactories, int maxConcurrency, CancellationToken ct)
        {
            var pending = new Queue<Func<Task>>(jobFactories);
            var running = new List<Task>();
            while (pending.Count > 0 || running.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                while (pending.Count > 0 && running.Count < maxConcurrency &&
                       (running.Count == 0 || CurrentMemoryLoadFraction() < MaxMemoryLoadFraction))
                {
                    running.Add(pending.Dequeue()());
                }

                var finished = await Task.WhenAny(running).ConfigureAwait(false);
                running.Remove(finished);
                await finished.ConfigureAwait(false); // rethrow on failure
            }
        }

        private const double MaxMemoryLoadFraction = 0.80;

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

        /// <summary>
        /// Fraction (0-1) of physical memory currently in use, via the OS's own "memory load"
        /// figure -- simpler and more reliable than a PerformanceCounter (no category/permission
        /// setup). Returns 0 (never throttles) if the call fails for any reason.
        /// </summary>
        private static double CurrentMemoryLoadFraction()
        {
            var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? status.dwMemoryLoad / 100.0 : 0;
        }

        /// <summary>
        /// Build a single-tile PDAL pipeline: read the whole tile (COPC or LAS), optionally crop to
        /// one or more AOI WKT polygon parts, and write to outputPath.
        ///
        /// COPC tiles crop via readers.copc's own "polygon" option, which takes an ARRAY of separate
        /// polygon WKT strings (unioned together) -- NOT a single combined MULTIPOLYGON WKT, which
        /// it rejects as "geometrically invalid". That lets one reader stage do the crop directly.
        ///
        /// Plain LAS/LAZ tiles crop via filters.crop instead, whose multi-region semantics differ
        /// (documented to produce one output point-view PER region, not a union) -- so a multi-part
        /// AOI needs one filters.crop branch per part off a single reader, recombined via
        /// filters.merge before the writer.
        /// </summary>
        private static string BuildSingleTileConvertPipelineJson(
            string tilePath, bool isCopc, string outputPath, IReadOnlyList<string> cropWktParts)
        {
            bool clip = cropWktParts != null && cropWktParts.Count > 0;

            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WritePropertyName("pipeline");
                w.WriteStartArray();

                const string readTag = "read";
                string finalInputTag = readTag;

                if (clip && isCopc)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "readers.copc");
                    w.WriteString("filename", tilePath);
                    w.WritePropertyName("polygon");
                    w.WriteStartArray();
                    foreach (var wkt in cropWktParts) w.WriteStringValue(wkt);
                    w.WriteEndArray();
                    w.WriteString("tag", readTag);
                    w.WriteEndObject();
                }
                else if (clip)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "readers.las");
                    w.WriteString("filename", tilePath);
                    w.WriteString("tag", readTag);
                    w.WriteEndObject();

                    var cropTags = new List<string>(cropWktParts.Count);
                    for (int i = 0; i < cropWktParts.Count; i++)
                    {
                        var cropTag = $"crop{i}";
                        cropTags.Add(cropTag);
                        w.WriteStartObject();
                        w.WriteString("type", "filters.crop");
                        w.WriteString("polygon", cropWktParts[i]);
                        w.WritePropertyName("inputs");
                        w.WriteStartArray();
                        w.WriteStringValue(readTag);
                        w.WriteEndArray();
                        w.WriteString("tag", cropTag);
                        w.WriteEndObject();
                    }

                    if (cropTags.Count > 1)
                    {
                        const string mergeTag = "merged";
                        w.WriteStartObject();
                        w.WriteString("type", "filters.merge");
                        w.WritePropertyName("inputs");
                        w.WriteStartArray();
                        foreach (var t in cropTags) w.WriteStringValue(t);
                        w.WriteEndArray();
                        w.WriteString("tag", mergeTag);
                        w.WriteEndObject();
                        finalInputTag = mergeTag;
                    }
                    else
                    {
                        finalInputTag = cropTags[0];
                    }
                }
                else
                {
                    w.WriteStartObject();
                    w.WriteString("type", isCopc ? "readers.copc" : "readers.las");
                    w.WriteString("filename", tilePath);
                    w.WriteString("tag", readTag);
                    w.WriteEndObject();
                }

                w.WriteStartObject();
                w.WriteString("type", "writers.las");
                w.WriteString("filename", outputPath);
                w.WritePropertyName("inputs");
                w.WriteStartArray();
                w.WriteStringValue(finalInputTag);
                w.WriteEndArray();
                w.WriteEndObject();

                w.WriteEndArray();
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
    }
}
