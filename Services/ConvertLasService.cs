/*
 * Wraps the "Convert LAS" geoprocessing tool (3D Analyst) to compress a .las file into Esri's own
 * zLAS format (.zlas). PDAL cannot write this itself -- PDAL's writers.las only supports
 * LASzip/.laz compression, not Esri's proprietary zLAS -- so this is a separate post-conversion
 * step rather than something foldable into CopcClipService's PDAL pipeline.
 *
 * Confirmed empirically (propy) that Convert LAS can compress a raw .laz/.copc.laz tile directly,
 * with no PDAL step at all, but that fast path isn't used here: clipping still needs PDAL, and
 * since this is only offered alongside the two "convert" output modes (never "download COPC only"),
 * every tile reaching this class has already been through PDAL regardless of whether it was clipped.
 *
 * Two constraints on Convert LAS, also confirmed empirically (ERROR 000572 / 000732 otherwise):
 * target_folder must already exist, and it cannot be the same folder the input file is already in.
 * So each call compresses into a throwaway temp subfolder next to the source file, then the result
 * is moved back alongside it and the uncompressed .las is deleted.
 *
 * file_version and point_format are passed as explicit values, not "" (empty) -- a real run hit
 * "ERROR 000735: %s: Value is required" calling this through ExecuteToolAsync, which neither an
 * identical raw GP engine call (arcpy.gp.ConvertLas_conversion, the closest Python equivalent to
 * this C# API) nor arcpy's own ConvertLas wrapper reproduced with the same empty-string arguments --
 * so whatever's intolerant of "" here is specific to this .NET API binding, not COPC-vs-plain-LAS or
 * some GP-engine-level rule. point_format in particular has no documented "use the input's own"
 * token (unlike file_version's "Same As Input"), and "" isn't among its allowed values, so this
 * reads the input's actual point format straight from its header instead of guessing at a string.
 */
using System;
using System.IO;
using System.Threading.Tasks;
using ArcGIS.Desktop.Core.Geoprocessing;

namespace KylidarAddin.Services
{
    public static class ConvertLasService
    {
        /// <summary>
        /// Compress one .las file to a .zlas sibling in the same folder, deleting the original.
        /// Returns the new path, or null (after reporting the GP error via progress) on failure,
        /// leaving the original file untouched.
        /// </summary>
        public static async Task<string> CompressToZlasAsync(string lasPath, IProgress<string> progress)
        {
            var folder = Path.GetDirectoryName(lasPath);
            var tempTarget = Path.Combine(folder, "_zlas_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempTarget);
            try
            {
                var pointFormat = ReadPointFormat(lasPath);
                var args = Geoprocessing.MakeValueArray(lasPath, tempTarget, "Same As Input", pointFormat, "zLAS Compression");
                var result = await Geoprocessing.ExecuteToolAsync("ConvertLas_conversion", args,
                    environments: null, flags: GPExecuteToolFlags.None).ConfigureAwait(false);
                if (result.IsFailed)
                {
                    progress?.Report($"Convert LAS (zLAS compression) failed on {Path.GetFileName(lasPath)}: " + string.Join("; ", result.ErrorMessages));
                    return null;
                }

                // Convert LAS names its output by swapping just the input's last extension for
                // ".zlas" (e.g. "tile.copc.laz" -> "tile.copc.zlas") -- same rule as Path.ChangeExtension.
                var producedName = Path.GetFileName(Path.ChangeExtension(lasPath, ".zlas"));
                var producedPath = Path.Combine(tempTarget, producedName);
                if (!File.Exists(producedPath))
                {
                    progress?.Report($"Convert LAS reported success for {Path.GetFileName(lasPath)}, but the expected .zlas output wasn't found.");
                    return null;
                }

                var finalPath = Path.Combine(folder, producedName);
                File.Move(producedPath, finalPath, overwrite: true);
                File.Delete(lasPath);
                return finalPath;
            }
            finally
            {
                try { Directory.Delete(tempTarget, recursive: true); } catch { /* ignore -- best-effort cleanup of the throwaway temp subfolder */ }
            }
        }

        /// <summary>
        /// The LAS public header block's "Point Data Record Format" field -- a single byte at a
        /// fixed offset (104) in every LAS version from 1.0 through 1.4; later versions only add
        /// fields after the legacy header, they never move this one. Confirmed empirically against
        /// a real file's PDAL-reported dataformat_id (both agreed: 7). Falls back to "3" (a common,
        /// widely-supported format with no extra bytes) if the file is unreadable or too short,
        /// rather than letting a parse problem here throw and skip Convert LAS's own error handling.
        /// </summary>
        private static string ReadPointFormat(string lasPath)
        {
            const int PointFormatOffset = 104;
            try
            {
                using var stream = new FileStream(lasPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length <= PointFormatOffset) return "3";
                stream.Seek(PointFormatOffset, SeekOrigin.Begin);
                var pointFormat = stream.ReadByte();
                return pointFormat < 0 ? "3" : pointFormat.ToString();
            }
            catch
            {
                return "3";
            }
        }
    }
}
