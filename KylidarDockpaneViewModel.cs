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
 * "Add hydro-enforced breaklines" (only meaningful with a LAS dataset) downloads the Phase 2/3
 * breaklines inside the search polygon, clips them to it, and adds them to the dataset as a
 * Hard_Line surface constraint -- see BreaklineService. It's best-effort: if the download or write
 * fails, the run continues and the dataset is built without the constraint.
 *
 * "Clip to area of interest" crops each tile to the AOI (plus an optional buffer -- see
 * CopcClipService.PrepareAoi) during conversion; it only affects the two convert output modes,
 * since the "download COPC only" mode never runs PDAL. A drawn/selected polygon AOI with holes,
 * gaps, or islands can make PDAL's crop step fail (see ShowAoiClipComplexityWarning) -- this is
 * inherent to how PDAL's crop filter/reader option handle multi-part polygons, not something this
 * add-in can fully paper over.
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

        private string _searchLimitText = CopcClipService.DefaultSearchLimit.ToString();
        /// <summary>Backs the "Search limit" field that only becomes visible once a search hits it
        /// (see SearchHitLimit) -- string-typed like BufferFeetText so an in-progress/invalid edit
        /// doesn't throw a WPF binding error.</summary>
        public string SearchLimitText
        {
            get => _searchLimitText;
            set => SetProperty(ref _searchLimitText, value);
        }

        private int GetSearchLimit() =>
            int.TryParse(SearchLimitText, out var v) && v > 0 ? v : CopcClipService.DefaultSearchLimit;

        private bool _searchHitLimit;
        /// <summary>True once a search comes back with exactly as many items as the current search
        /// limit -- there may be more coverage than what was found. Shows the "Search limit" field so
        /// the user can raise it and search again; Run/Export Script also pick up the raised limit.</summary>
        public bool SearchHitLimit
        {
            get => _searchHitLimit;
            set => SetProperty(ref _searchHitLimit, value);
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
                var searchLimit = GetSearchLimit();
                var aoiInfo = await QueuedTask.Run(() => CopcClipService.PrepareAoi(aoi, clipToAoi, bufferFeet));
                var (count, hitLimit) = await CopcClipService.SearchTileCountAsync(aoiInfo.GeoJson, collections, searchLimit);
                FoundTileCount = count;
                SearchHitLimit = hitLimit;
                CatalogSearchStatusText = count == 0
                    ? "No LiDAR coverage found for this AOI in the selected phase(s)."
                    : hitLimit
                        ? $"Found {count} LiDAR tile(s) -- hit the {searchLimit}-tile search limit, there may be more. Raise the limit below and search again."
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
                NotifyPropertyChanged(nameof(CanAddBreaklines));
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

        private bool _addBreaklines;
        /// <summary>Whether to add the hydro-enforced breaklines as a surface constraint. Only takes
        /// effect with a LAS dataset (see CanAddBreaklines) -- the constraint lives in the dataset.</summary>
        public bool AddBreaklines { get => _addBreaklines; set => SetProperty(ref _addBreaklines, value); }

        public bool CanAddBreaklines => SelectedDatasetAction != DatasetAction.None;

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
            set
            {
                SetProperty(ref _isRunning, value);
                NotifyPropertyChanged(nameof(ShowRunProgressBar));
                NotifyPropertyChanged(nameof(ShowIndeterminateProgressBar));
            }
        }

        public ObservableCollection<string> LogLines { get; } = new ObservableCollection<string>();

        private CancellationTokenSource _cts;

        // Aggregate run progress (see CopcClipService.RunProgress) -- a tqdm-style bar below the
        // line-by-line log, rather than making anyone read scrolling "Downloading X: N MB (P%)"
        // lines to gauge how far along the whole run is.
        private RunProgress _runProgress;
        private Stopwatch _runStopwatch;

        /// <summary>Shown once the tile count is known (after the STAC search); before that, and
        /// while idle, ShowIndeterminateProgressBar covers it instead.</summary>
        public bool ShowRunProgressBar => IsRunning && _runProgress.Total > 0;
        public bool ShowIndeterminateProgressBar => IsRunning && _runProgress.Total == 0;
        public double RunProgressFraction => _runProgress.OverallFraction;

        public string RunProgressText => _runProgress.Total == 0
            ? string.Empty
            : $"{_runProgress.Phase}: {_runProgress.Completed} / {_runProgress.Total} ({_runProgress.OverallFraction * 100:F0}%){FormatEta()}";

        private void SetRunProgress(RunProgress p)
        {
            _runProgress = p;
            NotifyPropertyChanged(nameof(ShowRunProgressBar));
            NotifyPropertyChanged(nameof(ShowIndeterminateProgressBar));
            NotifyPropertyChanged(nameof(RunProgressFraction));
            NotifyPropertyChanged(nameof(RunProgressText));
        }

        /// <summary>Simple linear ETA from elapsed time and how far OverallFraction has gotten --
        /// the same estimate tqdm itself uses. Blank until there's at least a couple seconds of
        /// data to extrapolate from, so early jitter (one fast/slow tile) doesn't flash a wild
        /// number.</summary>
        private string FormatEta()
        {
            var frac = _runProgress.OverallFraction;
            if (frac <= 0 || frac >= 1 || _runStopwatch == null) return string.Empty;
            var elapsed = _runStopwatch.Elapsed.TotalSeconds;
            if (elapsed < 2) return string.Empty;
            var remaining = TimeSpan.FromSeconds(elapsed * (1 - frac) / frac);
            return $" · ETA {FormatDuration(remaining)}";
        }

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
                var searchLimit = GetSearchLimit();
                var aoiInfo = await QueuedTask.Run(() => CopcClipService.PrepareAoi(aoi, clipToAoi, bufferFeet));
                var (tileUrls, hitLimit) = await CopcClipService.SearchTileUrlsAsync(aoiInfo.GeoJson, collections, searchLimit);
                if (tileUrls.Count == 0)
                {
                    ExportScriptStatusText = "No LiDAR coverage found for this AOI in the selected phase(s).";
                    return;
                }

                string outputPath;
                if (dlg.SelectedFormat == ExportScriptFormat.Executable)
                {
                    // The exe is a separate console project (tools\KylidarDownloader\), pre-built
                    // and bundled into this add-in's own package -- see the Content item in
                    // KylidarAddin.csproj -- so it lands right next to this assembly on disk.
                    var sourceExe = Path.Combine(
                        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
                        "KylidarDownloader.exe");
                    if (!File.Exists(sourceExe))
                    {
                        ExportScriptStatusText = "Could not find the bundled KylidarDownloader.exe alongside this add-in.";
                        return;
                    }
                    var destExe = Path.Combine(dlg.DestinationFolder, $"kylidar_export_{DateTime.Now:yyyyMMdd_HHmmss}.exe");
                    File.Copy(sourceExe, destExe, overwrite: true);

                    // Append the manifest straight onto the copied exe (see AppendEmbeddedManifest)
                    // instead of writing a sidecar .json -- Export Script's "Executable" option
                    // then produces exactly one file, matching what a plain "download exe" implies.
                    AppendEmbeddedManifest(destExe, BuildDownloaderManifest(tileUrls, dlg.DestinationFolder, convert, discardRaw, aoiInfo.CropWktParts));
                    outputPath = destExe;
                }
                else
                {
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
                    outputPath = scriptPath;
                }

                ExportScriptStatusText = hitLimit
                    ? $"Exported {tileUrls.Count}-tile script to {outputPath} -- hit the {searchLimit}-tile search limit, there may be more. Raise the limit and export again for the rest."
                    : $"Exported {tileUrls.Count}-tile script to {outputPath}.";
            }
            catch (Exception ex)
            {
                ExportScriptStatusText = "Could not export script: " + ex.Message;
            }
        }

        /// <summary>Manifest consumed by the bundled KylidarDownloader.exe (tools\KylidarDownloader\Program.cs).</summary>
        private static string BuildDownloaderManifest(
            IReadOnlyList<string> tileUrls, string destFolder, bool convert, bool discardRaw, IReadOnlyList<string> cropWktParts)
        {
            var manifest = new
            {
                destFolder,
                convert,
                discardRaw,
                cropWktParts = cropWktParts ?? Array.Empty<string>(),
                tiles = tileUrls
            };
            return System.Text.Json.JsonSerializer.Serialize(manifest);
        }

        // Must match KylidarDownloader's read side (tools\KylidarDownloader\Program.cs,
        // TryReadEmbeddedManifest) exactly: 8-byte ASCII magic at EOF, preceded by the 8-byte
        // little-endian byte-length of the JSON payload, which sits right before that.
        private static readonly byte[] ManifestFooterMagic = System.Text.Encoding.ASCII.GetBytes("KYLDMAN1");

        /// <summary>Append a manifest payload to a copy of KylidarDownloader.exe so it's a single, self-contained file (see ManifestFooterMagic).</summary>
        private static void AppendEmbeddedManifest(string exePath, string manifestJson)
        {
            var jsonBytes = System.Text.Encoding.UTF8.GetBytes(manifestJson);
            using var fs = new FileStream(exePath, FileMode.Append, FileAccess.Write);
            fs.Write(jsonBytes, 0, jsonBytes.Length);
            fs.Write(BitConverter.GetBytes((long)jsonBytes.Length), 0, 8);
            fs.Write(ManifestFooterMagic, 0, ManifestFooterMagic.Length);
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

            // Checked-but-disabled (dataset action None) is treated as off, not as an error.
            var wantBreaklines = AddBreaklines && CanAddBreaklines;
            if (wantBreaklines)
            {
                // Checked here, before any download starts, so a bad combination fails fast.
                if (!Phase2 && !Phase3)
                {
                    ValidationText = "Hydro-enforced breaklines exist for Phase 2 and Phase 3 only -- select one of them, or turn breaklines off.";
                    return;
                }
                if (AoiState.Current is not Polygon && !(ClipToAoi && TryGetBufferFeet(out _)))
                {
                    ValidationText = "Breaklines are clipped to the AOI, so they need a polygon AOI, or \"Clip to area of interest\" with a buffer.";
                    return;
                }
            }

            LogLines.Clear();
            IsRunning = true;
            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(msg => LogLines.Add(msg));
            var runProgress = new Progress<RunProgress>(SetRunProgress);
            SetRunProgress(default); // Total=0 -- shows the indeterminate bar until the search resolves a tile count
            _runStopwatch = Stopwatch.StartNew();

            ClipResult result = null;
            string surfaceConstraint = null;
            try
            {
                var aoi = AoiState.Current;
                var bufferFeet = TryGetBufferFeet(out var bf) ? bf : 0;
                var clipToAoi = ClipToAoi;
                BreaklineRegion breaklineRegion = null;
                var aoiInfo = await QueuedTask.Run(() =>
                {
                    var info = CopcClipService.PrepareAoi(aoi, clipToAoi, bufferFeet);
                    if (wantBreaklines) breaklineRegion = BreaklineService.PrepareRegion(info.SearchGeometry);
                    return info;
                });
                var searchLimit = GetSearchLimit();
                if (SelectedOutputMode == OutputMode.DownloadCopcOnly)
                {
                    result = await CopcClipService.DownloadTilesOnlyAsync(aoiInfo.GeoJson, collections, OutputFolder, progress, runProgress, searchLimit, _cts.Token);
                }
                else
                {
                    bool keepRawCopc = SelectedOutputMode == OutputMode.ConvertKeepCopc;
                    result = await CopcClipService.ConvertToLasAsync(aoiInfo.GeoJson, collections, keepRawCopc, OutputFolder, aoiInfo.CropWktParts, progress, runProgress, searchLimit, _cts.Token);
                }

                // After the point data so a breakline problem can't cost the tiles; still inside
                // the try so Cancel works and IsRunning stays true until it's done.
                if (result.Success && breaklineRegion != null)
                {
                    var phases = new List<int>();
                    if (Phase1) phases.Add(1);
                    if (Phase2) phases.Add(2);
                    if (Phase3) phases.Add(3);
                    surfaceConstraint = await BuildBreaklineConstraintAsync(breaklineRegion, phases, OutputFolder, progress, _cts.Token);
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
                LogLines.Add($"Total time: {FormatDuration(_runStopwatch.Elapsed)}");
                return;
            }

            if (!result.Success)
            {
                LogLines.Add("Failed: " + result.Error);
                LogLines.Add($"Total time: {FormatDuration(_runStopwatch.Elapsed)}");
                return;
            }

            var lasFolderMsg = SelectedOutputMode == OutputMode.ConvertKeepCopc
                ? Path.Combine(OutputFolder, "LAS")
                : OutputFolder;
            LogLines.Add($"Done: {result.OutputPaths.Count} file(s) written to {lasFolderMsg} ({FormatDuration(_runStopwatch.Elapsed)} so far).");

            await AddToLasDatasetAsync(result.OutputPaths, progress, surfaceConstraint);

            LogLines.Add($"Total time: {FormatDuration(_runStopwatch.Elapsed)}");
        }

        /// <summary>
        /// Download + clip + write the breaklines, returning the surface-constraint argument for the
        /// LAS dataset tools, or null (after logging why) when there's nothing to add. Best-effort:
        /// anything but cancellation is logged and swallowed so the point data still gets its dataset.
        /// </summary>
        private async Task<string> BuildBreaklineConstraintAsync(
            BreaklineRegion region, IReadOnlyCollection<int> phases, string outputFolder, IProgress<string> progress, CancellationToken ct)
        {
            try
            {
                progress.Report("Downloading hydro-enforced breaklines...");
                var fetched = await BreaklineService.FetchAsync(region, phases, progress, ct);
                foreach (var note in fetched.Notes) progress.Report(note);
                if (fetched.Lines.Count == 0)
                {
                    progress.Report("No usable breaklines in the AOI -- the LAS dataset will be built without a surface constraint.");
                    return null;
                }

                var written = await QueuedTask.Run(() => BreaklineService.WriteFeatureClass(region, fetched.Lines, outputFolder));
                if (written.FeatureCount == 0)
                {
                    progress.Report("Breaklines were found but none fall inside the clip area -- no surface constraint added.");
                    return null;
                }
                progress.Report($"Wrote {written.FeatureCount} clipped breakline(s) to {written.FeatureClassPath}.");
                return BreaklineService.ToConstraintArgument(written.FeatureClassPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                progress.Report("Breaklines skipped -- " + ex.Message);
                return null;
            }
        }

        private static string FormatDuration(TimeSpan elapsed) =>
            elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{elapsed.TotalSeconds:F1}s";

        /// <summary>
        /// Post-clip dataset step, now chosen up front (SelectedDatasetAction/BuildPyramids)
        /// instead of prompted for after the clip finishes.
        /// </summary>
        private async Task AddToLasDatasetAsync(IReadOnlyList<string> lasPaths, IProgress<string> progress, string surfaceConstraint)
        {
            if (SelectedDatasetAction == DatasetAction.None) return;

            var datasetStopwatch = Stopwatch.StartNew();
            var lasList = string.Join(";", lasPaths);
            string datasetPath;
            bool ok;
            if (SelectedDatasetAction == DatasetAction.CreateNew)
            {
                datasetPath = NewDatasetPath;
                ok = await LasDatasetService.CreateLasDatasetAsync(lasList, datasetPath, progress, surfaceConstraint);
            }
            else
            {
                datasetPath = ExistingDatasetPath;
                ok = await LasDatasetService.AddFilesToLasDatasetAsync(datasetPath, lasList, progress, surfaceConstraint);
            }

            if (!ok)
            {
                LogLines.Add("Failed to " + (SelectedDatasetAction == DatasetAction.CreateNew ? "create" : "update") + " the LAS dataset.");
                return;
            }
            LogLines.Add($"LAS dataset {(SelectedDatasetAction == DatasetAction.CreateNew ? "created" : "updated")}{(surfaceConstraint != null ? " with breakline surface constraint" : "")} ({FormatDuration(datasetStopwatch.Elapsed)}).");

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
