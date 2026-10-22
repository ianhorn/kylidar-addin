using System;
using System.IO;

namespace KylidarAddin
{
    /// <summary>Pro greys out the Kylidar button without a word when the pane fails to build, so a failure
    /// there is written to %LOCALAPPDATA%\Kylidar\startup-error.log and shown in a message box.</summary>
    internal static class StartupLog
    {
        public static void Report(string what, Exception ex)
        {
            string path = null;
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kylidar");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "startup-error.log");
                File.AppendAllText(path, $"{DateTime.Now:s} while {what}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
            try
            {
                ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show(
                    $"Kylidar failed while {what}:{Environment.NewLine}{Environment.NewLine}{ex.GetType().Name}: {ex.Message}"
                    + (path != null ? $"{Environment.NewLine}{Environment.NewLine}Details: {path}" : ""),
                    "Kylidar");
            }
            catch { }
        }
    }
}
