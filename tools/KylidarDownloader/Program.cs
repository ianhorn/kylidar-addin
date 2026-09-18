// KylidarDownloader: stand-alone console downloader/converter generated/bundled by the Kylidar
// ArcGIS Pro add-in's "Export Script" -> Executable option.
//
// Reads its manifest (tile URLs, destination folder, output mode, crop WKT) and, for each tile:
// downloads it, then (if the manifest says to convert) runs it through "pdal pipeline" on a
// temp pipeline JSON file -- never "pdal translate ... crop --filters.crop.polygon=<wkt>" on the
// command line, since a complex AOI's crop WKT can run tens of thousands of characters, enough to
// overflow Windows' CreateProcess command-line length limit regardless of which tile is being
// converted (this exact failure mode was hit and fixed in ExportScriptService's other formats --
// see that file's header comment). Cropping always reads via readers.las, even for a .copc.laz
// tile -- it reads a COPC file's points just fine, and readers.copc's own "polygon" option can
// crash PDAL outright on a complex/high-vertex AOI (also confirmed there). No ArcGIS Pro, and
// (published self-contained/single-file, as the add-in bundles it) no .NET runtime install, is
// required to run this.
//
// The manifest normally travels APPENDED to this exe's own file (see TryReadEmbeddedManifest /
// the matching write side in KylidarDockpaneViewModel.BuildDownloaderManifest), so Export
// Script's "Executable" option produces a single, self-contained .exe -- no sidecar file. A
// manifest.json path can still be passed explicitly (or found next to the exe, same filename
// stem) for local testing without rebuilding a manifest-embedded exe each time.
//
// Usage: KylidarDownloader.exe [path-to-manifest.json]

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KylidarDownloader;

internal sealed class Manifest
{
    public string DestFolder { get; set; } = "";
    public bool Convert { get; set; }
    public bool DiscardRaw { get; set; }
    public List<string> CropWktParts { get; set; } = new();
    public List<string> Tiles { get; set; } = new();
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Manifest))]
internal partial class ManifestJsonContext : JsonSerializerContext
{
}

internal static class Program
{
    // Must match KylidarDockpaneViewModel's write side exactly: 8 bytes ASCII magic, right at
    // EOF, with the 8-byte little-endian JSON byte-length immediately before it. Appending bytes
    // after a published single-file .NET exe is safe -- the runtime locates its own bundle via an
    // absolute offset baked into the PE headers at publish time, not by scanning from the end of
    // the file, so trailing bytes are simply ignored by the loader.
    private static readonly byte[] ManifestFooterMagic = "KYLDMAN1"u8.ToArray();

    private static async Task<int> Main(string[] args)
    {
        var manifest = TryReadEmbeddedManifest();

        if (manifest == null)
        {
            // Fall back to an external manifest.json: an explicit path, or one next to this exe
            // with the same filename stem -- useful for local testing.
            var manifestPath = args.Length > 0
                ? args[0]
                : Path.ChangeExtension(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "KylidarDownloader.exe"), ".json");

            if (!File.Exists(manifestPath))
            {
                Console.WriteLine($"No manifest embedded in this exe, and no manifest file found at: {manifestPath}");
                Pause();
                return 1;
            }

            try
            {
                var json = await File.ReadAllTextAsync(manifestPath);
                manifest = JsonSerializer.Deserialize(json, ManifestJsonContext.Default.Manifest)
                           ?? throw new InvalidDataException("Manifest deserialized to null.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not read manifest: {ex.Message}");
                Pause();
                return 1;
            }
        }

        if (manifest.Tiles.Count == 0)
        {
            Console.WriteLine("Manifest has no tiles to download.");
            Pause();
            return 0;
        }

        // Computed on THIS machine at run time, not baked in at export time -- matches
        // ExportScriptService's other formats (Python/PowerShell/Shell): each concurrent unit of
        // work here is CPU-bound (download is only half of it; conversion/crop runs a "pdal"
        // process), so saturating every core would leave nothing for the rest of this machine.
        var concurrency = Math.Max(1, (int)(Environment.ProcessorCount * 0.5));

        Directory.CreateDirectory(manifest.DestFolder);
        Console.WriteLine($"Kylidar downloader -- {manifest.Tiles.Count} tile(s) -> {manifest.DestFolder}");
        Console.WriteLine($"Convert: {manifest.Convert}, discard raw: {manifest.DiscardRaw}, cropping: {manifest.CropWktParts.Count > 0}");
        Console.WriteLine($"Concurrency: {concurrency}");
        Console.WriteLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var sem = new SemaphoreSlim(concurrency);
        long ok = 0, fail = 0;
        var lockObj = new object();

