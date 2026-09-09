/*
 * Ribbon button: runs the AOI -> STAC search -> download -> PDAL clip/merge -> LAS dataset
 * workflow against the geometry most recently captured by a Draw AOI tool (AoiState.Current).
 */
using System;
using System.IO;
using System.Threading;
using System.Windows;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using KylidarAddin.Services;

namespace KylidarAddin
{
    internal class ClipLidarButton : Button
    {
        protected override async void OnClick()
        {
            if (AoiState.Current == null)
            {
                MessageBox.Show("Draw an AOI first (point, line, or polygon), then click this button.",
                    "Kylidar", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new BufferInputDialog { Owner = Application.Current?.MainWindow };
            if (dialog.ShowDialog() != true) return;

            var progressDialog = new ProgressDialog("Clip LiDAR to AOI") { Owner = Application.Current?.MainWindow };
            var cts = new CancellationTokenSource();
            progressDialog.CancelRequested += (s, e) => cts.Cancel();

            var progress = new Progress<string>(msg => progressDialog.Append(msg));
            progressDialog.Show();

            ClipResult result = null;
            try
            {
                var aoi = AoiState.Current;
                var bufferFeet = dialog.BufferFeet;
                var (geoJson, wkt) = await QueuedTask.Run(() => CopcClipService.PrepareAoi(aoi, bufferFeet, progress));
                result = await CopcClipService.ClipToAoiAsync(geoJson, wkt, dialog.SelectedCollections, dialog.OutputLasPath, progress, cts.Token);
            }
            catch (OperationCanceledException)
            {
                progressDialog.Append("Cancelled.");
            }
            catch (Exception ex)
            {
                progressDialog.Append("Error: " + ex.Message);
            }
            finally
            {
                progressDialog.DisableCancel();
            }

            if (result == null)
            {
                progressDialog.CloseWhenReady();
                return;
            }

            if (!result.Success)
            {
                progressDialog.Append("Failed: " + result.Error);
                progressDialog.CloseWhenReady();
                return;
            }

            progressDialog.Append("Done: " + result.OutputLasPath);
            progressDialog.CloseWhenReady();

            await OfferAddToLasDatasetAsync(result.OutputLasPath);
        }

        private static async System.Threading.Tasks.Task OfferAddToLasDatasetAsync(string lasPath)
        {
            var choice = MessageBox.Show(
                "Add the clipped LAS to a new LAS dataset?\n\nYes = create new .lasd\nNo = add to an existing .lasd\nCancel = don't add",
                "Kylidar", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (choice == MessageBoxResult.Cancel) return;

            var progress = new Progress<string>(_ => { });

            if (choice == MessageBoxResult.Yes)
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "LAS Dataset (*.lasd)|*.lasd", FileName = Path.GetFileNameWithoutExtension(lasPath) + ".lasd" };
                if (dlg.ShowDialog() != true) return;

                var ok = await LasDatasetService.CreateLasDatasetAsync(lasPath, dlg.FileName, progress);
                if (ok) await LasDatasetService.AddToMapAsync(dlg.FileName);
                else MessageBox.Show("Failed to create the LAS dataset.", "Kylidar", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "LAS Dataset (*.lasd)|*.lasd" };
                if (dlg.ShowDialog() != true) return;

                var ok = await LasDatasetService.AddFilesToLasDatasetAsync(dlg.FileName, lasPath, progress);
                if (ok) await LasDatasetService.AddToMapAsync(dlg.FileName);
                else MessageBox.Show("Failed to add the LAS file to the dataset.", "Kylidar", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
