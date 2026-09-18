/*
 * Builds a stand-alone Python/PowerShell/shell script that reproduces the current AOI's
 * download-and-convert step outside ArcGIS Pro -- for very large batches, running on another
 * machine, or scheduling for later. Modeled on kyfromabove-ext's
 * SearchDockpaneViewModel.BuildPython/PowerShell/ShellDownloadScript (same add-in family, same
 * "portable download kit" idea for its own raster/point-cloud downloads), with a PDAL convert (and
 * optional crop) step added since that add-in has no such step to export. LAS dataset
 * creation/pyramids are NOT included -- those are ArcGIS Pro/arcpy-only operations with no portable
 * equivalent, so the exported script just leaves the .las files on disk.
 *
 * The convert step assumes the target machine has the "pdal" CLI on PATH -- reasonable for anyone
 * already working with LiDAR/point-cloud tooling, and the only way to make this genuinely portable
 * (ArcGIS Pro's own bundled PDAL, which PdalRunner uses everywhere else in this add-in, obviously
 * isn't available outside Pro).
 *
 * cropWktParts mirrors CopcClipService.ConvertToLasAsync's own cropWktParts parameter (see that
 * class for the full crop-strategy rationale): empty means every tile is converted in full.
 * Otherwise each tile is converted via a PDAL PIPELINE JSON FILE, not "pdal translate ... crop
 * --filters.crop.polygon=<wkt>" on the command line -- an earlier version of this class did exactly
 * that, and it broke every single conversion (identically, regardless of tile) with Windows'
 * [WinError 206] "The filename or extension is too long" whenever the AOI was a complex polygon
 * (e.g. a county boundary): the crop WKT alone can run tens of thousands of characters, which blows
 * past CreateProcess's command-line length limit long before any actual filename does (confirmed by
 * reproducing the exact error with a ~1500-vertex polygon, then confirming a pipeline JSON file --
 * which has no such length limit -- fixes it).
 *
 * The pipeline mirrors CopcClipService.BuildSingleTileConvertPipelineJson exactly: cropping always
 * reads via readers.las (even for a .copc.laz tile -- it reads a COPC file's points just fine, see
 * that class's header comment for why) and crops via filters.crop, one stage per AOI part (each
 * needs its own "a_srs":"EPSG:4326" -- cropWktParts are always WGS84, not the tile's own CRS, and
 * filters.crop otherwise assumes same-CRS-as-data), recombined with filters.merge when there's more
 * than one part. This deliberately avoids readers.copc's own "polygon" option, which can flat-out
 * crash PDAL (a native segfault) on a complex/high-vertex AOI polygon -- reproduced with the same
 * ~1500-vertex polygon used for the command-line-length repro above: identical WKT crops correctly
 * via filters.crop, but segfaults via readers.copc's polygon array every time.
 *
 * Concurrency is computed by the generated script itself, at the time it runs (50% of whatever
 * machine executes it, not the machine that exported it) -- matches CopcClipService's own
 * MaxTileConvertConcurrency policy for the in-app Run pipeline, for the same reason: each
 * concurrent unit of work here is CPU-bound (download is only half of it; conversion/crop runs a
 * "pdal" process), so saturating every core would leave nothing for the rest of that machine.
 *
 * The Notebook format (.ipynb) reuses the exact same Python logic as BuildPython, just split
 * across a few cells (config/tiles, helper functions, main run loop) instead of one flat script --
 * for anyone who'd rather step through/inspect results interactively (Jupyter, JupyterLab, VS
 * Code's notebook viewer, Google Colab) than run a .py file end-to-end from a terminal.
 */
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace KylidarAddin.Services
{
    /// <summary>Executable isn't handled by BuildScript below -- KylidarDockpaneViewModel.ExportScriptAsync
    /// special-cases it before ever calling in here, copying the bundled tools\KylidarDownloader\
    /// exe and appending a manifest instead of generating text (see that method).</summary>
    public enum ExportScriptFormat { Python, Notebook, PowerShell, Shell, Executable }

    internal static class ExportScriptService
    {
        public static string BuildScript(
            ExportScriptFormat format, IReadOnlyList<string> tileUrls, string destFolder,
            bool convert, bool discardRaw, IReadOnlyList<string> cropWktParts)
        {
            cropWktParts ??= Array.Empty<string>();
            return format switch
            {
                ExportScriptFormat.Python => BuildPython(tileUrls, destFolder, convert, discardRaw, cropWktParts),
                ExportScriptFormat.Notebook => BuildNotebook(tileUrls, destFolder, convert, discardRaw, cropWktParts),
                ExportScriptFormat.PowerShell => BuildPowerShell(tileUrls, destFolder, convert, discardRaw, cropWktParts),
                ExportScriptFormat.Shell => BuildShell(tileUrls, destFolder, convert, discardRaw, cropWktParts),
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };
        }

        private static string EscapePy(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        private static string EscapePs(string s) => (s ?? "").Replace("'", "''");
        private static string EscapeSh(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`");

        /// <summary>The "def _convert(raw_path, las_path): ..." function body shared by BuildPython
        /// and BuildNotebook -- see this file's header comment for why it builds a PDAL pipeline
        /// JSON file rather than shelling out to "pdal translate ... crop" with the WKT inline.</summary>
        private static IEnumerable<string> PythonConvertFunctionLines()
        {
            return new[]
            {
                "def _convert(raw_path, las_path):",
                "    is_copc = str(raw_path).lower().endswith(\".copc.laz\")",
                "    if not CROP_WKT_PARTS:",
                "        stages = [",
                "            {\"type\": \"readers.copc\" if is_copc else \"readers.las\", \"filename\": str(raw_path), \"tag\": \"read\"},",
                "            {\"type\": \"writers.las\", \"filename\": str(las_path), \"inputs\": [\"read\"]},",
                "        ]",
                "    else:",
                "        # Always readers.las + filters.crop, even for a .copc.laz tile -- it reads a COPC",
                "        # file's points just fine, and readers.copc's own \"polygon\" option can crash PDAL",
                "        # outright on a complex/high-vertex AOI (see this file's header comment).",
                "        stages = [{\"type\": \"readers.las\", \"filename\": str(raw_path), \"tag\": \"read\"}]",
                "        crop_tags = []",
                "        for i, wkt in enumerate(CROP_WKT_PARTS):",
                "            tag = f\"crop{i}\"",
                "            crop_tags.append(tag)",
                "            stages.append({\"type\": \"filters.crop\", \"polygon\": wkt, \"a_srs\": \"EPSG:4326\",",
                "                           \"inputs\": [\"read\"], \"tag\": tag})",
                "        final_tag = crop_tags[0]",
                "        if len(crop_tags) > 1:",
                "            stages.append({\"type\": \"filters.merge\", \"inputs\": crop_tags, \"tag\": \"merged\"})",
                "            final_tag = \"merged\"",
                "        stages.append({\"type\": \"writers.las\", \"filename\": str(las_path), \"inputs\": [final_tag]})",
                "",
                "    pipeline_path = las_path.with_suffix(\".pipeline.json\")",
                "    with open(pipeline_path, \"w\") as f:",
                "        json.dump({\"pipeline\": stages}, f)",
                "    try:",
                "        subprocess.run([\"pdal\", \"pipeline\", str(pipeline_path)], check=True, capture_output=True, text=True)",
                "    finally:",
                "        try:",
                "            pipeline_path.unlink()",
                "        except OSError:",
                "            pass"
            };
        }

        /// <summary>Build a stand-alone Python 3 script using only the standard library (no "requests" dependency) plus a "pdal" subprocess call for the convert/crop step.</summary>
        private static string BuildPython(IReadOnlyList<string> tileUrls, string destFolder, bool convert, bool discardRaw, IReadOnlyList<string> cropWktParts)
        {
            var lines = new List<string>
            {
                "#!/usr/bin/env python3",
                "\"\"\"",
                "Kylidar stand-alone download/convert script.",
                $"Generated {DateTime.Now:yyyy-MM-dd HH:mm} -- {tileUrls.Count} tile(s).",
                "",
                "Edit DEST_FOLDER below if needed, then run:",
                "    python \"<this file>\"",
                "",
                "CONVERT needs the \"pdal\" CLI on PATH. LAS dataset creation/pyramids (an ArcGIS Pro",
                "feature) are not included -- add the .las output to a dataset from within Pro if needed.",
                "\"\"\"",
                "import concurrent.futures",
                "import json",
                "import os",
                "import pathlib",
                "import subprocess",
                "import urllib.request",
                "",
                $"DEST_FOLDER = pathlib.Path(\"{EscapePy(destFolder)}\")",
                "CONCURRENCY = max(1, int((os.cpu_count() or 4) * 0.5))  # 50% of this machine's logical cores",
                $"CONVERT = {(convert ? "True" : "False")}",
                $"DISCARD_RAW = {(discardRaw ? "True" : "False")}",
                "",
                "# WKT polygon part(s) to crop each tile to (already buffered) -- empty means no cropping.",
                "CROP_WKT_PARTS = ["
            };
            foreach (var wkt in cropWktParts) lines.Add($"    \"{EscapePy(wkt)}\",");
            lines.Add("]");
            lines.Add("");
            lines.Add("TILES = [");
            foreach (var url in tileUrls) lines.Add($"    \"{EscapePy(url)}\",");
            lines.Add("]");
            lines.Add("");
            lines.Add("");
            lines.AddRange(PythonConvertFunctionLines());
            lines.Add("");
            lines.Add("");
            lines.Add("def process(url):");
            lines.Add("    fname = url.rsplit(\"/\", 1)[-1]");
            lines.Add("    if not CONVERT:");
            lines.Add("        raw_dir = DEST_FOLDER");
            lines.Add("    elif DISCARD_RAW:");
            lines.Add("        raw_dir = DEST_FOLDER / \"_raw_tmp\"");
            lines.Add("    else:");
            lines.Add("        raw_dir = DEST_FOLDER / \"COPC\"");
            lines.Add("    raw_dir.mkdir(parents=True, exist_ok=True)");
            lines.Add("    raw_path = raw_dir / fname");
            lines.Add("    try:");
            lines.Add("        urllib.request.urlretrieve(url, raw_path)");
            lines.Add("    except Exception as ex:");
            lines.Add("        return f\"FAIL download {fname}: {ex}\"");
            lines.Add("");
            lines.Add("    if not CONVERT:");
            lines.Add("        return f\"OK   {fname}\"");
            lines.Add("");
            lines.Add("    las_dir = DEST_FOLDER if DISCARD_RAW else DEST_FOLDER / \"LAS\"");
            lines.Add("    las_dir.mkdir(parents=True, exist_ok=True)");
            lines.Add("    base = fname");
            lines.Add("    for ext in (\".copc.laz\", \".laz\", \".las\"):");
            lines.Add("        if base.lower().endswith(ext):");
            lines.Add("            base = base[: -len(ext)]");
            lines.Add("            break");
            lines.Add("    las_path = las_dir / f\"{base}.las\"");
            lines.Add("");
            lines.Add("    try:");
            lines.Add("        _convert(raw_path, las_path)");
            lines.Add("    except Exception as ex:");
            lines.Add("        return f\"FAIL convert {fname}: {ex}\"");
            lines.Add("");
            lines.Add("    if DISCARD_RAW:");
            lines.Add("        try:");
            lines.Add("            raw_path.unlink()");
            lines.Add("        except OSError:");
            lines.Add("            pass");
            lines.Add("");
            lines.Add("    return f\"OK   {fname} -> {las_path.name}\"");
            lines.Add("");
            lines.Add("");
            lines.Add("def main():");
            lines.Add("    DEST_FOLDER.mkdir(parents=True, exist_ok=True)");
            lines.Add("    ok = fail = 0");
            lines.Add("    with concurrent.futures.ThreadPoolExecutor(max_workers=CONCURRENCY) as ex:");
            lines.Add("        for result in ex.map(process, TILES):");
            lines.Add("            print(result)");
            lines.Add("            if result.startswith(\"OK\"):");
            lines.Add("                ok += 1");
            lines.Add("            else:");
            lines.Add("                fail += 1");
            lines.Add("    print(f\"\\nDone: {ok} succeeded, {fail} failed -> {DEST_FOLDER}\")");
            lines.Add("");
            lines.Add("");
            lines.Add("if __name__ == \"__main__\":");
            lines.Add("    main()");
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Build a Jupyter notebook (.ipynb, nbformat 4) running the same download/convert/crop
        /// logic as BuildPython, split into cells: a markdown intro, a config cell (paths, tiles,
        /// crop WKT -- the part someone re-running this is most likely to edit), a helpers cell
        /// (the pdal/convert/process functions), and a run cell (the actual download+convert loop
        /// and summary). Written directly as nbformat JSON rather than via a notebook library --
        /// the format is simple enough, and this keeps ExportScriptService dependency-free.
        /// </summary>
        private static string BuildNotebook(IReadOnlyList<string> tileUrls, string destFolder, bool convert, bool discardRaw, IReadOnlyList<string> cropWktParts)
        {
            var introSource = new List<string>
            {
                "# Kylidar stand-alone download/convert notebook",
                "",
                $"Generated {DateTime.Now:yyyy-MM-dd HH:mm} -- {tileUrls.Count} tile(s).",
                "",
                "Run the cells below in order. `CONVERT` needs the `pdal` CLI on PATH (e.g. a conda",
                "environment with PDAL installed). LAS dataset creation/pyramids (an ArcGIS Pro feature)",
                "are not included -- add the `.las` output to a dataset from within Pro if needed."
            };

            var configLines = new List<string>
            {
                "import concurrent.futures",
                "import json",
                "import os",
                "import pathlib",
                "import subprocess",
                "import urllib.request",
                "",
                $"DEST_FOLDER = pathlib.Path(\"{EscapePy(destFolder)}\")",
                "CONCURRENCY = max(1, int((os.cpu_count() or 4) * 0.5))  # 50% of this machine's logical cores",
                $"CONVERT = {(convert ? "True" : "False")}",
                $"DISCARD_RAW = {(discardRaw ? "True" : "False")}",
                "",
                "# WKT polygon part(s) to crop each tile to (already buffered) -- empty means no cropping.",
                "CROP_WKT_PARTS = ["
            };
            foreach (var wkt in cropWktParts) configLines.Add($"    \"{EscapePy(wkt)}\",");
            configLines.Add("]");
            configLines.Add("");
            configLines.Add("TILES = [");
            foreach (var url in tileUrls) configLines.Add($"    \"{EscapePy(url)}\",");
            configLines.Add("]");

            var helperLines = new List<string>();
            helperLines.AddRange(PythonConvertFunctionLines());
            helperLines.AddRange(new[]
            {
                "",
                "",
                "def process(url):",
                "    fname = url.rsplit(\"/\", 1)[-1]",
                "    if not CONVERT:",
                "        raw_dir = DEST_FOLDER",
                "    elif DISCARD_RAW:",
                "        raw_dir = DEST_FOLDER / \"_raw_tmp\"",
                "    else:",
                "        raw_dir = DEST_FOLDER / \"COPC\"",
                "    raw_dir.mkdir(parents=True, exist_ok=True)",
                "    raw_path = raw_dir / fname",
                "    try:",
                "        urllib.request.urlretrieve(url, raw_path)",
                "    except Exception as ex:",
                "        return f\"FAIL download {fname}: {ex}\"",
                "",
                "    if not CONVERT:",
                "        return f\"OK   {fname}\"",
                "",
                "    las_dir = DEST_FOLDER if DISCARD_RAW else DEST_FOLDER / \"LAS\"",
                "    las_dir.mkdir(parents=True, exist_ok=True)",
                "    base = fname",
                "    for ext in (\".copc.laz\", \".laz\", \".las\"):",
                "        if base.lower().endswith(ext):",
                "            base = base[: -len(ext)]",
                "            break",
                "    las_path = las_dir / f\"{base}.las\"",
                "",
                "    try:",
                "        _convert(raw_path, las_path)",
                "    except Exception as ex:",
                "        return f\"FAIL convert {fname}: {ex}\"",
                "",
                "    if DISCARD_RAW:",
                "        try:",
                "            raw_path.unlink()",
                "        except OSError:",
                "            pass",
                "",
                "    return f\"OK   {fname} -> {las_path.name}\""
            });

            var runLines = new List<string>
            {
                "DEST_FOLDER.mkdir(parents=True, exist_ok=True)",
                "ok = fail = 0",
                "with concurrent.futures.ThreadPoolExecutor(max_workers=CONCURRENCY) as ex:",
                "    for result in ex.map(process, TILES):",
                "        print(result)",
                "        if result.startswith(\"OK\"):",
                "            ok += 1",
                "        else:",
                "            fail += 1",
                "print(f\"\\nDone: {ok} succeeded, {fail} failed -> {DEST_FOLDER}\")"
            };

            var notebook = new
            {
                cells = new object[]
                {
                    NotebookCell("markdown", introSource),
                    NotebookCell("code", configLines),
                    NotebookCell("code", helperLines),
                    NotebookCell("code", runLines)
                },
                metadata = new
                {
                    kernelspec = new { display_name = "Python 3", language = "python", name = "python3" },
                    language_info = new { name = "python" }
                },
                nbformat = 4,
                nbformat_minor = 5
            };
            return JsonSerializer.Serialize(notebook, new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>nbformat cell object: "source" is an array of lines, each (but the last) ending
        /// in "\n" -- nbformat's own convention for how a multi-line cell body is represented.</summary>
        private static object NotebookCell(string cellType, IReadOnlyList<string> lines)
        {
            var source = new string[lines.Count];
            for (int i = 0; i < lines.Count; i++)
                source[i] = i < lines.Count - 1 ? lines[i] + "\n" : lines[i];

            return cellType == "code"
                ? new { cell_type = cellType, metadata = new { }, execution_count = (int?)null, outputs = Array.Empty<object>(), source }
                : (object)new { cell_type = cellType, metadata = new { }, source };
        }

        /// <summary>
        /// Build a Windows PowerShell 5.1-compatible script (no external modules). Concurrency is
        /// via a throttled batch of background jobs rather than "ForEach-Object -Parallel", which
        /// needs PowerShell 7+; each job's scriptblock takes every value it needs as an explicit
        /// param rather than closing over outer variables, since Start-Job runs in an isolated
        /// runspace that can't see them otherwise. The pipeline JSON is built as a plain string
        /// (an "Esc" helper for backslash/quote escaping, not ConvertTo-Json) rather than via
        /// ConvertTo-Json on a hashtable -- that cmdlet silently unwraps a single-element array
        /// property (e.g. a one-part "polygon"/"inputs" list) down to a bare scalar, which PDAL
        /// would then reject.
        /// </summary>
        private static string BuildPowerShell(IReadOnlyList<string> tileUrls, string destFolder, bool convert, bool discardRaw, IReadOnlyList<string> cropWktParts)
        {
            var lines = new List<string>
            {
                "<#",
                "    Kylidar stand-alone download/convert script",
                $"    Generated {DateTime.Now:yyyy-MM-dd HH:mm} -- {tileUrls.Count} tile(s).",
                "",
                "    Edit $DestFolder below if needed, then run:",
                "        powershell -ExecutionPolicy Bypass -File \"<this file>\"",
                "",
                "    $Convert needs the \"pdal\" CLI on PATH. LAS dataset creation/pyramids (an ArcGIS",
                "    Pro feature) are not included -- add the .las output to a dataset from within Pro",
                "    if needed.",
                "#>",
                "",
                $"$DestFolder  = '{EscapePs(destFolder)}'",
                "$Concurrency = [Math]::Max(1, [Math]::Floor([Environment]::ProcessorCount * 0.5))  # 50% of this machine's logical cores",
                $"$Convert     = ${(convert ? "true" : "false")}",
                $"$DiscardRaw  = ${(discardRaw ? "true" : "false")}",
                "",
                "# WKT polygon part(s) to crop each tile to (already buffered) -- empty means no cropping.",
                "$CropWktParts = @("
            };
            foreach (var wkt in cropWktParts) lines.Add($"    '{EscapePs(wkt)}'");
            lines.Add(")");
            lines.Add("");
            lines.Add("$Urls = @(");
            foreach (var url in tileUrls) lines.Add($"    '{EscapePs(url)}'");
            lines.Add(")");
            lines.Add("");
            lines.Add("New-Item -ItemType Directory -Force -Path $DestFolder | Out-Null");
            lines.Add("");
            lines.Add("$jobs = @()");
            lines.Add("$ok = 0; $fail = 0");
            lines.Add("foreach ($url in $Urls) {");
            lines.Add("    while (@($jobs | Where-Object { $_.State -eq 'Running' }).Count -ge $Concurrency) {");
            lines.Add("        Start-Sleep -Milliseconds 250");
            lines.Add("    }");
            lines.Add("    $jobs += Start-Job -ScriptBlock {");
            lines.Add("        param($url, $destFolder, $convert, $discardRaw, $cropWktParts)");
            lines.Add("        $fname = Split-Path $url -Leaf");
            lines.Add("        if (-not $convert) { $rawDir = $destFolder }");
            lines.Add("        elseif ($discardRaw) { $rawDir = Join-Path $destFolder '_raw_tmp' }");
            lines.Add("        else { $rawDir = Join-Path $destFolder 'COPC' }");
            lines.Add("        New-Item -ItemType Directory -Force -Path $rawDir | Out-Null");
            lines.Add("        $rawPath = Join-Path $rawDir $fname");
            lines.Add("        try {");
            lines.Add("            Invoke-WebRequest -Uri $url -OutFile $rawPath -UseBasicParsing");
            lines.Add("        } catch {");
            lines.Add("            return \"FAIL download $fname : $($_.Exception.Message)\"");
            lines.Add("        }");
            lines.Add("");
            lines.Add("        if (-not $convert) { return \"OK   $fname\" }");
            lines.Add("");
            lines.Add("        if ($discardRaw) { $lasDir = $destFolder } else { $lasDir = Join-Path $destFolder 'LAS' }");
            lines.Add("        New-Item -ItemType Directory -Force -Path $lasDir | Out-Null");
            lines.Add("        $base = $fname");
            lines.Add("        foreach ($ext in @('.copc.laz', '.laz', '.las')) {");
            lines.Add("            if ($base.ToLower().EndsWith($ext)) { $base = $base.Substring(0, $base.Length - $ext.Length); break }");
            lines.Add("        }");
            lines.Add("        $lasPath = Join-Path $lasDir \"$base.las\"");
            lines.Add("");
            lines.Add("        function Esc([string]$s) { $s -replace '\\\\','\\\\' -replace '\"','\\\"' }");
            lines.Add("");
            lines.Add("        try {");
            lines.Add("            $isCopc = $fname.ToLower().EndsWith('.copc.laz')");
            lines.Add("            $readerType = if ($isCopc) { 'readers.copc' } else { 'readers.las' }");
            lines.Add("");
            lines.Add("            if ($cropWktParts.Count -eq 0) {");
            lines.Add("                $pipelineJson = '{\"pipeline\":[{\"type\":\"' + $readerType + '\",\"filename\":\"' + (Esc $rawPath) + '\",\"tag\":\"read\"},{\"type\":\"writers.las\",\"filename\":\"' + (Esc $lasPath) + '\",\"inputs\":[\"read\"]}]}'");
            lines.Add("            } else {");
            lines.Add("                # Always readers.las + filters.crop, even for a .copc.laz tile -- it reads a");
            lines.Add("                # COPC file's points just fine, and readers.copc's own \"polygon\" option can");
            lines.Add("                # crash PDAL outright on a complex/high-vertex AOI (see this file's header comment).");
            lines.Add("                $stageParts = @('{\"type\":\"readers.las\",\"filename\":\"' + (Esc $rawPath) + '\",\"tag\":\"read\"}')");
            lines.Add("                $cropTags = @()");
            lines.Add("                for ($i = 0; $i -lt $cropWktParts.Count; $i++) {");
            lines.Add("                    $tag = \"crop$i\"");
            lines.Add("                    $cropTags += $tag");
            lines.Add("                    $stageParts += '{\"type\":\"filters.crop\",\"polygon\":\"' + (Esc $cropWktParts[$i]) + '\",\"a_srs\":\"EPSG:4326\",\"inputs\":[\"read\"],\"tag\":\"' + $tag + '\"}'");
            lines.Add("                }");
            lines.Add("                $finalTag = $cropTags[0]");
            lines.Add("                if ($cropTags.Count -gt 1) {");
            lines.Add("                    $mergeInputs = ($cropTags | ForEach-Object { '\"' + $_ + '\"' }) -join ','");
            lines.Add("                    $stageParts += '{\"type\":\"filters.merge\",\"inputs\":[' + $mergeInputs + '],\"tag\":\"merged\"}'");
            lines.Add("                    $finalTag = 'merged'");
            lines.Add("                }");
            lines.Add("                $stageParts += '{\"type\":\"writers.las\",\"filename\":\"' + (Esc $lasPath) + '\",\"inputs\":[\"' + $finalTag + '\"]}'");
            lines.Add("                $pipelineJson = '{\"pipeline\":[' + ($stageParts -join ',') + ']}'");
            lines.Add("            }");
            lines.Add("");
            lines.Add("            $pipelinePath = \"$lasPath.pipeline.json\"");
            lines.Add("            Set-Content -Path $pipelinePath -Value $pipelineJson -Encoding utf8 -NoNewline");
            lines.Add("            try {");
            lines.Add("                & pdal pipeline $pipelinePath 2>&1 | Out-Null");
            lines.Add("                if ($LASTEXITCODE -ne 0) { return \"FAIL convert $fname (pdal exit $LASTEXITCODE)\" }");
            lines.Add("            } finally {");
            lines.Add("                Remove-Item $pipelinePath -Force -ErrorAction SilentlyContinue");
            lines.Add("            }");
            lines.Add("        } catch {");
            lines.Add("            return \"FAIL convert $fname : $($_.Exception.Message)\"");
            lines.Add("        }");
            lines.Add("");
            lines.Add("        if ($discardRaw) { Remove-Item $rawPath -Force -ErrorAction SilentlyContinue }");
            lines.Add("        return \"OK   $fname -> $(Split-Path $lasPath -Leaf)\"");
            lines.Add("    } -ArgumentList $url, $DestFolder, $Convert, $DiscardRaw, $CropWktParts");
            lines.Add("}");
            lines.Add("");
            lines.Add("$jobs | Wait-Job | Out-Null");
            lines.Add("foreach ($j in $jobs) {");
            lines.Add("    $result = Receive-Job $j");
            lines.Add("    Write-Host $result");
            lines.Add("    if ($result -like 'OK *') { $ok++ } else { $fail++ }");
            lines.Add("    Remove-Job $j");
            lines.Add("}");
            lines.Add("");
            lines.Add("Write-Host \"\"");
            lines.Add("Write-Host \"Done: $ok succeeded, $fail failed -> $DestFolder\"");
            return string.Join("\r\n", lines);
        }

        /// <summary>
        /// Build a POSIX shell script (macOS/Linux/WSL) using curl, run in batches of $CONCURRENCY
        /// at a time (a plain "wait" after each batch rather than "wait -n", so it works on the old
        /// bash 3.2 macOS still ships by default, not just bash 4.3+).
        /// </summary>
        private static string BuildShell(IReadOnlyList<string> tileUrls, string destFolder, bool convert, bool discardRaw, IReadOnlyList<string> cropWktParts)
        {
            var lines = new List<string>
            {
                "#!/usr/bin/env bash",
                "# Kylidar stand-alone download/convert script",
                $"# Generated {DateTime.Now:yyyy-MM-dd HH:mm} -- {tileUrls.Count} tile(s).",
                "# Edit DEST_FOLDER below if needed, then run: bash \"<this file>\"",
                "# CONVERT needs the \"pdal\" CLI on PATH. LAS dataset creation/pyramids (an ArcGIS Pro",
                "# feature) are not included -- add the .las output to a dataset from within Pro if needed.",
                "set -u",
                "",
                $"DEST_FOLDER=\"{EscapeSh(destFolder)}\"",
                "",
                "# 50% of this machine's logical cores (nproc: Linux/WSL, getconf: POSIX fallback, sysctl: macOS).",
                "if command -v nproc >/dev/null 2>&1; then NCPU=$(nproc)",
                "elif command -v getconf >/dev/null 2>&1; then NCPU=$(getconf _NPROCESSORS_ONLN)",
                "elif command -v sysctl >/dev/null 2>&1; then NCPU=$(sysctl -n hw.ncpu)",
                "else NCPU=4; fi",
                "CONCURRENCY=$(( NCPU * 50 / 100 ))",
                "if [ \"$CONCURRENCY\" -lt 1 ]; then CONCURRENCY=1; fi",
                "",
                $"CONVERT={(convert ? 1 : 0)}",
                $"DISCARD_RAW={(discardRaw ? 1 : 0)}",
                "",
                "mkdir -p \"$DEST_FOLDER\"",
                "",
                "# WKT polygon part(s) to crop each tile to (already buffered) -- empty means no cropping.",
                "CROP_WKT_PARTS=("
            };
            foreach (var wkt in cropWktParts) lines.Add($"  \"{EscapeSh(wkt)}\"");
            lines.Add(")");
            lines.Add("");
            lines.Add("URLS=(");
            foreach (var url in tileUrls) lines.Add($"  \"{EscapeSh(url)}\"");
            lines.Add(")");
            lines.Add("");
            lines.Add("# Builds a PDAL pipeline JSON file and runs \"pdal pipeline\" on it, rather than");
            lines.Add("# \"pdal translate ... crop --filters.crop.polygon=<wkt>\" on the command line: the crop");
            lines.Add("# WKT can run tens of thousands of characters for a complex AOI, which overflows the");
            lines.Add("# command-line length even on Linux/macOS for a large enough polygon. A file has no such limit.");
            lines.Add("");
            lines.Add("# JSON-escapes a string (backslash and double-quote) for embedding in the hand-built");
            lines.Add("# pipeline JSON below -- paths in particular need this (not just the crop WKT, which never");
            lines.Add("# has either character in practice): DEST_FOLDER is whatever the ArcGIS Pro side exported,");
            lines.Add("# and Pro only runs on Windows, so it's routinely a backslash-separated path.");
            lines.Add("json_escape() {");
            lines.Add("  printf '%s' \"$1\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g'");
            lines.Add("}");
            lines.Add("");
            lines.Add("convert_tile() {");
            lines.Add("  local raw_path=\"$1\" las_path=\"$2\" fname=\"$3\"");
            lines.Add("  local raw_path_j; raw_path_j=\"$(json_escape \"$raw_path\")\"");
            lines.Add("  local las_path_j; las_path_j=\"$(json_escape \"$las_path\")\"");
            lines.Add("  local is_copc=0");
            lines.Add("  case \"$fname\" in");
            lines.Add("    *.copc.laz) is_copc=1 ;;");
            lines.Add("  esac");
            lines.Add("  local reader_type=\"readers.las\"");
            lines.Add("  if [ \"$is_copc\" -eq 1 ]; then reader_type=\"readers.copc\"; fi");
            lines.Add("");
            lines.Add("  local pipeline_json");
            lines.Add("  if [ ${#CROP_WKT_PARTS[@]} -eq 0 ]; then");
            lines.Add("    pipeline_json=\"{\\\"pipeline\\\":[{\\\"type\\\":\\\"$reader_type\\\",\\\"filename\\\":\\\"$raw_path_j\\\",\\\"tag\\\":\\\"read\\\"},{\\\"type\\\":\\\"writers.las\\\",\\\"filename\\\":\\\"$las_path_j\\\",\\\"inputs\\\":[\\\"read\\\"]}]}\"");
            lines.Add("  else");
            lines.Add("    # Always readers.las + filters.crop, even for a .copc.laz tile -- it reads a COPC");
            lines.Add("    # file's points just fine, and readers.copc's own \"polygon\" option can crash PDAL");
            lines.Add("    # outright on a complex/high-vertex AOI (see this file's header comment).");
            lines.Add("    local stages=\"{\\\"type\\\":\\\"readers.las\\\",\\\"filename\\\":\\\"$raw_path_j\\\",\\\"tag\\\":\\\"read\\\"}\"");
            lines.Add("    local crop_tags=() i=0");
            lines.Add("    for wkt in \"${CROP_WKT_PARTS[@]}\"; do");
            lines.Add("      local wkt_j; wkt_j=\"$(json_escape \"$wkt\")\"");
            lines.Add("      local tag=\"crop${i}\"");
            lines.Add("      crop_tags+=(\"$tag\")");
            lines.Add("      stages=\"${stages},{\\\"type\\\":\\\"filters.crop\\\",\\\"polygon\\\":\\\"${wkt_j}\\\",\\\"a_srs\\\":\\\"EPSG:4326\\\",\\\"inputs\\\":[\\\"read\\\"],\\\"tag\\\":\\\"${tag}\\\"}\"");
            lines.Add("      i=$((i + 1))");
            lines.Add("    done");
            lines.Add("    local final_tag=\"${crop_tags[0]}\"");
            lines.Add("    if [ ${#crop_tags[@]} -gt 1 ]; then");
            lines.Add("      local merge_inputs=\"\" sep=\"\"");
            lines.Add("      for t in \"${crop_tags[@]}\"; do");
            lines.Add("        merge_inputs=\"${merge_inputs}${sep}\\\"${t}\\\"\"");
            lines.Add("        sep=\",\"");
            lines.Add("      done");
            lines.Add("      stages=\"${stages},{\\\"type\\\":\\\"filters.merge\\\",\\\"inputs\\\":[$merge_inputs],\\\"tag\\\":\\\"merged\\\"}\"");
            lines.Add("      final_tag=\"merged\"");
            lines.Add("    fi");
            lines.Add("    stages=\"${stages},{\\\"type\\\":\\\"writers.las\\\",\\\"filename\\\":\\\"$las_path_j\\\",\\\"inputs\\\":[\\\"${final_tag}\\\"]}\"");
            lines.Add("    pipeline_json=\"{\\\"pipeline\\\":[${stages}]}\"");
            lines.Add("  fi");
            lines.Add("");
            lines.Add("  local pipeline_path=\"${las_path}.pipeline.json\"");
            lines.Add("  printf '%s' \"$pipeline_json\" > \"$pipeline_path\"");
            lines.Add("  pdal pipeline \"$pipeline_path\" >/dev/null 2>&1");
            lines.Add("  local ok=$?");
            lines.Add("  rm -f \"$pipeline_path\"");
            lines.Add("  return $ok");
            lines.Add("}");
            lines.Add("");
            lines.Add("process() {");
            lines.Add("  local url=\"$1\"");
            lines.Add("  local fname; fname=\"$(basename \"$url\")\"");
            lines.Add("");
            lines.Add("  local raw_dir");
            lines.Add("  if [ \"$CONVERT\" -eq 0 ]; then raw_dir=\"$DEST_FOLDER\"");
            lines.Add("  elif [ \"$DISCARD_RAW\" -eq 1 ]; then raw_dir=\"$DEST_FOLDER/_raw_tmp\"");
            lines.Add("  else raw_dir=\"$DEST_FOLDER/COPC\"; fi");
            lines.Add("  mkdir -p \"$raw_dir\"");
            lines.Add("  local raw_path=\"$raw_dir/$fname\"");
            lines.Add("");
            lines.Add("  if ! curl -fsSL -o \"$raw_path\" \"$url\"; then");
            lines.Add("    echo \"FAIL download $fname\"");
            lines.Add("    return");
            lines.Add("  fi");
            lines.Add("");
            lines.Add("  if [ \"$CONVERT\" -eq 0 ]; then");
            lines.Add("    echo \"OK   $fname\"");
            lines.Add("    return");
            lines.Add("  fi");
            lines.Add("");
            lines.Add("  local las_dir");
            lines.Add("  if [ \"$DISCARD_RAW\" -eq 1 ]; then las_dir=\"$DEST_FOLDER\"; else las_dir=\"$DEST_FOLDER/LAS\"; fi");
            lines.Add("  mkdir -p \"$las_dir\"");
            lines.Add("  local base=\"$fname\"");
            lines.Add("  case \"$base\" in");
            lines.Add("    *.copc.laz) base=\"${base%.copc.laz}\" ;;");
            lines.Add("    *.laz) base=\"${base%.laz}\" ;;");
            lines.Add("    *.las) base=\"${base%.las}\" ;;");
            lines.Add("  esac");
            lines.Add("  local las_path=\"$las_dir/$base.las\"");
            lines.Add("");
            lines.Add("  if ! convert_tile \"$raw_path\" \"$las_path\" \"$fname\"; then");
            lines.Add("    echo \"FAIL convert $fname\"");
            lines.Add("    return");
            lines.Add("  fi");
            lines.Add("");
            lines.Add("  if [ \"$DISCARD_RAW\" -eq 1 ]; then rm -f \"$raw_path\"; fi");
            lines.Add("  echo \"OK   $fname -> $(basename \"$las_path\")\"");
            lines.Add("}");
            lines.Add("");
            lines.Add("i=0");
            lines.Add("for url in \"${URLS[@]}\"; do");
            lines.Add("  process \"$url\" &");
            lines.Add("  i=$((i + 1))");
            lines.Add("  if [ $((i % CONCURRENCY)) -eq 0 ]; then wait; fi");
            lines.Add("done");
            lines.Add("wait");
            lines.Add("");
            lines.Add("echo \"\"");
            lines.Add("echo \"Done -> $DEST_FOLDER\"");
            return string.Join("\n", lines);
        }
    }
}
