/*
 * Runs PDAL pipelines using the PDAL install bundled inside ArcGIS Pro's own conda Python
 * environment (arcgispro-py3) -- no separate PDAL install required.
 *
 * Invoked via that environment's python.exe + the "pdal" Python package, NOT the bundled
 * pdal.exe directly -- the raw exe fails to load (STATUS_DLL_NOT_FOUND) outside a fully
 * activated conda environment, while the Python package works unmodified.
 *
 * Pipelines must only reference LOCAL file paths. PDAL's bundled libcurl cannot complete TLS
 * verification against S3 on machines that intercept HTTPS (e.g. antivirus HTTPS scanning) --
 * confirmed during development on a Norton-equipped machine, where neither CURL_CA_BUNDLE,
 * SSL_CERT_FILE, nor an exported Windows-root CA bundle fixed it. Any remote asset must be
 * downloaded first (see CopcClipService) and only local paths handed to PDAL.
 */
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace KylidarAddin.Services
{
    public class PdalRunResult
    {
        public bool Success { get; set; }
        public string Log { get; set; }
        public string Error { get; set; }
    }

    public static class PdalRunner
    {
        /// <summary>
        /// Locate ArcGIS Pro's bundled conda Python (arcgispro-py3), which ships PDAL.
        /// Returns null if not found.
        /// </summary>
        public static string FindPythonExe()
        {
            string installDir = null;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ESRI\ArcGISPro");
                installDir = key?.GetValue("InstallDir") as string;
            }
            catch { /* fall through to default */ }

            installDir ??= @"C:\Program Files\ArcGIS\Pro\";
            var python = Path.Combine(installDir, "bin", "Python", "envs", "arcgispro-py3", "python.exe");
            return File.Exists(python) ? python : null;
        }

        /// <summary>
        /// Execute a PDAL pipeline (JSON string, referencing only local file paths) and report
        /// progress lines as they arrive.
        /// </summary>
        public static async Task<PdalRunResult> RunPipelineAsync(string pipelineJson, IProgress<string> progress = null, CancellationToken ct = default)
        {
            var pythonExe = FindPythonExe();
            if (pythonExe == null)
                return new PdalRunResult { Success = false, Error = "Could not find ArcGIS Pro's bundled Python (arcgispro-py3)." };

            var tempPipelinePath = Path.Combine(Path.GetTempPath(), $"kylidar_pipeline_{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(tempPipelinePath, pipelineJson, ct).ConfigureAwait(false);

            const string script =
                "import sys, pdal\n" +
                "with open(sys.argv[1], 'r', encoding='utf-8') as f:\n" +
                "    pipe_json = f.read()\n" +
                "p = pdal.Pipeline(pipe_json)\n" +
                "n = p.execute()\n" +
                "print(f'PDAL: {n} points written')\n";

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                ArgumentList = { "-c", script, tempPipelinePath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var log = new StringBuilder();
            try
            {
                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (s, e) => { if (e.Data != null) { log.AppendLine(e.Data); progress?.Report(e.Data); } };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) { log.AppendLine(e.Data); progress?.Report(e.Data); } };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await using (ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { /* ignore */ } }))
                {
                    await process.WaitForExitAsync(ct).ConfigureAwait(false);
                }

                return process.ExitCode == 0
                    ? new PdalRunResult { Success = true, Log = log.ToString() }
                    : new PdalRunResult { Success = false, Log = log.ToString(), Error = $"PDAL exited with code {process.ExitCode}" };
            }
            catch (Exception ex)
            {
                return new PdalRunResult { Success = false, Log = log.ToString(), Error = ex.Message };
            }
            finally
            {
                try { File.Delete(tempPipelinePath); } catch { /* ignore */ }
            }
        }
    }
}
