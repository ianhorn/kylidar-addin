/*
 * Dock pane view model: hosts the AOI tool launchers, clip settings, run/cancel, progress log,
 * and post-run "add to LAS dataset" follow-up that used to live on the ribbon tab and in the
 * BufferInputDialog/ProgressDialog modal windows. See ClipLidarButton's former OnClick for the
 * run/cancel/LAS-dataset logic this was moved from.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using KylidarAddin.Services;
using Microsoft.Win32;

namespace KylidarAddin
{
    internal class KylidarDockpaneViewModel : DockPane
    {
        private const string DockPaneId = "KylidarAddin_Dockpane";

        protected KylidarDockpaneViewModel()
        {
            // AOI changes come from a map-sketch completion, not an input event inside this pane,
            // so WPF's CommandManager.RequerySuggested (which RunCommand's CanExecute relies on)
            // never fires on its own -- without this, Run stays stale-disabled after finishing a
            // sketch until some unrelated click/keystroke elsewhere happens to trigger a requery.
            AoiState.Changed += (s, e) =>
            {
                UpdateAoiStatus();
                CommandManager.InvalidateRequerySuggested();
            };
            UpdateAoiStatus();
        }

        internal static void Show()
        {
            var pane = FrameworkApplication.DockPaneManager.Find(DockPaneId);
            pane?.Activate();
        }

        #region AOI

        private string _aoiStatusText;
        public string AoiStatusText
        {
            get => _aoiStatusText;
            set => SetProperty(ref _aoiStatusText, value);
        }

        private void UpdateAoiStatus()
        {
            AoiStatusText = AoiState.Current == null
                ? "No AOI yet -- draw or select one below."
                : $"AOI ready: {AoiState.Description}";
        }

        private static async void ActivateTool(string toolId) => await FrameworkApplication.SetCurrentToolAsync(toolId);

        public ICommand DrawPointCommand => new RelayCommand(() => ActivateTool(DrawPointAoiTool.ToolId));
        public ICommand DrawLineCommand => new RelayCommand(() => ActivateTool(DrawLineAoiTool.ToolId));
        public ICommand DrawPolygonCommand => new RelayCommand(() => ActivateTool(DrawPolygonAoiTool.ToolId));
        public ICommand SelectFeatureCommand => new RelayCommand(() => ActivateTool(SelectFeatureAoiTool.ToolId));

        #endregion

        #region Clip settings

        private string _bufferFeetText = "0";
        public string BufferFeetText
        {
            get => _bufferFeetText;
            set => SetProperty(ref _bufferFeetText, value);
        }

        private bool _phase1;
        public bool Phase1 { get => _phase1; set => SetProperty(ref _phase1, value); }

        private bool _phase2;
        public bool Phase2 { get => _phase2; set => SetProperty(ref _phase2, value); }

        private bool _phase3;
        public bool Phase3 { get => _phase3; set => SetProperty(ref _phase3, value); }

        private string _outputLasPath = DefaultOutputPath();
        public string OutputLasPath
        {
            get => _outputLasPath;
            set => SetProperty(ref _outputLasPath, value);
        }

        private static string DefaultOutputPath() =>
            Path.Combine(Path.GetTempPath(), $"kylidar_clip_{DateTime.Now:yyyyMMdd_HHmmss}.las");

        public ICommand BrowseOutputCommand => new RelayCommand(BrowseOutput);

        private void BrowseOutput()
        {
            var dlg = new SaveFileDialog { Filter = "LAS files (*.las)|*.las", FileName = Path.GetFileName(OutputLasPath) };
            if (dlg.ShowDialog() == true) OutputLasPath = dlg.FileName;
        }

        #endregion

        #region Run / cancel

        private string _validationText;
        public string ValidationText
        {
            get => _validationText;
            set => SetProperty(ref _validationText, value);
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set => SetProperty(ref _isRunning, value);
        }

        public ObservableCollection<string> LogLines { get; } = new ObservableCollection<string>();

        private CancellationTokenSource _cts;

        public ICommand RunCommand => new RelayCommand(async () => await RunAsync(), CanRun);
        public ICommand CancelCommand => new RelayCommand(() => _cts?.Cancel(), () => IsRunning);

        /// <summary>
        /// A non-polygon AOI (point or line) has no area to search/crop against until it's
        /// buffered -- matches the guard in CopcClipService.PrepareAoi, but checked up front here
        /// so Run is disabled instead of failing after a round trip to the STAC API.
        /// </summary>
        private bool CanRun()
        {
            if (IsRunning) return false;
            if (AoiState.Current != null && !(AoiState.Current is Polygon))
                return double.TryParse(BufferFeetText, out var feet) && feet > 0;
            return true;
        }

        private async Task RunAsync()
        {
            ValidationText = string.Empty;

            if (AoiState.Current == null)
            {
                ValidationText = "Draw an AOI (point, line, or polygon) or select a feature first.";
                return;
            }
            if (!double.TryParse(BufferFeetText, out var bufferFeet) || bufferFeet < 0)
            {
                ValidationText = "Enter a buffer distance of 0 or more.";
                return;
            }
            if (string.IsNullOrWhiteSpace(OutputLasPath))
            {
                ValidationText = "Choose an output .las file path.";
                return;
            }

            var collections = new List<string>();
            if (Phase1) collections.Add("laz-phase1");
            if (Phase2) collections.Add("laz-phase2");
            if (Phase3) collections.Add("laz-phase3");
            if (collections.Count == 0)
            {
                ValidationText = "Select at least one LiDAR phase.";
                return;
            }

            ShowLasDatasetPrompt = false;
            LogLines.Clear();
            IsRunning = true;
            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(msg => LogLines.Add(msg));

            ClipResult result = null;
            try
            {
                var aoi = AoiState.Current;
                var (geoJson, wkt, bufferedAoi) = await QueuedTask.Run(() => CopcClipService.PrepareAoi(aoi, bufferFeet, progress));
                result = await CopcClipService.ClipToAoiAsync(geoJson, wkt, bufferedAoi, collections, OutputLasPath, progress, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                LogLines.Add("Cancelled.");
            }
            catch (Exception ex)
            {
                LogLines.Add("Error: " + ex.Message);
            }
            finally
            {
                IsRunning = false;
                _cts = null;
            }

            if (result == null) return;

            if (!result.Success)
            {
                LogLines.Add("Failed: " + result.Error);
                return;
            }

            LogLines.Add("Done: " + result.OutputLasPath);
            _lastOutputLasPath = result.OutputLasPath;
            ShowLasDatasetPrompt = true;
        }

        #endregion

        #region Post-run: add to LAS dataset

        private string _lastOutputLasPath;

        private bool _showLasDatasetPrompt;
        public bool ShowLasDatasetPrompt
        {
            get => _showLasDatasetPrompt;
            set => SetProperty(ref _showLasDatasetPrompt, value);
        }

        public ICommand CreateNewLasDatasetCommand => new RelayCommand(async () => await CreateNewLasDatasetAsync());
        public ICommand AddToExistingLasDatasetCommand => new RelayCommand(async () => await AddToExistingLasDatasetAsync());

        private async Task CreateNewLasDatasetAsync()
        {
            var dlg = new SaveFileDialog
            {
                Filter = "LAS Dataset (*.lasd)|*.lasd",
                FileName = Path.GetFileNameWithoutExtension(_lastOutputLasPath) + ".lasd"
            };
            if (dlg.ShowDialog() != true) return;

            var progress = new Progress<string>(msg => LogLines.Add(msg));
            var ok = await LasDatasetService.CreateLasDatasetAsync(_lastOutputLasPath, dlg.FileName, progress);
            if (ok) await LasDatasetService.AddToMapAsync(dlg.FileName);
            else MessageBox.Show("Failed to create the LAS dataset.", "Kylidar", MessageBoxButton.OK, MessageBoxImage.Error);

            ShowLasDatasetPrompt = false;
        }

        private async Task AddToExistingLasDatasetAsync()
        {
            var dlg = new OpenFileDialog { Filter = "LAS Dataset (*.lasd)|*.lasd" };
            if (dlg.ShowDialog() != true) return;

            var progress = new Progress<string>(msg => LogLines.Add(msg));
            var ok = await LasDatasetService.AddFilesToLasDatasetAsync(dlg.FileName, _lastOutputLasPath, progress);
            if (ok) await LasDatasetService.AddToMapAsync(dlg.FileName);
            else MessageBox.Show("Failed to add the LAS file to the dataset.", "Kylidar", MessageBoxButton.OK, MessageBoxImage.Error);

            ShowLasDatasetPrompt = false;
        }

        #endregion
    }

    /// <summary>Ribbon entry point that activates the Kylidar dock pane.</summary>
    internal class KylidarDockpaneShowButton : Button
    {
        protected override void OnClick() => KylidarDockpaneViewModel.Show();
    }
}
