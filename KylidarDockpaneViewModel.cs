/*
 * Dock pane view model: hosts the AOI tool launchers, LiDAR phase selection, a catalog-search
 * preview, output mode (download raw COPC files only / convert to .las keeping the raw COPCs /
 * convert discarding them -- always as individual per-tile files, no merge option -- optionally
 * cropped to the drawn/selected AOI, see below), LAS dataset disposition (none/create/add-to-
 * existing + pyramids), run/cancel, an "Export Script" alternative to Run that writes a stand-alone
 * download/convert kit instead of running here (see ExportScriptService), and the progress log.
 * The output/dataset choices are all made up front, before Run, rather than prompted for afterward
 * -- RunAsync executes the whole chosen pipeline in one pass.
 *
 * "Clip to area of interest" crops each tile to the AOI (plus a required buffer -- see
 * CopcClipService.PrepareAoi for why a buffer is required even for polygon AOIs) during
 * conversion; it only affects the two convert output modes, since the "download COPC only" mode
 * never runs PDAL. A drawn/selected polygon AOI with holes, gaps, or islands can make PDAL's crop
 * step fail (see ShowAoiClipComplexityWarning) -- this is inherent to how PDAL's crop filter/reader
 * option handle multi-part polygons, not something this add-in can fully paper over.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
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
            IsPolygonAoi = AoiState.Current is Polygon;
            NotifyPropertyChanged(nameof(ShowAoiClipComplexityWarning));
            CommandManager.InvalidateRequerySuggested();
        }

        private bool _isPolygonAoi;
        /// <summary>Whether the current AOI is a polygon (drawn or from Select Feature) -- backs
        /// ShowAoiClipComplexityWarning, since only polygon AOIs can have holes/gaps/islands.</summary>
        public bool IsPolygonAoi { get => _isPolygonAoi; set => SetProperty(ref _isPolygonAoi, value); }

        private static async void ActivateTool(string toolId) => await FrameworkApplication.SetCurrentToolAsync(toolId);

        public ICommand DrawPointCommand => new RelayCommand(() => ActivateTool(DrawPointAoiTool.ToolId));
        public ICommand DrawLineCommand => new RelayCommand(() => ActivateTool(DrawLineAoiTool.ToolId));
        public ICommand DrawPolygonCommand => new RelayCommand(() => ActivateTool(DrawPolygonAoiTool.ToolId));
        public ICommand SelectFeatureCommand => new RelayCommand(() => ActivateTool(SelectFeatureAoiTool.ToolId));
        public ICommand ClearAoiCommand => new RelayCommand(ClearAoi, () => AoiState.Current != null);

        private static async void ClearAoi()
        {
            AoiState.Clear();
            var mapView = MapView.Active;
            if (mapView?.Map != null)
                await QueuedTask.Run(() => mapView.Map.SetSelection(null));
        }

        #endregion

        #region LiDAR phase selection

        private bool _phase1;
        public bool Phase1 { get => _phase1; set => SetProperty(ref _phase1, value); }

        private bool _phase2;
        public bool Phase2 { get => _phase2; set => SetProperty(ref _phase2, value); }

        private bool _phase3;
        public bool Phase3 { get => _phase3; set => SetProperty(ref _phase3, value); }

        #endregion

        #region Catalog search (preview only -- Run always does its own fresh search/download)

        private int _foundTileCount;
        public int FoundTileCount
        {
            get => _foundTileCount;
            set
            {
                SetProperty(ref _foundTileCount, value);
                NotifyPropertyChanged(nameof(DownloadCopcOnlyLabel));
            }
        }

        public string DownloadCopcOnlyLabel => FoundTileCount > 0
            ? $"Download {FoundTileCount} COPC file(s) only"
            : "Download COPC files only";

        private string _catalogSearchStatusText = string.Empty;
        public string CatalogSearchStatusText
        {
            get => _catalogSearchStatusText;
            set => SetProperty(ref _catalogSearchStatusText, value);
        }

        private bool _isSearching;
        public bool IsSearching
        {
            get => _isSearching;
            set => SetProperty(ref _isSearching, value);
        }

        public ICommand SearchCatalogCommand => new RelayCommand(async () => await SearchCatalogAsync(), CanSearchCatalog);

        private bool CanSearchCatalog() => !IsRunning && !IsSearching;

        private async Task SearchCatalogAsync()
        {
            if (AoiState.Current == null)
            {
                CatalogSearchStatusText = "Draw an AOI (point, line, or polygon) or select a feature first.";
                return;
            }

            var collections = new List<string>();
            if (Phase1) collections.Add("laz-phase1");
            if (Phase2) collections.Add("laz-phase2");
            if (Phase3) collections.Add("laz-phase3");
            if (collections.Count == 0)
            {
                CatalogSearchStatusText = "Select at least one LiDAR phase.";
                return;
            }

            IsSearching = true;
            CatalogSearchStatusText = "Searching STAC catalog...";
            try
            {
                var aoi = AoiState.Current;
                var bufferFeet = TryGetBufferFeet(out var bf) ? bf : 0;
                var clipToAoi = ClipToAoi;
                var aoiInfo = await QueuedTask.Run(() => CopcClipService.PrepareAoi(aoi, clipToAoi, bufferFeet));
                var count = await CopcClipService.SearchTileCountAsync(aoiInfo.GeoJson, collections);
                FoundTileCount = count;
                CatalogSearchStatusText = count == 0
                    ? "No LiDAR coverage found for this AOI in the selected phase(s)."
                    : $"Found {count} LiDAR tile(s) intersecting the AOI.";
            }
            catch (Exception ex)
            {
                CatalogSearchStatusText = "Search failed: " + ex.Message;
            }
            finally
            {
                IsSearching = false;
            }
        }

        #endregion

        #region Output settings

        public enum OutputMode { DownloadCopcOnly, ConvertKeepCopc, ConvertDiscardCopc }

        private OutputMode _outputMode = OutputMode.ConvertDiscardCopc;
        public OutputMode SelectedOutputMode
        {
            get => _outputMode;
            set
            {
                SetProperty(ref _outputMode, value);
                NotifyPropertyChanged(nameof(IsDownloadCopcOnlySelected));
                NotifyPropertyChanged(nameof(IsConvertKeepCopcSelected));
                NotifyPropertyChanged(nameof(IsConvertDiscardCopcSelected));
            }
        }

        // Checkbox-styled stand-ins for a radio group: checking one selects that OutputMode;
        // unchecking (the false branch) is a no-op other than re-raising PropertyChanged so the
        // checkbox snaps back to checked -- a checkbox has no "nothing selected" state to fall back
        // to here, unlike a real CheckBox used for an independent yes/no setting.
        public bool IsDownloadCopcOnlySelected
        {
            get => SelectedOutputMode == OutputMode.DownloadCopcOnly;
            set { if (value) SelectedOutputMode = OutputMode.DownloadCopcOnly; else NotifyPropertyChanged(nameof(IsDownloadCopcOnlySelected)); }
        }

        public bool IsConvertKeepCopcSelected
        {
            get => SelectedOutputMode == OutputMode.ConvertKeepCopc;
            set { if (value) SelectedOutputMode = OutputMode.ConvertKeepCopc; else NotifyPropertyChanged(nameof(IsConvertKeepCopcSelected)); }
        }

        public bool IsConvertDiscardCopcSelected
        {
            get => SelectedOutputMode == OutputMode.ConvertDiscardCopc;
            set { if (value) SelectedOutputMode = OutputMode.ConvertDiscardCopc; else NotifyPropertyChanged(nameof(IsConvertDiscardCopcSelected)); }
        }

        private bool _clipToAoi;
        /// <summary>Off by default -- clipping is the exception, not the default.</summary>
        public bool ClipToAoi
        {
            get => _clipToAoi;
            set
            {
                SetProperty(ref _clipToAoi, value);
                NotifyPropertyChanged(nameof(ShowBufferSettings));
                NotifyPropertyChanged(nameof(ShowAoiClipComplexityWarning));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool ShowBufferSettings => ClipToAoi;

        private string _bufferFeetText = string.Empty;
        public string BufferFeetText
        {
            get => _bufferFeetText;
            set => SetProperty(ref _bufferFeetText, value);
        }

        private bool TryGetBufferFeet(out double feet) =>
            double.TryParse(BufferFeetText, out feet) && feet > 0;

        /// <summary>Warns that a polygon AOI (drawn or from Select Feature) with holes, gaps, or
        /// islands can make PDAL's crop step error -- only relevant once clipping is turned on.</summary>
        public bool ShowAoiClipComplexityWarning => ClipToAoi && IsPolygonAoi;

        private string _outputFolder = DefaultOutputFolder();
        public string OutputFolder
        {
            get => _outputFolder;
            set => SetProperty(ref _outputFolder, value);
        }

        private static string DefaultOutputFolder() =>
            Path.Combine(Path.GetTempPath(), $"kylidar_clip_{DateTime.Now:yyyyMMdd_HHmmss}");

        public ICommand BrowseOutputFolderCommand => new RelayCommand(BrowseOutputFolder);

        private void BrowseOutputFolder()
        {
            var dlg = new OpenFolderDialog { FolderName = OutputFolder };
            if (dlg.ShowDialog() == true) OutputFolder = dlg.FolderName;
        }

        #endregion

        #region LAS dataset settings

        public enum DatasetAction { None, CreateNew, AddExisting }

        private DatasetAction _selectedDatasetAction = DatasetAction.None;
        public DatasetAction SelectedDatasetAction
        {
            get => _selectedDatasetAction;
            set
            {
                SetProperty(ref _selectedDatasetAction, value);
                NotifyPropertyChanged(nameof(ShowNewDatasetPath));
                NotifyPropertyChanged(nameof(ShowExistingDatasetPath));
                NotifyPropertyChanged(nameof(CanBuildPyramids));
                NotifyPropertyChanged(nameof(IsDatasetActionNoneSelected));
                NotifyPropertyChanged(nameof(IsDatasetActionCreateNewSelected));
                NotifyPropertyChanged(nameof(IsDatasetActionAddExistingSelected));
            }
        }

        public bool ShowNewDatasetPath => SelectedDatasetAction == DatasetAction.CreateNew;
        public bool ShowExistingDatasetPath => SelectedDatasetAction == DatasetAction.AddExisting;
        public bool CanBuildPyramids => SelectedDatasetAction != DatasetAction.None;

        // Checkbox-styled stand-ins for a radio group -- see IsDownloadCopcOnlySelected etc. above
        // for why unchecking is a no-op rather than clearing the selection.
        public bool IsDatasetActionNoneSelected
        {
            get => SelectedDatasetAction == DatasetAction.None;
            set { if (value) SelectedDatasetAction = DatasetAction.None; else NotifyPropertyChanged(nameof(IsDatasetActionNoneSelected)); }
        }

        public bool IsDatasetActionCreateNewSelected
        {
            get => SelectedDatasetAction == DatasetAction.CreateNew;
            set { if (value) SelectedDatasetAction = DatasetAction.CreateNew; else NotifyPropertyChanged(nameof(IsDatasetActionCreateNewSelected)); }
        }

        public bool IsDatasetActionAddExistingSelected
        {
            get => SelectedDatasetAction == DatasetAction.AddExisting;
            set { if (value) SelectedDatasetAction = DatasetAction.AddExisting; else NotifyPropertyChanged(nameof(IsDatasetActionAddExistingSelected)); }
        }

        private string _newDatasetPath = DefaultNewDatasetPath();
        public string NewDatasetPath
        {
            get => _newDatasetPath;
            set => SetProperty(ref _newDatasetPath, value);
        }

        private string _existingDatasetPath = string.Empty;
        public string ExistingDatasetPath
        {
            get => _existingDatasetPath;
            set => SetProperty(ref _existingDatasetPath, value);
        }

        private bool _buildPyramids;
        public bool BuildPyramids { get => _buildPyramids; set => SetProperty(ref _buildPyramids, value); }

        private static string DefaultNewDatasetPath() =>
            Path.Combine(Path.GetTempPath(), $"kylidar_clip_{DateTime.Now:yyyyMMdd_HHmmss}.lasd");

        public ICommand BrowseNewDatasetCommand => new RelayCommand(BrowseNewDataset);
        public ICommand BrowseExistingDatasetCommand => new RelayCommand(BrowseExistingDataset);

        private void BrowseNewDataset()
        {
            var dlg = new SaveFileDialog { Filter = "LAS Dataset (*.lasd)|*.lasd", FileName = Path.GetFileName(NewDatasetPath) };
            if (dlg.ShowDialog() == true) NewDatasetPath = dlg.FileName;
        }

        private void BrowseExistingDataset()
        {
            var dlg = new OpenFileDialog { Filter = "LAS Dataset (*.lasd)|*.lasd" };
            if (dlg.ShowDialog() == true) ExistingDatasetPath = dlg.FileName;
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

        private string _exportScriptStatusText = string.Empty;
        public string ExportScriptStatusText
        {
            get => _exportScriptStatusText;
            set => SetProperty(ref _exportScriptStatusText, value);
        }

        public ICommand ExportScriptCommand => new RelayCommand(async () => await ExportScriptAsync(), CanExportScript);

        private bool CanExportScript() => !IsRunning && !IsSearching;

        /// <summary>
        /// Write a stand-alone download/convert script for the AOI's current matching tiles, using
        /// the same output-mode settings as Run -- for very large batches, running on another
        /// machine, or scheduling for later. See ExportScriptService's header comment for what it
        /// does and doesn't reproduce (no LAS dataset step -- that's Pro/arcpy-only).
        /// </summary>
        private async Task ExportScriptAsync()
        {
            ExportScriptStatusText = string.Empty;

            if (AoiState.Current == null)
            {
                ExportScriptStatusText = "Draw an AOI (point, line, or polygon) or select a feature first.";
                return;
            }

            var collections = new List<string>();
            if (Phase1) collections.Add("laz-phase1");
            if (Phase2) collections.Add("laz-phase2");
            if (Phase3) collections.Add("laz-phase3");
            if (collections.Count == 0)
            {
                ExportScriptStatusText = "Select at least one LiDAR phase.";
                return;
            }

            bool convert = SelectedOutputMode != OutputMode.DownloadCopcOnly;
            bool discardRaw = SelectedOutputMode == OutputMode.ConvertDiscardCopc;

            var dlg = new ExportScriptDialog(OutputFolder) { Owner = System.Windows.Application.Current?.MainWindow };
            if (dlg.ShowDialog() != true) { ExportScriptStatusText = "Export cancelled."; return; }

            try
            {
                Directory.CreateDirectory(dlg.DestinationFolder);
            }
            catch (Exception ex)
            {
                ExportScriptStatusText = "Bad destination folder: " + ex.Message;
                return;
            }

            ExportScriptStatusText = "Searching STAC catalog...";
            try
            {
                var aoi = AoiState.Current;
                var bufferFeet = TryGetBufferFeet(out var bf) ? bf : 0;
                var clipToAoi = ClipToAoi;
                var aoiInfo = await QueuedTask.Run(() => CopcClipService.PrepareAoi(aoi, clipToAoi, bufferFeet));
                var tileUrls = await CopcClipService.SearchTileUrlsAsync(aoiInfo.GeoJson, collections);
                if (tileUrls.Count == 0)
                {
                    ExportScriptStatusText = "No LiDAR coverage found for this AOI in the selected phase(s).";
                    return;
                }

                var script = ExportScriptService.BuildScript(
                    dlg.SelectedFormat, tileUrls, dlg.DestinationFolder, convert, discardRaw, aoiInfo.CropWktParts);

                var ext = dlg.SelectedFormat switch
                {
                    ExportScriptFormat.Notebook => ".ipynb",
                    ExportScriptFormat.PowerShell => ".ps1",
                    ExportScriptFormat.Shell => ".sh",
                    _ => ".py"
                };
                var scriptPath = Path.Combine(dlg.DestinationFolder, $"kylidar_export_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
                await File.WriteAllTextAsync(scriptPath, script);

                ExportScriptStatusText = $"Exported {tileUrls.Count}-tile script to {scriptPath}.";
            }
            catch (Exception ex)
            {
                ExportScriptStatusText = "Could not export script: " + ex.Message;
            }
        }

        private bool CanRun() => !IsRunning && !IsSearching;

        private async Task RunAsync()
        {
            ValidationText = string.Empty;

            if (AoiState.Current == null)
            {
                ValidationText = "Draw an AOI (point, line, or polygon) or select a feature first.";
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

            if (string.IsNullOrWhiteSpace(OutputFolder))
            {
                ValidationText = "Choose an output folder.";
                return;
            }
            if (SelectedDatasetAction == DatasetAction.CreateNew && string.IsNullOrWhiteSpace(NewDatasetPath))
            {
                ValidationText = "Choose a path for the new LAS dataset.";
                return;
            }
            if (SelectedDatasetAction == DatasetAction.AddExisting && string.IsNullOrWhiteSpace(ExistingDatasetPath))
            {
                ValidationText = "Choose the existing LAS dataset to add to.";
                return;
            }

            LogLines.Clear();
            IsRunning = true;
            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(msg => LogLines.Add(msg));
            var runStopwatch = Stopwatch.StartNew();

            ClipResult result = null;
            try
            {
                var aoi = AoiState.Current;
                var bufferFeet = TryGetBufferFeet(out var bf) ? bf : 0;
                var clipToAoi = ClipToAoi;
                var aoiInfo = await QueuedTask.Run(() => CopcClipService.PrepareAoi(aoi, clipToAoi, bufferFeet));
                if (SelectedOutputMode == OutputMode.DownloadCopcOnly)
                {
                    result = await CopcClipService.DownloadTilesOnlyAsync(aoiInfo.GeoJson, collections, OutputFolder, progress, _cts.Token);
                }
                else
                {
                    bool keepRawCopc = SelectedOutputMode == OutputMode.ConvertKeepCopc;
                    result = await CopcClipService.ConvertToLasAsync(aoiInfo.GeoJson, collections, keepRawCopc, OutputFolder, aoiInfo.CropWktParts, progress, _cts.Token);
                }
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

            if (result == null)
            {
                LogLines.Add($"Total time: {FormatDuration(runStopwatch.Elapsed)}");
                return;
            }

            if (!result.Success)
            {
                LogLines.Add("Failed: " + result.Error);
                LogLines.Add($"Total time: {FormatDuration(runStopwatch.Elapsed)}");
                return;
            }

            var lasFolderMsg = SelectedOutputMode == OutputMode.ConvertKeepCopc
                ? Path.Combine(OutputFolder, "LAS")
                : OutputFolder;
            LogLines.Add($"Done: {result.OutputPaths.Count} file(s) written to {lasFolderMsg} ({FormatDuration(runStopwatch.Elapsed)} so far).");

            await AddToLasDatasetAsync(result.OutputPaths, progress);

            LogLines.Add($"Total time: {FormatDuration(runStopwatch.Elapsed)}");
        }

        private static string FormatDuration(TimeSpan elapsed) =>
            elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{elapsed.TotalSeconds:F1}s";

        /// <summary>
        /// Post-clip dataset step, now chosen up front (SelectedDatasetAction/BuildPyramids)
        /// instead of prompted for after the clip finishes.
        /// </summary>
        private async Task AddToLasDatasetAsync(IReadOnlyList<string> lasPaths, IProgress<string> progress)
        {
            if (SelectedDatasetAction == DatasetAction.None) return;

            var datasetStopwatch = Stopwatch.StartNew();
            var lasList = string.Join(";", lasPaths);
            string datasetPath;
            bool ok;
            if (SelectedDatasetAction == DatasetAction.CreateNew)
            {
                datasetPath = NewDatasetPath;
                ok = await LasDatasetService.CreateLasDatasetAsync(lasList, datasetPath, progress);
            }
            else
            {
                datasetPath = ExistingDatasetPath;
                ok = await LasDatasetService.AddFilesToLasDatasetAsync(datasetPath, lasList, progress);
            }

            if (!ok)
            {
                LogLines.Add("Failed to " + (SelectedDatasetAction == DatasetAction.CreateNew ? "create" : "update") + " the LAS dataset.");
                return;
            }
            LogLines.Add($"LAS dataset {(SelectedDatasetAction == DatasetAction.CreateNew ? "created" : "updated")} ({FormatDuration(datasetStopwatch.Elapsed)}).");

            if (BuildPyramids)
            {
                LogLines.Add("Building LAS dataset pyramids...");
                var pyramidStopwatch = Stopwatch.StartNew();
                if (!await LasDatasetService.BuildPyramidsAsync(datasetPath, progress))
                    LogLines.Add("Failed to build LAS dataset pyramids.");
                else
                    LogLines.Add($"Pyramids built ({FormatDuration(pyramidStopwatch.Elapsed)}).");
            }

            await LasDatasetService.AddToMapAsync(datasetPath);
        }

        #endregion
    }

    /// <summary>Ribbon entry point that activates the Kylidar dock pane.</summary>
    internal class KylidarDockpaneShowButton : Button
    {
        protected override void OnClick() => KylidarDockpaneViewModel.Show();
    }
}
