/*
 * Downloads COPC tiles to local disk via HttpClient (which correctly uses the OS certificate
 * store, unlike PDAL's bundled libcurl -- see PdalRunner). Trimmed from kyfromabove-ext's
 * DownloadService pattern.
 */
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KylidarAddin.Services
{
    public static class TileDownloadService
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        /// <summary>Download a single asset href to a destination path, reporting progress lines.</summary>
        public static async Task DownloadAsync(string href, string destinationPath, IProgress<string> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");

            using var resp = await _http.GetAsync(href, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;

            using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long received = 0;
            int read;
            var reportClock = System.Diagnostics.Stopwatch.StartNew();
            const int reportIntervalMs = 500;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                if (reportClock.ElapsedMilliseconds >= reportIntervalMs)
                {
                    var pct = total > 0 ? $" ({received * 100.0 / total.Value:F0}%)" : "";
                    progress?.Report($"Downloading {Path.GetFileName(destinationPath)}: {received / 1024 / 1024} MB{pct}");
                    reportClock.Restart();
                }
            }
            progress?.Report($"Downloaded {Path.GetFileName(destinationPath)}: {received / 1024 / 1024} MB");
        }
    }
}
