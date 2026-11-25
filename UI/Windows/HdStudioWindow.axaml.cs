using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mesen.Config;
using Mesen.HdStudio;
using Mesen.Interop;
using Mesen.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Windows
{
	/// <summary>
	/// RetroArcDo HD Studio: native authoring window.
	///
	/// The window is a thin, asynchronous shell over the shared HD Studio services
	/// (catalog, project, artwork pipeline, compiler). Captures run in a separate headless
	/// Mesen process so the UI thread never owns emulator state.
	/// </summary>
	public class HdStudioWindow : MesenWindow
	{
		private string _romPath = "";
		private string _packFolder = "";
		private bool _initialized;
		private bool _busy;

		private HdStudioProject? _project;
		private HdStudioCatalog _catalog = new();
		private uint[] _palette = Array.Empty<uint>();
		private readonly List<HdStudioCatalogAsset> _assetRows = new();
		private readonly List<HdStudioCatalogAsset> _animationRows = new();
		private readonly List<HdStudioCatalogAsset> _backgroundRows = new();
		private readonly List<HdStudioReplacement> _replacementRows = new();
		private readonly Dictionary<string, bool> _sharedRiskCache = new();
		private HdStudioCatalogAsset? _selectedAsset;
		private HdStudioCatalogAsset? _animationAsset;
		private HdStudioCatalogAsset? _backgroundAsset;
		private HdStudioReplacement? _selectedReplacement;
		private readonly List<Bitmap> _bitmaps = new();

		private TextBlock _romText = null!;
		private TextBlock _projectText = null!;
		private TextBlock _statusText = null!;
		private TextBlock _footerText = null!;
		private TextBox _assetSearchBox = null!;
		private ComboBox _categoryBox = null!;
		private ListBox _assetList = null!;
		private TextBlock _assetCountText = null!;
		private Image _originalImage = null!;
		private Image _replacementImage = null!;
		private TextBox _sourcePathBox = null!;
		private TextBlock _assetInfoText = null!;
		private CheckBox _removeBackgroundCheck = null!;
		private NumericUpDown _toleranceBox = null!;
		private CheckBox _keepLargestCheck = null!;
		private CheckBox _cropCheck = null!;
		private ComboBox _fitBox = null!;
		private ComboBox _anchorBox = null!;
		private NumericUpDown _paddingBox = null!;
		private NumericUpDown _offsetXBox = null!;
		private NumericUpDown _offsetYBox = null!;
		private NumericUpDown _zoomBox = null!;
		private CheckBox _flipHCheck = null!;
		private CheckBox _flipVCheck = null!;
		private CheckBox _allPalettesCheck = null!;
		private CheckBox _mirrorCheck = null!;
		private TextBlock _workshopStatusText = null!;
		private ListBox _animationList = null!;
		private Image _animationPreview = null!;
		private TextBlock _animationInfoText = null!;
		private ListBox _backgroundList = null!;
		private Image _backgroundPreview = null!;
		private TextBlock _backgroundInfoText = null!;
		private ListBox _replacementList = null!;
		private TextBlock _replacementInfoText = null!;
		private NumericUpDown _framesBox = null!;
		private ProgressBar _buildProgress = null!;
		private TextBox _logBox = null!;
		private TextBlock _validationText = null!;
		private TextBlock _projectPathText = null!;
		private NumericUpDown _scaleBox = null!;
		private TextBlock _baseStatusText = null!;
		private TextBlock _projectSummaryText = null!;
		private TabControl _tabs = null!;
		private TabItem _welcomeTab = null!;
		private TabItem _discoverTab = null!;
		private TabItem _workshopTab = null!;
		private TabItem _animationsTab = null!;
		private TabItem _backgroundsTab = null!;
		private TabItem _replacementsTab = null!;
		private TabItem _previewTab = null!;
		private TabItem _exportTab = null!;
		private TabItem _projectTab = null!;
		private TabItem _buildTab = null!;
		private ComboBox _statusFilterBox = null!;
		private TextBlock _sharedKeysText = null!;
		private CheckBox _matchBgPriorityCheck = null!;
		private CheckBox _allowSharedCheck = null!;
		private TextBlock _welcomeStatusText = null!;
		private ListBox _recentProjectsList = null!;
		private ComboBox _discoveryModeBox = null!;
		private NumericUpDown _discoveryBudgetBox = null!;
		private NumericUpDown _discoveryChunkBox = null!;
		private NumericUpDown _discoverySeedBox = null!;
		private CheckBox _discoveryRestartCheck = null!;
		private CheckBox _discoveryObserveBgCheck = null!;
		private Button _discoverStartButton = null!;
		private Button _discoverCancelButton = null!;
		private TextBox _discoveryMovieBox = null!;
		private TextBox _discoveryScriptBox = null!;
		private ProgressBar _discoveryProgress = null!;
		private TextBlock _discoveryStatusText = null!;
		private TextBox _discoveryMetricsText = null!;
		private ListBox _discoverySessionList = null!;
		private TextBox _exportRootBox = null!;
		private TextBox _exportNameBox = null!;
		private TextBlock _exportDestinationText = null!;
		private TextBlock _exportProjectFolderText = null!;
		private TextBlock _exportCurrentPackText = null!;
		private TextBlock _exportResultText = null!;
		private CheckBox _exportMinimalCheck = null!;
		private CheckBox _exportInstallCheck = null!;
		private CheckBox _exportOverwriteCheck = null!;
		private CheckBox _exportBackupCheck = null!;
		private Button _exportOpenButton = null!;
		private TextBlock _previewStatusText = null!;
		private TextBlock _verifySummaryText = null!;
		private Image _verifyOriginalImage = null!;
		private Image _verifyHdImage = null!;
		private TextBox _sheetPathBox = null!;
		private NumericUpDown _sliceColumnsBox = null!;
		private NumericUpDown _sliceRowsBox = null!;
		private NumericUpDown _sliceFrameWidthBox = null!;
		private NumericUpDown _sliceFrameHeightBox = null!;
		private NumericUpDown _sliceMarginBox = null!;
		private NumericUpDown _sliceSpacingBox = null!;
		private CheckBox _sliceTrimCheck = null!;
		private TextBlock _sliceStatusText = null!;
		private Image _slicePreviewImage = null!;

		private HdStudioSettings _settings = new();
		private CancellationTokenSource? _discoveryCancellation;
		private string _lastExportFolder = "";
		private List<HdStudioSliceFrame> _lastSliceFrames = new();
		private string _lastSliceFolder = "";

		public HdStudioWindow() : this("", "") { }

		public HdStudioWindow(string romPath, string packFolder)
		{
			_romPath = romPath ?? "";
			_packFolder = packFolder ?? "";
			AvaloniaXamlLoader.Load(this);
			BindControls();
			LoadSettings();
			Avalonia.Input.DragDrop.SetAllowDrop(this, true);
			AddHandler(Avalonia.Input.DragDrop.DropEvent, Window_Drop);
		}

		private void Window_Drop(object? sender, Avalonia.Input.DragEventArgs e)
		{
			var files = e.DataTransfer.TryGetFiles();
			string? path = files?.Select(f => f.Path.LocalPath).FirstOrDefault(File.Exists);
			if(path == null) {
				return;
			}
			if(Path.GetExtension(path).Equals(".nes", StringComparison.OrdinalIgnoreCase) ||
				Path.GetExtension(path).Equals(".fds", StringComparison.OrdinalIgnoreCase) ||
				Path.GetExtension(path).Equals(".unf", StringComparison.OrdinalIgnoreCase) ||
				Path.GetExtension(path).Equals(".unif", StringComparison.OrdinalIgnoreCase)) {
				_romPath = path;
				_packFolder = Path.Combine(ConfigManager.HdPackFolder, Path.GetFileNameWithoutExtension(path));
				_palette = ConfigManager.Config.Nes.UserPalette;
				_romText.Text = Path.GetFileName(path);
				_projectText.Text = Path.GetFileNameWithoutExtension(path);
				RefreshFromDisk();
				SetStatus("ROM opened by drag and drop: " + path);
				return;
			}
			_sourcePathBox.Text = path;
			_workshopStatusText.Text = "Artwork selected by drag and drop. Use Preview to check the composition.";
			_tabs.SelectedItem = _workshopTab;
		}

		private void LoadSettings()
		{
			try {
				_settings = HdStudioSettings.Load();
				_exportRootBox.Text = string.IsNullOrWhiteSpace(_settings.ExportRoot) ? HdStudioExport.GetDefaultExportRoot() : _settings.ExportRoot;
				_exportMinimalCheck.IsChecked = _settings.LastExportMinimal;
				_exportInstallCheck.IsChecked = _settings.LastExportInstall;
				_exportBackupCheck.IsChecked = _settings.LastExportBackup;
				_lastExportFolder = _settings.LastExportFolder;
				RefreshRecentProjects();
				UpdateExportPaths();
			} catch(Exception ex) {
				Log("Settings: " + ex.Message);
			}
		}

		private void RefreshRecentProjects()
		{
			List<string> rows = new();
			foreach(string project in _settings.RecentProjects) {
				if(File.Exists(project)) {
					rows.Add(project);
				}
			}
			_recentProjectsList.ItemsSource = rows;
			_recentProjectsList.SelectedIndex = rows.Count > 0 ? 0 : -1;
			_welcomeStatusText.Text = rows.Count == 0
				? "No recent project yet. Open a ROM to create one."
				: "Double-click a recent project to continue where you left off.";
		}

		private void Navigate_Click(object? sender, RoutedEventArgs e)
		{
			string target = (sender as Button)?.Tag?.ToString() ?? "";
			TabItem? tab = target switch {
				"welcome" => _welcomeTab,
				"discover" => _discoverTab,
				"workshop" => _workshopTab,
				"animations" => _animationsTab,
				"backgrounds" => _backgroundsTab,
				"replacements" => _replacementsTab,
				"preview" => _previewTab,
				"export" => _exportTab,
				"project" => _projectTab,
				"build" => _buildTab,
				_ => null
			};
			if(tab != null) {
				_tabs.SelectedItem = tab;
			}
		}

		private void UpdateExportPaths()
		{
			string projectFolder = string.IsNullOrWhiteSpace(_packFolder) ? "" : Path.Combine(_packFolder, HdStudioReport.StudioFolderName);
			_exportProjectFolderText.Text = string.IsNullOrWhiteSpace(projectFolder) ? "No project folder loaded." : projectFolder;
			_exportCurrentPackText.Text = string.IsNullOrWhiteSpace(_packFolder) ? "No pack folder loaded." : _packFolder;
			if(string.IsNullOrWhiteSpace(_exportNameBox.Text) && !string.IsNullOrWhiteSpace(_romPath)) {
				_exportNameBox.Text = HdStudioExport.SanitizePackName(Path.GetFileNameWithoutExtension(_romPath));
			}
			string root = _exportRootBox.Text?.Trim() ?? "";
			if(root.Length == 0) {
				root = HdStudioExport.GetDefaultExportRoot();
			}
			string name = HdStudioExport.SanitizePackName(_exportNameBox.Text ?? "");
			_exportDestinationText.Text = "Final destination: " + Path.GetFullPath(Path.Combine(root, name));
		}

		private async void OpenProject_Click(object? sender, RoutedEventArgs e)
		{
			var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions() {
				Title = "Open an HD Studio project",
				AllowMultiple = false,
				FileTypeFilter = new[] {
					new Avalonia.Platform.Storage.FilePickerFileType("HD Studio projects") { Patterns = new List<string>() { "project.hdstudio.json", "*.hdstudio.json", "*.json" } }
				}
			});
			if(files.Count == 0) {
				return;
			}
			OpenProjectFile(files[0].Path.LocalPath);
		}

		private void OpenProjectFile(string projectPath)
		{
			try {
				HdStudioProject project = HdStudioProject.Load(projectPath);
				if(string.IsNullOrWhiteSpace(project.RomPath) || !File.Exists(project.RomPath)) {
					SetStatus("The project ROM is missing: " + project.RomPath);
					return;
				}
				_romPath = project.RomPath;
				_packFolder = project.PackFolder;
				_palette = ConfigManager.Config.Nes.UserPalette;
				_romText.Text = Path.GetFileName(_romPath);
				_projectText.Text = project.ProjectName + "  |  " + project.ChrMode;
				RefreshFromDisk();
				_settings.RememberProject(projectPath);
				_settings.Save();
				RefreshRecentProjects();
				_tabs.SelectedItem = _workshopTab;
				SetStatus("Project opened: " + projectPath);
			} catch(Exception ex) {
				SetStatus("The project could not be opened: " + ex.Message);
				Log("ERROR: " + ex);
			}
		}

		private void RecentProjectsList_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
		{
			if(_recentProjectsList.SelectedItem is string projectPath) {
				OpenProjectFile(projectPath);
			}
		}

		private void OpenExample_Click(object? sender, RoutedEventArgs e)
		{
			string[] candidates = {
				Path.Combine(Program.OriginalFolder, "examples", "hdstudio_test.nes"),
				Path.Combine(AppContext.BaseDirectory, "examples", "hdstudio_test.nes"),
				@"D:\MesenHighWork\dist\RetroArcDoHDStudio\examples\hdstudio_test.nes"
			};
			string? rom = candidates.FirstOrDefault(File.Exists);
			if(rom == null) {
				SetStatus("The synthetic example is not installed next to this build. Use Open ROM instead.");
				return;
			}
			_romPath = rom;
			_packFolder = Path.Combine(ConfigManager.HdPackFolder, "hdstudio_test");
			_palette = ConfigManager.Config.Nes.UserPalette;
			_romText.Text = Path.GetFileName(rom);
			_projectText.Text = "hdstudio_test";
			try {
				RefreshFromDisk();
				_tabs.SelectedItem = _workshopTab;
				SetStatus("Synthetic example opened. The example pack is a legal, generated NES ROM project.");
			} catch(Exception ex) {
				SetStatus("The example could not be opened: " + ex.Message);
			}
		}

		private void BindControls()
		{
			_romText = this.GetControl<TextBlock>("RomText");
			_projectText = this.GetControl<TextBlock>("ProjectText");
			_statusText = this.GetControl<TextBlock>("StatusText");
			_footerText = this.GetControl<TextBlock>("FooterText");
			_assetSearchBox = this.GetControl<TextBox>("AssetSearchBox");
			_categoryBox = this.GetControl<ComboBox>("CategoryBox");
			_assetList = this.GetControl<ListBox>("AssetList");
			_assetCountText = this.GetControl<TextBlock>("AssetCountText");
			_originalImage = this.GetControl<Image>("OriginalImage");
			_replacementImage = this.GetControl<Image>("ReplacementImage");
			_sourcePathBox = this.GetControl<TextBox>("SourcePathBox");
			_assetInfoText = this.GetControl<TextBlock>("AssetInfoText");
			_removeBackgroundCheck = this.GetControl<CheckBox>("RemoveBackgroundCheck");
			_toleranceBox = this.GetControl<NumericUpDown>("ToleranceBox");
			_keepLargestCheck = this.GetControl<CheckBox>("KeepLargestCheck");
			_cropCheck = this.GetControl<CheckBox>("CropCheck");
			_fitBox = this.GetControl<ComboBox>("FitBox");
			_anchorBox = this.GetControl<ComboBox>("AnchorBox");
			_paddingBox = this.GetControl<NumericUpDown>("PaddingBox");
			_offsetXBox = this.GetControl<NumericUpDown>("OffsetXBox");
			_offsetYBox = this.GetControl<NumericUpDown>("OffsetYBox");
			_zoomBox = this.GetControl<NumericUpDown>("ZoomBox");
			_flipHCheck = this.GetControl<CheckBox>("FlipHCheck");
			_flipVCheck = this.GetControl<CheckBox>("FlipVCheck");
			_allPalettesCheck = this.GetControl<CheckBox>("AllPalettesCheck");
			_mirrorCheck = this.GetControl<CheckBox>("MirrorCheck");
			_workshopStatusText = this.GetControl<TextBlock>("WorkshopStatusText");
			_animationList = this.GetControl<ListBox>("AnimationList");
			_animationPreview = this.GetControl<Image>("AnimationPreview");
			_animationInfoText = this.GetControl<TextBlock>("AnimationInfoText");
			_backgroundList = this.GetControl<ListBox>("BackgroundList");
			_backgroundPreview = this.GetControl<Image>("BackgroundPreview");
			_backgroundInfoText = this.GetControl<TextBlock>("BackgroundInfoText");
			_replacementList = this.GetControl<ListBox>("ReplacementList");
			_replacementInfoText = this.GetControl<TextBlock>("ReplacementInfoText");
			_framesBox = this.GetControl<NumericUpDown>("FramesBox");
			_buildProgress = this.GetControl<ProgressBar>("BuildProgress");
			_logBox = this.GetControl<TextBox>("LogBox");
			_validationText = this.GetControl<TextBlock>("ValidationText");
			_projectPathText = this.GetControl<TextBlock>("ProjectPathText");
			_scaleBox = this.GetControl<NumericUpDown>("ScaleBox");
			_baseStatusText = this.GetControl<TextBlock>("BaseStatusText");
			_projectSummaryText = this.GetControl<TextBlock>("ProjectSummaryText");
			_tabs = this.GetControl<TabControl>("Tabs");
			_welcomeTab = this.GetControl<TabItem>("WelcomeTab");
			_discoverTab = this.GetControl<TabItem>("DiscoverTab");
			_workshopTab = this.GetControl<TabItem>("WorkshopTab");
			_animationsTab = this.GetControl<TabItem>("AnimationsTab");
			_backgroundsTab = this.GetControl<TabItem>("BackgroundsTab");
			_replacementsTab = this.GetControl<TabItem>("ReplacementsTab");
			_previewTab = this.GetControl<TabItem>("PreviewTab");
			_exportTab = this.GetControl<TabItem>("ExportTab");
			_projectTab = this.GetControl<TabItem>("ProjectTab");
			_buildTab = this.GetControl<TabItem>("BuildTab");
			_statusFilterBox = this.GetControl<ComboBox>("StatusFilterBox");
			_sharedKeysText = this.GetControl<TextBlock>("SharedKeysText");
			_matchBgPriorityCheck = this.GetControl<CheckBox>("MatchBgPriorityCheck");
			_allowSharedCheck = this.GetControl<CheckBox>("AllowSharedCheck");
			_welcomeStatusText = this.GetControl<TextBlock>("WelcomeStatusText");
			_recentProjectsList = this.GetControl<ListBox>("RecentProjectsList");
			_discoveryModeBox = this.GetControl<ComboBox>("DiscoveryModeBox");
			_discoveryBudgetBox = this.GetControl<NumericUpDown>("DiscoveryBudgetBox");
			_discoveryChunkBox = this.GetControl<NumericUpDown>("DiscoveryChunkBox");
			_discoverySeedBox = this.GetControl<NumericUpDown>("DiscoverySeedBox");
			_discoveryRestartCheck = this.GetControl<CheckBox>("DiscoveryRestartCheck");
			_discoveryObserveBgCheck = this.GetControl<CheckBox>("DiscoveryObserveBgCheck");
			_discoverStartButton = this.GetControl<Button>("DiscoverStartButton");
			_discoverCancelButton = this.GetControl<Button>("DiscoverCancelButton");
			_discoveryMovieBox = this.GetControl<TextBox>("DiscoveryMovieBox");
			_discoveryScriptBox = this.GetControl<TextBox>("DiscoveryScriptBox");
			_discoveryProgress = this.GetControl<ProgressBar>("DiscoveryProgress");
			_discoveryStatusText = this.GetControl<TextBlock>("DiscoveryStatusText");
			_discoveryMetricsText = this.GetControl<TextBox>("DiscoveryMetricsText");
			_discoverySessionList = this.GetControl<ListBox>("DiscoverySessionList");
			_exportRootBox = this.GetControl<TextBox>("ExportRootBox");
			_exportNameBox = this.GetControl<TextBox>("ExportNameBox");
			_exportDestinationText = this.GetControl<TextBlock>("ExportDestinationText");
			_exportProjectFolderText = this.GetControl<TextBlock>("ExportProjectFolderText");
			_exportCurrentPackText = this.GetControl<TextBlock>("ExportCurrentPackText");
			_exportResultText = this.GetControl<TextBlock>("ExportResultText");
			_exportMinimalCheck = this.GetControl<CheckBox>("ExportMinimalCheck");
			_exportInstallCheck = this.GetControl<CheckBox>("ExportInstallCheck");
			_exportOverwriteCheck = this.GetControl<CheckBox>("ExportOverwriteCheck");
			_exportBackupCheck = this.GetControl<CheckBox>("ExportBackupCheck");
			_exportOpenButton = this.GetControl<Button>("ExportOpenButton");
			_previewStatusText = this.GetControl<TextBlock>("PreviewStatusText");
			_verifySummaryText = this.GetControl<TextBlock>("VerifySummaryText");
			_verifyOriginalImage = this.GetControl<Image>("VerifyOriginalImage");
			_verifyHdImage = this.GetControl<Image>("VerifyHdImage");
			_sheetPathBox = this.GetControl<TextBox>("SheetPathBox");
			_sliceColumnsBox = this.GetControl<NumericUpDown>("SliceColumnsBox");
			_sliceRowsBox = this.GetControl<NumericUpDown>("SliceRowsBox");
			_sliceFrameWidthBox = this.GetControl<NumericUpDown>("SliceFrameWidthBox");
			_sliceFrameHeightBox = this.GetControl<NumericUpDown>("SliceFrameHeightBox");
			_sliceMarginBox = this.GetControl<NumericUpDown>("SliceMarginBox");
			_sliceSpacingBox = this.GetControl<NumericUpDown>("SliceSpacingBox");
			_sliceTrimCheck = this.GetControl<CheckBox>("SliceTrimCheck");
			_sliceStatusText = this.GetControl<TextBlock>("SliceStatusText");
			_slicePreviewImage = this.GetControl<Image>("SlicePreviewImage");
			_footerText.Text = "RetroArcDo HD Studio v0.2 Alpha - original NES geometry is preserved; only appearance changes.";
		}

		protected override void OnOpened(EventArgs e)
		{
			base.OnOpened(e);
			if(_initialized) {
				return;
			}
			_initialized = true;
			try {
				InitializeStudio();
			} catch(Exception ex) {
				SetStatus("The Studio could not start: " + ex.Message);
				Log("ERROR: " + ex);
			}
		}

		protected override void OnClosed(EventArgs e)
		{
			foreach(Bitmap bitmap in _bitmaps) {
				bitmap.Dispose();
			}
			_bitmaps.Clear();
			base.OnClosed(e);
		}

		private void InitializeStudio()
		{
			if(string.IsNullOrWhiteSpace(_romPath)) {
				try {
					RomInfo info = EmuApi.GetRomInfo();
					_romPath = ((ResourcePath)info.RomPath).Path;
				} catch {
					_romPath = "";
				}
			}
			if(string.IsNullOrWhiteSpace(_romPath) || !File.Exists(_romPath)) {
				SetStatus("No NES ROM is loaded. Use Open ROM... or start a game, then open Tools > RetroArcDo HD Studio.");
				_assetInfoText.Text = "A ROM is required. The standalone launcher accepts a ROM path and opens this window for it.";
				return;
			}
			if(string.IsNullOrWhiteSpace(_packFolder)) {
				_packFolder = Path.Combine(ConfigManager.HdPackFolder, Path.GetFileNameWithoutExtension(_romPath));
			}
			_palette = ConfigManager.Config.Nes.UserPalette;
			_romText.Text = Path.GetFileName(_romPath);
			_projectText.Text = Path.GetFileNameWithoutExtension(_romPath);
			RefreshFromDisk();
			Log("Studio opened for " + _romPath);
			Log("Pack folder: " + _packFolder);
			UpdateExportPaths();
			if(_catalog.Assets.Count > 0) {
				_tabs.SelectedItem = _workshopTab;
			}
		}

		private void RefreshFromDisk()
		{
			ReloadProject();
			_catalog = HdStudioCatalog.Load(_packFolder);
			_catalog.RomPath = _romPath;
			RefreshAssetLists();
			RefreshReplacements();
			RefreshProjectSummary();
			if(_catalog.Assets.Count == 0) {
				SetStatus("No semantic asset catalog yet. Run Discover to build one, or export assets from an existing capture.");
			} else {
				SetStatus($"{_catalog.Assets.Count} assets loaded from {_catalog.AssetsPath}");
			}
			UpdateExportPaths();
			try {
				HdStudioDiscoverySession? session = HdStudioDiscoverySession.Load(_packFolder);
				if(session != null) {
					ShowDiscoverySession(session, session.StopReason);
				}
				string verifyFolder = Path.Combine(_packFolder, HdStudioReport.StudioFolderName, "verify");
				if(Directory.Exists(verifyFolder)) {
					string? original = Directory.GetFiles(verifyFolder, "frame_*_original.png").OrderBy(f => f).FirstOrDefault();
					string? hd = Directory.GetFiles(verifyFolder, "frame_*_hd.png").OrderBy(f => f).FirstOrDefault();
					if(original != null) ShowBitmap(_verifyOriginalImage, original);
					if(hd != null) ShowBitmap(_verifyHdImage, hd);
					if(original != null || hd != null) {
						_verifySummaryText.Text = "Last verification images loaded from " + verifyFolder + ". Use Verify to run a new deterministic comparison.";
					}
				}
			} catch {
			}
		}

		private void ReloadProject()
		{
			string projectPath = HdStudioProject.GetDefaultPath(_packFolder);
			try {
				if(File.Exists(projectPath)) {
					_project = HdStudioProject.Load(projectPath);
					Log("Project loaded: " + projectPath);
				} else if(File.Exists(_romPath)) {
					_project = HdStudioProject.Create(_romPath, _packFolder, 4, Log);
					_project.Save(projectPath);
					Log("Project created: " + projectPath);
				} else {
					_project = null;
				}
				if(_project != null) {
					_project.ImportLegacyManifests(Log);
					_scaleBox.Value = Math.Clamp(_project.OutputScale, 1, 10);
					_projectPathText.Text = projectPath;
					_projectText.Text = _project.ProjectName + "  |  " + _project.ChrMode;
					_settings.RememberProject(projectPath);
					_settings.Save();
					RefreshRecentProjects();
				}
			} catch(Exception ex) {
				_project = null;
				SetStatus("Project load failed: " + ex.Message);
				Log("ERROR: " + ex);
			}
		}

		private void RefreshProjectSummary()
		{
			if(_project == null) {
				_projectSummaryText.Text = "No project.";
				_baseStatusText.Text = "";
				return;
			}
			string basePath = Path.Combine(_project.ProjectFolder, "base-hires.txt");
			_baseStatusText.Text = File.Exists(basePath)
				? "Base snapshot: " + basePath
				: "Base snapshot: not created yet (the first build snapshots the current capture pack).";
			int approved = _project.Replacements.Count(r => r.Approved);
			int drafts = _project.Replacements.Count - approved;
			_projectSummaryText.Text =
				$"ROM SHA1: {_project.RomSha1}\r\nMapper: {_project.Mapper}\r\n{_project.ChrMode}: {_project.ChrBytes:N0} bytes\r\n" +
				$"Scale: {_project.OutputScale}x\r\nReplacements: {approved} approved, {drafts} draft\r\n" +
				$"Project file: {HdStudioProject.GetDefaultPath(_packFolder)}";
			if(_project.ValidationNotes.Count > 0) {
				_validationText.Text = string.Join(Environment.NewLine, _project.ValidationNotes);
			}
		}

		private void RefreshAssetLists()
		{
			_assetRows.Clear();
			_sharedRiskCache.Clear();
			string search = _assetSearchBox.Text?.Trim() ?? "";
			string category = SelectedCategory();
			string status = SelectedStatus();
			IEnumerable<HdStudioCatalogAsset> query = _catalog.Assets;
			if(category != "All assets") {
				query = query.Where(a => string.Equals(a.Category, category, StringComparison.OrdinalIgnoreCase));
			}
			if(search.Length > 0) {
				query = query.Where(a =>
					a.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
					a.Label.Contains(search, StringComparison.OrdinalIgnoreCase) ||
					a.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
					a.Provenance.Contains(search, StringComparison.OrdinalIgnoreCase));
			}
			if(status == "Replaced") {
				query = query.Where(a => FindReplacement(a.Id) != null);
			} else if(status == "Missing replacement") {
				query = query.Where(a => FindReplacement(a.Id) == null);
			} else if(status == "Shared-key risk") {
				query = query.Where(HasSharedRisk);
			}
			_assetRows.AddRange(query.OrderBy(a => a.Category).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase));
			_assetList.ItemsSource = _assetRows.Select(a => FormatAssetRow(a)).ToList();
			_assetCountText.Text = _assetRows.Count + " of " + _catalog.Assets.Count + " assets";
			if(_assetRows.Count > 0) {
				_assetList.SelectedIndex = 0;
			} else {
				_selectedAsset = null;
				_originalImage.Source = null;
				_assetInfoText.Text = _catalog.Assets.Count == 0 ? "No assets captured yet." : "No asset matches the filter.";
				_sharedKeysText.Text = "";
			}

			_animationRows.Clear();
			_animationRows.AddRange(_catalog.Assets
				.Where(a => a.Kind == "animationSequence" || a.Kind == "animationFrame")
				.OrderBy(a => a.Kind == "animationSequence" ? 0 : 1)
				.ThenBy(a => a.AnimationId)
				.ThenBy(a => a.FrameIndex)
				.ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase));
			_animationList.ItemsSource = _animationRows.Select(a => FormatAnimationRow(a)).ToList();
			if(_animationRows.Count > 0) {
				_animationList.SelectedIndex = 0;
			}

			_backgroundRows.Clear();
			_backgroundRows.AddRange(_catalog.Assets
				.Where(a => a.Category == "Backgrounds" || a.Category == "Background tiles")
				.OrderBy(a => a.Category)
				.ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase));
			_backgroundList.ItemsSource = _backgroundRows.Select(a => FormatAssetRow(a)).ToList();
			if(_backgroundRows.Count > 0) {
				_backgroundList.SelectedIndex = 0;
			}
		}

		private string SelectedStatus()
		{
			return (_statusFilterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Any status";
		}

		private bool HasSharedRisk(HdStudioCatalogAsset asset)
		{
			if(_sharedRiskCache.TryGetValue(asset.Id, out bool cached)) {
				return cached;
			}
			bool risk = false;
			if(asset.Tiles.Count > 0) {
				risk = HdStudioCatalog.FindAssetsSharingKeys(_catalog, asset.Id,
					asset.Tiles.Select(t => (HdStudioCatalog.GetTileKey(t), t.Palette))).Count > 0;
			}
			_sharedRiskCache[asset.Id] = risk;
			return risk;
		}

		private string SelectedCategory()
		{
			return (_categoryBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All assets";
		}

		private string FormatAssetRow(HdStudioCatalogAsset asset)
		{
			HdStudioReplacement? replacement = FindReplacement(asset.Id);
			string replaced = replacement == null ? "" : replacement.Approved ? "  [replaced]" : "  [draft]";
			string tag = asset.Kind switch {
				"semanticObject" => "OBJ",
				"metasprite" => "SPR",
				"animationFrame" => "FRM",
				"animationSequence" => "SEQ",
				"backgroundBlock" => "BG",
				"backgroundTile" => "TILE",
				_ => "?"
			};
			return $"{tag}  {asset.Name}{replaced}";
		}

		private static string FormatAnimationRow(HdStudioCatalogAsset asset)
		{
			if(asset.Kind == "animationSequence") {
				return "Sequence: " + asset.Name;
			}
			return $"    Frame {asset.FrameIndex + 1}: {asset.Name}";
		}

		private void RefreshReplacements()
		{
			_replacementRows.Clear();
			if(_project != null) {
				_replacementRows.AddRange(_project.Replacements.OrderBy(r => r.AssetName, StringComparer.OrdinalIgnoreCase));
			}
			_replacementList.ItemsSource = _replacementRows.Select(r => FormatReplacementRow(r)).ToList();
			if(_replacementRows.Count > 0) {
				_replacementList.SelectedIndex = 0;
			} else {
				_selectedReplacement = null;
				_replacementInfoText.Text = "No replacements yet. Pick an asset, load artwork and use Approve & Apply.";
			}
		}

		private static string FormatReplacementRow(HdStudioReplacement replacement)
		{
			string state = replacement.Approved ? "APPROVED" : "DRAFT";
			return $"[{state}] {replacement.AssetName}  ({replacement.Cells.Count} tiles{(string.IsNullOrWhiteSpace(replacement.Sheet) ? "" : ", " + replacement.Sheet)})";
		}

		private void AssetList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			int index = _assetList.SelectedIndex;
			if(index < 0 || index >= _assetRows.Count) {
				return;
			}
			SelectAsset(_assetRows[index]);
		}

		private void SelectAsset(HdStudioCatalogAsset asset)
		{
			_selectedAsset = asset;
			HdStudioCatalogAsset previewAsset = RepresentativeAsset(asset);
			string original = HdStudioCatalog.EnsureOriginalPreview(_catalog, previewAsset, _palette);
			ShowBitmap(_originalImage, original);
			SetStatus($"Selected {asset.Name} ({asset.Kind}).");
			_assetInfoText.Text = DescribeAsset(asset, previewAsset);
			List<HdStudioCatalogAsset> shared = asset.Tiles.Count == 0 ? new() : HdStudioCatalog.FindAssetsSharingKeys(_catalog, asset.Id,
				asset.Tiles.Select(t => (HdStudioCatalog.GetTileKey(t), t.Palette)));
			if(shared.Count == 0) {
				_sharedKeysText.Text = "";
			} else {
				string names = string.Join(", ", shared.Take(5).Select(a => a.Name));
				if(shared.Count > 5) {
					names += ", ...";
				}
				_sharedKeysText.Text = $"Shared hardware keys: {shared.Count} other captured asset(s) use the same pattern+palette ({names}). Standard Mesen replaces them together unless a condition distinguishes them. Enable the explicit shared approval only if that is intended.";
			}
			HdStudioReplacement? replacement = FindReplacement(asset.Id);
			if(replacement != null) {
				FillWorkshop(replacement);
			} else {
				FillWorkshopDefaults();
			}
			_workshopStatusText.Text = "";
			_replacementImage.Source = null;
			if(replacement != null && !string.IsNullOrWhiteSpace(replacement.Sheet)) {
				string sheet = Path.Combine(_packFolder, replacement.Sheet);
				if(File.Exists(sheet)) {
					ShowBitmap(_replacementImage, sheet);
				}
			}
		}

		private static string DescribeAsset(HdStudioCatalogAsset asset, HdStudioCatalogAsset previewAsset)
		{
			string dimensions = asset.Width > 0 && asset.Height > 0 ? $"{asset.Width}x{asset.Height} NES pixels" : "unknown size";
			string frames = asset.Kind == "animationSequence"
				? $" | frames: {asset.FrameIndex + 1}"
				: "";
			string palette = asset.PaletteVariants.Count > 0 ? " | palettes: " + string.Join(", ", asset.PaletteVariants.Take(4)) : "";
			string validation = string.IsNullOrWhiteSpace(asset.ValidationStatus) ? "" : " | " + asset.ValidationStatus;
			string preview = !ReferenceEquals(asset, previewAsset) ? $" | showing frame: {previewAsset.Name}" : "";
			return $"{asset.Name} ({asset.Kind}){validation}\r\n{dimensions} | tiles: {asset.Tiles.Count}{palette}{frames}{preview}\r\nID: {asset.Id}\r\n{asset.Provenance}";
		}

		private HdStudioCatalogAsset RepresentativeAsset(HdStudioCatalogAsset asset)
		{
			if(asset.Tiles.Count > 0) {
				return asset;
			}
			HdStudioCatalogAsset? child = _catalog.Assets.FirstOrDefault(a => a.Tiles.Count > 0 &&
				(string.Equals(a.ParentId, asset.Id, StringComparison.OrdinalIgnoreCase) ||
				 string.Equals(a.AnimationId, asset.Id, StringComparison.OrdinalIgnoreCase)));
			return child ?? asset;
		}

		private HdStudioReplacement? FindReplacement(string assetId)
		{
			if(_project == null) {
				return null;
			}
			return _project.Replacements
				.Where(r => string.Equals(r.AssetId, assetId, StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(r => r.Approved)
				.ThenByDescending(r => r.UpdatedAtUtc, StringComparer.Ordinal)
				.FirstOrDefault();
		}

		private void FillWorkshopDefaults()
		{
			_sourcePathBox.Text = "";
			_removeBackgroundCheck.IsChecked = false;
			_toleranceBox.Value = 40;
			_keepLargestCheck.IsChecked = false;
			_cropCheck.IsChecked = true;
			_fitBox.SelectedIndex = 0;
			_anchorBox.SelectedIndex = 2;
			_paddingBox.Value = 0;
			_offsetXBox.Value = 0;
			_offsetYBox.Value = 0;
			_zoomBox.Value = 100;
			_flipHCheck.IsChecked = false;
			_flipVCheck.IsChecked = false;
			_allPalettesCheck.IsChecked = true;
			//Whole-object replacements normally apply to both orientations; Mesen mirrors
			//the replacement at render time. Conditional variants are for asymmetric art.
			_mirrorCheck.IsChecked = false;
			_matchBgPriorityCheck.IsChecked = false;
			_allowSharedCheck.IsChecked = false;
		}

		private void FillWorkshop(HdStudioReplacement replacement)
		{
			string source = _project?.ResolvePath(replacement.SourceImage) ?? replacement.SourceImage;
			_sourcePathBox.Text = File.Exists(source) ? source : replacement.SourceImage;
			HdStudioAdaptationSettings s = replacement.Settings;
			_removeBackgroundCheck.IsChecked = s.RemoveBackground;
			_toleranceBox.Value = s.BackgroundTolerance;
			_keepLargestCheck.IsChecked = s.KeepLargestComponent;
			_cropCheck.IsChecked = s.Crop;
			_fitBox.SelectedIndex = string.Equals(s.Fit, "stretch", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
			_anchorBox.SelectedIndex = (s.Anchor ?? "center").ToLowerInvariant() switch {
				"top" => 0,
				"left" => 1,
				"center" => 2,
				"right" => 3,
				"bottom" => 4,
				_ => 2
			};
			_paddingBox.Value = s.Padding;
			_offsetXBox.Value = s.OffsetX;
			_offsetYBox.Value = s.OffsetY;
			_zoomBox.Value = s.ZoomPercent;
			_flipHCheck.IsChecked = s.FlipH;
			_flipVCheck.IsChecked = s.FlipV;
			_allPalettesCheck.IsChecked = s.AllPaletteVariants;
			_mirrorCheck.IsChecked = s.MirrorConditions;
			_matchBgPriorityCheck.IsChecked = s.MatchBackgroundPriority;
			_allowSharedCheck.IsChecked = replacement.AllowSharedReplacement;
		}

		private HdStudioAdaptationSettings ReadSettings()
		{
			return new HdStudioAdaptationSettings() {
				RemoveBackground = _removeBackgroundCheck.IsChecked == true,
				BackgroundTolerance = IntValue(_toleranceBox, 40),
				KeepLargestComponent = _keepLargestCheck.IsChecked == true,
				Crop = _cropCheck.IsChecked == true,
				Fit = _fitBox.SelectedIndex == 1 ? "stretch" : "contain",
				Anchor = AnchorValue(_anchorBox.SelectedIndex),
				Padding = IntValue(_paddingBox, 0),
				OffsetX = IntValue(_offsetXBox, 0),
				OffsetY = IntValue(_offsetYBox, 0),
				ZoomPercent = IntValue(_zoomBox, 100),
				FlipH = _flipHCheck.IsChecked == true,
				FlipV = _flipVCheck.IsChecked == true,
				AllPaletteVariants = _allPalettesCheck.IsChecked == true,
				MirrorConditions = _mirrorCheck.IsChecked == true
			};
		}

		private static string AnchorValue(int index)
		{
			return index switch {
				0 => "top",
				1 => "left",
				3 => "right",
				4 => "bottom",
				_ => "center"
			};
		}

		private static int IntValue(NumericUpDown box, int fallback)
		{
			try {
				return box.Value.HasValue ? (int)Math.Round((double)box.Value.Value) : fallback;
			} catch {
				return fallback;
			}
		}

		private HdStudioCatalogAsset? EffectiveAsset()
		{
			if(_selectedAsset == null) {
				return null;
			}
			if(_selectedAsset.Tiles.Count > 0) {
				return _selectedAsset;
			}
			string animationId = _selectedAsset.Id;
			return _catalog.Assets.FirstOrDefault(a => a.Tiles.Count > 0 &&
				(string.Equals(a.ParentId, animationId, StringComparison.OrdinalIgnoreCase) ||
				 string.Equals(a.AnimationId, animationId, StringComparison.OrdinalIgnoreCase)));
		}

		private HdStudioReplacement? BuildDraft(bool store, bool approve)
		{
			if(_project == null) {
				_workshopStatusText.Text = "No project is loaded.";
				return null;
			}
			HdStudioCatalogAsset? asset = EffectiveAsset();
			if(asset == null) {
				_workshopStatusText.Text = "Select an asset with tile occurrences first.";
				return null;
			}
			string source = _sourcePathBox.Text?.Trim() ?? "";
			if(!File.Exists(source)) {
				_workshopStatusText.Text = "Choose an existing artwork file first.";
				return null;
			}

			int scale = Math.Clamp(_project.OutputScale, 1, 10);
			HdStudioReplacement? replacement = _project.Replacements
				.FirstOrDefault(r => string.Equals(r.AssetId, asset.Id, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(r.LegacyManifest)) ??
				_project.Replacements.FirstOrDefault(r => string.Equals(r.AssetId, asset.Id, StringComparison.OrdinalIgnoreCase));
			if(replacement == null) {
				replacement = new HdStudioReplacement() {
					Id = "replacement:" + HdStudioProject.StableId(asset.Id + "|managed"),
					AssetId = asset.Id,
					AssetName = asset.Name,
					AssetKind = asset.Kind,
					CreatedAtUtc = DateTime.UtcNow.ToString("O")
				};
				_project.Replacements.Add(replacement);
			}
			replacement.AssetName = asset.Name;
			replacement.AssetKind = asset.Kind;
			replacement.SourceImage = store ? _project.StoreArtwork(source, replacement.Id) : source;
			replacement.PackScale = scale;
			replacement.OriginalWidth = asset.Width;
			replacement.OriginalHeight = asset.Height;
			replacement.TargetWidth = asset.Width * scale;
			replacement.TargetHeight = asset.Height * scale;
			replacement.Approval = approve ? "approved" : "draft";
			replacement.AllowSharedReplacement = _allowSharedCheck.IsChecked == true;
			replacement.LegacyManifest = "";
			replacement.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
			replacement.Settings.RemoveBackground = _removeBackgroundCheck.IsChecked == true;
			replacement.Settings.BackgroundTolerance = IntValue(_toleranceBox, 40);
			replacement.Settings.KeepLargestComponent = _keepLargestCheck.IsChecked == true;
			replacement.Settings.Crop = _cropCheck.IsChecked == true;
			replacement.Settings.Fit = _fitBox.SelectedIndex == 1 ? "stretch" : "contain";
			replacement.Settings.Anchor = AnchorValue(_anchorBox.SelectedIndex);
			replacement.Settings.Padding = IntValue(_paddingBox, 0);
			replacement.Settings.OffsetX = IntValue(_offsetXBox, 0);
			replacement.Settings.OffsetY = IntValue(_offsetYBox, 0);
			replacement.Settings.ZoomPercent = IntValue(_zoomBox, 100);
			replacement.Settings.FlipH = _flipHCheck.IsChecked == true;
			replacement.Settings.FlipV = _flipVCheck.IsChecked == true;
			replacement.Settings.AllPaletteVariants = _allPalettesCheck.IsChecked == true;
			replacement.Settings.MirrorConditions = _mirrorCheck.IsChecked == true;
			replacement.Settings.MatchBackgroundPriority = _matchBgPriorityCheck.IsChecked == true;
			return replacement;
		}

		private void Preview_Click(object? sender, RoutedEventArgs e)
		{
			HdStudioReplacement? draft = BuildDraft(false, false);
			if(draft == null || _project == null) {
				return;
			}
			string path = HdStudioCompiler.PreviewReplacement(_project, draft, out List<string> messages);
			if(path.Length > 0 && File.Exists(path)) {
				ShowBitmap(_replacementImage, path);
				_workshopStatusText.Text = "Preview ready.";
			} else {
				_workshopStatusText.Text = messages.FirstOrDefault() ?? "Preview failed.";
			}
			foreach(string message in messages) {
				Log("Preview: " + message);
			}
		}

		private void SaveDraft_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null) {
				return;
			}
			HdStudioReplacement? draft = BuildDraft(true, false);
			if(draft == null) {
				return;
			}
			_project.Save(HdStudioProject.GetDefaultPath(_packFolder));
			RefreshReplacements();
			_workshopStatusText.Text = "Draft saved. It will not be exported until approved.";
			Log("Draft saved for " + draft.AssetName + ".");
			SetStatus("Draft saved.");
		}

		private async void Apply_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null) {
				return;
			}
			HdStudioReplacement? draft = BuildDraft(true, true);
			if(draft == null) {
				return;
			}
			_project.Save(HdStudioProject.GetDefaultPath(_packFolder));
			await BuildAsync("Building the standard pack...");
		}

		private async Task BuildAsync(string message)
		{
			if(_project == null) {
				return;
			}
			await RunBusy(message, async () => {
				_project.OutputScale = Math.Clamp(IntValue(_scaleBox, _project.OutputScale), 1, 10);
				HdStudioCompileResult result = await Task.Run(() => HdStudioCompiler.Compile(_project, Log));
				RefreshReplacements();
				RefreshProjectSummary();
				_validationText.Text = FormatCompileResult(result);
				SetStatus(result.Success ? $"Build OK: {result.CompiledReplacements} replacements, {result.TileEntries} tile entries." : "Build failed; see Validation.");
				if(!result.Success) {
					_tabs.SelectedItem = _projectTab;
				}
			});
		}

		private static string FormatCompileResult(HdStudioCompileResult result)
		{
			System.Text.StringBuilder sb = new();
			sb.AppendLine(result.Success ? "Build succeeded." : "Build failed.");
			sb.AppendLine($"Replacements: {result.CompiledReplacements} compiled, {result.SkippedReplacements} skipped");
			sb.AppendLine($"Tile entries: {result.TileEntries}");
			sb.AppendLine($"Images in hires.txt: {result.Images}");
			sb.AppendLine("Output: " + result.HiresPath);
			if(result.Errors.Count > 0) {
				sb.AppendLine();
				sb.AppendLine("Errors:");
				foreach(string error in result.Errors) sb.AppendLine("  - " + error);
			}
			if(result.Warnings.Count > 0) {
				sb.AppendLine();
				sb.AppendLine("Warnings:");
				foreach(string warning in result.Warnings) sb.AppendLine("  - " + warning);
			}
			return sb.ToString();
		}

		private async void Validate_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null) {
				return;
			}
			await RunBusy("Validating project...", async () => {
				HdStudioCompileResult result = await Task.Run(() => HdStudioCompiler.Compile(_project, Log, dryRun: true));
				_validationText.Text = FormatCompileResult(result);
				SetStatus(result.Success ? "Validation passed." : "Validation found errors.");
				RefreshProjectSummary();
			});
		}

		private async void Compile_Click(object? sender, RoutedEventArgs e)
		{
			await BuildAsync("Building the standard pack...");
		}

		private async void Capture_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null || string.IsNullOrWhiteSpace(_romPath)) {
				SetStatus("A ROM and project are required before capturing.");
				return;
			}
			int frames = IntValue(_framesBox, 18000);
			int scale = _project != null ? Math.Clamp(IntValue(_scaleBox, _project.OutputScale), 1, 10) : 4;
			await RunBusy("Running headless capture...", async () => {
				SetStatus("Capture running in a separate process...");
				//Prescale keeps untouched tiles visually identical to the original NES frame at
				//the project scale; authored replacements are layered on top by the compiler.
				int code = await RunHeadlessAsync("--hdstudio", _romPath, "--generate", "--bgscan", "--frames=" + frames, "--scale=" + scale, "--filter=prescale", "--out=" + _packFolder);
				if(code != 0) {
					SetStatus("Capture failed with exit code " + code + ". See the log.");
					return;
				}
				Log("Capture finished; exporting the semantic asset catalog...");
				int assetCode = await RunHeadlessAsync("--hdstudio", _romPath, "--assets", "--out=" + _packFolder);
				if(assetCode != 0) {
					SetStatus("Asset export failed with exit code " + assetCode + ".");
					return;
				}
				RefreshFromDisk();
				SetStatus("Capture and asset export completed.");
			});
		}

		private async Task<int> RunHeadlessAsync(params string[] args)
		{
			string logFolder = Path.Combine(_packFolder, "HdStudio");
			Directory.CreateDirectory(logFolder);
			string logPath = Path.Combine(logFolder, "studio-ui-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".log");
			ProcessStartInfo info = new(Program.ExePath) {
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				WorkingDirectory = Program.OriginalFolder
			};
			foreach(string arg in args) {
				info.ArgumentList.Add(arg);
			}
			info.ArgumentList.Add("--log=" + logPath);
			using Process process = new() { StartInfo = info, EnableRaisingEvents = true };
			process.OutputDataReceived += (s, e) => { if(!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data); };
			process.ErrorDataReceived += (s, e) => { if(!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data); };
			Log("> " + Path.GetFileName(Program.ExePath) + " " + string.Join(" ", args));
			process.Start();
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			await process.WaitForExitAsync();
			return process.ExitCode;
		}

		private async Task RunBusy(string message, Func<Task> action)
		{
			if(_busy) {
				SetStatus("Another operation is already running.");
				return;
			}
			_busy = true;
			_buildProgress.IsIndeterminate = true;
			SetStatus(message);
			try {
				await action();
			} catch(Exception ex) {
				SetStatus("Operation failed: " + ex.Message);
				Log("ERROR: " + ex);
			} finally {
				_busy = false;
				_buildProgress.IsIndeterminate = false;
				_buildProgress.Value = 0;
			}
		}

		private void Browse_Click(object? sender, RoutedEventArgs e)
		{
			_ = BrowseAsync();
		}

		private void OpenRom_Click(object? sender, RoutedEventArgs e)
		{
			_ = OpenRomAsync();
		}

		private async Task OpenRomAsync()
		{
			var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions() {
				Title = "Select a NES ROM",
				AllowMultiple = false,
				FileTypeFilter = new[] {
					new Avalonia.Platform.Storage.FilePickerFileType("NES ROMs") { Patterns = new List<string>() { "*.nes", "*.fds", "*.unf", "*.unif" } },
					new Avalonia.Platform.Storage.FilePickerFileType("All files") { Patterns = new List<string>() { "*" } }
				}
			});
			if(files.Count == 0) {
				return;
			}
			string path = files[0].Path.LocalPath;
			if(!File.Exists(path)) {
				SetStatus("The selected ROM file no longer exists.");
				return;
			}
			_romPath = path;
			_packFolder = Path.Combine(ConfigManager.HdPackFolder, Path.GetFileNameWithoutExtension(path));
			_palette = ConfigManager.Config.Nes.UserPalette;
			_romText.Text = Path.GetFileName(path);
			_projectText.Text = Path.GetFileNameWithoutExtension(path);
			try {
				RefreshFromDisk();
				Log("Studio opened for " + path);
				Log("Pack folder: " + _packFolder);
				_tabs.SelectedItem = _catalog.Assets.Count > 0 ? _workshopTab : _discoverTab;
			} catch(Exception ex) {
				SetStatus("The ROM could not be opened: " + ex.Message);
				Log("ERROR: " + ex);
			}
		}

		private async Task BrowseAsync()
		{
			var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions() {
				Title = "Select artwork",
				AllowMultiple = false,
				FileTypeFilter = new[] {
					new Avalonia.Platform.Storage.FilePickerFileType("Images") { Patterns = new List<string>() { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" } },
					new Avalonia.Platform.Storage.FilePickerFileType("All files") { Patterns = new List<string>() { "*" } }
				}
			});
			if(files.Count > 0) {
				_sourcePathBox.Text = files[0].Path.LocalPath;
				_workshopStatusText.Text = "Artwork selected. Use Preview to check the composition.";
			}
		}

		private void AssetSearch_TextChanged(object? sender, TextChangedEventArgs e)
		{
			if(_initialized) {
				RefreshAssetLists();
			}
		}

		private void Category_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			if(_initialized) {
				RefreshAssetLists();
			}
		}

		private void StatusFilter_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			if(_initialized) {
				RefreshAssetLists();
			}
		}

		private async void BrowseMovie_Click(object? sender, RoutedEventArgs e)
		{
			var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions() {
				Title = "Select an input movie",
				AllowMultiple = false,
				FileTypeFilter = new[] {
					new Avalonia.Platform.Storage.FilePickerFileType("NES movies") { Patterns = new List<string>() { "*.fm2", "*.mmo", "*.bk2" } }
				}
			});
			if(files.Count > 0) {
				_discoveryMovieBox.Text = files[0].Path.LocalPath;
			}
		}

		private async void BrowseScript_Click(object? sender, RoutedEventArgs e)
		{
			var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions() {
				Title = "Select a Lua input script",
				AllowMultiple = false,
				FileTypeFilter = new[] {
					new Avalonia.Platform.Storage.FilePickerFileType("Lua scripts") { Patterns = new List<string>() { "*.lua" } }
				}
			});
			if(files.Count > 0) {
				_discoveryScriptBox.Text = files[0].Path.LocalPath;
			}
		}

		private void OpenWorkshop_Click(object? sender, RoutedEventArgs e)
		{
			if(_selectedAsset != null) {
				_tabs.SelectedItem = _workshopTab;
				SelectAsset(_selectedAsset);
			}
		}

		private void AnimationList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			int index = _animationList.SelectedIndex;
			if(index < 0 || index >= _animationRows.Count) {
				return;
			}
			_animationAsset = _animationRows[index];
			//A sequence has no pixels of its own; show its first captured frame as a preview.
			HdStudioCatalogAsset previewAsset = RepresentativeAsset(_animationAsset);
			string image = HdStudioCatalog.EnsureOriginalPreview(_catalog, previewAsset, _palette);
			ShowBitmap(_animationPreview, image);
			int frames = _animationAsset.Kind == "animationSequence"
				? _catalog.Assets.Count(a => string.Equals(a.AnimationId, _animationAsset.Id, StringComparison.OrdinalIgnoreCase) || string.Equals(a.ParentId, _animationAsset.Id, StringComparison.OrdinalIgnoreCase))
				: 0;
			_animationInfoText.Text = _animationAsset.Name + " (" + _animationAsset.Kind + ")\r\n" +
				(_animationAsset.Kind == "animationSequence"
					? "Sequence with " + frames + " known frame reference(s). Showing: " + previewAsset.Name + ". Select a frame to open it in the Workshop."
					: "Frame " + (_animationAsset.FrameIndex + 1) + " of " + (_animationAsset.AnimationId.Length > 0 ? _animationAsset.AnimationId : "an animation") + ". Open it in the Workshop to replace it.");
		}

		private void OpenAnimation_Click(object? sender, RoutedEventArgs e)
		{
			if(_animationAsset == null) {
				return;
			}
			_tabs.SelectedItem = _workshopTab;
			if(_animationAsset.Tiles.Count > 0) {
				SelectAsset(_animationAsset);
			} else {
				HdStudioCatalogAsset? frame = _catalog.Assets.FirstOrDefault(a => a.Tiles.Count > 0 &&
					(string.Equals(a.ParentId, _animationAsset.Id, StringComparison.OrdinalIgnoreCase) ||
					 string.Equals(a.AnimationId, _animationAsset.Id, StringComparison.OrdinalIgnoreCase)));
				if(frame != null) {
					SelectAsset(frame);
				} else {
					SetStatus("No replaceable frame was found for this animation sequence.");
				}
			}
		}

		private void BackgroundList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			int index = _backgroundList.SelectedIndex;
			if(index < 0 || index >= _backgroundRows.Count) {
				return;
			}
			_backgroundAsset = _backgroundRows[index];
			string image = HdStudioCatalog.EnsureOriginalPreview(_catalog, _backgroundAsset, _palette);
			ShowBitmap(_backgroundPreview, image);
			_backgroundInfoText.Text = _backgroundAsset.Name + " (" + _backgroundAsset.Kind + ")\r\n" +
				$"{_backgroundAsset.Width}x{_backgroundAsset.Height} NES pixels, {_backgroundAsset.Tiles.Count} tiles" +
				"\r\nBackground replacements are segmented into the same original 8x8 cells; Mesen's background priority conditions remain untouched.";
		}

		private void OpenBackground_Click(object? sender, RoutedEventArgs e)
		{
			if(_backgroundAsset == null) {
				return;
			}
			_tabs.SelectedItem = _workshopTab;
			SelectAsset(_backgroundAsset);
		}

		private void ReplacementList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			int index = _replacementList.SelectedIndex;
			if(index < 0 || index >= _replacementRows.Count) {
				return;
			}
			_selectedReplacement = _replacementRows[index];
			HdStudioReplacement replacement = _selectedReplacement;
			_replacementInfoText.Text =
				$"{replacement.AssetName} ({replacement.AssetKind})\r\n" +
				$"State: {(replacement.Approved ? "approved" : "draft")}\r\n" +
				$"Asset: {replacement.AssetId}\r\n" +
				$"Source: {replacement.SourceImage}\r\n" +
				$"Sheet: {replacement.Sheet}\r\n" +
				$"Scale: {replacement.PackScale}x, target: {replacement.TargetWidth}x{replacement.TargetHeight}\r\n" +
				$"Cells: {replacement.Cells.Count}" +
				(string.IsNullOrWhiteSpace(replacement.LegacyManifest) ? "" : "\r\nLegacy manifest: " + replacement.LegacyManifest);
			if(!string.IsNullOrWhiteSpace(replacement.Sheet)) {
				string sheet = Path.Combine(_packFolder, replacement.Sheet);
				if(File.Exists(sheet)) {
					ShowBitmap(_replacementImage, sheet);
				}
			}
			if(replacement.Warnings.Count > 0) {
				_replacementInfoText.Text += "\r\nWarnings: " + string.Join("; ", replacement.Warnings);
			}
		}

		private void ApproveReplacement_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null || _selectedReplacement == null) {
				return;
			}
			_selectedReplacement.Approval = "approved";
			_selectedReplacement.LegacyManifest = "";
			_selectedReplacement.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
			_project.Save(HdStudioProject.GetDefaultPath(_packFolder));
			RefreshReplacements();
			SetStatus("Replacement approved. Build to export it.");
		}

		private void UnapproveReplacement_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null || _selectedReplacement == null) {
				return;
			}
			_selectedReplacement.Approval = "draft";
			_selectedReplacement.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
			_project.Save(HdStudioProject.GetDefaultPath(_packFolder));
			RefreshReplacements();
			SetStatus("Replacement moved to draft. It is no longer exported.");
		}

		private async void ApplyReplacement_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null || _selectedReplacement == null) {
				return;
			}
			_selectedReplacement.Approval = "approved";
			_selectedReplacement.LegacyManifest = "";
			_selectedReplacement.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
			_project.Save(HdStudioProject.GetDefaultPath(_packFolder));
			await BuildAsync("Applying the selected replacement...");
		}

		private async void RemoveReplacement_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null || _selectedReplacement == null) {
				return;
			}
			HdStudioReplacement removed = _selectedReplacement;
			_project.Replacements.Remove(removed);
			_project.Save(HdStudioProject.GetDefaultPath(_packFolder));
			RefreshReplacements();
			Log("Removed replacement " + removed.AssetName + ".");
			await BuildAsync("Rebuilding the pack without the removed replacement...");
		}

		private void SaveProject_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null) {
				return;
			}
			int scale = Math.Clamp(IntValue(_scaleBox, _project.OutputScale), 1, 10);
			_project.OutputScale = scale;
			foreach(HdStudioReplacement replacement in _project.Replacements) {
				replacement.PackScale = scale;
			}
			_project.Save(HdStudioProject.GetDefaultPath(_packFolder));
			RefreshProjectSummary();
			SetStatus("Project saved at " + scale + "x.");
		}

		private void ReloadProject_Click(object? sender, RoutedEventArgs e)
		{
			RefreshFromDisk();
			SetStatus("Project and catalog reloaded from disk.");
		}

		private async void RebuildBase_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null) {
				return;
			}
			string basePath = Path.Combine(_project.ProjectFolder, "base-hires.txt");
			if(File.Exists(basePath)) {
				File.Delete(basePath);
				Log("Base snapshot deleted; it will be recreated from the current pack.");
			}
			await BuildAsync("Re-snapshotting the base pack and rebuilding...");
		}

		private async void MinimalExport_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null) {
				return;
			}
			var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions() {
				Title = "Select a folder for the minimal HD pack",
				AllowMultiple = false
			});
			if(folders.Count == 0) {
				return;
			}
			string folder = folders[0].Path.LocalPath;
			await RunBusy("Exporting a minimal pack...", async () => {
				HdStudioCompileResult result = await Task.Run(() => HdStudioCompiler.Compile(_project, Log, minimal: true, outputFolder: folder));
				_validationText.Text = FormatCompileResult(result);
				SetStatus(result.Success ? "Minimal pack exported to " + folder : "Minimal export failed.");
			});
		}

		private async void DiscoverStart_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null || string.IsNullOrWhiteSpace(_romPath)) {
				SetStatus("Open a ROM and create a project before discovery.");
				return;
			}
			if(_busy) {
				SetStatus("Another operation is already running.");
				return;
			}
			string mode = _discoveryModeBox.SelectedIndex switch {
				0 => HdStudioDiscoveryModes.Quick,
				1 => HdStudioDiscoveryModes.Deep,
				2 => HdStudioDiscoveryModes.Replay,
				3 => HdStudioDiscoveryModes.Assisted,
				_ => HdStudioDiscoveryModes.Deep
			};
			HdStudioDiscoveryOptions options = new() {
				Mode = mode,
				Frames = IntValue(_discoveryBudgetBox, 18000),
				ChunkFrames = IntValue(_discoveryChunkBox, 1800),
				Seed = IntValue(_discoverySeedBox, 20261124),
				Scale = _project.OutputScale,
				MoviePath = _discoveryMovieBox.Text?.Trim() ?? "",
				LuaScriptPath = _discoveryScriptBox.Text?.Trim() ?? "",
				ObserveBackgrounds = _discoveryObserveBgCheck.IsChecked == true,
				Restart = _discoveryRestartCheck.IsChecked == true
			};
			if(mode == HdStudioDiscoveryModes.Replay && !File.Exists(options.MoviePath)) {
				SetStatus("Replay capture needs an existing movie file.");
				return;
			}
			if(mode == HdStudioDiscoveryModes.Assisted && !File.Exists(options.LuaScriptPath) && !File.Exists(options.MoviePath)) {
				SetStatus("Assisted exploration needs an existing Lua script or movie.");
				return;
			}

			_busy = true;
			_discoverStartButton.IsEnabled = false;
			_discoverCancelButton.IsEnabled = true;
			_discoveryProgress.Value = 0;
			_discoveryCancellation = new CancellationTokenSource();
			SetStatus(HdStudioDiscoveryModes.DisplayName(mode) + " running...");
			Progress<HdStudioDiscoveryProgress> progress = new(p => {
				_discoveryProgress.Value = p.RequestedFrames > 0 ? Math.Clamp(100.0 * p.FramesCaptured / p.RequestedFrames, 0, 100) : 0;
				_discoveryStatusText.Text = p.Message;
			});
			try {
				HdStudioDiscoveryResult result = await Task.Run(() => HdStudioDiscovery.RunAsync(_project, options, Log, progress, _discoveryCancellation.Token));
				foreach(string error in result.Errors) {
					Log("Discovery error: " + error);
				}
				RefreshFromDisk();
				ShowDiscoverySession(result.Session, result.StopReason);
				SetStatus(result.Success
					? $"Discovery stopped: {result.StopReason} after {result.Session.FramesCaptured:N0} frames. This is measured coverage, not a completeness proof."
					: "Discovery stopped with errors or was cancelled. Previous chunks and approved artwork are preserved.");
			} catch(Exception ex) {
				SetStatus("Discovery failed: " + ex.Message);
				Log("ERROR: " + ex);
			} finally {
				_busy = false;
				_discoverStartButton.IsEnabled = true;
				_discoverCancelButton.IsEnabled = false;
				_discoveryCancellation?.Dispose();
				_discoveryCancellation = null;
			}
		}

		private void DiscoverCancel_Click(object? sender, RoutedEventArgs e)
		{
			if(_discoveryCancellation == null) {
				return;
			}
			_discoveryCancellation.Cancel();
			_discoveryStatusText.Text = "Cancel requested. The current chunk will be stopped; earlier chunks are already saved.";
			_discoverCancelButton.IsEnabled = false;
		}

		private void ShowDiscoverySession(HdStudioDiscoverySession session, string stopReason)
		{
			List<string> rows = new();
			foreach(HdStudioDiscoverySnapshot snapshot in session.Snapshots) {
				rows.Add($"#{snapshot.Chunk}  frames +{snapshot.Frames:N0}  assets {snapshot.Assets:N0} (+{snapshot.NewAssets})  patterns {snapshot.ChrTilesSeen:N0} (+{snapshot.NewPatterns})  {snapshot.Seconds:0.0}s  seed {snapshot.Seed}");
			}
			_discoverySessionList.ItemsSource = rows;
			if(rows.Count > 0) {
				_discoverySessionList.SelectedIndex = rows.Count - 1;
			}
			_discoveryStatusText.Text = $"Session: {session.FramesCaptured:N0} of {session.RequestedFrames:N0} frames, {session.Chunks} chunk(s), stopped by {stopReason}.";
			System.Text.StringBuilder metrics = new();
			metrics.AppendLine("Mode: " + HdStudioDiscoveryModes.DisplayName(session.Mode));
			metrics.AppendLine("Frames captured: " + session.FramesCaptured.ToString("N0"));
			metrics.AppendLine("Chunks: " + session.Chunks);
			metrics.AppendLine("Assets: " + session.LastAssets.ToString("N0"));
			metrics.AppendLine("Observed CHR patterns: " + session.LastPatterns.ToString("N0"));
			metrics.AppendLine("Observed tile+palette combinations: " + session.LastSeenCombos.ToString("N0"));
			metrics.AppendLine("Stop reason: " + stopReason);
			metrics.AppendLine();
			metrics.AppendLine("A chunk is a complete capture process; the pack is merged and approved artwork is preserved.");
			metrics.AppendLine("Saturation means the measured asset/pattern counts stopped growing. It does not prove that every state or visual element was reached.");
			_discoveryMetricsText.Text = metrics.ToString();
			_discoveryProgress.Value = session.RequestedFrames > 0 ? Math.Clamp(100.0 * session.FramesCaptured / session.RequestedFrames, 0, 100) : 0;
		}

		private async void BrowseExportRoot_Click(object? sender, RoutedEventArgs e)
		{
			var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions() {
				Title = "Choose the export root folder",
				AllowMultiple = false
			});
			if(folders.Count > 0) {
				_exportRootBox.Text = folders[0].Path.LocalPath;
				UpdateExportPaths();
			}
		}

		private void UseRomName_Click(object? sender, RoutedEventArgs e)
		{
			if(!string.IsNullOrWhiteSpace(_romPath)) {
				_exportNameBox.Text = HdStudioExport.SanitizePackName(Path.GetFileNameWithoutExtension(_romPath));
				UpdateExportPaths();
			}
		}

		private void ExportPath_TextChanged(object? sender, TextChangedEventArgs e)
		{
			if(_initialized) {
				UpdateExportPaths();
			}
		}

		private string ExportDestination()
		{
			string root = _exportRootBox.Text?.Trim() ?? "";
			if(root.Length == 0) {
				root = HdStudioExport.GetDefaultExportRoot();
			}
			string name = HdStudioExport.SanitizePackName(_exportNameBox.Text ?? "");
			return Path.GetFullPath(Path.Combine(root, name));
		}

		private async void ExportRun_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null) {
				SetStatus("Open a project before exporting.");
				return;
			}
			await RunBusy("Exporting a validated HD pack...", async () => {
				string destination = ExportDestination();
				HdStudioExportOptions options = new() {
					DestinationFolder = destination,
					Minimal = _exportMinimalCheck.IsChecked == true,
					InstallIntoMesen = _exportInstallCheck.IsChecked == true,
					OverwriteExisting = _exportOverwriteCheck.IsChecked == true,
					KeepBackup = _exportBackupCheck.IsChecked == true
				};
				HdStudioExportResult result = await Task.Run(() => HdStudioExport.Export(_project, options, Log));
				_exportResultText.Text = FormatExportResult(result);
				if(result.Success) {
					_lastExportFolder = result.FinalFolder;
					_settings.ExportRoot = _exportRootBox.Text?.Trim() ?? "";
					_settings.LastExportFolder = _lastExportFolder;
					_settings.LastExportMinimal = options.Minimal;
					_settings.LastExportInstall = options.InstallIntoMesen;
					_settings.LastExportBackup = options.KeepBackup;
					_settings.Save();
					SetStatus("Export completed: " + result.FinalFolder);
				} else {
					SetStatus("Export failed; the previous pack was not modified.");
				}
			});
		}

		private static string FormatExportResult(HdStudioExportResult result)
		{
			System.Text.StringBuilder sb = new();
			sb.AppendLine(result.Success ? "Export succeeded." : "Export failed.");
			sb.AppendLine("Destination: " + result.FinalFolder);
			if(!string.IsNullOrWhiteSpace(result.InstalledFolder)) {
				sb.AppendLine("Installed for Mesen: " + result.InstalledFolder);
			}
			sb.AppendLine($"Files: {result.Files:N0}, bytes: {result.Bytes:N0}, images: {result.Images:N0}, tile entries: {result.TileEntries:N0}, replacements: {result.Replacements:N0}");
			if(result.Errors.Count > 0) {
				sb.AppendLine();
				sb.AppendLine("Errors:");
				foreach(string error in result.Errors) sb.AppendLine("  - " + error);
			}
			if(result.Warnings.Count > 0) {
				sb.AppendLine();
				sb.AppendLine("Warnings:");
				foreach(string warning in result.Warnings) sb.AppendLine("  - " + warning);
			}
			return sb.ToString();
		}

		private void ExportOpen_Click(object? sender, RoutedEventArgs e)
		{
			string folder = !string.IsNullOrWhiteSpace(_lastExportFolder) ? _lastExportFolder : ExportDestination();
			try {
				Directory.CreateDirectory(folder);
				Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
			} catch(Exception ex) {
				SetStatus("Could not open the export folder: " + ex.Message);
			}
		}

		private async void ExportCopy_Click(object? sender, RoutedEventArgs e)
		{
			string folder = !string.IsNullOrWhiteSpace(_lastExportFolder) ? _lastExportFolder : ExportDestination();
			var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
			if(clipboard != null) {
				await clipboard.SetTextAsync(folder);
				SetStatus("Copied export path: " + folder);
			}
		}

		private async void PreviewBuildInstall_Click(object? sender, RoutedEventArgs e)
		{
			await BuildAsync("Building and installing the pack for Mesen...");
			try {
				InstallCurrentPack();
				_previewStatusText.Text = "Pack installed into Mesen's HD Pack folder: " + ExpectedMesenPackFolder();
				SetStatus("Pack built and installed for preview.");
			} catch(Exception ex) {
				_previewStatusText.Text = "Install failed: " + ex.Message;
			}
		}

		private string ExpectedMesenPackFolder()
		{
			return Path.Combine(ConfigManager.HdPackFolder, Path.GetFileNameWithoutExtension(_romPath));
		}

		private void InstallCurrentPack()
		{
			if(_project == null) {
				return;
			}
			string expected = ExpectedMesenPackFolder();
			if(string.Equals(Path.GetFullPath(expected).TrimEnd('\\', '/'), Path.GetFullPath(_packFolder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) {
				return;
			}
			CopyDirectoryContents(_packFolder, expected, new[] { "HdStudio" });
		}

		private static void CopyDirectoryContents(string source, string target, string[] excludedFolders)
		{
			Directory.CreateDirectory(target);
			foreach(string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
				string relative = Path.GetRelativePath(source, file);
				string first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
				if(excludedFolders.Contains(first, StringComparer.OrdinalIgnoreCase)) {
					continue;
				}
				string destination = Path.Combine(target, relative);
				Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? target);
				File.Copy(file, destination, true);
			}
		}

		private void PreviewLaunch_Click(object? sender, RoutedEventArgs e)
		{
			try {
				if(_project != null && File.Exists(Path.Combine(_packFolder, "hires.txt"))) {
					InstallCurrentPack();
				}
				Process.Start(new ProcessStartInfo(Program.ExePath, "\"" + _romPath + "\"") { UseShellExecute = true });
				SetStatus("Launched Mesen with the current ROM and installed pack.");
			} catch(Exception ex) {
				SetStatus("Could not launch Mesen: " + ex.Message);
			}
		}

		private async void Verify_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null || string.IsNullOrWhiteSpace(_romPath)) {
				return;
			}
			await RunBusy("Verifying the pack in the real emulator...", async () => {
				InstallCurrentPack();
				int frames = 1200;
				int code = await RunHeadlessAsync("--hdstudio", _romPath, "--verify", "--shots=2", "--frames=" + frames, "--out=" + _packFolder);
				string verifyFolder = Path.Combine(_packFolder, HdStudioReport.StudioFolderName, "verify");
				string html = Path.Combine(verifyFolder, "verify.html");
				string? original = Directory.Exists(verifyFolder) ? Directory.GetFiles(verifyFolder, "frame_*_original.png").OrderBy(f => f).FirstOrDefault() : null;
				string? hd = Directory.Exists(verifyFolder) ? Directory.GetFiles(verifyFolder, "frame_*_hd.png").OrderBy(f => f).FirstOrDefault() : null;
				if(original != null) ShowBitmap(_verifyOriginalImage, original);
				if(hd != null) ShowBitmap(_verifyHdImage, hd);
				_verifySummaryText.Text = File.Exists(html)
					? "Verification page: " + html + "\r\nExit code: " + code + ". The page compares the same deterministic frames and reports gameplay-state hashes."
					: "Verification did not produce a report (exit code " + code + "). See the diagnostics log.";
				_previewStatusText.Text = code == 0 ? "HD pack active and deterministic state comparison completed." : "Verification reported a problem. See the report and log.";
				SetStatus(code == 0 ? "Verification completed." : "Verification reported a problem.");
			});
		}

		private async void BrowseSheet_Click(object? sender, RoutedEventArgs e)
		{
			var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions() {
				Title = "Select a sprite sheet",
				AllowMultiple = false,
				FileTypeFilter = new[] {
					new Avalonia.Platform.Storage.FilePickerFileType("Images") { Patterns = new List<string>() { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" } }
				}
			});
			if(files.Count > 0) {
				_sheetPathBox.Text = files[0].Path.LocalPath;
			}
		}

		private HdStudioSliceOptions ReadSliceOptions(string outputFolder)
		{
			return new HdStudioSliceOptions() {
				ImagePath = _sheetPathBox.Text?.Trim() ?? "",
				OutputFolder = outputFolder,
				Columns = IntValue(_sliceColumnsBox, 0),
				Rows = IntValue(_sliceRowsBox, 0),
				FrameWidth = IntValue(_sliceFrameWidthBox, 0),
				FrameHeight = IntValue(_sliceFrameHeightBox, 0),
				MarginX = IntValue(_sliceMarginBox, 0),
				MarginY = IntValue(_sliceMarginBox, 0),
				SpacingX = IntValue(_sliceSpacingBox, 0),
				SpacingY = IntValue(_sliceSpacingBox, 0),
				TrimTransparent = _sliceTrimCheck.IsChecked == true
			};
		}

		private void SlicePreview_Click(object? sender, RoutedEventArgs e)
		{
			try {
				string folder = Path.Combine(_project?.ProjectFolder ?? _packFolder, "slices");
				_lastSliceFolder = folder;
				_lastSliceFrames = HdStudioSheetSlicer.Slice(ReadSliceOptions(folder), Log);
				if(_lastSliceFrames.Count > 0) {
					ShowBitmap(_slicePreviewImage, _lastSliceFrames[0].Path);
					_sliceStatusText.Text = $"{_lastSliceFrames.Count} frames sliced into {folder}. First frame: {_lastSliceFrames[0].Width}x{_lastSliceFrames[0].Height} at ({_lastSliceFrames[0].X},{_lastSliceFrames[0].Y}).";
				}
			} catch(Exception ex) {
				_sliceStatusText.Text = "Slicing failed: " + ex.Message;
				Log("Slice error: " + ex);
			}
		}

		private void SliceAssign_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null) {
				SetStatus("Open a project before assigning animation frames.");
				return;
			}
			if(_lastSliceFrames.Count == 0) {
				SlicePreview_Click(sender, e);
			}
			if(_lastSliceFrames.Count == 0) {
				return;
			}
			if(_animationAsset == null) {
				SliceStatusText("Select an animation sequence first.");
				return;
			}
			List<HdStudioCatalogAsset> frames;
			if(_animationAsset.Kind == "animationSequence") {
				frames = _catalog.Assets
					.Where(a => a.Kind == "animationFrame" && a.Tiles.Count > 0 &&
						(string.Equals(a.ParentId, _animationAsset.Id, StringComparison.OrdinalIgnoreCase) ||
						 string.Equals(a.AnimationId, _animationAsset.Id, StringComparison.OrdinalIgnoreCase)))
					.OrderBy(a => a.FrameIndex)
					.ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
					.ToList();
			} else {
				frames = new() { _animationAsset };
			}
			if(frames.Count == 0) {
				SliceStatusText("The selected sequence has no captured frames with tile occurrences.");
				return;
			}
			int assigned = 0;
			for(int i = 0; i < frames.Count && i < _lastSliceFrames.Count; i++) {
				HdStudioCatalogAsset asset = frames[i];
				HdStudioSliceFrame slice = _lastSliceFrames[i];
				string id = "replacement:" + HdStudioProject.StableId(asset.Id + "|sheet");
				HdStudioReplacement? replacement = _project.Replacements.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
				if(replacement == null) {
					replacement = new HdStudioReplacement() {
						Id = id,
						AssetId = asset.Id,
						AssetName = asset.Name,
						AssetKind = asset.Kind,
						CreatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
					};
					_project.Replacements.Add(replacement);
				}
				replacement.SourceImage = _project.StoreArtwork(slice.Path, id);
				replacement.PackScale = _project.OutputScale;
				replacement.OriginalWidth = asset.Width;
				replacement.OriginalHeight = asset.Height;
				replacement.TargetWidth = asset.Width * _project.OutputScale;
				replacement.TargetHeight = asset.Height * _project.OutputScale;
				replacement.Approval = "draft";
				replacement.AllowSharedReplacement = false;
				replacement.Settings.Crop = true;
				replacement.Settings.Fit = "contain";
				replacement.Settings.Anchor = "center";
				replacement.Settings.AllPaletteVariants = true;
				replacement.UpdatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
				assigned++;
			}
			_project.Save(HdStudioProject.GetDefaultPath(_packFolder));
			RefreshReplacements();
			SliceStatusText($"Assigned {assigned} sliced frame(s) to {_animationAsset.Name} as drafts. Review and approve them in Replacements.");
			SetStatus($"Assigned {assigned} animation frame(s) as drafts.");
		}

		private void SliceStatusText(string message)
		{
			_sliceStatusText.Text = message;
			SetStatus(message);
		}

		private void OpenPackFolder_Click(object? sender, RoutedEventArgs e)
		{
			try {
				Directory.CreateDirectory(_packFolder);
				Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _packFolder + "\"") { UseShellExecute = true });
			} catch(Exception ex) {
				SetStatus("Could not open the pack folder: " + ex.Message);
			}
		}

		private void ShowBitmap(Image target, string path)
		{
			if(string.IsNullOrWhiteSpace(path) || !File.Exists(path)) {
				target.Source = null;
				return;
			}
			try {
				Bitmap bitmap = new(path);
				_bitmaps.Add(bitmap);
				target.Source = bitmap;
			} catch {
				target.Source = null;
			}
		}

		private void SetStatus(string message)
		{
			if(Dispatcher.UIThread.CheckAccess()) {
				_statusText.Text = message;
			} else {
				Dispatcher.UIThread.Post(() => _statusText.Text = message);
			}
		}

		private void Log(string message)
		{
			if(Dispatcher.UIThread.CheckAccess()) {
				AppendLog(message);
			} else {
				Dispatcher.UIThread.Post(() => AppendLog(message));
			}
		}

		private void AppendLog(string message)
		{
			_logBox.Text = (_logBox.Text ?? "") + message + Environment.NewLine;
			if(_logBox.Text.Length > 400000) {
				_logBox.Text = _logBox.Text.Substring(_logBox.Text.Length - 300000);
			}
			_logBox.CaretIndex = _logBox.Text.Length;
			_footerText.Text = message.Length > 160 ? message.Substring(0, 160) : message;
		}
	}
}