        var tasks = manifest.Tiles.Select(async url =>
        {
            await sem.WaitAsync();
            try
            {
                var result = await ProcessTileAsync(http, url, manifest);
                lock (lockObj) { if (result.StartsWith("OK")) ok++; else fail++; }
                Console.WriteLine(result);
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);

        Console.WriteLine();
        Console.WriteLine($"Done: {ok} succeeded, {fail} failed -> {manifest.DestFolder}");
        Pause();
        return fail == 0 ? 0 : 2;
    }

    private static async Task<string> ProcessTileAsync(HttpClient http, string url, Manifest manifest)
    {
        var fname = url[(url.LastIndexOf('/') + 1)..];
        bool isCopc = fname.EndsWith(".copc.laz", StringComparison.OrdinalIgnoreCase);

        string rawDir = !manifest.Convert
            ? manifest.DestFolder
            : manifest.DiscardRaw
                ? Path.Combine(manifest.DestFolder, "_raw_tmp")
                : Path.Combine(manifest.DestFolder, "COPC");
        Directory.CreateDirectory(rawDir);
        var rawPath = Path.Combine(rawDir, fname);

        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = new FileStream(rawPath, FileMode.Create, FileAccess.Write))
            {
                await src.CopyToAsync(dst);
            }
        }
        catch (Exception ex)
        {
            return $"FAIL download {fname}: {ex.Message}";
        }

        if (!manifest.Convert)
            return $"OK   {fname}";

        var lasDir = manifest.DiscardRaw ? manifest.DestFolder : Path.Combine(manifest.DestFolder, "LAS");
        Directory.CreateDirectory(lasDir);
        var lasPath = Path.Combine(lasDir, TileBaseName(fname) + ".las");

        try
        {
            await ConvertAsync(rawPath, lasPath, isCopc, manifest.CropWktParts);
        }
        catch (Exception ex)
        {
            return $"FAIL convert {fname}: {ex.Message}";
        }

        if (manifest.DiscardRaw)
        {
            try { File.Delete(rawPath); } catch (IOException) { /* ignore */ }
        }

        return $"OK   {fname} -> {Path.GetFileName(lasPath)}";
    }

    private static string TileBaseName(string fname)
    {
        foreach (var ext in new[] { ".copc.laz", ".laz", ".las" })
            if (fname.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return fname[..^ext.Length];
        return Path.GetFileNameWithoutExtension(fname);
    }

    /// <summary>Build a single-tile PDAL pipeline JSON file and run "pdal pipeline" on it -- see
    /// this file's header comment for why (command-line length, and readers.copc's polygon-crash
    /// bug). Mirrors CopcClipService.BuildSingleTileConvertPipelineJson exactly.</summary>
    private static async Task ConvertAsync(string rawPath, string lasPath, bool isCopc, List<string> cropWktParts)
    {
        var pipelineJson = BuildPipelineJson(rawPath, lasPath, isCopc, cropWktParts);
        var pipelinePath = Path.ChangeExtension(lasPath, ".pipeline.json");
        await File.WriteAllTextAsync(pipelinePath, pipelineJson);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pdal",
                ArgumentList = { "pipeline", pipelinePath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start \"pdal\" -- is it on PATH?");
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"pdal exited with code {process.ExitCode}: {stderr}");
        }
        finally
        {
            try { File.Delete(pipelinePath); } catch (IOException) { /* ignore */ }
        }
    }

    /// <summary>Built via Utf8JsonWriter, not JsonSerializer.Serialize on an anonymous type --
    /// this project publishes trimmed, and anonymous-type reflection-based serialization isn't
    /// trim-safe (unlike the source-generated ManifestJsonContext used for the manifest itself).</summary>
    private static string BuildPipelineJson(string rawPath, string lasPath, bool isCopc, List<string> cropWktParts)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WritePropertyName("pipeline");
            w.WriteStartArray();

            const string readTag = "read";
            string finalInputTag = readTag;

            if (cropWktParts.Count > 0)
            {
                w.WriteStartObject();
                w.WriteString("type", "readers.las");
                w.WriteString("filename", rawPath);
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
                    w.WriteString("a_srs", "EPSG:4326");
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
                w.WriteString("filename", rawPath);
                w.WriteString("tag", readTag);
                w.WriteEndObject();
            }

            w.WriteStartObject();
            w.WriteString("type", "writers.las");
            w.WriteString("filename", lasPath);
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

    /// <summary>
    /// Look for a manifest appended to this exe's own file: [JSON bytes][8-byte little-endian
    /// JSON length][8-byte magic] at EOF. Returns null (not an error) if absent or malformed, so
    /// callers fall back to an external manifest.json.
    /// </summary>
    private static Manifest? TryReadEmbeddedManifest()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return null;

            using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 16) return null;

            var footer = new byte[16];
            fs.Seek(-16, SeekOrigin.End);
            fs.ReadExactly(footer, 0, 16);

            for (int i = 0; i < 8; i++)
                if (footer[8 + i] != ManifestFooterMagic[i]) return null;

            long jsonLen = BitConverter.ToInt64(footer, 0);
            if (jsonLen <= 0 || jsonLen > fs.Length - 16) return null;

            fs.Seek(-16 - jsonLen, SeekOrigin.End);
            var jsonBytes = new byte[jsonLen];
            fs.ReadExactly(jsonBytes, 0, (int)jsonLen);

            var json = Encoding.UTF8.GetString(jsonBytes);
            return JsonSerializer.Deserialize(json, ManifestJsonContext.Default.Manifest);
        }
        catch
        {
            return null;
        }
    }

    // Keeps the console window open when double-clicked (no redirected stdin means an
    // interactive console, i.e. not launched from a script/CI pipe).
    private static void Pause()
    {
        if (Console.IsInputRedirected) return;
        Console.WriteLine();
        Console.Write("Press Enter to exit...");
        Console.ReadLine();
    }
}
