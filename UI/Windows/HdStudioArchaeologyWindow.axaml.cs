using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Mesen.Config;
using Mesen.HdStudio;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Windows
{
	/// <summary>
	/// Native RetroArchaeology workspace. It is presentation only: the deterministic
	/// HdStudioArchaeology pipeline does the work, and the existing Studio catalog,
	/// importer and verifier remain the authoring path.
	/// </summary>
	public partial class HdStudioArchaeologyWindow : Window
	{
		private readonly HdStudioWindow? _owner;
		private string _romPath = "";
		private string _packFolder = "";
		private string _catalogPath = "";
		private bool _running;
		private bool _paused;
		private CancellationTokenSource? _cancellation;
		private ManualResetEventSlim? _pauseGate;
		private DateTime _startedUtc;
		private DispatcherTimer? _timer;
		private readonly ObservableCollection<HdStudioArchaeologyAssetRow> _allAssets = new();
		private readonly ObservableCollection<HdStudioArchaeologyAssetRow> _filteredAssets = new();
		private readonly ObservableCollection<HdStudioArchaeologyCandidateRow> _candidates = new();
		private readonly ObservableCollection<HdStudioArchaeologyGapRow> _gaps = new();
		private readonly Dictionary<string, Bitmap> _bitmapCache = new(StringComparer.OrdinalIgnoreCase);

		private ListBox _assetList = null!, _candidateList = null!, _gapList = null!;
		private TextBox _searchBox = null!;
		private ComboBox _filterBox = null!, _modeBox = null!;
		private CheckBox _dynamicCheck = null!;
		private NumericUpDown _framesBox = null!;
		private Button _startButton = null!, _pauseButton = null!, _cancelButton = null!, _openInStudioButton = null!;
		private TextBlock _romText = null!, _packText = null!, _budgetText = null!, _phaseText = null!, _phaseDetailText = null!, _elapsedText = null!;
		private TextBlock _phaseStaticText = null!, _phaseStructuralText = null!, _phaseRuntimeText = null!, _phaseCatalogText = null!;
		private TextBlock _sumPatterns = null!, _sumObserved = null!, _sumAssets = null!, _sumCandidates = null!, _sumGaps = null!;
		private TextBlock _statusText = null!, _statusLeft = null!, _statusRight = null!, _catalogPathText = null!, _summaryText = null!;
		private TextBlock _detailName = null!, _detailMeta = null!, _detailStatus = null!, _detailConfidence = null!, _detailProvenance = null!, _detailGraph = null!, _detailTiles = null!, _detailNotes = null!;
		private Border _detailStatusBadge = null!;
		private Image _detailImage = null!;
		private ProgressBar _progress = null!;
		private HdStudioArchaeologyAssetRow? _selectedAsset;
		private HdStudioArchaeologyCatalogSummary? _summary;

		public HdStudioArchaeologyWindow() : this("", "", null) { }

		public HdStudioArchaeologyWindow(string romPath, string packFolder, HdStudioWindow? owner)
		{
			_owner = owner;
			_romPath = romPath ?? "";
			_packFolder = packFolder ?? "";
			AvaloniaXamlLoader.Load(this);
			BindControls();
			SetRom(_romPath, _packFolder);
			_modeBox.SelectedIndex = 1;
			UpdateBudgetText();
			_filterBox.SelectionChanged += (_, _) => ApplyFilter();
			_searchBox.TextChanged += (_, _) => ApplyFilter();
			_modeBox.SelectionChanged += (_, _) => UpdateBudgetText();
			Closing += (_, _) => CancelRunning();
			LoadCatalog();
		}

		private T C<T>(string name) where T : Control => this.FindControl<T>(name) ?? throw new InvalidOperationException("control not found: " + name);

		private void BindControls()
		{
			_assetList = C<ListBox>("AssetList");
			_candidateList = C<ListBox>("CandidateList");
			_gapList = C<ListBox>("GapList");
			_searchBox = C<TextBox>("ArchSearchBox");
			_filterBox = C<ComboBox>("ArchFilterBox");
			_modeBox = C<ComboBox>("ArchModeBox");
			_dynamicCheck = C<CheckBox>("ArchDynamicCheck");
			_framesBox = C<NumericUpDown>("ArchFramesBox");
			_startButton = C<Button>("ArchStartButton");
			_pauseButton = C<Button>("ArchPauseButton");
			_cancelButton = C<Button>("ArchCancelButton");
			_openInStudioButton = C<Button>("OpenInStudioButton");
			_romText = C<TextBlock>("ArchaeologyRomText");
			_packText = C<TextBlock>("ArchaeologyPackText");
			_budgetText = C<TextBlock>("ArchBudgetText");
			_phaseText = C<TextBlock>("ArchPhaseText");
			_phaseDetailText = C<TextBlock>("ArchPhaseDetailText");
			_elapsedText = C<TextBlock>("ArchElapsedText");
			_phaseStaticText = C<TextBlock>("PhaseStaticText");
			_phaseStructuralText = C<TextBlock>("PhaseStructuralText");
			_phaseRuntimeText = C<TextBlock>("PhaseRuntimeText");
			_phaseCatalogText = C<TextBlock>("PhaseCatalogText");
			_sumPatterns = C<TextBlock>("SumPatterns");
			_sumObserved = C<TextBlock>("SumObserved");
			_sumAssets = C<TextBlock>("SumAssets");
			_sumCandidates = C<TextBlock>("SumCandidates");
			_sumGaps = C<TextBlock>("SumGaps");
			_statusText = C<TextBlock>("ArchStatusText");
			_statusLeft = C<TextBlock>("ArchStatusLeft");
			_statusRight = C<TextBlock>("ArchStatusRight");
			_catalogPathText = C<TextBlock>("ArchCatalogPathText");
			_summaryText = C<TextBlock>("ArchSummaryText");
			_detailName = C<TextBlock>("DetailName");
			_detailMeta = C<TextBlock>("DetailMeta");
			_detailStatus = C<TextBlock>("DetailStatus");
			_detailConfidence = C<TextBlock>("DetailConfidence");
			_detailProvenance = C<TextBlock>("DetailProvenance");
			_detailGraph = C<TextBlock>("DetailGraph");
			_detailTiles = C<TextBlock>("DetailTiles");
			_detailNotes = C<TextBlock>("DetailNotes");
			_detailStatusBadge = C<Border>("DetailStatusBadge");
			_detailImage = C<Image>("DetailImage");
			_progress = C<ProgressBar>("ArchProgress");
		}

		private void SetRom(string romPath, string packFolder)
		{
			_romPath = romPath ?? "";
			_packFolder = packFolder ?? "";
			if(_romPath.Length > 0 && _packFolder.Length == 0) {
				_packFolder = Path.Combine(ConfigManager.HdPackFolder, Path.GetFileNameWithoutExtension(_romPath));
			}
			_romText.Text = _romPath.Length > 0 ? Path.GetFileName(_romPath) + "  -  " + _romPath : "No NES ROM loaded";
			_packText.Text = _packFolder.Length > 0 ? _packFolder : "(derived from ROM)";
			Title = _romPath.Length > 0 ? "RetroArchaeology - " + Path.GetFileNameWithoutExtension(_romPath) : "RetroArchaeology - RetroArcDo HD Studio";
			_startButton.IsEnabled = _romPath.Length > 0 && File.Exists(_romPath);
			_catalogPathText.Text = "";
			_summary = null;
		}

		private void UpdateBudgetText()
		{
			HdStudioArchaeologyOptions options = HdStudioArchaeologyOptions.ForMode(SelectedMode());
			_budgetText.Text = options.Mode + ": " + options.MaxAssets + " assets / " + options.MaxCandidateStreams +
				" streams / " + options.MaxSecondsPerRom + " s per ROM";
		}

		private string SelectedMode() => _modeBox.SelectedIndex switch { 0 => "quick", 2 => "exhaustive", _ => "deep" };

		private void LoadCatalog()
		{
			_allAssets.Clear();
			_candidates.Clear();
			_gaps.Clear();
			_summary = null;
			if(_packFolder.Length == 0) {
				SetEmpty("Open an NES ROM to begin.");
				return;
			}
			_catalogPath = Path.Combine(_packFolder, HdStudioReport.StudioFolderName, "archaeology", "catalog.json");
			if(!File.Exists(_catalogPath)) {
				SetEmpty("No archaeology catalog yet. Press Run Archaeology to build one from the ROM.");
				return;
			}
			try {
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_catalogPath));
				JsonElement root = doc.RootElement;
				_summary = HdStudioArchaeologyCatalogSummary.FromJson(root);
				_summary.CatalogPath = _catalogPath;
				_summary.GraphPath = Path.Combine(_packFolder, HdStudioReport.StudioFolderName, "archaeology", "evidence-graph.json");
				if(root.TryGetProperty("rom", out JsonElement rom) && rom.ValueKind == JsonValueKind.Object) {
					string fileName = GetString(rom, "fileName");
					if(fileName.Length > 0 && _romPath.Length == 0) {
						_romText.Text = fileName;
					}
				}
				if(root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array) {
					foreach(JsonElement item in assets.EnumerateArray()) {
						HdStudioArchaeologyAssetRow? row = HdStudioArchaeologyAssetRow.FromJson(item, _packFolder, ThumbnailFor);
						if(row != null) {
							_allAssets.Add(row);
						}
					}
				}
				if(root.TryGetProperty("candidates", out JsonElement candidates) && candidates.ValueKind == JsonValueKind.Array) {
					foreach(JsonElement item in candidates.EnumerateArray()) {
						HdStudioArchaeologyCandidateRow? row = HdStudioArchaeologyCandidateRow.FromJson(item, _packFolder, ThumbnailFor);
						if(row != null) {
							_candidates.Add(row);
						}
					}
				}
				if(root.TryGetProperty("gaps", out JsonElement gaps) && gaps.ValueKind == JsonValueKind.Array) {
					foreach(JsonElement item in gaps.EnumerateArray()) {
						HdStudioArchaeologyGapRow? row = HdStudioArchaeologyGapRow.FromJson(item);
						if(row != null) {
							_gaps.Add(row);
						}
					}
				}
				ApplyFilter();
				UpdateSummary();
				_statusText.Text = _summary.AssetsExposed + " assets (" + _summary.AssetsObserved + " observed, " + _summary.AssetsStatic +
					" static), " + _summary.CandidateStreams + " PRG candidates, " + _summary.OpenGaps + " open gaps.";
				_catalogPathText.Text = _catalogPath;
				_statusLeft.Text = "Catalog loaded. Static patterns are extraction; observed patterns have runtime witnesses; replacement verification is separate.";
				_phaseText.Text = "Catalog loaded";
				_phaseDetailText.Text = "Press Run Archaeology to refresh the evidence graph. Existing approved artwork is preserved.";
				_phaseStaticText.Text = "Static: " + _summary.UniquePatterns + " unique patterns, " + _summary.BlankPatternOccurrences + " blank occurrences";
				_phaseStructuralText.Text = "Structural: " + _summary.CodeBlocks + " code blocks, " + _summary.PointerTables + " pointer tables";
				_phaseRuntimeText.Text = _summary.ObservedChrRomPatterns + " observed CHR-ROM, " + _summary.ObservedChrRamPatterns + " observed CHR-RAM";
				_phaseCatalogText.Text = "Catalog: " + _summary.AssetsExposed + " assets";
				_progress.Value = 100;
			} catch(Exception ex) {
				SetEmpty("The archaeology catalog could not be read: " + ex.Message);
			}
		}

		private void SetEmpty(string message)
		{
			_allAssets.Clear();
			_candidates.Clear();
			_gaps.Clear();
			_assetList.ItemsSource = _allAssets;
			_candidateList.ItemsSource = _candidates;
			_gapList.ItemsSource = _gaps;
			_statusText.Text = message;
			_statusLeft.Text = message;
			_catalogPathText.Text = _catalogPath.Length > 0 ? _catalogPath : "";
			_summaryText.Text = message;
			_progress.Value = 0;
			UpdateCounts(0, 0, 0, 0, 0);
		}

		private void UpdateSummary()
		{
			if(_summary == null) {
				return;
			}
			UpdateCounts(_summary.UniquePatterns, _summary.ObservedChrRomPatterns + _summary.ObservedChrRamPatterns,
				_summary.AssetsExposed, _summary.CandidateStreams, _summary.OpenGaps);
			_summaryText.Text =
				"ROM: " + _summary.RomFileName + "\n" +
				"Mapper: " + _summary.Mapper + " / " + _summary.ChrMode + "\n" +
				"Physical CHR banks: " + _summary.ChrBanks + ", pages: " + _summary.ChrPages + "\n" +
				"Unique nonblank CHR patterns: " + _summary.UniquePatterns + "\n" +
				"Blank pattern occurrences: " + _summary.BlankPatternOccurrences + "\n" +
				"Observed CHR-ROM patterns: " + _summary.ObservedChrRomPatterns + "\n" +
				"Observed CHR-RAM patterns: " + _summary.ObservedChrRamPatterns + "\n" +
				"Code blocks: " + _summary.CodeBlocks + "\n" +
				"PPU writes: " + _summary.PpuWrites + "\n" +
				"Referenced data ranges: " + _summary.DataReferences + "\n" +
				"Pointer-table candidates: " + _summary.PointerTables + "\n" +
				"PRG pattern-stream candidates: " + _summary.CandidateStreams + "\n" +
				"Palette-table candidates: " + _summary.PaletteCandidates + "\n" +
				"Catalog assets: " + _summary.AssetsExposed + " (" + _summary.AssetsObserved + " observed, " + _summary.AssetsStatic + " static)\n" +
				"Open evidence gaps: " + _summary.OpenGaps + "\n" +
				"Graph: " + _summary.GraphPath + "\n" +
				"Catalog: " + _summary.CatalogPath;
		}

		private void UpdateCounts(int patterns, int observed, int assets, int candidates, int gaps)
		{
			_sumPatterns.Text = patterns.ToString("N0", CultureInfo.InvariantCulture);
			_sumObserved.Text = observed.ToString("N0", CultureInfo.InvariantCulture);
			_sumAssets.Text = assets.ToString("N0", CultureInfo.InvariantCulture);
			_sumCandidates.Text = candidates.ToString("N0", CultureInfo.InvariantCulture);
			_sumGaps.Text = gaps.ToString("N0", CultureInfo.InvariantCulture);
		}

		private Bitmap? ThumbnailFor(HdStudioArchaeologyThumbSource source)
		{
			if(source.OriginalImage.Length == 0) {
				return null;
			}
			string key = source.OriginalImage + "|" + source.RomFolder;
			if(_bitmapCache.TryGetValue(key, out Bitmap? cached)) {
				return cached;
			}
			try {
				string folder = Path.Combine(source.PackFolder, HdStudioReport.StudioFolderName);
				string path = Path.Combine(folder, source.OriginalImage.Replace('/', Path.DirectorySeparatorChar));
				if(!File.Exists(path)) {
					string direct = Path.Combine(source.PackFolder, source.OriginalImage.Replace('/', Path.DirectorySeparatorChar));
					if(File.Exists(direct)) {
						path = direct;
					} else {
						path = Directory.EnumerateFiles(folder, Path.GetFileName(source.OriginalImage), SearchOption.AllDirectories).FirstOrDefault() ?? "";
					}
				}
				if(path.Length == 0 || !File.Exists(path)) {
					return null;
				}
				Bitmap bitmap = new(path);
				_bitmapCache[key] = bitmap;
				return bitmap;
			} catch {
				return null;
			}
		}

		private void ApplyFilter()
		{
			_filteredAssets.Clear();
			string search = _searchBox.Text?.Trim() ?? "";
			string filter = _filterBox.SelectedIndex switch { 1 => "observed", 2 => "static", 3 => "chrram", _ => "all" };
			foreach(HdStudioArchaeologyAssetRow row in _allAssets) {
				if(filter == "observed" && !row.Observed) continue;
				if(filter == "static" && (row.Observed || row.Kind != "staticPattern")) continue;
				if(filter == "chrram" && !string.Equals(row.ChrType, "NesChrRam", StringComparison.OrdinalIgnoreCase)) continue;
				if(search.Length > 0 &&
					row.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
					row.Provenance.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
					row.Id.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) {
					continue;
				}
				_filteredAssets.Add(row);
			}
			_assetList.ItemsSource = _filteredAssets;
			_candidateList.ItemsSource = _candidates;
			_gapList.ItemsSource = _gaps;
			_statusText.Text = _filteredAssets.Count + " of " + _allAssets.Count + " assets shown.";
		}

		private async void OpenRom_Click(object? sender, RoutedEventArgs e)
		{
			IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions() {
				Title = "Open an NES ROM for RetroArchaeology",
				AllowMultiple = false,
				FileTypeFilter = new[] { new FilePickerFileType("NES ROMs") { Patterns = new List<string>() { "*.nes" } } }
			});
			if(files.Count == 0) {
				return;
			}
			string path = files[0].Path.LocalPath;
			string pack = Path.Combine(ConfigManager.HdPackFolder, Path.GetFileNameWithoutExtension(path));
			SetRom(path, pack);
			LoadCatalog();
		}

		private async void Start_Click(object? sender, RoutedEventArgs e)
		{
			if(_running) {
				return;
			}
			if(_romPath.Length == 0 || !File.Exists(_romPath)) {
				SetEmpty("Open a valid NES ROM first.");
				return;
			}
			HdStudioArchaeologyOptions options = HdStudioArchaeologyOptions.ForMode(SelectedMode());
			options.RunDynamicConfirmation = _dynamicCheck.IsChecked == true;
			options.DynamicFrames = _framesBox.Value is { } value ? (int)value : 1800;
			options.PauseGate = _pauseGate = new ManualResetEventSlim(true);
			_cancellation = new CancellationTokenSource();
			_running = true;
			_paused = false;
			_startedUtc = DateTime.UtcNow;
			_startButton.IsEnabled = false;
			_pauseButton.IsEnabled = true;
			_cancelButton.IsEnabled = true;
			C<TextBlock>("ArchPauseText").Text = "Pause";
			_progress.Value = 2;
			_phaseText.Text = "Starting";
			_phaseDetailText.Text = "Reading ROM identity and physical banks. No gameplay is required.";
			_statusLeft.Text = "RetroArchaeology running.";
			_timer?.Stop();
			_timer = new DispatcherTimer() { Interval = TimeSpan.FromMilliseconds(400) };
			_timer.Tick += (_, _) => {
				_elapsedText.Text = (DateTime.UtcNow - _startedUtc).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
			};
			_timer.Start();
			string rom = _romPath;
			string pack = _packFolder.Length > 0 ? _packFolder : Path.Combine(ConfigManager.HdPackFolder, Path.GetFileNameWithoutExtension(rom));
			SetRom(rom, pack);
			Directory.CreateDirectory(pack);
			Progress<HdStudioArchaeologyProgress> progress = new(value => {
				Dispatcher.UIThread.Post(() => ApplyProgress(value));
			});
			Action<string> log = message => {
				Dispatcher.UIThread.Post(() => {
					_statusRight.Text = message.Length > 120 ? message.Substring(0, 117) + "..." : message;
				});
			};
			try {
				HdStudioArchaeologyResult result = await Task.Run(() =>
					HdStudioArchaeology.Run(rom, pack, options, ConfigManager.Config.Nes.UserPalette, log, progress, _cancellation.Token));
				foreach(string error in result.Errors) {
					_statusLeft.Text = "Archaeology: " + error;
				}
				_progress.Value = result.Success ? 100 : 0;
				_phaseText.Text = result.Success ? "Complete" : "Stopped: " + result.StopReason;
				_phaseDetailText.Text = result.Success
					? "Evidence graph and catalog written. Select an asset to open it in the Artwork Workshop."
					: "The run ended without claiming completion. See the log and evidence gaps.";
				_statusLeft.Text = result.Success
					? "RetroArchaeology complete in " + result.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s. " +
					  result.UniquePatterns + " unique patterns, " + result.RuntimeObservedPatterns + " observed, " + result.AssetsExposed + " catalog assets."
					: "RetroArchaeology stopped: " + result.StopReason + ". " + string.Join("; ", result.Errors);
				LoadCatalog();
			} catch(Exception ex) {
				_statusLeft.Text = "RetroArchaeology failed: " + ex.Message;
				_phaseText.Text = "Failed";
			} finally {
				_timer?.Stop();
				_running = false;
				_pauseGate?.Set();
				_startButton.IsEnabled = true;
				_pauseButton.IsEnabled = false;
				_cancelButton.IsEnabled = false;
				_elapsedText.Text = (DateTime.UtcNow - _startedUtc).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
				_cancellation?.Dispose();
				_cancellation = null;
			}
		}

		private void ApplyProgress(HdStudioArchaeologyProgress value)
		{
			_progress.Value = Math.Clamp(value.Percent * 100.0, 0, 100);
			_phaseText.Text = value.Phase + (value.PhaseCount > 0 ? " (" + value.PhaseIndex + "/" + value.PhaseCount + ")" : "");
			_phaseDetailText.Text = value.Message;
			_elapsedText.Text = value.ElapsedSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
			UpdateCounts(value.UniquePatterns, value.ObservedPatterns, value.Assets, 0, value.Gaps);
			if(value.Phase == "Static" || value.Phase == "Palettes") {
				_phaseStaticText.Text = "Static: " + value.Message;
			} else if(value.Phase == "Structural") {
				_phaseStructuralText.Text = "Structural: " + value.Message;
			} else if(value.Phase == "Runtime" || value.Phase == "Dynamic") {
				_phaseRuntimeText.Text = value.Phase + ": " + value.Message;
			} else if(value.Phase == "Catalog") {
				_phaseCatalogText.Text = "Catalog: " + value.Message;
			}
			_statusLeft.Text = value.Message;
		}

		private void Pause_Click(object? sender, RoutedEventArgs e)
		{
			if(_pauseGate == null) {
				return;
			}
			if(_paused) {
				_pauseGate.Set();
				_paused = false;
				C<TextBlock>("ArchPauseText").Text = "Pause";
				_phaseDetailText.Text = "Resumed.";
			} else {
				_pauseGate.Reset();
				_paused = true;
				C<TextBlock>("ArchPauseText").Text = "Resume";
				_phaseDetailText.Text = "Paused. The current phase will hold at its next safe checkpoint.";
			}
		}

		private void Cancel_Click(object? sender, RoutedEventArgs e)
		{
			CancelRunning();
			_phaseDetailText.Text = "Cancel requested. Completed evidence remains on disk.";
		}

		private void CancelRunning()
		{
			try {
				_cancellation?.Cancel();
			} catch {
			}
			_pauseGate?.Set();
		}

		private void ReloadCatalog_Click(object? sender, RoutedEventArgs e) => LoadCatalog();

		private void OpenPack_Click(object? sender, RoutedEventArgs e)
		{
			if(_packFolder.Length == 0 || !Directory.Exists(_packFolder)) {
				_statusLeft.Text = "The pack folder does not exist yet.";
				return;
			}
			Process.Start(new ProcessStartInfo() { FileName = _packFolder, UseShellExecute = true });
		}

		private void AssetList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			if(_assetList.SelectedItem is HdStudioArchaeologyAssetRow row) {
				_selectedAsset = row;
				ShowAsset(row);
			}
		}

		private void CandidateList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			if(_candidateList.SelectedItem is HdStudioArchaeologyCandidateRow row) {
				_selectedAsset = null;
				_openInStudioButton.IsEnabled = false;
				_detailName.Text = row.Name;
				_detailStatus.Text = "candidate";
				_detailConfidence.Text = row.ConfidenceText;
				_detailMeta.Text = "Diagnostic candidate only; not a replaceable pattern.";
				_detailProvenance.Text = row.Provenance;
				_detailGraph.Text = row.GraphNode;
				_detailTiles.Text = row.Detail;
				_detailNotes.Text = "No replacement rule is created from a PRG stream candidate. It must first be associated with CHR/mapper identity or runtime evidence.";
				_detailImage.Source = row.Thumbnail;
				SetBadge("warning");
			}
		}

		private void GapList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			if(_gapList.SelectedItem is HdStudioArchaeologyGapRow row) {
				_selectedAsset = null;
				_openInStudioButton.IsEnabled = false;
				_detailName.Text = row.Kind;
				_detailStatus.Text = "open gap";
				_detailConfidence.Text = row.PriorityText;
				_detailMeta.Text = row.Description;
				_detailProvenance.Text = "Action: " + row.Action;
				_detailGraph.Text = row.Id;
				_detailTiles.Text = "";
				_detailNotes.Text = "A gap is an explicit unknown, not a failure count. It is retained until evidence resolves it.";
				_detailImage.Source = null;
				SetBadge("warning");
			}
		}

		private void ShowAsset(HdStudioArchaeologyAssetRow row)
		{
			_openInStudioButton.IsEnabled = row.Replaceable;
			_detailName.Text = row.Name;
			_detailStatus.Text = row.Status;
			_detailConfidence.Text = row.ConfidenceText;
			_detailMeta.Text = row.Kind + " - " + row.Pattern + " - " + row.ChrType;
			_detailProvenance.Text = row.Provenance;
			_detailGraph.Text = row.GraphNode;
			_detailTiles.Text = row.Detail;
			_detailNotes.Text = row.Notes;
			_detailImage.Source = row.Thumbnail;
			SetBadge(row.Observed ? "success" : "info");
		}

		private void SetBadge(string kind)
		{
			_detailStatusBadge.Classes.Set("success", kind == "success");
			_detailStatusBadge.Classes.Set("info", kind == "info");
			_detailStatusBadge.Classes.Set("warning", kind == "warning");
			_detailStatusBadge.Classes.Set("error", kind == "error");
		}

		private void OpenInStudio_Click(object? sender, RoutedEventArgs e)
		{
			if(_selectedAsset == null || _romPath.Length == 0) {
				return;
			}
			if(_owner != null) {
				_owner.OpenArchaeologyAsset(_romPath, _packFolder, _selectedAsset.Id);
				Close();
				return;
			}
			HdStudioWindow studio = new(_romPath, _packFolder);
			studio.Show();
			Close();
		}

		private static string GetString(JsonElement element, string name, string fallback = "")
		{
			return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
		}

		private static int GetInt(JsonElement element, string name, int fallback = 0)
		{
			return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : fallback;
		}

		private static bool GetBool(JsonElement element, string name)
		{
			return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
		}
	}

	internal sealed class HdStudioArchaeologyThumbSource
	{
		public string PackFolder = "";
		public string OriginalImage = "";
		public string RomFolder = "";
	}

	internal sealed class HdStudioArchaeologyAssetRow
	{
		public string Id = "";
		public string Name = "";
		public string Kind = "";
		public string Status = "";
		public string Provenance = "";
		public string GraphNode = "";
		public string Detail = "";
		public string Notes = "";
		public string OriginalImage = "";
		public string Pattern = "";
		public string ChrType = "";
		public string ConfidenceText = "";
		public bool Observed;
		public bool Replaceable;
		public double Confidence;
		private Func<HdStudioArchaeologyThumbSource, Bitmap?>? _loader;
		private bool _thumbLoaded;
		private Bitmap? _thumb;

		public Bitmap? Thumbnail {
			get {
				if(!_thumbLoaded) {
					_thumbLoaded = true;
					_thumb = _loader?.Invoke(new HdStudioArchaeologyThumbSource() { PackFolder = PackFolder, OriginalImage = OriginalImage, RomFolder = PackFolder });
				}
				return _thumb;
			}
		}
		internal string PackFolder = "";

		public static HdStudioArchaeologyAssetRow? FromJson(JsonElement item, string packFolder, Func<HdStudioArchaeologyThumbSource, Bitmap?> loader)
		{
			string id = GetString(item, "id");
			if(id.Length == 0) {
				return null;
			}
			HdStudioArchaeologyAssetRow row = new() {
				Id = id,
				Name = GetString(item, "name"),
				Kind = GetString(item, "kind"),
				Status = item.TryGetProperty("validation", out JsonElement validation) ? GetString(validation, "status") : "",
				Provenance = GetString(item, "provenance"),
				GraphNode = GetString(item, "graphNode"),
				OriginalImage = GetString(item, "originalImage"),
				Observed = GetBool(item, "observed"),
				Replaceable = !item.TryGetProperty("replaceable", out JsonElement replaceable) || replaceable.ValueKind != JsonValueKind.False,
				Confidence = item.TryGetProperty("confidence", out JsonElement confidence) && confidence.TryGetDouble(out double c) ? c : 0,
				PackFolder = packFolder,
				_loader = loader
			};
			row.ConfidenceText = "conf " + row.Confidence.ToString("0.00", CultureInfo.InvariantCulture);
			row.Status = row.Status.Length > 0 ? row.Status : (row.Observed ? "runtime-observed" : "static-extracted");
			if(item.TryGetProperty("tiles", out JsonElement tiles) && tiles.ValueKind == JsonValueKind.Array && tiles.GetArrayLength() > 0) {
				row.Pattern = GetString(tiles[0], "pattern");
				row.ChrType = GetString(tiles[0], "chrType", "NesChrRom");
				string palette = GetString(tiles[0], "palette");
				row.Detail = "pattern " + row.Pattern + " / " + palette + " / " + row.ChrType;
			}
			List<string> notes = new();
			if(item.TryGetProperty("validation", out JsonElement validation2) && validation2.TryGetProperty("notes", out JsonElement noteList) && noteList.ValueKind == JsonValueKind.Array) {
				foreach(JsonElement note in noteList.EnumerateArray()) {
					if(note.ValueKind == JsonValueKind.String) {
						notes.Add(note.GetString() ?? "");
					}
				}
			}
			row.Notes = string.Join(" ", notes);
			if(row.Notes.Length == 0) {
				row.Notes = row.Observed ? "Runtime-observed pattern; semantic grouping and full replacement coverage are not claimed." : "Static extraction only; runtime applicability and palette are unresolved.";
			}
			return row;
		}

		private static string GetString(JsonElement element, string name, string fallback = "")
		{
			return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
		}

		private static bool GetBool(JsonElement element, string name)
		{
			return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
		}
	}

	internal sealed class HdStudioArchaeologyCandidateRow
	{
		public string Id = "";
		public string Name = "";
		public string Status = "";
		public string Detail = "";
		public string Provenance = "";
		public string GraphNode = "";
		public string ConfidenceText = "";
		public string Preview = "";
		internal string PackFolder = "";
		private Func<HdStudioArchaeologyThumbSource, Bitmap?>? _loader;
		private bool _loaded;
		private Bitmap? _thumb;

		public Bitmap? Thumbnail {
			get {
				if(!_loaded) {
					_loaded = true;
					_thumb = _loader?.Invoke(new HdStudioArchaeologyThumbSource() { PackFolder = PackFolder, OriginalImage = Preview, RomFolder = PackFolder });
				}
				return _thumb;
			}
		}

		public static HdStudioArchaeologyCandidateRow? FromJson(JsonElement item, string packFolder, Func<HdStudioArchaeologyThumbSource, Bitmap?> loader)
		{
			string id = GetString(item, "id");
			if(id.Length == 0) {
				return null;
			}
			string preview = "";
			string detail = "";
			if(item.TryGetProperty("properties", out JsonElement properties) && properties.ValueKind == JsonValueKind.Object) {
				preview = GetString(properties, "preview");
				List<string> parts = new();
				foreach(JsonProperty property in properties.EnumerateObject()) {
					if(property.Name is "preview" or "contentHash") continue;
					if(property.Value.ValueKind == JsonValueKind.String) {
						parts.Add(property.Name + "=" + property.Value.GetString());
					}
				}
				detail = string.Join(" / ", parts.Take(6));
			}
			return new HdStudioArchaeologyCandidateRow() {
				Id = id,
				Name = GetString(item, "name"),
				Status = GetString(item, "status"),
				Provenance = GetString(item, "provenance"),
				GraphNode = id,
				ConfidenceText = "conf " + (item.TryGetProperty("confidence", out JsonElement confidence) && confidence.TryGetDouble(out double c) ? c.ToString("0.00", CultureInfo.InvariantCulture) : "0"),
				Preview = preview,
				Detail = detail,
				PackFolder = packFolder,
				_loader = loader
			};
		}

		private static string GetString(JsonElement element, string name, string fallback = "")
		{
			return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
		}
	}

	internal sealed class HdStudioArchaeologyGapRow
	{
		public string Id = "";
		public string Kind = "";
		public string Description = "";
		public string Action = "";
		public string Status = "";
		public string PriorityText = "";
		public string Detail => Description;

		public static HdStudioArchaeologyGapRow? FromJson(JsonElement item)
		{
			string id = GetString(item, "id");
			if(id.Length == 0) {
				return null;
			}
			double priority = item.TryGetProperty("priority", out JsonElement p) && p.TryGetDouble(out double value) ? value : 0;
			return new HdStudioArchaeologyGapRow() {
				Id = id,
				Kind = GetString(item, "kind"),
				Description = GetString(item, "description"),
				Action = "action: " + GetString(item, "action"),
				Status = GetString(item, "status"),
				PriorityText = "priority " + priority.ToString("0.000", CultureInfo.InvariantCulture)
			};
		}

		private static string GetString(JsonElement element, string name, string fallback = "")
		{
			return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
		}
	}

	internal sealed class HdStudioArchaeologyCatalogSummary
	{
		public string RomFileName = "";
		public int Mapper;
		public string ChrMode = "";
		public int ChrBanks;
		public int ChrPages;
		public int UniquePatterns;
		public int BlankPatternOccurrences;
		public int ObservedChrRomPatterns;
		public int ObservedChrRamPatterns;
		public int AssetsExposed;
		public int AssetsObserved;
		public int AssetsStatic;
		public int CodeBlocks;
		public int PpuWrites;
		public int DataReferences;
		public int PointerTables;
		public int CandidateStreams;
		public int PaletteCandidates;
		public int OpenGaps;
		public string GraphPath = "";
		public string CatalogPath = "";

		public static HdStudioArchaeologyCatalogSummary FromJson(JsonElement root)
		{
			HdStudioArchaeologyCatalogSummary summary = new() {
				CatalogPath = ""
			};
			if(root.TryGetProperty("rom", out JsonElement rom)) {
				summary.RomFileName = GetString(rom, "fileName");
				summary.Mapper = GetInt(rom, "mapper");
				summary.ChrMode = GetString(rom, "chrMode");
			}
			if(!root.TryGetProperty("summary", out JsonElement s) || s.ValueKind != JsonValueKind.Object) {
				return summary;
			}
			summary.ChrBanks = GetInt(s, "chrBanks");
			summary.ChrPages = GetInt(s, "chrPages");
			summary.UniquePatterns = GetInt(s, "uniquePatterns");
			summary.BlankPatternOccurrences = GetInt(s, "blankPatternOccurrences");
			summary.ObservedChrRomPatterns = GetInt(s, "observedChrRomPatterns");
			summary.ObservedChrRamPatterns = GetInt(s, "observedChrRamPatterns");
			summary.AssetsExposed = GetInt(s, "assetsExposed");
			summary.AssetsObserved = GetInt(s, "assetsObserved");
			summary.AssetsStatic = GetInt(s, "assetsStatic");
			summary.CodeBlocks = GetInt(s, "codeBlocks");
			summary.PpuWrites = GetInt(s, "ppuWrites");
			summary.DataReferences = GetInt(s, "dataReferences");
			summary.PointerTables = GetInt(s, "pointerTables");
			summary.CandidateStreams = GetInt(s, "candidateStreams");
			summary.PaletteCandidates = GetInt(s, "paletteCandidates");
			summary.OpenGaps = GetInt(s, "openGaps");
			summary.GraphPath = "";
			return summary;
		}

		private static string GetString(JsonElement element, string name, string fallback = "")
		{
			return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
		}

		private static int GetInt(JsonElement element, string name, int fallback = 0)
		{
			return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : fallback;
		}
	}
}
