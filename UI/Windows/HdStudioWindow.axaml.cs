using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mesen.Config;
using Mesen.Controls;
using Mesen.HdStudio;
using Mesen.Interop;
using Mesen.Utilities;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Windows
{
	/// <summary>
	/// RetroArcDo HD Studio v0.9 - graphical authoring shell with static-first
	/// RetroArchaeology, the evidence graph and the existing authoring/verification path.
	///
	/// All file, catalog, compiler and exporter work remains in the shared HdStudio services.
	/// This window owns presentation only: it selects an asset, edits the project's adaptation
	/// settings, previews the exact composed artwork and starts the same headless processes as
	/// the command line. It never edits ROM bytes, tile placement, animation timing or gameplay.
	/// </summary>
	public partial class HdStudioWindow : MesenWindow
	{
		private const string Version = "0.9.0 Alpha";

		private string _romPath = "";
		private string _packFolder = "";
		private bool _initialized;
		private bool _busy;
		private bool _dirty;
		private bool _syncingEditor;
		private bool _syncingSelection;
		private bool _assetGrid = true;
		private bool _suppressFilterRefresh;
		private bool _suppressAssetListSelection;
		private bool _bottomOpen = true;
		private string _activeWorkspace = "home";
		private string _bottomTab = "timeline";
		private string _tool = "select";

		private HdStudioProject? _project;
		private HdStudioCatalog _catalog = new();
		private uint[] _palette = Array.Empty<uint>();
		private HdStudioSettings _settings = new();

		private readonly List<HdStudioCatalogAsset> _allAssets = new();
		private readonly List<HdStudioAssetRow> _assetRows = new();
		private readonly List<HdStudioCatalogAsset> _filteredAssets = new();
		private int _assetLimit = 160;
		private readonly List<HdStudioAssetRow> _spriteRows = new();
		private readonly List<HdStudioAssetRow> _backgroundRows = new();
		private readonly List<HdStudioCatalogAsset> _animationRows = new();
		private readonly List<HdStudioFrameRow> _timelineRows = new();
		private readonly List<HdStudioCandidateRow> _candidateRows = new();
		private readonly List<HdStudioReplacement> _replacementRows = new();
		private readonly HashSet<string> _newAssetIds = new(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> _reviewAssetIds = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, bool> _sharedRiskCache = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, Bitmap> _bitmapCache = new(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> _thumbnailRequests = new(StringComparer.OrdinalIgnoreCase);
		private readonly SemaphoreSlim _thumbnailGate = new(4);

		private HdStudioCatalogAsset? _selectedAsset;
		private HdStudioCatalogAsset? _sequenceAsset;
		private HdStudioCatalogAsset? _frameAsset;
		private HdStudioCatalogAsset? _backgroundAsset;
		private HdStudioReplacement? _selectedReplacement;
		private HdStudioAdaptationSettings _editSettings = new();
		private string _editSourcePath = "";

		private CancellationTokenSource? _discoveryCancellation;
		private bool _captureActive;
		private bool _capturePaused;
		private string _captureState = "IDLE";
		private HdStudioLiveSession? _liveSession;
		private DispatcherTimer? _liveTimer;
		private HdStudioPicking.PauseFrame? _pausedLiveFrame;
		private readonly List<HdStudioPicking.PickCandidate> _pickCandidates = new();
		private string _lastExportFolder = "";
		private string _exactExportDestination = "";
		private List<HdStudioSliceFrame> _lastSliceFrames = new();
		private string _lastSliceFolder = "";
		private DispatcherTimer? _animationTimer;
		private DispatcherTimer? _renderShotTimer;
		private int _animationIndex;
		private bool _loopAnimation = true;
		private bool _animationPlaying;

		private readonly Stack<EditorSnapshot> _undo = new();
		private readonly Stack<EditorSnapshot> _redo = new();

		private sealed class EditorSnapshot
		{
			public List<HdStudioReplacement> Replacements = new();
			public List<string> Locked = new();
			public string AssetId = "";
			public string SourcePath = "";
			public HdStudioAdaptationSettings Settings = new();
		}

		//Top bar and workspace
		private TextBlock _romText = null!, _projectText = null!;
		private Grid _workspaceGrid = null!, _centerGrid = null!;
		private ThemeVariantScope _themeScope = null!;
		private Border _assetLibraryPanel = null!, _inspectorPanel = null!, _topBar = null!, _navRail = null!, _statusBar = null!;
		private Control _bottomDock = null!;
		private GridSplitter _assetSplitter = null!, _inspectorSplitter = null!;
		private Grid _homePage = null!, _discoveryPage = null!, _spritesPage = null!, _backgroundsPage = null!;
		private Grid _animationsPage = null!, _artworkPage = null!, _previewPage = null!, _exportPage = null!, _settingsPage = null!;
		private Grid _libraryPage = null!, _batchPage = null!;
		private ListBox _romLibraryList = null!, _batchGameList = null!;
		private readonly List<HdStudioRomRow> _romRows = new();
		private readonly List<HdStudioBatchRow> _batchRows = new();
		private HdStudioRomLibrary.Catalog? _romCatalog;
		private Process? _batchProcess;
		private DispatcherTimer? _batchTimer;
		private string _batchOutputRoot = "";
		private string _batchResultsPath = "";
		private DateTime _batchStartedUtc;

		//Asset library
		private ListBox _assetList = null!, _spriteGalleryList = null!, _backgroundList = null!;
		private TextBox _assetSearchBox = null!;
		private ComboBox _statusFilterBox = null!;
		private TextBlock _assetCountText = null!, _assetCountsDetailText = null!, _thumbnailSizeText = null!;
		private Slider _thumbnailSizeSlider = null!;
		private Button _gridViewButton = null!, _listViewButton = null!, _loadMoreAssetsButton = null!;

		//Inspector
		private TextBlock _inspectorAssetName = null!, _inspectorAssetKind = null!, _assetInfoText = null!;
		private TextBlock _inspectorStatusBadgeText = null!, _logicalSizeText = null!, _hdSizeText = null!, _sourceSizeText = null!, _inspectorScaleText = null!;
		private Image _inspectorThumbnail = null!;
		private Border _inspectorStatusBadge = null!, _inspectorNewBadge = null!;
		private NumericUpDown _offsetXBox = null!, _offsetYBox = null!, _zoomBox = null!, _toleranceBox = null!;
		private Slider _toleranceSlider = null!;
		private ComboBox _anchorBox = null!;
		private CheckBox _cropCheck = null!, _removeBackgroundCheck = null!, _keepLargestCheck = null!, _checkerToggleCheck = null!;
		private CheckBox _allPalettesCheck = null!, _mirrorCheck = null!, _matchBgPriorityCheck = null!, _allowSharedCheck = null!;
		private TextBox _extraConditionBox = null!;
		private WrapPanel _paletteList = null!;
		private TextBlock _paletteCountText = null!, _cropInfoText = null!, _sharedKeysText = null!, _assetIdText = null!, _provenanceText = null!, _hardwareText = null!, _tileListText = null!;
		private TextBlock _inspectorStatusText = null!;
		private Button _applyButton = null!;

		//Artwork
		private HdStudioArtworkCanvas _originalCanvas = null!, _replacementCanvas = null!, _homePreviewCanvas = null!;
		private TextBox _sourcePathBox = null!;
		private TextBlock _workshopTitleText = null!, _workshopSubtitleText = null!, _workshopStatusText = null!, _workshopStatusInspector = null!;
		private TextBlock _originalSizeBadge = null!, _replacementSizeBadge = null!;
		private CheckBox _showGridCheck = null!, _showCheckerCheck = null!;
		private Button _gridToggleButton = null!, _cropToolButton = null!;
		private ListBox _animationList = null!, _animationTimeline = null!;
		private Image _animationPreview = null!;
		private TextBlock _animationInfoText = null!, _timelineInfoText = null!, _dockTimelineInfo = null!, _animationSubtitle = null!;
		private Button _playAnimationButton = null!, _loopAnimationButton = null!;

		//Backgrounds
		private Image _backgroundPreview = null!;
		private TextBlock _backgroundInfoText = null!, _backgroundsSubtitle = null!, _backgroundGridLabel = null!;

		//Discovery / capture
		private HdStudioGameplayCanvas _gameplayCanvas = null!;
		private ListBox _gameplayCandidateList = null!;
		private TextBlock _discoveryStatusText = null!, _discoveryMetricsText = null!, _gameplaySelectionText = null!, _gameplayHintText = null!;
		private TextBlock _captureStateText = null!, _captureSessionText = null!, _capturePercentText = null!;
		private TextBlock _captureFramesText = null!, _captureAssetsText = null!, _capturePatternsText = null!, _captureNewText = null!;
		private TextBlock _candidateFilterText = null!, _candidateHintText = null!;
		private Border _captureStateBadge = null!;
		private ComboBox _discoveryModeBox = null!, _samplingModeBox = null!;
		private NumericUpDown _discoveryBudgetBox = null!, _discoveryChunkBox = null!, _discoverySeedBox = null!;
		private CheckBox _discoveryRestartCheck = null!, _discoveryObserveBgCheck = null!;
		private Button _discoverStartButton = null!, _discoverCancelButton = null!, _selectObjectButton = null!;
		private TextBox _discoveryMovieBox = null!, _discoveryScriptBox = null!;
		private ProgressBar _discoveryProgress = null!;
		private ListBox _discoverySessionList = null!;
		private ProgressBar _dockCaptureProgress = null!;
		private TextBlock _dockCaptureStatus = null!, _dockCaptureMode = null!, _dockCaptureFrames = null!, _dockCaptureChunks = null!, _dockCaptureReason = null!;
		private Button _recordButton = null!, _pauseCaptureButton = null!, _stopCaptureButton = null!;

		//Preview
		private Image _verifyOriginalImage = null!, _verifyHdImage = null!;
		private TextBlock _previewStatusText = null!, _verifySummaryText = null!;

		//Export
		private TextBox _exportRootBox = null!, _exportNameBox = null!;
		private TextBlock _exportDestinationText = null!, _exportDestinationHint = null!, _exportProjectFolderText = null!, _exportCurrentPackText = null!, _exportResultText = null!;
		private TextBlock _exportSummaryPackName = null!, _exportSummaryApproved = null!, _exportSummaryScale = null!, _exportSummaryValidation = null!, _exportSummaryFiles = null!, _exportSummaryDestination = null!;
		private CheckBox _exportMinimalCheck = null!, _exportInstallCheck = null!, _exportOverwriteCheck = null!, _exportBackupCheck = null!;
		private Button _exportRunButton = null!;

		//Settings / diagnostics
		private ComboBox _themeBox = null!;
		private CheckBox _defaultGridCheck = null!, _defaultCheckerCheck = null!;
		private NumericUpDown _scaleBox = null!, _framesBox = null!;
		private ProgressBar _buildProgress = null!;
		private TextBox _logBox = null!;
		private TextBlock _validationText = null!, _projectSummaryText = null!, _projectPathText = null!, _baseStatusText = null!, _projectVersionText = null!;
		private TextBox _validationDetailsBox = null!;
		private TextBlock _dockValidationHeadline = null!, _dockValidationDetail = null!;
		private TextBox _logBoxDock = null!;

		//Bottom dock
		private Grid _timelineDockPanel = null!, _captureDockPanel = null!, _historyDockPanel = null!, _validationDockPanel = null!, _conflictDockPanel = null!, _consoleDockPanel = null!, _replacementsDockPanel = null!;
		private TextBlock _conflictSummaryText = null!;
		private ListBox _conflictList = null!;
		private ListBox _replacementList = null!;
		private TextBlock _replacementInfoText = null!;

		//Status bar
		private TextBlock _statusRomText = null!, _statusSaveText = null!, _statusScaleText = null!, _statusAssetCountText = null!, _statusApprovedText = null!, _statusCaptureText = null!, _statusOperationText = null!;

		//Home
		private ItemsControl _recentProjectsList = null!;
		private TextBlock _welcomeStatusText = null!, _homeAssetStat = null!, _homeApprovedStat = null!, _homeVerifiedStat = null!;
		private TextBlock _homeAssetDetail = null!, _homeApprovedDetail = null!, _homeVerifiedDetail = null!;

		//Sheet slicing (inside flyout)
		private TextBox _sheetPathBox = null!;
		private NumericUpDown _sliceColumnsBox = null!, _sliceRowsBox = null!, _sliceFrameWidthBox = null!, _sliceFrameHeightBox = null!, _sliceMarginBox = null!, _sliceSpacingBox = null!;
		private CheckBox _sliceTrimCheck = null!;
		private TextBlock _sliceStatusText = null!;
		private Image _slicePreviewImage = null!;

		public HdStudioWindow() : this("", "") { }

		public HdStudioWindow(string romPath, string packFolder)
		{
			_romPath = romPath ?? "";
			_packFolder = packFolder ?? "";
			_settings = HdStudioSettings.Load();
			RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
			AvaloniaXamlLoader.Load(this);
			BindControls();
			LoadSettings();
			ApplyTheme();
			DragDrop.SetAllowDrop(this, true);
			AddHandler(DragDrop.DropEvent, Window_Drop);
			AddHandler(DragDrop.DragOverEvent, (_, e) => {
				if(e.DataTransfer.TryGetFiles()?.Any() == true) {
					e.DragEffects = DragDropEffects.Copy;
				}
			});
			KeyDown += Window_KeyDown;
			SizeChanged += (_, _) => ApplyResponsiveToolbar();
		}

		private T C<T>(string name) where T : Control => this.GetControl<T>(name);

		private void BindControls()
		{
			_romText = C<TextBlock>("RomText");
			_projectText = C<TextBlock>("ProjectText");
			_workspaceGrid = C<Grid>("WorkspaceGrid");
			_themeScope = C<ThemeVariantScope>("ThemeScope");
			_centerGrid = C<Grid>("CenterGrid");
			_topBar = C<Border>("TopBar");
			_navRail = C<Border>("NavRail");
			_statusBar = C<Border>("StatusBar");
			_assetLibraryPanel = C<Border>("AssetLibraryPanel");
			_inspectorPanel = C<Border>("InspectorPanel");
			_bottomDock = C<Control>("BottomDock");
			_assetSplitter = C<GridSplitter>("AssetSplitter");
			_inspectorSplitter = C<GridSplitter>("InspectorSplitter");

			_homePage = C<Grid>("HomePage");
			_discoveryPage = C<Grid>("DiscoveryPage");
			_spritesPage = C<Grid>("SpritesPage");
			_backgroundsPage = C<Grid>("BackgroundsPage");
			_animationsPage = C<Grid>("AnimationsPage");
			_artworkPage = C<Grid>("ArtworkPage");
			_previewPage = C<Grid>("PreviewPage");
			_exportPage = C<Grid>("ExportPage");
			_settingsPage = C<Grid>("SettingsPage");
			_libraryPage = C<Grid>("LibraryPage");
			_batchPage = C<Grid>("BatchPage");
			_romLibraryList = C<ListBox>("RomLibraryList");
			_batchGameList = C<ListBox>("BatchGameList");

			_assetList = C<ListBox>("AssetList");
			_spriteGalleryList = C<ListBox>("SpriteGalleryList");
			_backgroundList = C<ListBox>("BackgroundList");
			_assetSearchBox = C<TextBox>("AssetSearchBox");
			_statusFilterBox = C<ComboBox>("StatusFilterBox");
			_assetCountText = C<TextBlock>("AssetCountText");
			_assetCountsDetailText = C<TextBlock>("AssetCountsDetailText");
			_thumbnailSizeText = C<TextBlock>("ThumbnailSizeText");
			_thumbnailSizeSlider = C<Slider>("ThumbnailSizeSlider");
			_gridViewButton = C<Button>("GridViewButton");
			_listViewButton = C<Button>("ListViewButton");
			_loadMoreAssetsButton = C<Button>("LoadMoreAssetsButton");

			_inspectorAssetName = C<TextBlock>("InspectorAssetName");
			_inspectorAssetKind = C<TextBlock>("InspectorAssetKind");
			_assetInfoText = C<TextBlock>("AssetInfoText");
			_inspectorStatusBadgeText = C<TextBlock>("InspectorStatusBadgeText");
			_inspectorStatusBadge = C<Border>("InspectorStatusBadge");
			_inspectorNewBadge = C<Border>("InspectorNewBadge");
			_inspectorThumbnail = C<Image>("InspectorThumbnail");
			_logicalSizeText = C<TextBlock>("LogicalSizeText");
			_hdSizeText = C<TextBlock>("HdSizeText");
			_sourceSizeText = C<TextBlock>("SourceSizeText");
			_inspectorScaleText = C<TextBlock>("InspectorScaleText");
			_offsetXBox = C<NumericUpDown>("OffsetXBox");
			_offsetYBox = C<NumericUpDown>("OffsetYBox");
			_zoomBox = C<NumericUpDown>("ZoomBox");
			_anchorBox = C<ComboBox>("AnchorBox");
			_cropCheck = C<CheckBox>("CropCheck");
			_cropInfoText = C<TextBlock>("CropInfoText");
			_removeBackgroundCheck = C<CheckBox>("RemoveBackgroundCheck");
			_toleranceSlider = C<Slider>("ToleranceSlider");
			_toleranceBox = C<NumericUpDown>("ToleranceBox");
			_keepLargestCheck = C<CheckBox>("KeepLargestCheck");
			_checkerToggleCheck = C<CheckBox>("CheckerToggleCheck");
			_allPalettesCheck = C<CheckBox>("AllPalettesCheck");
			_mirrorCheck = C<CheckBox>("MirrorCheck");
			_matchBgPriorityCheck = C<CheckBox>("MatchBgPriorityCheck");
			_extraConditionBox = C<TextBox>("ExtraConditionBox");
			_allowSharedCheck = C<CheckBox>("AllowSharedCheck");
			_paletteList = C<WrapPanel>("PaletteList");
			_paletteCountText = C<TextBlock>("PaletteCountText");
			_sharedKeysText = C<TextBlock>("SharedKeysText");
			_assetIdText = C<TextBlock>("AssetIdText");
			_provenanceText = C<TextBlock>("ProvenanceText");
			_hardwareText = C<TextBlock>("HardwareText");
			_tileListText = C<TextBlock>("TileListText");
			_inspectorStatusText = C<TextBlock>("InspectorStatusText");
			_applyButton = C<Button>("ApplyButton");

			_originalCanvas = C<HdStudioArtworkCanvas>("OriginalCanvas");
			_replacementCanvas = C<HdStudioArtworkCanvas>("ReplacementCanvas");
			_homePreviewCanvas = C<HdStudioArtworkCanvas>("HomePreviewCanvas");
			_sourcePathBox = C<TextBox>("SourcePathBox");
			_workshopTitleText = C<TextBlock>("WorkshopTitleText");
			_workshopSubtitleText = C<TextBlock>("WorkshopSubtitleText");
			_workshopStatusText = C<TextBlock>("WorkshopStatusText");
			_workshopStatusInspector = C<TextBlock>("WorkshopStatusTextInspector");
			_originalSizeBadge = C<TextBlock>("OriginalSizeBadge");
			_replacementSizeBadge = C<TextBlock>("ReplacementSizeBadge");
			_showGridCheck = C<CheckBox>("ShowGridCheck");
			_showCheckerCheck = C<CheckBox>("ShowCheckerCheck");
			_gridToggleButton = C<Button>("GridToggleButton");
			_cropToolButton = C<Button>("CropToolButton");
			_animationList = C<ListBox>("AnimationList");
			_animationTimeline = C<ListBox>("AnimationTimeline");
			_animationPreview = C<Image>("AnimationPreview");
			_animationInfoText = C<TextBlock>("AnimationInfoText");
			_timelineInfoText = C<TextBlock>("TimelineInfoText");
			_dockTimelineInfo = C<TextBlock>("DockTimelineInfo");
			_animationSubtitle = C<TextBlock>("AnimationSubtitle");
			_playAnimationButton = C<Button>("PlayAnimationButton");
			_loopAnimationButton = C<Button>("LoopAnimationButton");

			_backgroundPreview = C<Image>("BackgroundPreview");
			_backgroundInfoText = C<TextBlock>("BackgroundInfoText");
			_backgroundsSubtitle = C<TextBlock>("BackgroundsSubtitle");
			_backgroundGridLabel = C<TextBlock>("BackgroundGridLabel");

			_gameplayCanvas = C<HdStudioGameplayCanvas>("GameplayCanvas");
			_gameplayCandidateList = C<ListBox>("GameplayCandidateList");
			_discoveryStatusText = C<TextBlock>("DiscoveryStatusText");
			_discoveryMetricsText = C<TextBlock>("DiscoveryMetricsText");
			_gameplaySelectionText = C<TextBlock>("GameplaySelectionText");
			_gameplayHintText = C<TextBlock>("GameplayHintText");
			_captureStateText = C<TextBlock>("CaptureStateText");
			_captureStateBadge = C<Border>("CaptureStateBadge");
			_captureSessionText = C<TextBlock>("CaptureSessionText");
			_capturePercentText = C<TextBlock>("CapturePercentText");
			_captureFramesText = C<TextBlock>("CaptureFramesText");
			_captureAssetsText = C<TextBlock>("CaptureAssetsText");
			_capturePatternsText = C<TextBlock>("CapturePatternsText");
			_captureNewText = C<TextBlock>("CaptureNewText");
			_candidateFilterText = C<TextBlock>("CandidateFilterText");
			_candidateHintText = C<TextBlock>("CandidateHintText");
			_discoveryModeBox = C<ComboBox>("DiscoveryModeBox");
			_samplingModeBox = C<ComboBox>("SamplingModeBox");
			_discoveryBudgetBox = C<NumericUpDown>("DiscoveryBudgetBox");
			_discoveryChunkBox = C<NumericUpDown>("DiscoveryChunkBox");
			_discoverySeedBox = C<NumericUpDown>("DiscoverySeedBox");
			_discoveryRestartCheck = C<CheckBox>("DiscoveryRestartCheck");
			_discoveryObserveBgCheck = C<CheckBox>("DiscoveryObserveBgCheck");
			_discoverStartButton = C<Button>("DiscoverStartButton");
			_discoverCancelButton = C<Button>("DiscoverCancelButton");
			_selectObjectButton = C<Button>("SelectObjectButton");
			_discoveryMovieBox = C<TextBox>("DiscoveryMovieBox");
			_discoveryScriptBox = C<TextBox>("DiscoveryScriptBox");
			_discoveryProgress = C<ProgressBar>("DiscoveryProgress");
			_discoverySessionList = C<ListBox>("DiscoverySessionList");
			_dockCaptureProgress = C<ProgressBar>("DockCaptureProgress");
			_dockCaptureStatus = C<TextBlock>("DockCaptureStatus");
			_dockCaptureMode = C<TextBlock>("DockCaptureMode");
			_dockCaptureFrames = C<TextBlock>("DockCaptureFrames");
			_dockCaptureChunks = C<TextBlock>("DockCaptureChunks");
			_dockCaptureReason = C<TextBlock>("DockCaptureReason");
			_recordButton = C<Button>("RecordButton");
			_pauseCaptureButton = C<Button>("PauseCaptureButton");
			_stopCaptureButton = C<Button>("StopCaptureButton");

			_verifyOriginalImage = C<Image>("VerifyOriginalImage");
			_verifyHdImage = C<Image>("VerifyHdImage");
			_previewStatusText = C<TextBlock>("PreviewStatusText");
			_verifySummaryText = C<TextBlock>("VerifySummaryText");

			_exportRootBox = C<TextBox>("ExportRootBox");
			_exportNameBox = C<TextBox>("ExportNameBox");
			_exportDestinationText = C<TextBlock>("ExportDestinationText");
			_exportDestinationHint = C<TextBlock>("ExportDestinationHint");
			_exportProjectFolderText = C<TextBlock>("ExportProjectFolderText");
			_exportCurrentPackText = C<TextBlock>("ExportCurrentPackText");
			_exportResultText = C<TextBlock>("ExportResultText");
			_exportSummaryPackName = C<TextBlock>("ExportSummaryPackName");
			_exportSummaryApproved = C<TextBlock>("ExportSummaryApproved");
			_exportSummaryScale = C<TextBlock>("ExportSummaryScale");
			_exportSummaryValidation = C<TextBlock>("ExportSummaryValidation");
			_exportSummaryFiles = C<TextBlock>("ExportSummaryFiles");
			_exportSummaryDestination = C<TextBlock>("ExportSummaryDestination");
			_exportMinimalCheck = C<CheckBox>("ExportMinimalCheck");
			_exportInstallCheck = C<CheckBox>("ExportInstallCheck");
			_exportOverwriteCheck = C<CheckBox>("ExportOverwriteCheck");
			_exportBackupCheck = C<CheckBox>("ExportBackupCheck");
			_exportRunButton = C<Button>("ExportRunButton");

			_themeBox = C<ComboBox>("ThemeBox");
			_defaultGridCheck = C<CheckBox>("DefaultGridCheck");
			_defaultCheckerCheck = C<CheckBox>("DefaultCheckerCheck");
			_scaleBox = C<NumericUpDown>("ScaleBox");
			_framesBox = C<NumericUpDown>("FramesBox");
			_buildProgress = C<ProgressBar>("BuildProgress");
			_logBox = C<TextBox>("LogBox");
			_validationText = C<TextBlock>("ValidationText");
			_projectSummaryText = C<TextBlock>("ProjectSummaryText");
			_projectPathText = C<TextBlock>("ProjectPathText");
			_baseStatusText = C<TextBlock>("BaseStatusText");
			_projectVersionText = C<TextBlock>("ProjectVersionText");
			_validationDetailsBox = C<TextBox>("ValidationDetailsBox");
			_dockValidationHeadline = C<TextBlock>("DockValidationHeadline");
			_dockValidationDetail = C<TextBlock>("DockValidationDetail");
			_logBoxDock = _logBox;

			_statusRomText = C<TextBlock>("StatusRomText");
			_statusSaveText = C<TextBlock>("StatusSaveText");
			_statusScaleText = C<TextBlock>("StatusScaleText");
			_statusAssetCountText = C<TextBlock>("StatusAssetCountText");
			_statusApprovedText = C<TextBlock>("StatusApprovedText");
			_statusCaptureText = C<TextBlock>("StatusCaptureText");
			_statusOperationText = C<TextBlock>("StatusOperationText");

			_recentProjectsList = C<ItemsControl>("RecentProjectsList");
			_welcomeStatusText = C<TextBlock>("WelcomeStatusText");
			_homeAssetStat = C<TextBlock>("HomeAssetStat");
			_homeApprovedStat = C<TextBlock>("HomeApprovedStat");
			_homeVerifiedStat = C<TextBlock>("HomeVerifiedStat");
			_homeAssetDetail = C<TextBlock>("HomeAssetDetail");
			_homeApprovedDetail = C<TextBlock>("HomeApprovedDetail");
			_homeVerifiedDetail = C<TextBlock>("HomeVerifiedDetail");

			_sheetPathBox = C<TextBox>("SheetPathBox");
			_sliceColumnsBox = C<NumericUpDown>("SliceColumnsBox");
			_sliceRowsBox = C<NumericUpDown>("SliceRowsBox");
			_sliceFrameWidthBox = C<NumericUpDown>("SliceFrameWidthBox");
			_sliceFrameHeightBox = C<NumericUpDown>("SliceFrameHeightBox");
			_sliceMarginBox = C<NumericUpDown>("SliceMarginBox");
			_sliceSpacingBox = C<NumericUpDown>("SliceSpacingBox");
			_sliceTrimCheck = C<CheckBox>("SliceTrimCheck");
			_sliceStatusText = C<TextBlock>("SliceStatusText");
			_slicePreviewImage = C<Image>("SlicePreviewImage");

			_timelineDockPanel = C<Grid>("TimelineDockPanel");
			_captureDockPanel = C<Grid>("CaptureDockPanel");
			_historyDockPanel = C<Grid>("HistoryDockPanel");
			_validationDockPanel = C<Grid>("ValidationDockPanel");
			_conflictDockPanel = C<Grid>("ConflictDockPanel");
			_consoleDockPanel = C<Grid>("ConsoleDockPanel");
			_replacementsDockPanel = C<Grid>("ReplacementsDockPanel");
			_conflictSummaryText = C<TextBlock>("ConflictSummaryText");
			_conflictList = C<ListBox>("ConflictList");
			_replacementList = C<ListBox>("ReplacementList");
			_replacementInfoText = C<TextBlock>("ReplacementInfoText");

			//Live editor wiring. The canvas mutates the same settings object; every control
			//change is mirrored to it and marks the project as changed.
			_replacementCanvas.SettingsChanged += (_, _) => SyncEditorControlsFromSettings();
			_originalCanvas.ViewZoomChanged += (_, _) => { };
			_replacementCanvas.ViewZoomChanged += (_, _) => { };
			_sourcePathBox.TextChanged += (_, _) => {
				if(_syncingEditor) return;
				_editSourcePath = _sourcePathBox.Text?.Trim() ?? "";
				RefreshEditorCanvases();
				MarkDirty();
			};
			_offsetXBox.ValueChanged += (_, _) => EditorControlChanged();
			_offsetYBox.ValueChanged += (_, _) => EditorControlChanged();
			_zoomBox.ValueChanged += (_, _) => EditorControlChanged();
			_anchorBox.SelectionChanged += (_, _) => EditorControlChanged();
			_cropCheck.Click += (_, _) => EditorControlChanged();
			_removeBackgroundCheck.Click += (_, _) => EditorControlChanged();
			_keepLargestCheck.Click += (_, _) => EditorControlChanged();
			_allPalettesCheck.Click += (_, _) => EditorControlChanged();
			_mirrorCheck.Click += (_, _) => EditorControlChanged();
			_matchBgPriorityCheck.Click += (_, _) => EditorControlChanged();
			_extraConditionBox.TextChanged += (_, _) => {
				if(_syncingEditor) return;
				_editSettings.ExtraCondition = _extraConditionBox.Text?.Trim() ?? "";
				List<string> errors = new();
				HdStudioArtwork.GetCustomConditionDefinitions(_editSettings.ExtraCondition, errors);
				_sharedKeysText.Text = errors.Count > 0
					? "Condition error: " + errors[0]
					: string.IsNullOrWhiteSpace(_editSettings.ExtraCondition)
						? "No extra condition; shared graphics require explicit approval."
						: "Advanced condition will be emitted as a standard Mesen <condition>.";
				MarkDirty();
			};
			_allowSharedCheck.Click += (_, _) => EditorControlChanged();
			_toleranceBox.ValueChanged += (_, _) => {
				if(_syncingEditor) return;
				_syncingEditor = true;
				_toleranceSlider.Value = (double)(_toleranceBox.Value ?? 40);
				_syncingEditor = false;
				EditorControlChanged();
			};
			_scaleBox.ValueChanged += (_, _) => {
				if(_syncingEditor) return;
				if(_project != null) {
					_project.OutputScale = Math.Clamp(IntValue(_scaleBox, 4), 1, 10);
				}
				RefreshEditorCanvases();
				UpdateStatusBar();
				MarkDirty();
			};
			_gameplayCanvas.RegionSelected += (_, _) => UpdateGameplayCandidates();
			_gameplayCanvas.PointPicked += (_, point) => HandleGameplayPointPicked(point);
			_animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
			_animationTimer.Tick += (_, _) => AdvanceAnimation(true);
		}

		private void LoadSettings()
		{
			try {
				_settings = HdStudioSettings.Load();
				_exportMinimalCheck.IsChecked = _settings.LastExportMinimal;
				_exportInstallCheck.IsChecked = _settings.LastExportInstall;
				_exportBackupCheck.IsChecked = _settings.LastExportBackup;
				_lastExportFolder = _settings.LastExportFolder;
				_bottomOpen = _settings.BottomPanelOpen;
				_thumbnailSizeSlider.Value = _settings.AssetThumbnailSize;
				_thumbnailSizeText.Text = ((int)_settings.AssetThumbnailSize).ToString();
				_exportRootBox.Text = string.IsNullOrWhiteSpace(_settings.ExportRoot) ? HdStudioExport.GetDefaultExportRoot() : _settings.ExportRoot;
				_defaultGridCheck.IsChecked = _settings.ShowTileGrid;
				_defaultCheckerCheck.IsChecked = _settings.ShowCheckerboard;
				ShowAssetView(_settings.AssetGridView);
				SetEditorGridDefaults(_settings.ShowTileGrid, _settings.ShowCheckerboard);
				_themeBox.SelectedIndex = string.Equals(_settings.Theme, "light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
				RefreshRecentProjects();
				UpdateExportPaths();
			} catch(Exception ex) {
				Log("Settings: " + ex.Message);
			}
		}

		private void ApplyTheme()
		{
			//v0.6 Alpha ships the validated dark appearance. The palette is centralized so a
			//light variant can be added without touching individual pages.
			bool light = false;
			_settings.Theme = "dark";
			RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
			_themeScope.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
			ApplyStudioPalette(light);
			Background = new SolidColorBrush(Color.Parse(light ? "#F3F5F8" : "#181B20"));
			Foreground = new SolidColorBrush(Color.Parse(light ? "#1B2430" : "#F0F2F5"));
			Classes.Set("light", light);
			_themeScope.Classes.Set("light", light);
			_topBar.Background = new SolidColorBrush(Color.Parse(light ? "#FFFFFF" : "#1E2228"));
			_topBar.BorderBrush = new SolidColorBrush(Color.Parse(light ? "#C9D2DC" : "#3B424C"));
			_navRail.Background = new SolidColorBrush(Color.Parse(light ? "#E9EDF2" : "#1D2127"));
			_statusBar.Background = new SolidColorBrush(Color.Parse(light ? "#E9EDF2" : "#14171B"));
			_assetLibraryPanel.Background = new SolidColorBrush(Color.Parse(light ? "#FFFFFF" : "#22262D"));
			_inspectorPanel.Background = new SolidColorBrush(Color.Parse(light ? "#FFFFFF" : "#22262D"));
			_originalCanvas.LightTheme = light;
			_replacementCanvas.LightTheme = light;
			_homePreviewCanvas.LightTheme = light;
			_gameplayCanvas.LightTheme = light;
			ApplyResponsiveToolbar();
		}

		private void ApplyStudioPalette(bool light)
		{
			//The Studio uses a ThemeVariantScope above the whole editor. Replacing resources on
			//that scope updates every DynamicResource in the already-created visual tree, which
			//is what makes the Light menu item change panels, cards and Fluent controls at once.
			(Dictionary<string, string> colors, Dictionary<string, string> brushes) = light ? (LightPalette, LightBrushes) : (DarkPalette, DarkBrushes);
			foreach(KeyValuePair<string, string> pair in brushes) {
				_themeScope.Resources[pair.Key] = new SolidColorBrush(Color.Parse(pair.Value));
			}
			foreach(KeyValuePair<string, string> pair in colors) {
				_themeScope.Resources[pair.Key] = Color.Parse(pair.Value);
			}
			_themeScope.Resources["SystemAccentColor"] = Color.Parse(light ? "#D97726" : "#F28C38");
			_themeScope.Resources["SystemControlHighlightAccentBrush"] = new SolidColorBrush(Color.Parse(light ? "#D97726" : "#F28C38"));
			_themeScope.Resources["SystemControlBackgroundAccentBrush"] = new SolidColorBrush(Color.Parse(light ? "#D97726" : "#F28C38"));
			_themeScope.Resources["SystemControlForegroundAccentBrush"] = new SolidColorBrush(Color.Parse(light ? "#D97726" : "#F28C38"));
			_themeScope.Resources["CheckBoxCheckBackgroundFillChecked"] = new SolidColorBrush(Color.Parse(light ? "#D97726" : "#F28C38"));
			_themeScope.Resources["CheckBoxCheckBackgroundStrokeChecked"] = new SolidColorBrush(Color.Parse(light ? "#D97726" : "#F28C38"));
			_themeScope.Resources["SliderThumbBackground"] = new SolidColorBrush(Color.Parse(light ? "#D97726" : "#F28C38"));
			_themeScope.Resources["SliderTrackValueFill"] = new SolidColorBrush(Color.Parse(light ? "#D97726" : "#F28C38"));
			_themeScope.Resources["AccentButtonBackground"] = new SolidColorBrush(Color.Parse(light ? "#D97726" : "#F28C38"));
		}

		private static readonly Dictionary<string, string> DarkBrushes = new() {
			["StudioBgBrush"] = "#181B20", ["StudioPanelBrush"] = "#22262D", ["StudioPanelAltBrush"] = "#1D2127",
			["StudioElevatedBrush"] = "#2D333C", ["StudioInputBrush"] = "#1B1F25", ["StudioBorderBrush"] = "#3B424C",
			["StudioBorderStrongBrush"] = "#4B545F", ["StudioTextBrush"] = "#F0F2F5", ["StudioTextSecondaryBrush"] = "#A5AFBD",
			["StudioTextTertiaryBrush"] = "#748091", ["StudioAccentBrush"] = "#F28C38", ["StudioAccentHoverBrush"] = "#FFA04F",
			["StudioAccentPressedBrush"] = "#D97726", ["StudioAccentSoftBrush"] = "#33F28C38", ["StudioSuccessBrush"] = "#45B883",
			["StudioWarningBrush"] = "#E9B44C", ["StudioErrorBrush"] = "#E16464", ["StudioInfoBrush"] = "#5796DF",
			["StudioCanvasBrush"] = "#14171B", ["StudioHoverBrush"] = "#343B45", ["StudioSelectionBrush"] = "#2C3744",
			["StudioToolbarBrush"] = "#1E2228", ["StudioStatusBrush"] = "#14171B", ["StudioOverlayBrush"] = "#CC12151A"
		};

		private static readonly Dictionary<string, string> LightBrushes = new() {
			["StudioBgBrush"] = "#F3F5F8", ["StudioPanelBrush"] = "#FFFFFF", ["StudioPanelAltBrush"] = "#E9EDF2",
			["StudioElevatedBrush"] = "#E1E7EE", ["StudioInputBrush"] = "#FFFFFF", ["StudioBorderBrush"] = "#C9D2DC",
			["StudioBorderStrongBrush"] = "#AAB6C3", ["StudioTextBrush"] = "#1B2430", ["StudioTextSecondaryBrush"] = "#556273",
			["StudioTextTertiaryBrush"] = "#788697", ["StudioAccentBrush"] = "#D97726", ["StudioAccentHoverBrush"] = "#C96818",
			["StudioAccentPressedBrush"] = "#B85C10", ["StudioAccentSoftBrush"] = "#22D97726", ["StudioSuccessBrush"] = "#248A5C",
			["StudioWarningBrush"] = "#A87400", ["StudioErrorBrush"] = "#C33D3D", ["StudioInfoBrush"] = "#2E6DB4",
			["StudioCanvasBrush"] = "#DDE3EA", ["StudioHoverBrush"] = "#D7DFE8", ["StudioSelectionBrush"] = "#CFE1F3",
			["StudioToolbarBrush"] = "#FFFFFF", ["StudioStatusBrush"] = "#E9EDF2", ["StudioOverlayBrush"] = "#DDF3F5F8"
		};

		private static readonly Dictionary<string, string> DarkPalette = new() {
			["StudioBgColor"] = "#181B20", ["StudioPanelColor"] = "#22262D", ["StudioPanelAltColor"] = "#1D2127",
			["StudioElevatedColor"] = "#2D333C", ["StudioInputColor"] = "#1B1F25", ["StudioBorderColor"] = "#3B424C",
			["StudioBorderStrongColor"] = "#4B545F", ["StudioTextColor"] = "#F0F2F5", ["StudioTextSecondaryColor"] = "#A5AFBD",
			["StudioTextTertiaryColor"] = "#748091", ["StudioAccentColor"] = "#F28C38", ["StudioAccentHoverColor"] = "#FFA04F",
			["StudioAccentPressedColor"] = "#D97726", ["StudioAccentSoftColor"] = "#33F28C38", ["StudioSuccessColor"] = "#45B883",
			["StudioWarningColor"] = "#E9B44C", ["StudioErrorColor"] = "#E16464", ["StudioInfoColor"] = "#5796DF",
			["StudioCanvasColor"] = "#14171B", ["StudioHoverColor"] = "#343B45", ["StudioSelectionColor"] = "#2C3744",
			["StudioToolbarColor"] = "#1E2228", ["StudioStatusColor"] = "#14171B", ["StudioOverlayColor"] = "#CC12151A"
		};

		private static readonly Dictionary<string, string> LightPalette = new() {
			["StudioBgColor"] = "#F3F5F8", ["StudioPanelColor"] = "#FFFFFF", ["StudioPanelAltColor"] = "#E9EDF2",
			["StudioElevatedColor"] = "#E1E7EE", ["StudioInputColor"] = "#FFFFFF", ["StudioBorderColor"] = "#C9D2DC",
			["StudioBorderStrongColor"] = "#AAB6C3", ["StudioTextColor"] = "#1B2430", ["StudioTextSecondaryColor"] = "#556273",
			["StudioTextTertiaryColor"] = "#788697", ["StudioAccentColor"] = "#D97726", ["StudioAccentHoverColor"] = "#C96818",
			["StudioAccentPressedColor"] = "#B85C10", ["StudioAccentSoftColor"] = "#22D97726", ["StudioSuccessColor"] = "#248A5C",
			["StudioWarningColor"] = "#A87400", ["StudioErrorColor"] = "#C33D3D", ["StudioInfoColor"] = "#2E6DB4",
			["StudioCanvasColor"] = "#DDE3EA", ["StudioHoverColor"] = "#D7DFE8", ["StudioSelectionColor"] = "#CFE1F3",
			["StudioToolbarColor"] = "#FFFFFF", ["StudioStatusColor"] = "#E9EDF2", ["StudioOverlayColor"] = "#DDF3F5F8"
		};

		private void SetEditorGridDefaults(bool grid, bool checker)
		{
			_showGridCheck.IsChecked = grid;
			_showCheckerCheck.IsChecked = checker;
			_checkerToggleCheck.IsChecked = checker;
			_originalCanvas.ShowTileGrid = grid;
			_replacementCanvas.ShowTileGrid = grid;
			_originalCanvas.ShowCheckerboard = checker;
			_replacementCanvas.ShowCheckerboard = checker;
			_gridToggleButton.Classes.Set("active", grid);
		}

		protected override void OnOpened(EventArgs e)
		{
			base.OnOpened(e);
			if(_initialized) return;
			_initialized = true;
			try {
				StudioWindowTheme.Apply(this, !_settings.Theme.Equals("light", StringComparison.OrdinalIgnoreCase));
				InitializeStudio();
				ApplyLaunchOptions();
				ApplyAccessibleNames();
			} catch(Exception ex) {
				SetStatus("The Studio could not start: " + ex.Message);
				Log("ERROR: " + ex);
			}
		}

		protected override void OnClosed(EventArgs e)
		{
			_animationTimer?.Stop();
			_discoveryCancellation?.Cancel();
			foreach(Bitmap bitmap in _bitmapCache.Values) {
				bitmap.Dispose();
			}
			_bitmapCache.Clear();
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
				ShowWorkspace("home");
				SetStatus("Open a NES game to begin.");
				UpdateHomeStats();
				return;
			}
			if(string.IsNullOrWhiteSpace(_packFolder)) {
				_packFolder = Path.Combine(ConfigManager.HdPackFolder, Path.GetFileNameWithoutExtension(_romPath));
			}
			_palette = ConfigManager.Config.Nes.UserPalette;
			_romText.Text = Path.GetFileName(_romPath);
			_projectText.Text = Path.GetFileNameWithoutExtension(_romPath);
			Title = "RetroArcDo HD Studio — " + Path.GetFileNameWithoutExtension(_romPath);
			RefreshFromDisk();
			ShowWorkspace(_catalog.Assets.Count > 0 ? "artwork" : "discover");
			Log("Studio opened for " + _romPath);
			Log("Pack folder: " + _packFolder);
		}

		private void ApplyLaunchOptions()
		{
			string[] args = Program.CommandLineArgs;
			string? Option(string name)
			{
				foreach(string arg in args) {
					if(arg.StartsWith("--" + name + "=", StringComparison.OrdinalIgnoreCase)) {
						return arg.Substring(name.Length + 3);
					}
				}
				return null;
			}

			string? workspace = Option("workspace");
			string? assetQuery = Option("asset");
			if(!string.IsNullOrWhiteSpace(assetQuery)) {
				HdStudioCatalogAsset? asset = _catalog.Assets.FirstOrDefault(a =>
					string.Equals(a.Id, assetQuery, StringComparison.OrdinalIgnoreCase)) ??
					_catalog.Assets.FirstOrDefault(a => a.Name.Contains(assetQuery, StringComparison.OrdinalIgnoreCase));
				if(asset != null) {
					_suppressFilterRefresh = true;
					_assetSearchBox.Text = asset.Name;
					_suppressFilterRefresh = false;
					_assetLimit = 160;
					_selectedAsset = asset;
					RefreshAssetRows();
					SelectAsset(asset);
					if(args.Contains("--clear-list-selection")) {
						_syncingSelection = true;
						_assetList.SelectedItem = null;
						_syncingSelection = false;
						Focus();
					}
				}
			}

			string? captureImage = Option("capture-image");
			if(!string.IsNullOrWhiteSpace(captureImage) && File.Exists(captureImage)) {
				if(HdStudioPicking.PauseFrame.Load(_packFolder) != null) {
					LoadLivePauseFrame();
					SetCaptureState("LIVE-PAUSED");
					UpdateLiveCaptureStatusFromDisk();
				} else {
					_gameplayCanvas.FramePath = captureImage;
					_gameplayCanvas.Reload();
					SetCaptureState("PAUSED");
				}
			}
			string? region = Option("select-region");
			if(!string.IsNullOrWhiteSpace(region)) {
				string[] p = region.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
				if(p.Length == 4 && int.TryParse(p[0], out int rx) && int.TryParse(p[1], out int ry) &&
					int.TryParse(p[2], out int rw) && int.TryParse(p[3], out int rh)) {
					_gameplayCanvas.SelectMode = true;
					_selectObjectButton.Classes.Set("active", true);
					DispatcherTimer selectionTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
					int attempts = 0;
					selectionTimer.Tick += (_, _) => {
						attempts++;
						if(_gameplayCanvas.Bounds.Width > 100 || attempts > 10) {
							selectionTimer.Stop();
							_gameplayCanvas.SetSelectionNes(rx, ry, rw, rh);
						}
					};
					selectionTimer.Start();
				}
			}
			if(args.Contains("--open-destination")) {
				Dispatcher.UIThread.Post(() => BrowseExportRoot_Click(null, new RoutedEventArgs()));
			}
			if(args.Contains("--run-export")) {
				DispatcherTimer exportTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
				exportTimer.Tick += (_, _) => {
					exportTimer.Stop();
					_ = RunExportAsync(zip: false);
				};
				exportTimer.Start();
			}
			if(args.Contains("--run-export-zip")) {
				DispatcherTimer exportTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
				exportTimer.Tick += (_, _) => {
					exportTimer.Stop();
					_ = RunExportAsync(zip: true);
				};
				exportTimer.Start();
			}
			string? renderShot = Option("render-shot");
			string? exportTo = Option("export-to");
			if(!string.IsNullOrWhiteSpace(exportTo)) {
				_exactExportDestination = exportTo;
				_exportRootBox.Text = Path.GetDirectoryName(exportTo) ?? exportTo;
				_exportNameBox.Text = Path.GetFileName(exportTo);
				UpdateExportPaths();
			}
			if(!string.IsNullOrWhiteSpace(renderShot)) {
				//Keep a strong reference: a local DispatcherTimer can be collected before
				//its first tick, which made the offscreen screenshot nondeterministic.
				DispatcherTimer shotTimer = new() { Interval = TimeSpan.FromMilliseconds(3200) };
				_renderShotTimer = shotTimer;
				shotTimer.Tick += (_, _) => {
					shotTimer.Stop();
					try {
						int shotWidth = (int)Math.Round(ClientSize.Width > 100 ? ClientSize.Width : Width);
						int shotHeight = (int)Math.Round(ClientSize.Height > 100 ? ClientSize.Height : Height);
						RenderTargetBitmap bitmap = new(new PixelSize(shotWidth, shotHeight), new Vector(96, 96));
						bitmap.Render(_themeScope);
						Directory.CreateDirectory(Path.GetDirectoryName(renderShot) ?? ".");
						bitmap.Save(renderShot, PngBitmapEncoderOptions.Default);
						Log("Render screenshot: " + renderShot + " (" + shotWidth + "x" + shotHeight + ")");
					} catch(Exception ex) {
						File.WriteAllText(renderShot + ".log", ex.ToString());
						Log("Render screenshot failed: " + ex.Message);
					}
				};
				shotTimer.Start();
			}

			if(args.Contains("--select-object")) {
				SelectObject_Click(null, new RoutedEventArgs());
			}
			if(args.Contains("--conflict")) {
				_ = ShowSelectedConflictAsync();
			}

			if(!string.IsNullOrWhiteSpace(workspace)) {
				ShowWorkspace(workspace);
			}
			string? size = Option("size");
			if(!string.IsNullOrWhiteSpace(size)) {
				string[] parts = size.Split('x', 'X', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
				if(parts.Length == 2 && double.TryParse(parts[0], out double width) && double.TryParse(parts[1], out double height)) {
					Width = Math.Max(1180, width);
					Height = Math.Max(720, height);
				}
			}
			_dirty = false;
			UpdateStatusBar();
			ApplyResponsiveToolbar();
			DispatcherTimer cleanTimer = new() { Interval = TimeSpan.FromMilliseconds(1100) };
			cleanTimer.Tick += (_, _) => {
				cleanTimer.Stop();
				if(!_busy) {
					_dirty = false;
					UpdateStatusBar();
				}
			};
			cleanTimer.Start();
		}

		private void RefreshFromDisk()
		{
			HashSet<string> previousIds = new(_allAssets.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
			ReloadProject();
			_catalog = HdStudioCatalog.Load(_packFolder);
			_catalog.RomPath = _romPath;
			_allAssets.Clear();
			_allAssets.AddRange(_catalog.Assets);
			_newAssetIds.Clear();
			if(previousIds.Count > 0) {
				foreach(HdStudioCatalogAsset asset in _catalog.Assets) {
					if(!previousIds.Contains(asset.Id)) {
						_newAssetIds.Add(asset.Id);
					}
				}
			}
			_sharedRiskCache.Clear();
			RefreshAssetRows();
			RefreshSpriteGallery();
			RefreshBackgroundRows();
			RefreshReplacements();
			RefreshAnimationList();
			RefreshProjectSummary();
			RefreshRecentProjects();
			UpdateExportPaths();
			UpdateHomeStats();
			LoadLastVerifyImages();
			LoadDiscoverySession();
			UpdateStatusBar();

			if(_catalog.Assets.Count == 0) {
				SetStatus("No semantic asset catalog yet. Run Discovery to build one.");
			} else {
				SetStatus($"{_catalog.Assets.Count:N0} assets loaded.");
			}
		}

		private void ReloadProject()
		{
			string projectPath = HdStudioProject.GetDefaultPath(_packFolder);
			try {
				if(File.Exists(projectPath)) {
					_project = HdStudioProject.Load(projectPath);
				} else if(File.Exists(_romPath)) {
					_project = HdStudioProject.Create(_romPath, _packFolder, 4, Log);
					_project.Save(projectPath);
				} else {
					_project = null;
				}
				if(_project != null) {
					_project.ImportLegacyManifests(Log);
					_syncingEditor = true;
					_scaleBox.Value = Math.Clamp(_project.OutputScale, 1, 10);
					_syncingEditor = false;
					_projectPathText.Text = "Project file: " + projectPath;
					_projectText.Text = _project.ProjectName + "  ·  " + _project.ChrMode;
					_settings.RememberProject(projectPath);
					_settings.Save();
					_newAssetIds.UnionWith(_project.Replacements.Where(r => !r.Approved).Select(r => r.AssetId));
				}
				_dirty = false;
			} catch(Exception ex) {
				_project = null;
				SetStatus("Project load failed: " + ex.Message);
				Log("ERROR: " + ex);
			}
		}

		private void RefreshProjectSummary()
		{
			if(_project == null) {
				_projectSummaryText.Text = "No project loaded.";
				_baseStatusText.Text = "";
				UpdateStatusBar();
				return;
			}
			string basePath = Path.Combine(_project.ProjectFolder, "base-hires.txt");
			_baseStatusText.Text = File.Exists(basePath)
				? "Base snapshot: " + basePath
				: "Base snapshot is created by the first build.";
			int approved = _project.Replacements.Count(r => r.Approved);
			int drafts = _project.Replacements.Count - approved;
			_projectSummaryText.Text =
				$"ROM SHA1: {_project.RomSha1}\nMapper: {_project.Mapper} · {_project.ChrMode} · {_project.ChrBytes:N0} bytes\n" +
				$"Scale: {_project.OutputScale}× · Replacements: {approved} approved, {drafts} draft\n" +
				$"Project folder: {_project.ProjectFolder}";
			_projectVersionText.Text = $"RetroArcDo HD Studio v{Version} · Original NES geometry preserved.";
			UpdateStatusBar();
		}

		private void ReplacementList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			int index = _replacementList.SelectedIndex;
			if(index < 0 || index >= _replacementRows.Count) return;
			_selectedReplacement = _replacementRows[index];
			_replacementInfoText.Text =
				$"{_selectedReplacement.AssetName}  ·  {( _selectedReplacement.Approved ? "approved" : "draft")}\n" +
				$"Source: {Path.GetFileName(_selectedReplacement.SourceImage)}\n" +
				$"Target: {_selectedReplacement.TargetWidth}×{_selectedReplacement.TargetHeight} px at {_selectedReplacement.PackScale}×  ·  {_selectedReplacement.Cells.Count} cells" +
				(string.IsNullOrWhiteSpace(_selectedReplacement.Sheet) ? "" : "\nSheet: " + _selectedReplacement.Sheet);
		}

		private void ReplacementList_DoubleTapped(object? sender, TappedEventArgs e)
		{
			if(_selectedReplacement == null) return;
			HdStudioCatalogAsset? asset = _catalog.Assets.FirstOrDefault(a => string.Equals(a.Id, _selectedReplacement.AssetId, StringComparison.OrdinalIgnoreCase));
			if(asset != null) {
				SelectAsset(asset);
				ShowWorkspace("artwork");
			}
		}

		private void ApproveReplacement_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null || _selectedReplacement == null) return;
			PushUndo();
			_selectedReplacement.Approval = "approved";
			_selectedReplacement.LegacyManifest = "";
			_selectedReplacement.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
			_project.Save(HdStudioProject.GetDefaultPath(_packFolder));
			RefreshAssetRows();
			RefreshSpriteGallery();
			RefreshReplacements();
			SetStatus("Replacement approved. Build to export it.");
		}

		private void UnapproveReplacement_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null || _selectedReplacement == null) return;
			PushUndo();
			_selectedReplacement.Approval = "draft";
			_selectedReplacement.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
			_project.Save(HdStudioProject.GetDefaultPath(_packFolder));
			RefreshAssetRows();
			RefreshSpriteGallery();
			RefreshReplacements();
			SetStatus("Replacement moved to draft. It is no longer exported.");
		}

		private async void ApplyReplacement_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null || _selectedReplacement == null) return;
			PushUndo();
			_selectedReplacement.Approval = "approved";
			_selectedReplacement.LegacyManifest = "";
			_selectedReplacement.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
			_project.Save(HdStudioProject.GetDefaultPath(_packFolder));
			await BuildAsync("Applying the selected replacement...");
		}

		private async void RemoveReplacement_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null || _selectedReplacement == null) return;
			HdStudioReplacement removed = _selectedReplacement;
			DialogResult confirm = await MessageBox.Show(this,
				$"Remove the replacement for “{removed.AssetName}”? The original captured pack is kept; this only removes the authored replacement.",
				"Remove replacement", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
			if(confirm != DialogResult.Yes) return;
			PushUndo();
			_project.Replacements.Remove(removed);
			_project.Save(HdStudioProject.GetDefaultPath(_packFolder));
			RefreshAssetRows();
			RefreshSpriteGallery();
			RefreshReplacements();
			Log("Removed replacement " + removed.AssetName + ".");
			await BuildAsync("Rebuilding the pack without the removed replacement...");
		}

		private void RefreshRecentProjects()
		{
			List<HdStudioRecentRow> rows = new();
			foreach(string projectPath in _settings.RecentProjects) {
				if(!File.Exists(projectPath)) continue;
				string name = Path.GetFileName(Path.GetDirectoryName(projectPath)) ?? "Project";
				string game = "";
				try {
					HdStudioProject p = HdStudioProject.Load(projectPath);
					name = string.IsNullOrWhiteSpace(p.ProjectName) ? name : p.ProjectName;
					game = Path.GetFileName(p.RomPath);
				} catch { }
				rows.Add(new HdStudioRecentRow() {
					Path = projectPath,
					Name = name,
					Game = string.IsNullOrWhiteSpace(game) ? "NES project" : game,
					When = "Last edited " + File.GetLastWriteTime(projectPath).ToString("MMM d, HH:mm", CultureInfo.InvariantCulture)
				});
			}
			_recentProjectsList.ItemsSource = rows;
			_welcomeStatusText.Text = rows.Count == 0
				? "No recent project yet. Open a NES game to create one."
				: "Double-click a recent card to continue where you left off.";
		}

		private void UpdateHomeStats()
		{
			int approved = _project?.Replacements.Count(r => r.Approved) ?? 0;
			int total = _catalog.Assets.Count;
			bool verified = Directory.Exists(Path.Combine(_packFolder, HdStudioReport.StudioFolderName, "verify")) &&
				Directory.EnumerateFiles(Path.Combine(_packFolder, HdStudioReport.StudioFolderName, "verify"), "frame_*_hd.png").Any();
			_homeAssetStat.Text = total.ToString("N0");
			_homeApprovedStat.Text = approved.ToString("N0");
			_homeVerifiedStat.Text = verified ? "Verified" : (approved > 0 ? "Ready" : "Not verified");
			_homeAssetDetail.Text = total == 0 ? "Open a game to build the catalog." : $"{_catalog.Assets.Count(a => a.Tiles.Count > 0):N0} with replaceable tiles";
			_homeApprovedDetail.Text = _project == null ? "No project loaded." : $"{_project.Replacements.Count - approved:N0} drafts stay local";
			_homeVerifiedDetail.Text = verified ? "Last real-emulator check passed." : "Verify compares gameplay state.";
			if(_selectedAsset != null && string.IsNullOrWhiteSpace(_homePreviewCanvas.OriginalImagePath)) {
				_homePreviewCanvas.Mode = HdStudioArtworkCanvas.CanvasMode.Original;
				_homePreviewCanvas.OriginalImagePath = HdStudioCatalog.EnsureOriginalPreview(_catalog, _selectedAsset, _palette);
			}
			if(string.IsNullOrWhiteSpace(_homePreviewCanvas.SourceImagePath) && string.IsNullOrWhiteSpace(_homePreviewCanvas.OriginalImagePath)) {
				UpdateHomeHeroFromRecent();
			}
		}

		private void UpdateHomeHeroFromRecent()
		{
			foreach(string projectPath in _settings.RecentProjects) {
				if(!File.Exists(projectPath)) continue;
				try {
					HdStudioProject project = HdStudioProject.Load(projectPath);
					HdStudioReplacement? replacement = project.Replacements
						.OrderByDescending(r => r.Approved)
						.FirstOrDefault(r => File.Exists(project.ResolvePath(r.SourceImage)));
					if(replacement == null) continue;
					_homePreviewCanvas.Mode = HdStudioArtworkCanvas.CanvasMode.Replacement;
					_homePreviewCanvas.SourceImagePath = project.ResolvePath(replacement.SourceImage);
					_homePreviewCanvas.Settings = replacement.Settings;
					_homePreviewCanvas.LogicalWidth = Math.Max(8, replacement.OriginalWidth);
					_homePreviewCanvas.LogicalHeight = Math.Max(8, replacement.OriginalHeight);
					_homePreviewCanvas.OutputScale = 4;
					_homePreviewCanvas.InvalidateSettings();
					return;
				} catch {
					//The hero preview is decorative; a missing recent project must not block
					//the welcome screen.
				}
			}
		}

		private void ApplyAccessibleNames()
		{
			foreach(Button button in this.GetLogicalDescendants().OfType<Button>()) {
				if(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))) {
					continue;
				}
				string? tip = ToolTip.GetTip(button) as string;
				if(!string.IsNullOrWhiteSpace(tip)) {
					AutomationProperties.SetName(button, tip);
				} else if(button.Content is string text) {
					AutomationProperties.SetName(button, text);
				}
			}
		}

		private void ApplyResponsiveToolbar()
		{
			double width = ClientSize.Width > 0 ? ClientSize.Width : Width;
			bool compact = width < 1480;
			C<TextBlock>("PreviewButtonText").IsVisible = !compact;
			C<TextBlock>("BuildButtonText").IsVisible = !compact;
			C<TextBlock>("ProjectText").IsVisible = width >= 1320;
			C<TextBlock>("BrandSubtitle").IsVisible = width >= 1250;
			C<StackPanel>("ProjectContextPanel").MaxWidth = compact ? 172 : 250;
			C<TextBlock>("RomText").MaxWidth = compact ? 165 : 230;
			C<StackPanel>("TopToolbar").Margin = compact ? new Thickness(8, 0, 0, 0) : new Thickness(18, 0, 0, 0);
		}

		private void UpdateStatusBar()
		{			_statusRomText.Text = string.IsNullOrWhiteSpace(_romPath) ? "No game" : Path.GetFileName(_romPath);
			_statusSaveText.Text = _project == null ? "No project" : (_dirty ? "Unsaved changes" : "Saved");
			_statusScaleText.Text = (_project?.OutputScale ?? IntValue(_scaleBox, 4)) + "× scale";
			_statusAssetCountText.Text = _catalog.Assets.Count.ToString("N0") + " assets";
			_statusApprovedText.Text = (_project?.Replacements.Count(r => r.Approved) ?? 0).ToString("N0") + " approved";
			_statusCaptureText.Text = _captureState switch {
				"LIVE" => "Live recording",
				"LIVE-PAUSED" => "Live capture paused",
				"RECORDING" => "Headless discovery",
				"PAUSED" => "Discovery paused",
				"COMPLETE" => "Capture complete",
				_ => "Capture idle"
			};
			_captureStateBadge.Classes.Set("warning", _captureState == "RECORDING" || _captureState == "LIVE");
			_captureStateBadge.Classes.Set("info", _captureState == "PAUSED" || _captureState == "LIVE-PAUSED");
			_captureStateBadge.Classes.Set("success", _captureState == "COMPLETE");
			_recordButton.Classes.Set("active", _captureActive);
			_pauseCaptureButton.IsEnabled = _captureActive;
			_stopCaptureButton.IsEnabled = _captureActive || _capturePaused;
		}

		//------------------------------------------------------------------ navigation

		private void ShowWorkspace(string target)
		{
			target = target switch {
				"welcome" => "home",
				"workshop" => "artwork",
				"project" or "build" => "settings",
				_ => target
			};
			if(!new[] { "home", "discover", "sprites", "backgrounds", "animations", "artwork", "preview", "export", "library", "batch", "settings" }.Contains(target)) {
				target = "home";
			}
			_activeWorkspace = target;
			_homePage.IsVisible = target == "home";
			_discoveryPage.IsVisible = target == "discover";
			_spritesPage.IsVisible = target == "sprites";
			_backgroundsPage.IsVisible = target == "backgrounds";
			_animationsPage.IsVisible = target == "animations";
			_artworkPage.IsVisible = target == "artwork";
			_previewPage.IsVisible = target == "preview";
			_exportPage.IsVisible = target == "export";
			_libraryPage.IsVisible = target == "library";
			_batchPage.IsVisible = target == "batch";
			_settingsPage.IsVisible = target == "settings";

			bool library = target is "artwork" or "sprites" or "backgrounds" or "animations";
			_assetLibraryPanel.IsVisible = library;
			_inspectorPanel.IsVisible = library;
			_assetSplitter.IsVisible = library;
			_inspectorSplitter.IsVisible = library;
			_workspaceGrid.ColumnDefinitions[1].Width = new GridLength(library ? 350 : 0);
			_workspaceGrid.ColumnDefinitions[2].Width = new GridLength(library ? 5 : 0);
			_workspaceGrid.ColumnDefinitions[3].Width = new GridLength(1, GridUnitType.Star);
			_workspaceGrid.ColumnDefinitions[4].Width = new GridLength(library ? 5 : 0);
			_workspaceGrid.ColumnDefinitions[5].Width = new GridLength(library ? 336 : 0);

			bool dock = library || target == "discover";
			_bottomDock.IsVisible = dock && _bottomOpen;
			_centerGrid.RowDefinitions[1].Height = new GridLength(dock && _bottomOpen ? 5 : 0);
			_centerGrid.RowDefinitions[2].Height = new GridLength(dock && _bottomOpen ? 238 : 0);

			foreach(Button button in this.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("nav"))) {
				button.Classes.Set("active", string.Equals(button.Tag?.ToString(), target, StringComparison.OrdinalIgnoreCase));
			}
			if(target == "animations") {
				EnsureAnimationTimeline();
			}
			if(target == "library" && _romRows.Count == 0) {
				InitializeLibraryWorkspace();
			}
			if(target == "batch" && _batchRows.Count == 0) {
				PopulateBatchRows();
			}
			if(target == "backgrounds" && !string.Equals(SelectedCategory(), "Backgrounds", StringComparison.OrdinalIgnoreCase)) {
				SetCategoryFilterFor("Backgrounds");
				RefreshAssetRows();
			} else if(target == "animations" && !string.Equals(SelectedCategory(), "Animation frames", StringComparison.OrdinalIgnoreCase)) {
				SetCategoryFilterFor("Animation frames");
				RefreshAssetRows();
			} else if(target == "sprites" && !string.Equals(SelectedCategory(), "Rendered metasprites", StringComparison.OrdinalIgnoreCase)) {
				SetCategoryFilterFor("Rendered metasprites");
				RefreshAssetRows();
			}
			if(target == "backgrounds" && _backgroundList.SelectedItem is HdStudioAssetRow selectedBackground) {
				SelectBackground(selectedBackground.Asset);
			} else if(target == "sprites" && _spriteGalleryList.SelectedItem is HdStudioAssetRow selectedSprite) {
				SelectAsset(selectedSprite.Asset, navigate: false);
			} else if(target == "animations" && _sequenceAsset != null) {
				SelectAsset(RepresentativeAsset(_sequenceAsset), navigate: false);
			}
			UpdateStatusBar();
		}

		private void Navigate_Click(object? sender, RoutedEventArgs e)
		{
			ShowWorkspace((sender as Button)?.Tag?.ToString() ?? "home");
		}

		//------------------------------------------------------------------ ROM library

		private string InventoryPath()
		{
			string[] candidates = {
				Path.Combine(Program.OriginalFolder, "..", "MesenHighWork", "validation-v06", "inventory", "library-inventory.json"),
				Path.Combine(Program.OriginalFolder, "HdStudio", "library-inventory.json"),
				"D:\\MesenHighWork\\validation-v06\\inventory\\library-inventory.json"
			};
			return candidates.FirstOrDefault(File.Exists) ?? "";
		}

		private void InitializeLibraryWorkspace()
		{
			TextBox rootBox = C<TextBox>("LibraryRootBox");
			if(string.IsNullOrWhiteSpace(rootBox.Text)) {
				rootBox.Text = Directory.Exists("D:\\NESGames\\Library") ? "D:\\NESGames\\Library" : "";
			}
			if(!string.IsNullOrWhiteSpace(rootBox.Text)) {
				LoadLibrary(rootBox.Text);
			}
		}

		private void LoadLibrary(string root)
		{
			try {
				_romCatalog = HdStudioRomLibrary.Load(root, InventoryPath(), Log);
				_romRows.Clear();
				foreach(HdStudioRomLibrary.RomEntry entry in _romCatalog.Entries) {
					string pack = HdStudioRomLibrary.FindPackFolder(entry);
					string project = string.IsNullOrWhiteSpace(pack) ? "" : Path.Combine(pack, HdStudioReport.StudioFolderName, "project.hdstudio.json");
					bool hasProject = File.Exists(project);
					bool hasHdPack = !string.IsNullOrWhiteSpace(pack) && File.Exists(Path.Combine(pack, "hires.txt"));
					bool captured = !string.IsNullOrWhiteSpace(pack) &&
						File.Exists(Path.Combine(pack, HdStudioReport.StudioFolderName, "live", "pause-frame.json"));
					string replacement = "No authored replacement";
					if(hasProject) {
						try {
							HdStudioProject p = HdStudioProject.Load(project);
							int approved = p.Replacements.Count(r => r.Approved);
							replacement = approved > 0 ? $"{approved} approved" : "Project draft";
						} catch {
							replacement = "Project present";
						}
					}
					_romRows.Add(new HdStudioRomRow() {
						Name = entry.Name,
						Path = entry.Path,
						Mapper = HdStudioRomLibrary.FormatMapper(entry),
						ChrType = HdStudioRomLibrary.FormatChrType(entry),
						ProjectStatus = hasProject ? "Project" : "No project",
						CaptureStatus = captured ? "Captured" : "Not captured",
						ReplacementStatus = replacement,
						HdPackStatus = hasHdPack ? "HD Pack" : "",
						HasProject = hasProject,
						HasHdPack = hasHdPack,
						ThumbnailLoader = row => {
							QueueRomThumbnail(row);
							return null;
						}
					});
				}
				C<TextBlock>("LibraryStatusText").Text = _romCatalog.Error.Length > 0
					? _romCatalog.Error
					: $"{_romRows.Count:N0} NES ROM(s) indexed. Showing only local metadata; ROM contents are not modified.";
				C<TextBlock>("LibraryFooterText").Text = "Thumbnails come from local captured frames or project previews. " +
					"Double-click a game to open it; existing projects are loaded from the standard Mesen HD Pack folder.";
				ApplyLibraryFilter();
				PopulateBatchRows();
			} catch(Exception ex) {
				C<TextBlock>("LibraryStatusText").Text = "Library error: " + ex.Message;
				Log("ERROR: " + ex);
			}
		}

		private void QueueRomThumbnail(HdStudioRomRow row)
		{
			if(_romCatalog == null) return;
			HdStudioRomLibrary.RomEntry? entry = _romCatalog.Entries.FirstOrDefault(e =>
				string.Equals(e.Path, row.Path, StringComparison.OrdinalIgnoreCase));
			if(entry == null) return;
			_ = Task.Run(async () => {
				await _thumbnailGate.WaitAsync();
				try {
					string thumb = HdStudioRomLibrary.FindThumbnail(_romCatalog, entry);
					if(string.IsNullOrWhiteSpace(thumb) || !File.Exists(thumb)) return;
					Dispatcher.UIThread.Post(() => row.SetThumbnail(LoadBitmap(thumb)));
				} catch {
					//A missing thumbnail is never a library error.
				} finally {
					_thumbnailGate.Release();
				}
			});
		}

		private void ApplyLibraryFilter()
		{
			if(_romCatalog == null) return;
			string search = C<TextBox>("LibrarySearchBox").Text?.Trim() ?? "";
			int mapperFilter = C<ComboBox>("LibraryMapperFilter").SelectedIndex;
			int chrFilter = C<ComboBox>("LibraryChrFilter").SelectedIndex;
			int statusFilter = C<ComboBox>("LibraryStatusFilter").SelectedIndex;
			IEnumerable<HdStudioRomRow> rows = _romRows;
			if(search.Length > 0) {
				rows = rows.Where(r => r.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
			}
			if(mapperFilter > 0) {
				string wanted = mapperFilter <= 6 ? (mapperFilter - 1).ToString(CultureInfo.InvariantCulture) : "";
				rows = mapperFilter == 7
					? rows.Where(r => !r.Mapper.StartsWith("Mapper 0", StringComparison.OrdinalIgnoreCase) &&
						!r.Mapper.StartsWith("Mapper 1", StringComparison.OrdinalIgnoreCase) &&
						!r.Mapper.StartsWith("Mapper 2", StringComparison.OrdinalIgnoreCase) &&
						!r.Mapper.StartsWith("Mapper 3", StringComparison.OrdinalIgnoreCase) &&
						!r.Mapper.StartsWith("Mapper 4", StringComparison.OrdinalIgnoreCase) &&
						!r.Mapper.StartsWith("Mapper 5", StringComparison.OrdinalIgnoreCase))
					: rows.Where(r => r.Mapper.StartsWith("Mapper " + wanted, StringComparison.OrdinalIgnoreCase));
			}
			if(chrFilter == 1) rows = rows.Where(r => r.ChrType == "CHR-ROM");
			if(chrFilter == 2) rows = rows.Where(r => r.ChrType == "CHR-RAM");
			if(statusFilter == 1) rows = rows.Where(r => r.HasProject);
			if(statusFilter == 2) rows = rows.Where(r => r.HasHdPack);
			if(statusFilter == 3) rows = rows.Where(r => !r.HasProject && !r.HasHdPack);
			List<HdStudioRomRow> list = rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
			_romLibraryList.ItemsSource = list;
			C<TextBlock>("LibraryCountBadge").Text = list.Count.ToString("N0", CultureInfo.InvariantCulture) + " GAMES";
			UpdateLibrarySelection();
		}

		private void UpdateLibrarySelection()
		{
			HdStudioRomRow? row = _romLibraryList.SelectedItem as HdStudioRomRow;
			C<TextBlock>("LibrarySelectedName").Text = row?.Name ?? "No game selected";
			C<TextBlock>("LibrarySelectedDetail").Text = row == null ? "" : row.Detail;
			C<TextBlock>("LibrarySelectedPath").Text = row == null ? "" : row.Path;
		}

		private void LibrarySearch_TextChanged(object? sender, TextChangedEventArgs e) => ApplyLibraryFilter();
		private void LibraryFilter_Changed(object? sender, SelectionChangedEventArgs e) => ApplyLibraryFilter();
		private void ClearLibraryFilters_Click(object? sender, RoutedEventArgs e)
		{
			C<TextBox>("LibrarySearchBox").Text = "";
			C<ComboBox>("LibraryMapperFilter").SelectedIndex = 0;
			C<ComboBox>("LibraryChrFilter").SelectedIndex = 0;
			C<ComboBox>("LibraryStatusFilter").SelectedIndex = 0;
			ApplyLibraryFilter();
		}
		private void RomLibraryList_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateLibrarySelection();
		private void BrowseLibrary_Click(object? sender, RoutedEventArgs e) => _ = BrowseLibraryAsync();
		private async Task BrowseLibraryAsync()
		{
			var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions() {
				Title = "Choose the folder that contains your NES ROM library",
				AllowMultiple = false
			});
			if(folders.Count == 0) return;
			string path = folders[0].Path.LocalPath;
			C<TextBox>("LibraryRootBox").Text = path;
			LoadLibrary(path);
		}
		private void RefreshLibrary_Click(object? sender, RoutedEventArgs e) => LoadLibrary(C<TextBox>("LibraryRootBox").Text ?? "");
		private void RomLibraryList_DoubleTapped(object? sender, TappedEventArgs e) => OpenSelectedRom_Click(sender, new RoutedEventArgs());
		private void OpenSelectedRom_Click(object? sender, RoutedEventArgs e)
		{
			if(_romLibraryList.SelectedItem is not HdStudioRomRow row) {
				SetStatus("Select a ROM in the library first.");
				return;
			}
			if(_romCatalog != null) {
				string project = _romCatalog.FindProject(new HdStudioRomLibrary.RomEntry() { Path = row.Path, Stem = Path.GetFileNameWithoutExtension(row.Path), Name = row.Name });
				if(!string.IsNullOrWhiteSpace(project)) {
					OpenProjectFile(project);
					return;
				}
			}
			OpenRomPath(row.Path);
		}
		private void OpenLibraryProject_Click(object? sender, RoutedEventArgs e)
		{
			if(_romLibraryList.SelectedItem is not HdStudioRomRow row || _romCatalog == null) {
				SetStatus("Select a ROM in the library first.");
				return;
			}
			string project = _romCatalog.FindProject(new HdStudioRomLibrary.RomEntry() { Path = row.Path, Stem = Path.GetFileNameWithoutExtension(row.Path), Name = row.Name });
			if(!string.IsNullOrWhiteSpace(project)) {
				OpenProjectFile(project);
			} else {
				SetStatus("This game has no HD Studio project yet. Choose Open game to start discovery.");
			}
		}

		//------------------------------------------------------------------ batch workspace

		private void PopulateBatchRows()
		{
			_batchRows.Clear();
			foreach(HdStudioRomRow row in _romRows) {
				_batchRows.Add(new HdStudioBatchRow() {
					Name = row.Name,
					Path = row.Path,
					Sha256 = "",
					Mapper = row.Mapper,
					ChrType = row.ChrType,
					Eligible = true,
					IsSelected = false
				});
			}
			_batchGameList.ItemsSource = _batchRows;
			C<TextBlock>("BatchCountBadge").Text = "0 SELECTED";
			C<TextBlock>("BatchFooterText").Text = _batchRows.Count == 0
				? "Load the ROM library first; batch selection uses the same catalog."
				: $"{_batchRows.Count:N0} game(s) available. Select a subset or all eligible games.";
		}

		private void BatchSelectAll_Click(object? sender, RoutedEventArgs e) => SetBatchSelection(r => true);
		private void BatchSelectEligible_Click(object? sender, RoutedEventArgs e) => SetBatchSelection(r => r.Eligible);
		private void SetBatchSelection(Func<HdStudioBatchRow, bool> predicate)
		{
			foreach(HdStudioBatchRow row in _batchRows) {
				row.IsSelected = predicate(row);
			}
			UpdateBatchCount();
		}
		private void BatchRow_Checked(object? sender, RoutedEventArgs e) => UpdateBatchCount();
		private void UpdateBatchCount()
		{
			int count = _batchRows.Count(r => r.IsSelected);
			C<TextBlock>("BatchCountBadge").Text = count.ToString("N0", CultureInfo.InvariantCulture) + " SELECTED";
		}

		private string FindBatchScript()
		{
			string[] candidates = {
				Path.Combine(Program.OriginalFolder, "Tools", "hdstudio_v07_autoreplace.py"),
				Path.Combine(Program.OriginalFolder, "scripts", "hdstudio_v07_autoreplace.py"),
				Path.Combine(Program.OriginalFolder, "..", "RetroArcDo_HDStudio", "source", "scripts", "hdstudio_v07_autoreplace.py"),
				"D:\\RetroArcDo_HDStudio\\source\\scripts\\hdstudio_v07_autoreplace.py"
			};
			return candidates.FirstOrDefault(File.Exists) ?? "";
		}

		private void BatchStart_Click(object? sender, RoutedEventArgs e)
		{
			if(_batchProcess != null) {
				SetStatus("A batch job is already running.");
				return;
			}
			List<HdStudioBatchRow> selected = _batchRows.Where(r => r.IsSelected).ToList();
			if(selected.Count == 0) {
				SetStatus("Select at least one game for batch processing.");
				return;
			}
			string script = FindBatchScript();
			if(string.IsNullOrWhiteSpace(script)) {
				SetStatus("The validation queue script is not installed next to the Studio. The queue is available in the development package.");
				return;
			}
			string outRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RetroArcDo HD Studio", "Batch");
			Directory.CreateDirectory(outRoot);
			string selectionFile = Path.Combine(outRoot, "selection-v07.txt");
			File.WriteAllLines(selectionFile, selected.Select(r => r.Name));
			string python = "python";
			string mode = C<ComboBox>("BatchModeBox").SelectedIndex switch {
				1 => "background",
				2 => "auto",
				_ => "sprite"
			};
			string depth = C<ComboBox>("BatchDepthBox").SelectedIndex switch {
				1 => "300",
				2 => "6000",
				_ => "1800"
			};
			int workers = (int)(C<NumericUpDown>("BatchWorkersBox").Value ?? 3);
			bool allowShared = C<CheckBox>("BatchAllowSharedCheck").IsChecked == true;
			_batchOutputRoot = outRoot;
			_batchResultsPath = Path.Combine(outRoot, "replacement-results.json");
			_batchStartedUtc = DateTime.UtcNow;
			ProcessStartInfo info = new() {
				FileName = python,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				WorkingDirectory = Path.GetDirectoryName(script) ?? outRoot
			};
			info.ArgumentList.Add(script);
			info.ArgumentList.Add("--mesen");
			info.ArgumentList.Add(Program.ExePath);
			info.ArgumentList.Add("--base-home");
			info.ArgumentList.Add(ConfigManager.HomeFolder);
			info.ArgumentList.Add("--scale");
			info.ArgumentList.Add("2");
			info.ArgumentList.Add("--selected");
			info.ArgumentList.Add(string.Join(";", selected.Select(r => r.Name)));
			info.ArgumentList.Add("--target-frames");
			info.ArgumentList.Add(depth);
			info.ArgumentList.Add("--target-mode");
			info.ArgumentList.Add(mode);
			info.ArgumentList.Add("--workers");
			info.ArgumentList.Add(workers.ToString(CultureInfo.InvariantCulture));
			info.ArgumentList.Add("--verify-scenes");
			info.ArgumentList.Add("3");
			info.ArgumentList.Add("--out-root");
			info.ArgumentList.Add(outRoot);
			if(allowShared) info.ArgumentList.Add("--allow-shared");
			try {
				_batchProcess = Process.Start(info);
				if(_batchProcess == null) {
					SetStatus("The batch process could not start.");
					return;
				}
				_batchProcess.OutputDataReceived += (_, args) => {
					if(string.IsNullOrWhiteSpace(args.Data)) return;
					Dispatcher.UIThread.Post(() => UpdateBatchFromLine(args.Data));
				};
				_batchProcess.BeginOutputReadLine();
				_batchProcess.BeginErrorReadLine();
				_captureState = "RECORDING";
				C<TextBlock>("BatchStatusText").Text = $"Running {selected.Count} game(s), {workers} worker(s), {mode} mode.";
				C<TextBlock>("BatchFooterText").Text = "Results are written to " + outRoot + ". Pause stops the current queue; Resume continues with cached rows.";
				_batchTimer = new DispatcherTimer() { Interval = TimeSpan.FromSeconds(1) };
				_batchTimer.Tick += (_, _) => PollBatchResults();
				_batchTimer.Start();
				UpdateStatusBar();
			} catch(Exception ex) {
				SetStatus("Batch start failed: " + ex.Message);
			}
		}

		private void UpdateBatchFromLine(string line)
		{
			//The queue prints "[index/total] ROM -> status". Map it to the visible rows.
			int close = line.IndexOf(']');
			if(line.StartsWith("[", StringComparison.Ordinal) && close > 0) {
				string rest = line.Substring(close + 1).Trim();
				int arrow = rest.IndexOf(" -> ", StringComparison.Ordinal);
				if(arrow > 0) {
					string name = rest.Substring(0, arrow).Trim();
					string status = rest.Substring(arrow + 4).Trim();
					HdStudioBatchRow? row = _batchRows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
					if(row != null) {
						row.Status = status.StartsWith("verified", StringComparison.OrdinalIgnoreCase) ? "Verified" : "Failed";
						row.Detail = status;
					}
				}
			}
			C<TextBlock>("BatchStatusText").Text = line.Length > 220 ? line.Substring(0, 220) + "..." : line;
		}

		private void PollBatchResults()
		{
			if(_batchProcess == null) return;
			try {
				if(File.Exists(_batchResultsPath)) {
					using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_batchResultsPath));
					if(doc.RootElement.ValueKind == JsonValueKind.Array) {
						int verified = 0, shared = 0, failed = 0;
						foreach(JsonElement row in doc.RootElement.EnumerateArray()) {
							string status = row.TryGetProperty("status", out JsonElement s) ? s.GetString() ?? "" : "";
							if(status.StartsWith("verified", StringComparison.OrdinalIgnoreCase)) verified++;
							else failed++;
							if(status.Contains("shared", StringComparison.OrdinalIgnoreCase)) shared++;
						}
						C<TextBlock>("BatchProcessedText").Text = doc.RootElement.GetArrayLength().ToString(CultureInfo.InvariantCulture);
						C<TextBlock>("BatchVerifiedText").Text = verified.ToString(CultureInfo.InvariantCulture);
						C<TextBlock>("BatchSharedText").Text = shared.ToString(CultureInfo.InvariantCulture);
						C<TextBlock>("BatchFailedText").Text = failed.ToString(CultureInfo.InvariantCulture);
						if(_batchRows.Count > 0) {
							C<ProgressBar>("BatchProgress").Value = Math.Clamp(100.0 * doc.RootElement.GetArrayLength() / _batchRows.Count(r => r.IsSelected), 0, 100);
						}
					}
				}
			} catch {
				//Partial JSON is written atomically; a transient read must never fail the UI.
			}
			if(_batchProcess.HasExited) {
				int code = _batchProcess.ExitCode;
				_batchProcess.Dispose();
				_batchProcess = null;
				_batchTimer?.Stop();
				_batchTimer = null;
				C<TextBlock>("BatchStatusText").Text = "Batch finished (exit " + code.ToString(CultureInfo.InvariantCulture) + ").";
				C<TextBlock>("BatchFooterText").Text = "Open results or review failures from the buttons. Verified rows are cached for the next run.";
				C<ProgressBar>("BatchProgress").Value = 100;
				_captureState = "IDLE";
				UpdateStatusBar();
			}
		}

		private void BatchPause_Click(object? sender, RoutedEventArgs e)
		{
			if(_batchProcess == null) return;
			try {
				_batchProcess.Kill(entireProcessTree: true);
			} catch { }
			C<TextBlock>("BatchStatusText").Text = "Paused. Verified rows remain cached; press Resume to continue.";
			C<TextBlock>("BatchFooterText").Text = "Resume starts the same selection again. Completed exact-scene rows are skipped from cache.";
		}
		private void BatchResume_Click(object? sender, RoutedEventArgs e) => BatchStart_Click(sender, e);
		private void BatchCancel_Click(object? sender, RoutedEventArgs e)
		{
			if(_batchProcess == null) return;
			try { _batchProcess.Kill(entireProcessTree: true); } catch { }
			C<TextBlock>("BatchStatusText").Text = "Batch cancelled.";
			_captureState = "IDLE";
			UpdateStatusBar();
		}
		private void BatchOpenResults_Click(object? sender, RoutedEventArgs e) => OpenFolder(_batchOutputRoot);
		private void BatchOpenFailures_Click(object? sender, RoutedEventArgs e)
		{
			if(string.IsNullOrWhiteSpace(_batchOutputRoot) || !Directory.Exists(_batchOutputRoot)) {
				SetStatus("No batch output yet.");
				return;
			}
			string failures = Path.Combine(_batchOutputRoot, "failures.txt");
			try {
				List<string> failed = Directory.EnumerateFiles(_batchOutputRoot, "result.json", SearchOption.AllDirectories)
					.Select(p => { try { using JsonDocument d = JsonDocument.Parse(File.ReadAllText(p)); return d.RootElement.TryGetProperty("status", out JsonElement s) && !(s.GetString() ?? "").StartsWith("verified", StringComparison.OrdinalIgnoreCase) ? p : ""; } catch { return ""; } })
					.Where(p => p.Length > 0).ToList();
				File.WriteAllLines(failures, failed);
				SetStatus($"{failed.Count} failure record(s) listed in {failures}.");
			} catch(Exception ex) {
				SetStatus("Could not collect failures: " + ex.Message);
			}
		}
		private void BatchOpenProjects_Click(object? sender, RoutedEventArgs e) => OpenFolder(_batchOutputRoot);

		private void BottomTab_Click(object? sender, RoutedEventArgs e)
		{
			string tab = (sender as Button)?.Tag?.ToString() ?? "timeline";
			ShowBottomTab(tab);
		}

		private void ShowBottomTab(string tab)
		{
			_bottomTab = tab;
			_timelineDockPanel.IsVisible = tab == "timeline";
			_captureDockPanel.IsVisible = tab == "capture";
			_historyDockPanel.IsVisible = tab == "history";
			_validationDockPanel.IsVisible = tab == "validation";
			_conflictDockPanel.IsVisible = tab == "conflicts";
			_consoleDockPanel.IsVisible = tab == "console";
			_replacementsDockPanel.IsVisible = tab == "replacements";
			foreach(Button button in this.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("segment"))) {
				button.Classes.Set("active", string.Equals(button.Tag?.ToString(), tab, StringComparison.OrdinalIgnoreCase));
			}
		}

		private void ToggleBottom_Click(object? sender, RoutedEventArgs e)
		{
			_bottomOpen = !_bottomOpen;
			_settings.BottomPanelOpen = _bottomOpen;
			_settings.Save();
			ShowWorkspace(_activeWorkspace);
		}

		//------------------------------------------------------------------ asset library

		private void RefreshAssetRows()
		{
			if(_catalog.Assets.Count == 0) {
				_assetRows.Clear();
				_assetList.ItemsSource = null;
				_assetCountText.Text = "No catalog loaded";
				_assetCountsDetailText.Text = "Raw patterns · compositions · animations · backgrounds · approved · verified";
				return;
			}
			string search = _assetSearchBox.Text?.Trim() ?? "";
			string category = SelectedCategory();
			string status = SelectedStatus();
			IEnumerable<HdStudioCatalogAsset> query = _catalog.Assets;
			if(category != "All assets") {
				if(category == "Backgrounds") {
					query = query.Where(a => a.Category is "Backgrounds" or "Background tiles");
				} else {
					query = query.Where(a => string.Equals(a.Category, category, StringComparison.OrdinalIgnoreCase));
				}
			}
			if(search.Length > 0) {
				query = query.Where(a =>
					a.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
					a.Label.Contains(search, StringComparison.OrdinalIgnoreCase) ||
					a.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
					a.Provenance.Contains(search, StringComparison.OrdinalIgnoreCase));
			}
			query = query.Where(a => MatchesStatusFilter(a, status));
			List<HdStudioCatalogAsset> filtered = query
				.OrderBy(a => a.Category)
				.ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
				.ToList();
			_filteredAssets.Clear();
			_filteredAssets.AddRange(filtered);
			_assetRows.Clear();
			foreach(HdStudioCatalogAsset asset in filtered.Take(_assetLimit)) {
				_assetRows.Add(CreateAssetRow(asset));
			}
			string? selectedId = _selectedAsset?.Id;
			_assetList.ItemsSource = _assetRows.ToList();
			_assetCountText.Text = $"{_assetRows.Count:N0} of {filtered.Count:N0} assets";
			int rawPatterns = _catalog.Assets.SelectMany(a => a.Tiles)
				.Select(t => HdStudioCatalog.GetTileKey(t))
				.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
			int compositions = _catalog.Assets.Count(a => a.Kind is "metasprite" or "semanticObject" or "pickedObject" or "manualComposition");
			int sequences = _catalog.Assets.Count(a => a.Kind == "animationSequence");
			int backgrounds = _catalog.Assets.Count(a => a.Kind is "backgroundBlock" or "backgroundTile" or "pickedBackground");
			int approved = _project?.Replacements.Count(r => r.Approved) ?? 0;
			//A verify screenshot does not prove which approved replacement produced it.
			//Only per-replacement verification records count; otherwise report zero.
			int verified = _project?.Replacements.Count(r => r.Approved &&
				r.Validation.Any(v => v.StartsWith("verified", StringComparison.OrdinalIgnoreCase))) ?? 0;
			Dictionary<string, int> keyUsers = new(StringComparer.OrdinalIgnoreCase);
			foreach(HdStudioCatalogAsset asset in _catalog.Assets.Where(a => a.Kind != "rawPattern" && a.Tiles.Count > 0)) {
				foreach(string key in asset.Tiles.Select(t => HdStudioCatalog.GetTileKey(t) + "|" + t.Palette.ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase)) {
					keyUsers[key] = keyUsers.TryGetValue(key, out int count) ? count + 1 : 1;
				}
			}
			int conflicts = _catalog.Assets.Count(a => a.Kind != "rawPattern" && a.Tiles.Any(t => {
				string key = HdStudioCatalog.GetTileKey(t) + "|" + t.Palette.ToUpperInvariant();
				return keyUsers.TryGetValue(key, out int users) && users > 1;
			}));
			_assetCountsDetailText.Text =
				$"{rawPatterns:N0} raw · {compositions:N0} compositions · {sequences:N0} sequences · " +
				$"{backgrounds:N0} backgrounds · {approved:N0} approved · {verified:N0} verified · {conflicts:N0} shared-key";
			_loadMoreAssetsButton.IsVisible = filtered.Count > _assetRows.Count;
			ApplyThumbnailSizeToRows();
			if(_assetRows.Count > 0) {
				HdStudioAssetRow? row = _assetRows.FirstOrDefault(r => string.Equals(r.Asset.Id, selectedId, StringComparison.OrdinalIgnoreCase));
				_assetList.SelectedItem = !string.IsNullOrWhiteSpace(selectedId) ? row : _assetRows[0];
			}
		}

		private HdStudioAssetRow CreateAssetRow(HdStudioCatalogAsset asset)
		{
			HdStudioAssetRow row = new() {
				Asset = asset,
				Category = FriendlyCategory(asset),
				Detail = DescribeAssetShort(asset),
				IsNew = _newAssetIds.Contains(asset.Id),
				HasReplacement = FindReplacement(asset.Id) != null,
				Locked = _project?.LockedAssets.Contains(asset.Id, StringComparer.OrdinalIgnoreCase) == true,
				ThumbnailLoader = LoadRowThumbnail,
				ReplacementThumbnailLoader = LoadRowReplacementThumbnail
			};
			ApplyStatus(row);
			return row;
		}

		private void SetCategoryFilterFor(string category)
		{
			string tag = category switch {
				"Objects" => "Objects",
				"Rendered metasprites" => "Rendered metasprites",
				"Backgrounds" or "Background tiles" => "Backgrounds",
				"Animation frames" or "Animation sequences" => "Animation frames",
				_ => "All assets"
			};
			foreach(Button button in this.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("chip") && b.Tag != null)) {
				button.Classes.Set("active", string.Equals(button.Tag?.ToString(), tag, StringComparison.Ordinal));
			}
			_suppressFilterRefresh = true;
			_statusFilterBox.SelectedIndex = 0;
			_assetSearchBox.Text = "";
			_suppressFilterRefresh = false;
		}

		private void ApplyStatus(HdStudioAssetRow row)
		{
			HdStudioCatalogAsset asset = row.Asset;
			HdStudioReplacement? replacement = FindReplacement(asset.Id);
			bool locked = row.Locked;
			bool unsupported = asset.Tiles.Count == 0 && asset.Kind != "animationSequence" && asset.Kind != "other";
			bool shared = replacement != null && !replacement.AllowSharedReplacement && HasSharedRisk(asset);
			bool verified = IsReplacementVerified(replacement);

			if(locked) {
				SetStatus(row, "Icon.Lock", "StudioWarningBrush", "Locked frame — excluded from automatic frame assignment.");
			} else if(unsupported) {
				SetStatus(row, "Icon.TriangleAlert", "StudioErrorBrush", "Unsupported replacement: no tile occurrences were captured.");
			} else if(shared) {
				SetStatus(row, "Icon.TriangleAlert", "StudioWarningBrush", "Shared graphics conflict: the same tile+palette is used by another object.");
			} else if(replacement != null && replacement.Approved && verified) {
				SetStatus(row, "Icon.ShieldCheck", "StudioSuccessBrush", "Approved and present in the last emulator verification.");
			} else if(replacement != null && replacement.Approved) {
				SetStatus(row, "Icon.CircleCheck", "StudioSuccessBrush", "Approved replacement.");
			} else if(replacement != null) {
				SetStatus(row, "Icon.Pencil", "StudioAccentBrush", "Draft replacement — not exported until approved.");
			} else if(row.IsNew) {
				SetStatus(row, "Icon.Sparkles", "StudioInfoBrush", "Newly discovered asset.");
			} else if(asset.Kind is "other" or "" || asset.Name.Contains("unknown", StringComparison.OrdinalIgnoreCase)) {
				SetStatus(row, "Icon.CircleHelp", "StudioTextTertiaryBrush", "Unclassified asset — visible so its tiles can still be replaced.");
			} else {
				SetStatus(row, "Icon.Circle", "StudioTextTertiaryBrush", "Discovered; no replacement yet.");
			}
		}

		private void SetStatus(HdStudioAssetRow row, string iconKey, string brushKey, string tip)
		{
			row.StatusGeometry = IconGeometry(iconKey);
			row.StatusBrush = Brush(brushKey);
			row.StatusTip = tip;
		}

		private bool MatchesStatusFilter(HdStudioCatalogAsset asset, string status)
		{
			if(string.IsNullOrWhiteSpace(status) || status == "Any status") return true;
			HdStudioReplacement? replacement = FindReplacement(asset.Id);
			bool locked = _project?.LockedAssets.Contains(asset.Id, StringComparer.OrdinalIgnoreCase) == true;
			return status switch {
				"Discovered" => replacement == null && !locked,
				"Newly discovered" => _newAssetIds.Contains(asset.Id),
				"Artwork assigned" => replacement != null,
				"Draft replacement" => replacement != null && !replacement.Approved,
				"Approved replacement" => replacement != null && replacement.Approved,
				"Verified in emulator" => IsReplacementVerified(replacement),
				"Shared graphics conflict" => replacement != null && HasSharedRisk(asset),
				"Unsupported replacement" => asset.Tiles.Count == 0 && asset.Kind is not ("animationSequence" or "other"),
				"Unclassified" => asset.Kind is "other" or "" || asset.Name.Contains("unknown", StringComparison.OrdinalIgnoreCase),
				"Locked frame" => locked,
				_ => true
			};
		}

		private void RefreshSpriteGallery()
		{
			_spriteRows.Clear();
			//The ListBox virtualizes its rows and thumbnails load lazily. Do not truncate the
			//sprite library: every observed composition/animation frame must be reachable.
			foreach(HdStudioCatalogAsset asset in _catalog.Assets
				.Where(a => a.Category is "Objects" or "Rendered metasprites" || a.Kind == "animationFrame")
				.OrderBy(a => a.Category).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)) {
				_spriteRows.Add(CreateAssetRow(asset));
			}
			_spriteGalleryList.ItemsSource = _spriteRows.ToList();
			if(_spriteRows.Count > 0) {
				_spriteGalleryList.SelectedItem = _spriteRows.FirstOrDefault(r => string.Equals(r.Asset.Id, _selectedAsset?.Id, StringComparison.OrdinalIgnoreCase)) ?? _spriteRows[0];
			}
			TextBlock? title = C<TextBlock>("SpriteLibrarySubtitle");
			if(title != null) {
				title.Text = $"{_spriteRows.Count:N0} captured sprite compositions. Original OAM composition is reconstructed where possible.";
			}
		}

		private void RefreshBackgroundRows()
		{
			_backgroundRows.Clear();
			foreach(HdStudioCatalogAsset asset in _catalog.Assets
				.Where(a => a.Category is "Backgrounds" or "Background tiles")
				.OrderBy(a => a.Category).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)) {
				_backgroundRows.Add(CreateAssetRow(asset));
			}
			_backgroundList.ItemsSource = _backgroundRows.ToList();
			if(_backgroundRows.Count > 0) {
				_backgroundList.SelectedItem = _backgroundRows.FirstOrDefault(r => string.Equals(r.Asset.Id, _backgroundAsset?.Id, StringComparison.OrdinalIgnoreCase)) ?? _backgroundRows[0];
			}
			_backgroundsSubtitle.Text = $"{_backgroundRows.Count:N0} background objects and tiles. 8×8 geometry preserved.";
		}

		private static string FriendlyCategory(HdStudioCatalogAsset asset)
		{
			return asset.Kind switch {
				"semanticObject" => "Object",
				"metasprite" => "Sprite",
				"animationFrame" => "Animation frame",
				"animationSequence" => "Animation sequence",
				"backgroundBlock" => "Background block",
				"backgroundTile" => "Background tile",
				_ => "Asset"
			};
		}

		private static string DescribeAssetShort(HdStudioCatalogAsset asset)
		{
			string size = asset.Width > 0 && asset.Height > 0 ? $"{asset.Width}×{asset.Height}" : "unknown size";
			return $"{size} · {asset.Tiles.Count} tile{(asset.Tiles.Count == 1 ? "" : "s")}";
		}

		private void AssetList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			if(_syncingSelection || _suppressAssetListSelection) return;
			if(_assetList.SelectedItem is HdStudioAssetRow row) {
				SelectAsset(row.Asset);
			}
		}

		private void SpriteGallery_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			if(_activeWorkspace is not ("sprites" or "artwork")) return;
			if(_spriteGalleryList.SelectedItem is HdStudioAssetRow row) {
				SelectAsset(row.Asset, navigate: false);
			}
		}

		private void BackgroundList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			if(_activeWorkspace is not ("backgrounds" or "artwork")) return;
			if(_backgroundList.SelectedItem is HdStudioAssetRow row) {
				SelectBackground(row.Asset);
			}
		}

		private void AssetList_DoubleTapped(object? sender, TappedEventArgs e)
		{
			if(_selectedAsset != null) {
				ShowWorkspace("artwork");
			}
		}

		private void AssetCategory_Click(object? sender, RoutedEventArgs e)
		{
			foreach(Button button in this.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("chip") && b.Tag != null)) {
				button.Classes.Set("active", ReferenceEquals(button, sender));
			}
			_assetLimit = 160;
			RefreshAssetRows();
		}

		private void StatusFilter_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			if(_suppressFilterRefresh) return;
			if(_initialized) {
				_assetLimit = 160;
				RefreshAssetRows();
			}
		}

		private void AssetSearch_TextChanged(object? sender, TextChangedEventArgs e)
		{
			if(_suppressFilterRefresh) return;
			if(_initialized) {
				_assetLimit = 160;
				RefreshAssetRows();
			}
		}

		private void LoadMoreAssets_Click(object? sender, RoutedEventArgs e)
		{
			_assetLimit += 160;
			RefreshAssetRows();
		}

		private void ClearFilters_Click(object? sender, RoutedEventArgs e)
		{
			_assetSearchBox.Text = "";
			_statusFilterBox.SelectedIndex = 0;
			foreach(Button button in this.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("chip") && b.Tag != null)) {
				button.Classes.Set("active", string.Equals(button.Tag?.ToString(), "All assets", StringComparison.Ordinal));
			}
			_assetLimit = 160;
			RefreshAssetRows();
		}

		private string SelectedCategory()
		{
			Button? button = this.GetLogicalDescendants().OfType<Button>()
				.FirstOrDefault(b => b.Classes.Contains("chip") && b.Classes.Contains("active"));
			return button?.Tag?.ToString() ?? "All assets";
		}

		private string SelectedStatus() => (_statusFilterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Any status";

		private void GridView_Click(object? sender, RoutedEventArgs e) => ShowAssetView(true);

		private void ListView_Click(object? sender, RoutedEventArgs e) => ShowAssetView(false);

		private void ShowAssetView(bool grid)
		{
			_assetGrid = grid;
			_assetList.ItemTemplate = (IDataTemplate)this.FindResource(grid ? "AssetGridTemplate" : "AssetListTemplate")!;
			ApplyAssetItemsPanel();
			_gridViewButton.Classes.Set("active", grid);
			_listViewButton.Classes.Set("active", !grid);
			_settings.AssetGridView = grid;
			if(_initialized) _settings.Save();
		}

		private void ApplyAssetItemsPanel()
		{
			double itemWidth = Math.Clamp(_thumbnailSizeSlider.Value, 96, 200) + 10;
			bool grid = _assetGrid;
			_assetList.ItemsPanel = new FuncTemplate<Panel?>(() => grid
				? new WrapPanel() { Orientation = Avalonia.Layout.Orientation.Horizontal, ItemWidth = itemWidth }
				: new VirtualizingStackPanel());
			_assetList.InvalidateMeasure();
		}

		private void ThumbnailSize_Changed(object? sender, RoutedEventArgs e)
		{
			double size = Math.Clamp(_thumbnailSizeSlider.Value, 96, 200);
			_thumbnailSizeText.Text = ((int)size).ToString();
			_settings.AssetThumbnailSize = size;
			ApplyThumbnailSizeToRows();
			ApplyAssetItemsPanel();
			if(_initialized) {
				_settings.Save();
			}
		}

		private void ApplyThumbnailSizeToRows()
		{
			double size = Math.Clamp(_thumbnailSizeSlider.Value, 96, 200);
			foreach(HdStudioAssetRow row in _assetRows.Concat(_spriteRows)) {
				row.ThumbnailSize = size;
			}
		}

		private void FilterSpriteShared_Click(object? sender, RoutedEventArgs e)
		{
			_statusFilterBox.SelectedItem = _statusFilterBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Content?.ToString() ?? "").StartsWith("Shared")) ?? _statusFilterBox.SelectedItem;
			RefreshAssetRows();
		}

		private void OpenWorkshop_Click(object? sender, RoutedEventArgs e)
		{
			if(_selectedAsset == null) {
				SetStatus("Select an asset first.");
				return;
			}
			ShowWorkspace("artwork");
		}

		private void OpenCandidate_Click(object? sender, RoutedEventArgs e)
		{
			if((sender as Button)?.Tag is HdStudioCatalogAsset asset) {
				SelectAsset(asset);
				ShowWorkspace("artwork");
			}
		}

		private void RecentProject_Click(object? sender, RoutedEventArgs e)
		{
			if((sender as Button)?.Tag is string path) {
				OpenProjectFile(path);
			}
		}

		private void ContinueLast_Click(object? sender, RoutedEventArgs e)
		{
			string? path = _settings.RecentProjects.FirstOrDefault(File.Exists);
			if(path != null) OpenProjectFile(path);
			else OpenProject_Click(sender, e);
		}

		//------------------------------------------------------------------ selection + inspector

		private void SelectAsset(HdStudioCatalogAsset asset, bool navigate = true)
		{
			HdStudioCatalogAsset effective = RepresentativeAsset(asset);
			_selectedAsset = effective;
			_syncingSelection = true;
			HdStudioAssetRow? selectedRow = _assetRows.FirstOrDefault(r => string.Equals(r.Asset.Id, effective.Id, StringComparison.OrdinalIgnoreCase));
			if(selectedRow == null) {
				int filteredIndex = _filteredAssets.FindIndex(a => string.Equals(a.Id, effective.Id, StringComparison.OrdinalIgnoreCase));
				if(filteredIndex < 0 || filteredIndex >= _assetRows.Count) {
					SetCategoryFilterFor(effective.Category);
					_suppressFilterRefresh = true;
					_assetSearchBox.Text = effective.Name;
					_suppressFilterRefresh = false;
					_assetLimit = 160;
					RefreshAssetRows();
					selectedRow = _assetRows.FirstOrDefault(r => string.Equals(r.Asset.Id, effective.Id, StringComparison.OrdinalIgnoreCase));
				}
			}
			_assetList.SelectedItem = selectedRow;
			HdStudioAssetRow? galleryRow = _spriteRows.FirstOrDefault(r => string.Equals(r.Asset.Id, effective.Id, StringComparison.OrdinalIgnoreCase));
			if(galleryRow != null) _spriteGalleryList.SelectedItem = galleryRow;
			HdStudioAssetRow? backgroundRow = _backgroundRows.FirstOrDefault(r => string.Equals(r.Asset.Id, effective.Id, StringComparison.OrdinalIgnoreCase));
			if(backgroundRow != null) _backgroundList.SelectedItem = backgroundRow;
			_syncingSelection = false;
			if(_assetList.SelectedItem is HdStudioAssetRow chosenRow) {
				Dispatcher.UIThread.Post(() => _assetList.ScrollIntoView(chosenRow), DispatcherPriority.Loaded);
				DispatcherTimer scrollTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
				scrollTimer.Tick += (_, _) => {
					scrollTimer.Stop();
					_assetList.ScrollIntoView(chosenRow);
				};
				scrollTimer.Start();
			}
			HdStudioReplacement? replacement = FindReplacement(effective.Id);
			_editSettings = CloneSettings(replacement?.Settings);
			_editSourcePath = replacement == null ? "" : _project?.ResolvePath(replacement.SourceImage) ?? replacement.SourceImage;
			_allowSharedCheck.IsChecked = replacement?.AllowSharedReplacement == true;

			FillInspector(effective, replacement);
			FillEditor(effective, replacement);
			UpdateWorkspaceHeader(effective);
			UpdateHomePreview(effective);
			UpdateConflictPanel(effective);
			if(navigate && _activeWorkspace is "home" or "discover" or "preview" or "export" or "settings") {
				//Selecting from the library only opens the editor when the user asked for it.
			}
			SetStatus($"Selected {effective.Name}.");
		}

		private HdStudioCatalogAsset RepresentativeAsset(HdStudioCatalogAsset asset)
		{
			if(asset.Tiles.Count > 0) return asset;
			HdStudioCatalogAsset? child = _catalog.Assets.FirstOrDefault(a => a.Tiles.Count > 0 &&
				(string.Equals(a.ParentId, asset.Id, StringComparison.OrdinalIgnoreCase) ||
				 string.Equals(a.AnimationId, asset.Id, StringComparison.OrdinalIgnoreCase)));
			return child ?? asset;
		}

		private void FillInspector(HdStudioCatalogAsset asset, HdStudioReplacement? replacement)
		{
			int scale = Math.Clamp(_project?.OutputScale ?? IntValue(_scaleBox, 4), 1, 10);
			_inspectorAssetName.Text = asset.Name;
			C<TextBox>("AssetNameBox").Text = asset.Name;
			C<TextBox>("AssetCategoryBox").Text = asset.CustomCategory;
			_inspectorAssetKind.Text = $"{FriendlyCategory(asset)} · {asset.Tiles.Count} tiles";
			_assetInfoText.Text = string.IsNullOrWhiteSpace(asset.Provenance) ? "Captured from the original game." : asset.Provenance;
			_logicalSizeText.Text = asset.Width > 0 && asset.Height > 0 ? $"{asset.Width}×{asset.Height} NES px" : "—";
			_hdSizeText.Text = asset.Width > 0 ? $"{asset.Width * scale}×{asset.Height * scale} px" : "—";
			_inspectorScaleText.Text = scale + "×";
			_assetIdText.Text = asset.Id;
			_provenanceText.Text = string.IsNullOrWhiteSpace(asset.Provenance) ? "discovery" : asset.Provenance;
			_hardwareText.Text = $"{(_project?.ChrMode ?? (_catalog.HasChrRom ? "CHR-ROM" : "CHR-RAM"))} · mapper {_catalog.Mapper} · {asset.Tiles.Count} tile occurrences";
			_tileListText.Text = asset.Tiles.Count == 0 ? "No tile occurrences (composition or sequence)." :
				string.Join(", ", asset.Tiles.Take(12).Select(t => HdStudioCatalog.GetTileKey(t) + "/" + t.Palette)) +
				(asset.Tiles.Count > 12 ? " …" : "");
			string preview = HdStudioCatalog.EnsureOriginalPreview(_catalog, asset, _palette);
			_inspectorThumbnail.Source = LoadBitmap(preview);
			_sourceSizeText.Text = replacement != null && File.Exists(_editSourcePath) ? DescribeSource(_editSourcePath) : "—";
			_inspectorNewBadge.IsVisible = _newAssetIds.Contains(asset.Id);

			ApplyInspectorStatus(asset, replacement);
			PopulatePalettes(asset);
			UpdateSharedWarning(asset, replacement);
		}

		private void ApplyInspectorStatus(HdStudioCatalogAsset asset, HdStudioReplacement? replacement)
		{
			string status;
			string badgeClass = "";
			if(_project?.LockedAssets.Contains(asset.Id, StringComparer.OrdinalIgnoreCase) == true) {
				status = "LOCKED"; badgeClass = "warning";
			} else if(replacement == null) {
				status = _newAssetIds.Contains(asset.Id) ? "NEW" : "DISCOVERED"; badgeClass = _newAssetIds.Contains(asset.Id) ? "info" : "";
			} else if(replacement.Approved) {
				status = IsReplacementVerified(replacement) ? "VERIFIED" : "APPROVED"; badgeClass = "success";
			} else {
				status = "DRAFT"; badgeClass = "";
			}
			_inspectorStatusBadgeText.Text = status;
			foreach(string cls in new[] { "success", "warning", "info", "error" }) {
				_inspectorStatusBadge.Classes.Set(cls, badgeClass == cls);
			}
		}

		private void PopulatePalettes(HdStudioCatalogAsset asset)
		{
			_paletteList.Children.Clear();
			_paletteCountText.Text = asset.PaletteVariants.Count.ToString();
			foreach(string variant in asset.PaletteVariants.Take(8)) {
				Border chip = new() {
					Background = Brush("StudioPanelAltBrush"),
					BorderBrush = Brush("StudioBorderBrush"),
					BorderThickness = new Thickness(1),
					CornerRadius = new CornerRadius(5),
					Padding = new Thickness(6, 3),
					Margin = new Thickness(0, 0, 4, 4)
				};
				StackPanel content = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 3 };
				content.Children.Add(new TextBlock() { Text = variant, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 9.5, Foreground = Brush("StudioTextSecondaryBrush") });
				chip.Child = content;
				_paletteList.Children.Add(chip);
			}
			if(asset.PaletteVariants.Count == 0) {
				_paletteList.Children.Add(new TextBlock() { Text = "No exact palette variant recorded.", FontSize = 10.5, Foreground = Brush("StudioTextTertiaryBrush") });
			}
		}

		private void UpdateSharedWarning(HdStudioCatalogAsset asset, HdStudioReplacement? replacement)
		{
			List<HdStudioCatalogAsset> shared = SharedAssets(asset);
			string warning = "";
			if(replacement != null && !replacement.AllowSharedReplacement && shared.Count > 0) {
				string names = string.Join(", ", shared.Take(4).Select(a => a.Name));
				if(shared.Count > 4) names += ", …";
				warning = $"Shared graphics conflict: {shared.Count} other asset(s) use the same tile+palette ({names}). Approval is explicit.";
			}
			_sharedKeysText.Text = warning;
			_sharedKeysText.IsVisible = warning.Length > 0;
		}

		private void FillEditor(HdStudioCatalogAsset asset, HdStudioReplacement? replacement)
		{
			_workshopTitleText.Text = asset.Name;
			_workshopSubtitleText.Text = $"{FriendlyCategory(asset)} · {asset.Width}×{asset.Height} NES pixels · {asset.Tiles.Count} tiles";
			_originalSizeBadge.Text = $"{asset.Width}×{asset.Height}";
			_replacementSizeBadge.Text = $"{asset.Width * CurrentScale()}×{asset.Height * CurrentScale()}";
			_syncingEditor = true;
			_sourcePathBox.Text = _editSourcePath;
			WriteEditorControlsFromSettings();
			_syncingEditor = false;
			RefreshEditorCanvases();
			UpdateInspectorEditorFields();
		}

		private void UpdateWorkspaceHeader(HdStudioCatalogAsset asset)
		{
			_originalCanvas.OriginalImagePath = HdStudioCatalog.EnsureOriginalPreview(_catalog, asset, _palette);
			_originalCanvas.LogicalWidth = Math.Max(8, asset.Width);
			_originalCanvas.LogicalHeight = Math.Max(8, asset.Height);
			_homePreviewCanvas.OriginalImagePath = _originalCanvas.OriginalImagePath;
			_homePreviewCanvas.LogicalWidth = _originalCanvas.LogicalWidth;
			_homePreviewCanvas.LogicalHeight = _originalCanvas.LogicalHeight;
			_homePreviewCanvas.Mode = HdStudioArtworkCanvas.CanvasMode.Original;
			_homePreviewCanvas.InvalidateVisual();
			_originalCanvas.InvalidateVisual();
		}

		private void UpdateHomePreview(HdStudioCatalogAsset asset)
		{
			_homePreviewCanvas.Mode = HdStudioArtworkCanvas.CanvasMode.Original;
			_homePreviewCanvas.SourceImagePath = "";
			_homePreviewCanvas.OriginalImagePath = HdStudioCatalog.EnsureOriginalPreview(_catalog, asset, _palette);
			_homePreviewCanvas.LogicalWidth = Math.Max(8, asset.Width);
			_homePreviewCanvas.LogicalHeight = Math.Max(8, asset.Height);
			_homePreviewCanvas.InvalidateVisual();
		}

		private void RefreshEditorCanvases()
		{
			if(_selectedAsset == null) return;
			int scale = CurrentScale();
			int width = Math.Max(8, _selectedAsset.Width);
			int height = Math.Max(8, _selectedAsset.Height);
			_originalCanvas.LogicalWidth = width;
			_originalCanvas.LogicalHeight = height;
			_originalCanvas.OriginalImagePath = HdStudioCatalog.EnsureOriginalPreview(_catalog, _selectedAsset, _palette);
			_originalCanvas.ShowTileGrid = _showGridCheck.IsChecked == true;
			_originalCanvas.ShowCheckerboard = _showCheckerCheck.IsChecked == true;
			_replacementCanvas.LogicalWidth = width;
			_replacementCanvas.LogicalHeight = height;
			_replacementCanvas.OutputScale = scale;
			_replacementCanvas.SourceImagePath = _editSourcePath;
			_replacementCanvas.Settings = _editSettings;
			_replacementCanvas.ShowTileGrid = _showGridCheck.IsChecked == true;
			_replacementCanvas.ShowCheckerboard = _showCheckerCheck.IsChecked == true;
			_replacementCanvas.CropMode = _tool == "crop";
			_replacementCanvas.InvalidateSettings();
			_originalCanvas.OutputScale = scale;
			_originalCanvas.InvalidateVisual();
			_replacementSizeBadge.Text = $"{width * scale}×{height * scale}";
			_hdSizeText.Text = $"{width * scale}×{height * scale} px";
			_sourceSizeText.Text = File.Exists(_editSourcePath) ? DescribeSource(_editSourcePath) : "—";
			_cropInfoText.Text = $"{_editSettings.CropLeft},{_editSettings.CropTop},{_editSettings.CropRight},{_editSettings.CropBottom}";
			UpdateInspectorEditorFields();
		}

		private void UpdateInspectorEditorFields()
		{
			_inspectorScaleText.Text = CurrentScale() + "×";
			_paletteCountText.Text = _selectedAsset?.PaletteVariants.Count.ToString() ?? "0";
		}

		private void EditorControlChanged()
		{
			if(_syncingEditor) return;
			ReadSettingsFromControls();
			RefreshEditorCanvases();
			MarkDirty();
		}

		private void SyncEditorControlsFromSettings()
		{
			_syncingEditor = true;
			WriteEditorControlsFromSettings();
			_syncingEditor = false;
			RefreshEditorCanvases();
			MarkDirty();
		}

		private void WriteEditorControlsFromSettings()
		{
			_offsetXBox.Value = _editSettings.OffsetX;
			_offsetYBox.Value = _editSettings.OffsetY;
			_zoomBox.Value = _editSettings.ZoomPercent;
			_anchorBox.SelectedIndex = (_editSettings.Anchor ?? "center").ToLowerInvariant() switch {
				"top" => 0, "left" => 1, "center" => 2, "right" => 3, "bottom" => 4, _ => 2
			};
			_cropCheck.IsChecked = _editSettings.Crop;
			_removeBackgroundCheck.IsChecked = _editSettings.RemoveBackground;
			_keepLargestCheck.IsChecked = _editSettings.KeepLargestComponent;
			_toleranceSlider.Value = _editSettings.BackgroundTolerance;
			_toleranceBox.Value = _editSettings.BackgroundTolerance;
			_allPalettesCheck.IsChecked = _editSettings.AllPaletteVariants;
			_mirrorCheck.IsChecked = _editSettings.MirrorConditions;
			_matchBgPriorityCheck.IsChecked = _editSettings.MatchBackgroundPriority;
			_syncingEditor = true;
			_extraConditionBox.Text = _editSettings.ExtraCondition;
			_syncingEditor = false;
			_showGridCheck.IsChecked = _replacementCanvas.ShowTileGrid;
			_showCheckerCheck.IsChecked = _replacementCanvas.ShowCheckerboard;
			_checkerToggleCheck.IsChecked = _replacementCanvas.ShowCheckerboard;
		}

		private void ReadSettingsFromControls()
		{
			_editSettings.OffsetX = IntValue(_offsetXBox, 0);
			_editSettings.OffsetY = IntValue(_offsetYBox, 0);
			_editSettings.ZoomPercent = Math.Clamp(IntValue(_zoomBox, 100), 10, 400);
			_editSettings.Anchor = AnchorValue(_anchorBox.SelectedIndex);
			_editSettings.Crop = _cropCheck.IsChecked == true;
			_editSettings.RemoveBackground = _removeBackgroundCheck.IsChecked == true;
			_editSettings.KeepLargestComponent = _keepLargestCheck.IsChecked == true;
			_editSettings.BackgroundTolerance = IntValue(_toleranceBox, 40);
			_editSettings.AllPaletteVariants = _allPalettesCheck.IsChecked == true;
			_editSettings.MirrorConditions = _mirrorCheck.IsChecked == true;
			_editSettings.MatchBackgroundPriority = _matchBgPriorityCheck.IsChecked == true;
			_editSettings.ExtraCondition = _extraConditionBox.Text?.Trim() ?? "";
			_sourcePathBox.Text = _editSourcePath;
		}

		private void SelectBackground(HdStudioCatalogAsset asset)
		{
			_backgroundAsset = asset;
			_suppressAssetListSelection = true;
			SelectAsset(asset, navigate: false);
			_syncingSelection = true;
			_assetList.SelectedItem = _assetRows.FirstOrDefault(r => string.Equals(r.Asset.Id, asset.Id, StringComparison.OrdinalIgnoreCase));
			_syncingSelection = false;
			Dispatcher.UIThread.Post(() => _suppressAssetListSelection = false, DispatcherPriority.Background);
			string image = HdStudioCatalog.EnsureOriginalPreview(_catalog, asset, _palette);
			ShowBitmap(_backgroundPreview, image);
			_backgroundInfoText.Text = $"{asset.Name} · {asset.Width}×{asset.Height} NES pixels · {asset.Tiles.Count} tiles";
			_backgroundGridLabel.Text = $"{Math.Max(1, asset.Width / 8)}×{Math.Max(1, asset.Height / 8)} tile grid";
		}

		private void OpenBackground_Click(object? sender, RoutedEventArgs e)
		{
			HdStudioCatalogAsset? asset = _backgroundAsset ?? _backgroundRows.FirstOrDefault()?.Asset;
			if(asset == null) {
				SetStatus("Select a background object first.");
				return;
			}
			SelectAsset(asset);
			ShowWorkspace("artwork");
		}

		private void UpdateConflictPanel(HdStudioCatalogAsset asset)
		{
			List<HdStudioCatalogAsset> shared = SharedAssets(asset);
			HdStudioReplacement? replacement = FindReplacement(asset.Id);
			bool risk = replacement != null && !replacement.AllowSharedReplacement && shared.Count > 0;
			_conflictSummaryText.Text = risk
				? $"{shared.Count} other discovered asset(s) use the same pattern+palette as {asset.Name}. A standard Mesen pack replaces them together."
				: shared.Count > 0
					? $"{shared.Count} other asset(s) share these tiles. No conflicting authored replacement is present."
					: "No shared tile+palette was found for the selected asset.";
			_conflictList.ItemsSource = shared.Select(a => new HdStudioAssetRow() { Asset = a, Category = FriendlyCategory(a) }).ToList();
			ReviewConflictButtonState(risk || shared.Count > 0);
		}

		private void ReviewConflictButtonState(bool enabled)
		{
			C<Button>("ReviewConflictButton").IsEnabled = enabled;
		}

		private async void ReviewConflict_Click(object? sender, RoutedEventArgs e)
		{
			await ShowSelectedConflictAsync();
		}

		private async Task ShowSelectedConflictAsync()
		{
			if(_selectedAsset == null) return;
			List<HdStudioCatalogAsset> shared = SharedAssets(_selectedAsset);
			if(shared.Count == 0) {
				SetStatus("No shared graphics were found for the selected asset.");
				return;
			}
			HdStudioConflictWindow dialog = new(_selectedAsset.Name,
				$"{FriendlyCategory(_selectedAsset)} · {_selectedAsset.Width}×{_selectedAsset.Height} NES pixels · {_selectedAsset.Tiles.Count} tiles",
				shared.Select(a => new HdStudioConflictWindow.SharedAssetRow() { Name = a.Name, Category = FriendlyCategory(a) }).ToList(),
				_settings.Theme.Equals("light", StringComparison.OrdinalIgnoreCase),
				HdStudioContextResolver.Analyze(_catalog, _selectedAsset));
			bool accepted = await dialog.ShowDialog<bool>(this);
			if(dialog.KeepOriginal) {
				SetStatus("Original graphics preserved; no replacement was written for this object.");
				return;
			}
			if(dialog.AllowShared) {
				_allowSharedCheck.IsChecked = true;
				_editSettings.ExtraCondition = "";
				_syncingEditor = true;
				_extraConditionBox.Text = "";
				_syncingEditor = false;
				_sharedKeysText.Text = "Shared replacement approved explicitly for this asset.";
				SetStatus("Shared replacement approved. Build to apply it.");
				MarkDirty();
			} else if(!string.IsNullOrWhiteSpace(dialog.ChosenRule)) {
				_allowSharedCheck.IsChecked = false;
				_editSettings.ExtraCondition = dialog.ChosenRule.Trim('[', ']');
				_syncingEditor = true;
				_extraConditionBox.Text = _editSettings.ExtraCondition;
				_syncingEditor = false;
				_sharedKeysText.Text = "Context rule: " + dialog.ChosenRule + " - other occurrences keep their original graphics.";
				SetStatus("Context rule selected: " + dialog.ChosenRule + ". Apply to compile it as a standard Mesen condition.");
				MarkDirty();
			} else if(accepted) {
				SetStatus("No rule was selected.");
			}
		}

		//------------------------------------------------------------------ artwork actions

		private void SelectTool_Click(object? sender, RoutedEventArgs e) => SetTool("select");
		private void MoveTool_Click(object? sender, RoutedEventArgs e) => SetTool("move");
		private void ResizeTool_Click(object? sender, RoutedEventArgs e) => SetTool("resize");
		private void CropTool_Click(object? sender, RoutedEventArgs e) => SetTool("crop");

		private void SetTool(string tool)
		{
			_tool = tool;
			_cropToolButton.Classes.Set("active", tool == "crop");
			RefreshEditorCanvases();
			_workshopStatusText.Text = tool switch {
				"move" => "Drag the artwork on the replacement canvas to position it.",
				"resize" => "Drag the bottom-right handle to scale the artwork.",
				"crop" => "Drag the orange crop edges to trim the source artwork.",
				_ => "Select and drag. Mouse wheel zooms the canvas."
			};
		}

		private void FlipH_Click(object? sender, RoutedEventArgs e)
		{
			PushUndo();
			_editSettings.FlipH = !_editSettings.FlipH;
			RefreshEditorCanvases();
			MarkDirty();
		}

		private void FlipV_Click(object? sender, RoutedEventArgs e)
		{
			PushUndo();
			_editSettings.FlipV = !_editSettings.FlipV;
			RefreshEditorCanvases();
			MarkDirty();
		}

		private void ZoomIn_Click(object? sender, RoutedEventArgs e)
		{
			_originalCanvas.ZoomBy(1.2);
			_replacementCanvas.ZoomBy(1.2);
		}

		private void ZoomOut_Click(object? sender, RoutedEventArgs e)
		{
			_originalCanvas.ZoomBy(1 / 1.2);
			_replacementCanvas.ZoomBy(1 / 1.2);
		}

		private void FitView_Click(object? sender, RoutedEventArgs e)
		{
			_originalCanvas.FitToView();
			_replacementCanvas.FitToView();
			SetStatus("Canvas fitted to view.");
		}

		private void GridToggle_Click(object? sender, RoutedEventArgs e)
		{
			bool show = !_replacementCanvas.ShowTileGrid;
			SetEditorGridDefaults(show, _replacementCanvas.ShowCheckerboard);
			RefreshEditorCanvases();
		}

		private void ShowGrid_Click(object? sender, RoutedEventArgs e)
		{
			bool show = _showGridCheck.IsChecked == true;
			_originalCanvas.ShowTileGrid = show;
			_replacementCanvas.ShowTileGrid = show;
			_gridToggleButton.Classes.Set("active", show);
			RefreshEditorCanvases();
		}

		private void ShowChecker_Click(object? sender, RoutedEventArgs e)
		{
			bool show = ReferenceEquals(sender, _showCheckerCheck)
				? _showCheckerCheck.IsChecked == true
				: _checkerToggleCheck.IsChecked == true;
			_showCheckerCheck.IsChecked = show;
			_checkerToggleCheck.IsChecked = show;
			_originalCanvas.ShowCheckerboard = show;
			_replacementCanvas.ShowCheckerboard = show;
			RefreshEditorCanvases();
		}

		private void ResetCrop_Click(object? sender, RoutedEventArgs e)
		{
			PushUndo();
			_editSettings.CropLeft = _editSettings.CropTop = _editSettings.CropRight = _editSettings.CropBottom = 0;
			_editSettings.OffsetX = 0;
			_editSettings.OffsetY = 0;
			_editSettings.ZoomPercent = 100;
			RefreshEditorCanvases();
			MarkDirty();
		}

		private void ResetTransform_Click(object? sender, RoutedEventArgs e)
		{
			PushUndo();
			_editSettings = new HdStudioAdaptationSettings() {
				RemoveBackground = _editSettings.RemoveBackground,
				BackgroundTolerance = _editSettings.BackgroundTolerance,
				KeepLargestComponent = _editSettings.KeepLargestComponent,
				Crop = _editSettings.Crop,
				AllPaletteVariants = _editSettings.AllPaletteVariants,
				ExtraCondition = _editSettings.ExtraCondition
			};
			WriteEditorControlsFromSettings();
			RefreshEditorCanvases();
			MarkDirty();
		}

		private void Tolerance_Changed(object? sender, RoutedEventArgs e)
		{
			if(_syncingEditor) return;
			_syncingEditor = true;
			_toleranceBox.Value = (decimal)_toleranceSlider.Value;
			_syncingEditor = false;
			EditorControlChanged();
		}

		private async void Browse_Click(object? sender, RoutedEventArgs e)
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
				SetSourceArtwork(files[0].Path.LocalPath);
			}
		}

		private void SetSourceArtwork(string path)
		{
			if(_selectedAsset == null) {
				SetStatus("Select an asset before importing artwork.");
				return;
			}
			_editSourcePath = path;
			_sourcePathBox.Text = path;
			RefreshEditorCanvases();
			_workshopStatusText.Text = "Artwork loaded. Adjust the composition and use Preview or Approve & Apply.";
			MarkDirty();
		}

		private HdStudioReplacement? BuildEditorReplacement(bool store, bool approve)
		{
			if(_project == null) {
				SetStatus("No project is loaded.");
				return null;
			}
			HdStudioCatalogAsset? asset = _selectedAsset;
			if(asset == null || asset.Tiles.Count == 0) {
				SetStatus("Select an asset with tile occurrences first.");
				return null;
			}
			string source = _editSourcePath;
			if(!File.Exists(source)) {
				SetStatus("Choose existing artwork first.");
				return null;
			}
			int scale = CurrentScale();
			HdStudioReplacement? replacement;
			if(!store) {
				//Preview must not change the approved project state. Build a transient
				//replacement with the same geometry and settings instead.
				replacement = new HdStudioReplacement() {
					Id = "preview:" + HdStudioProject.StableId(asset.Id + "|preview"),
					AssetId = asset.Id,
					AssetName = asset.Name,
					AssetKind = asset.Kind,
					CreatedAtUtc = DateTime.UtcNow.ToString("O")
				};
			} else {
				replacement = _project.Replacements
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
			}
			if(replacement.Locked) {
				SetStatus("This frame is locked. Unlock it before changing its artwork.");
				return null;
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
			CopySettings(_editSettings, replacement.Settings);
			return replacement;
		}

		private async void Preview_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null || _selectedAsset == null) {
				SetStatus("Select an asset and a project first.");
				return;
			}
			HdStudioReplacement? draft = BuildEditorReplacement(false, false);
			if(draft == null) return;
			string path = HdStudioCompiler.PreviewReplacement(_project, draft, out List<string> messages);
			if(path.Length > 0 && File.Exists(path)) {
				ShowImagePreview(path, draft.AssetName + " — HD replacement");
				_workshopStatusText.Text = "Preview ready.";
			} else {
				_workshopStatusText.Text = messages.FirstOrDefault() ?? "Preview failed.";
			}
			foreach(string message in messages) Log("Preview: " + message);
			await Task.CompletedTask;
		}

		private void SaveDraft_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null) return;
			PushUndo();
			HdStudioReplacement? draft = BuildEditorReplacement(true, false);
			if(draft == null) return;
			_project.Save(HdStudioProject.GetDefaultPath(_packFolder));
			_dirty = false;
			RefreshAssetRows();
			RefreshSpriteGallery();
			RefreshReplacements();
			UpdateStatusBar();
			SetStatus("Draft saved. It is not exported until approved.");
			Log("Draft saved for " + draft.AssetName + ".");
		}

		private async void Apply_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null) return;
			if(_selectedAsset == null || !File.Exists(_editSourcePath)) {
				SetStatus("Choose an existing artwork file first.");
				return;
			}
			if(HasSharedRisk(_selectedAsset) && _allowSharedCheck.IsChecked != true && string.IsNullOrWhiteSpace(_editSettings.ExtraCondition)) {
				await ShowSelectedConflictAsync();
				if(_allowSharedCheck.IsChecked != true && string.IsNullOrWhiteSpace(_editSettings.ExtraCondition)) {
					SetStatus("Apply cancelled: no shared replacement rule was chosen.");
					return;
				}
			}
			PushUndo();
			HdStudioReplacement? draft = BuildEditorReplacement(true, true);
			if(draft == null) return;
			_project.Save(HdStudioProject.GetDefaultPath(_packFolder));
			_dirty = false;
			RefreshAssetRows();
			RefreshSpriteGallery();
			RefreshReplacements();
			UpdateStatusBar();
			await BuildAsync("Building the standard pack...");
		}

		private async Task BuildAsync(string message)
		{
			if(_project == null) return;
			await RunBusy(message, async () => {
				_project.OutputScale = Math.Clamp(IntValue(_scaleBox, _project.OutputScale), 1, 10);
				HdStudioCompileResult result = await Task.Run(() => HdStudioCompiler.Compile(_project, Log));
				SetCompileResult(result);
				RefreshReplacements();
				RefreshProjectSummary();
				UpdateHomeStats();
				SetStatus(result.Success ? $"Build OK: {result.CompiledReplacements} replacements, {result.TileEntries} tile entries." : "Build failed; see Validation.");
				if(!result.Success) ShowBottomTab("validation");
			});
		}

		private void SetCompileResult(HdStudioCompileResult result)
		{
			string text = FormatCompileResult(result);
			_validationText.Text = text;
			_validationDetailsBox.Text = text;
			_dockValidationHeadline.Text = result.Success ? "Build succeeded" : "Build failed";
			_dockValidationDetail.Text = result.Success
				? $"{result.CompiledReplacements} replacements · {result.TileEntries:N0} tile entries · {result.Images:N0} images"
				: (result.Errors.FirstOrDefault() ?? "See the validation log.");
			_dockValidationHeadline.Foreground = result.Success ? Brush("StudioSuccessBrush") : Brush("StudioErrorBrush");
			_exportSummaryValidation.Text = result.Success ? "Validation: passed on build" : "Validation: errors — review before export";
		}

		private static string FormatCompileResult(HdStudioCompileResult result)
		{
			System.Text.StringBuilder sb = new();
			sb.AppendLine(result.Success ? "Build succeeded." : "Build failed.");
			sb.AppendLine($"Replacements: {result.CompiledReplacements} compiled, {result.SkippedReplacements} skipped");
			sb.AppendLine($"Tile entries: {result.TileEntries:N0}");
			sb.AppendLine($"Images in hires.txt: {result.Images:N0}");
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

		//------------------------------------------------------------------ undo / redo

		private void PushUndo()
		{
			if(_project == null) return;
			_undo.Push(CaptureSnapshot());
			if(_undo.Count > 40) {
				EditorSnapshot[] items = _undo.ToArray();
				_undo.Clear();
				foreach(EditorSnapshot item in items.Take(40).Reverse()) _undo.Push(item);
			}
			_redo.Clear();
			UpdateUndoButtons();
		}

		private EditorSnapshot CaptureSnapshot()
		{
			return new EditorSnapshot() {
				Replacements = _project?.Replacements.Select(CloneReplacement).ToList() ?? new(),
				Locked = _project?.LockedAssets.ToList() ?? new(),
				AssetId = _selectedAsset?.Id ?? "",
				SourcePath = _editSourcePath,
				Settings = CloneSettings(_editSettings)
			};
		}

		private void Undo_Click(object? sender, RoutedEventArgs e)
		{
			if(_undo.Count == 0) {
				SetStatus("Nothing to undo.");
				return;
			}
			if(_project == null) return;
			_redo.Push(CaptureSnapshot());
			RestoreSnapshot(_undo.Pop());
			SetStatus("Undo applied.");
		}

		private void Redo_Click(object? sender, RoutedEventArgs e)
		{
			if(_redo.Count == 0) {
				SetStatus("Nothing to redo.");
				return;
			}
			if(_project == null) return;
			_undo.Push(CaptureSnapshot());
			RestoreSnapshot(_redo.Pop());
			SetStatus("Redo applied.");
		}

		private void RestoreSnapshot(EditorSnapshot snapshot)
		{
			if(_project == null) return;
			_project.Replacements.Clear();
			_project.Replacements.AddRange(snapshot.Replacements.Select(CloneReplacement));
			_project.LockedAssets.Clear();
			_project.LockedAssets.AddRange(snapshot.Locked);
			_project.Save(HdStudioProject.GetDefaultPath(_packFolder));
			_dirty = false;
			_editSettings = CloneSettings(snapshot.Settings);
			_editSourcePath = snapshot.SourcePath;
			RefreshAssetRows();
			RefreshSpriteGallery();
			RefreshBackgroundRows();
			RefreshReplacements();
			RefreshAnimationList();
			RefreshProjectSummary();
			UpdateStatusBar();
			HdStudioCatalogAsset? asset = _catalog.Assets.FirstOrDefault(a => string.Equals(a.Id, snapshot.AssetId, StringComparison.OrdinalIgnoreCase)) ?? _selectedAsset;
			if(asset != null) SelectAsset(asset);
			UpdateUndoButtons();
		}

		private void UpdateUndoButtons()
		{
			C<Button>("UndoButton").IsEnabled = _undo.Count > 0;
			C<Button>("RedoButton").IsEnabled = _redo.Count > 0;
		}

		private static HdStudioReplacement CloneReplacement(HdStudioReplacement source)
		{
			HdStudioReplacement clone = new() {
				Id = source.Id, AssetId = source.AssetId, AssetName = source.AssetName, AssetKind = source.AssetKind,
				SourceImage = source.SourceImage, Sheet = source.Sheet, PackScale = source.PackScale,
				OriginalWidth = source.OriginalWidth, OriginalHeight = source.OriginalHeight,
				TargetWidth = source.TargetWidth, TargetHeight = source.TargetHeight, Approval = source.Approval,
				AllowSharedReplacement = source.AllowSharedReplacement, Locked = source.Locked,
				CreatedAtUtc = source.CreatedAtUtc, UpdatedAtUtc = source.UpdatedAtUtc, LegacyManifest = source.LegacyManifest
			};
			CopySettings(source.Settings, clone.Settings);
			clone.Cells.AddRange(source.Cells.Select(c => new HdStudioReplacementCell() { Key = c.Key, Palette = c.Palette, Condition = c.Condition, SheetX = c.SheetX }));
			clone.Warnings.AddRange(source.Warnings);
			clone.Validation.AddRange(source.Validation);
			return clone;
		}

		private static HdStudioAdaptationSettings CloneSettings(HdStudioAdaptationSettings? source)
		{
			HdStudioAdaptationSettings settings = new();
			if(source != null) CopySettings(source, settings);
			return settings;
		}

		private static void CopySettings(HdStudioAdaptationSettings source, HdStudioAdaptationSettings target)
		{
			target.RemoveBackground = source.RemoveBackground;
			target.BackgroundTolerance = source.BackgroundTolerance;
			target.KeepLargestComponent = source.KeepLargestComponent;
			target.Crop = source.Crop;
			target.Fit = source.Fit;
			target.Anchor = source.Anchor;
			target.Padding = source.Padding;
			target.OffsetX = source.OffsetX;
			target.OffsetY = source.OffsetY;
			target.ZoomPercent = source.ZoomPercent;
			target.FlipH = source.FlipH;
			target.FlipV = source.FlipV;
			target.MirrorConditions = source.MirrorConditions;
			target.MatchBackgroundPriority = source.MatchBackgroundPriority;
			target.AllPaletteVariants = source.AllPaletteVariants;
			target.ExtraCondition = source.ExtraCondition;
			target.ContextRule = source.ContextRule;
			target.ContextStatus = source.ContextStatus;
			target.CellConditions.Clear();
			foreach(KeyValuePair<string, string> pair in source.CellConditions) {
				target.CellConditions[pair.Key] = pair.Value;
			}
			target.CropLeft = source.CropLeft;
			target.CropTop = source.CropTop;
			target.CropRight = source.CropRight;
			target.CropBottom = source.CropBottom;
		}

		//------------------------------------------------------------------ animation

		private void RefreshAnimationList()
		{
			_animationRows.Clear();
			_animationRows.AddRange(_catalog.Assets
				.Where(a => a.Kind is "animationSequence" or "animationFrame")
				.OrderBy(a => a.Kind == "animationSequence" ? 0 : 1)
				.ThenBy(a => a.AnimationId)
				.ThenBy(a => a.FrameIndex)
				.ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase));
			_animationList.ItemsSource = _animationRows.Select(a => a.Kind == "animationSequence"
				? "Sequence · " + a.Name
				: $"    Frame {a.FrameIndex + 1} · {a.Name}").ToList();
			if(_animationRows.Count > 0) {
				HdStudioCatalogAsset? selected = _animationRows.FirstOrDefault(a => string.Equals(a.Id, _sequenceAsset?.Id, StringComparison.OrdinalIgnoreCase));
				_animationList.SelectedIndex = selected == null ? 0 : _animationRows.IndexOf(selected);
			}
			_animationSubtitle.Text = $"{_animationRows.Count:N0} captured sequence and frame records. Original frame order and timing are preserved.";
			EnsureAnimationTimeline();
		}

		private void AnimationList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			int index = _animationList.SelectedIndex;
			if(index < 0 || index >= _animationRows.Count) return;
			_sequenceAsset = _animationRows[index];
			if(_activeWorkspace is "animations" or "artwork") {
				SelectAsset(RepresentativeAsset(_sequenceAsset), navigate: false);
			}
			BuildAnimationTimeline(_sequenceAsset);
		}

		private void EnsureAnimationTimeline()
		{
			if(_timelineRows.Count == 0 && _sequenceAsset == null && _animationRows.Count > 0) {
				BuildAnimationTimeline(_animationRows[0]);
			}
		}

		private void BuildAnimationTimeline(HdStudioCatalogAsset sequence)
		{
			_timelineRows.Clear();
			List<HdStudioCatalogAsset> frames;
			if(sequence.Kind == "animationFrame") {
				frames = new() { sequence };
			} else {
				frames = _catalog.Assets
					.Where(a => a.Kind == "animationFrame" && a.Tiles.Count > 0 &&
						(string.Equals(a.ParentId, sequence.Id, StringComparison.OrdinalIgnoreCase) ||
						 string.Equals(a.AnimationId, sequence.Id, StringComparison.OrdinalIgnoreCase)))
					.OrderBy(a => a.FrameIndex).ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
					.ToList();
			}
			foreach(HdStudioCatalogAsset frame in frames) {
				HdStudioReplacement? replacement = FindReplacement(frame.Id);
				string image = HdStudioCatalog.EnsureOriginalPreview(_catalog, frame, _palette);
				Geometry? geometry = replacement == null ? IconGeometry("Icon.Circle") : replacement.Approved ? IconGeometry("Icon.CircleCheck") : IconGeometry("Icon.Pencil");
				IBrush? brush = replacement == null ? Brush("StudioTextTertiaryBrush") : replacement.Approved ? Brush("StudioSuccessBrush") : Brush("StudioAccentBrush");
				_timelineRows.Add(new HdStudioFrameRow() {
					Asset = frame, Thumbnail = LoadBitmap(image), Name = frame.Name,
					FrameLabel = (frame.FrameIndex >= 0 ? (frame.FrameIndex + 1).ToString("00") : "—") + "/" + Math.Max(1, frames.Count).ToString("00"),
					StatusGeometry = geometry, StatusBrush = brush,
					StatusTip = replacement == null ? "Original captured frame. No replacement yet." : replacement.Approved ? "Approved frame replacement." : "Draft frame replacement.",
					Detail = $"{frame.Width}×{frame.Height}"
				});
			}
			_animationTimeline.ItemsSource = _timelineRows.ToList();
			if(_timelineRows.Count > 0) {
				_animationIndex = 0;
				_animationTimeline.SelectedIndex = 0;
				SelectAnimationFrame(0);
			}
			string info = $"{sequence.Name} · {_timelineRows.Count} frame(s) · timing not provided by capture";
			_timelineInfoText.Text = info;
			_dockTimelineInfo.Text = info;
		}

		private void AnimationTimeline_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			if(_animationTimeline.SelectedIndex >= 0) {
				SelectAnimationFrame(_animationTimeline.SelectedIndex);
			}
		}

		private void SelectAnimationFrame(int index)
		{
			if(index < 0 || index >= _timelineRows.Count) return;
			_animationIndex = index;
			HdStudioFrameRow row = _timelineRows[index];
			_frameAsset = row.Asset;
			ShowBitmap(_animationPreview, HdStudioCatalog.EnsureOriginalPreview(_catalog, row.Asset, _palette));
			HdStudioReplacement? replacement = FindReplacement(row.Asset.Id);
			_animationInfoText.Text = $"{row.Asset.Name}\nFrame {row.Asset.FrameIndex + 1} · {row.Asset.Width}×{row.Asset.Height} NES pixels\n" +
				(replacement == null ? "No replacement yet." : replacement.Approved ? "Approved replacement." : "Draft replacement.");
			_animationTimeline.SelectedIndex = index;
			_playAnimationButton.Classes.Set("active", _animationPlaying);
		}

		private void PreviousFrame_Click(object? sender, RoutedEventArgs e) => StepAnimation(-1);
		private void NextFrame_Click(object? sender, RoutedEventArgs e) => StepAnimation(1);

		private void StepAnimation(int delta)
		{
			if(_timelineRows.Count == 0) return;
			int index = (_animationIndex + delta + _timelineRows.Count) % _timelineRows.Count;
			SelectAnimationFrame(index);
		}

		private void PlayAnimation_Click(object? sender, RoutedEventArgs e)
		{
			if(_timelineRows.Count < 2) {
				SetStatus("This animation has only one captured frame.");
				return;
			}
			_animationPlaying = !_animationPlaying;
			if(_animationPlaying) _animationTimer?.Start(); else _animationTimer?.Stop();
			_playAnimationButton.Classes.Set("active", _animationPlaying);
			SetStatus(_animationPlaying ? "Animation preview playing." : "Animation preview paused.");
		}

		private void AdvanceAnimation(bool loop)
		{
			if(_timelineRows.Count == 0) return;
			int next = _animationIndex + 1;
			if(next >= _timelineRows.Count) {
				if(!_loopAnimation) {
					_animationPlaying = false;
					_animationTimer?.Stop();
					_playAnimationButton.Classes.Set("active", false);
					return;
				}
				next = 0;
			}
			SelectAnimationFrame(next);
		}

		private void LoopAnimation_Click(object? sender, RoutedEventArgs e)
		{
			_loopAnimation = !_loopAnimation;
			_loopAnimationButton.Classes.Set("active", _loopAnimation);
			SetStatus(_loopAnimation ? "Animation loop enabled." : "Animation loop disabled.");
		}

		private void OpenAnimation_Click(object? sender, RoutedEventArgs e)
		{
			HdStudioCatalogAsset? target = _frameAsset ?? _sequenceAsset;
			if(target == null) return;
			SelectAsset(RepresentativeAsset(target));
			ShowWorkspace("artwork");
		}

		private void ApproveFrame_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null || _frameAsset == null) return;
			HdStudioReplacement? replacement = FindReplacement(_frameAsset.Id);
			if(replacement == null) {
				SetStatus("This frame has no replacement to approve.");
				return;
			}
			if(replacement.Locked) {
				SetStatus("This frame is locked. Unlock it first.");
				return;
			}
			PushUndo();
			replacement.Approval = "approved";
			replacement.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
			_project.Save(HdStudioProject.GetDefaultPath(_packFolder));
			RefreshAssetRows();
			RefreshSpriteGallery();
			RefreshReplacements();
			BuildAnimationTimeline(_sequenceAsset ?? _frameAsset);
			SetStatus("Frame approved.");
		}

		private void LockFrame_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null || _frameAsset == null) return;
			PushUndo();
			bool locked = _project.LockedAssets.Contains(_frameAsset.Id, StringComparer.OrdinalIgnoreCase);
			if(locked) _project.LockedAssets.RemoveAll(id => string.Equals(id, _frameAsset.Id, StringComparison.OrdinalIgnoreCase));
			else _project.LockedAssets.Add(_frameAsset.Id);
			HdStudioReplacement? replacement = FindReplacement(_frameAsset.Id);
			if(replacement != null) replacement.Locked = !locked;
			_project.Save(HdStudioProject.GetDefaultPath(_packFolder));
			RefreshAssetRows();
			RefreshSpriteGallery();
			BuildAnimationTimeline(_sequenceAsset ?? _frameAsset);
			SetStatus(locked ? "Frame unlocked." : "Frame locked.");
		}

		private void SlicePanelToggle_Click(object? sender, RoutedEventArgs e)
		{
			SetStatus("Sprite-sheet import: choose a sheet, preview slices and assign drafts.");
		}

		//------------------------------------------------------------------ discovery

		private async void Record_Click(object? sender, RoutedEventArgs e)
		{
			if(_liveSession != null) {
				if(_liveSession.Paused) {
					ResumeLiveCapture();
				} else {
					SetStatus("Live capture is already recording.");
				}
				return;
			}
			if(_capturePaused && !_captureActive) {
				await StartDiscoveryAsync(resume: true);
				return;
			}
			await StartDiscoveryAsync(resume: _capturePaused);
		}

		private string SelectedDiscoveryMode()
		{
			return _discoveryModeBox.SelectedIndex switch {
				0 => HdStudioDiscoveryModes.Live,
				1 => HdStudioDiscoveryModes.Quick,
				2 => HdStudioDiscoveryModes.Deep,
				3 => HdStudioDiscoveryModes.Replay,
				4 => HdStudioDiscoveryModes.Assisted,
				_ => HdStudioDiscoveryModes.Deep
			};
		}

		private async Task StartDiscoveryAsync(bool resume)
		{
			if(_project == null || string.IsNullOrWhiteSpace(_romPath)) {
				SetStatus("Open a ROM and create a project before discovery.");
				return;
			}
			if(_busy) {
				SetStatus("Another operation is already running.");
				return;
			}
			string mode = SelectedDiscoveryMode();
			if(mode == HdStudioDiscoveryModes.Live) {
				StartLiveCapture();
				return;
			}
			HdStudioDiscoveryOptions options = new() {
				Mode = mode,
				Frames = IntValue(_discoveryBudgetBox, 18000),
				ChunkFrames = IntValue(_discoveryChunkBox, 1800),
				Seed = IntValue(_discoverySeedBox, 20261124),
				Scale = _project.OutputScale,
				MoviePath = _discoveryMovieBox.Text?.Trim() ?? "",
				LuaScriptPath = _discoveryScriptBox.Text?.Trim() ?? "",
				ObserveBackgrounds = _discoveryObserveBgCheck.IsChecked == true,
				Restart = resume ? false : _discoveryRestartCheck.IsChecked == true
			};
			if(mode == HdStudioDiscoveryModes.Replay && !File.Exists(options.MoviePath)) {
				SetStatus("Replay capture needs an existing movie file.");
				return;
			}
			if(mode == HdStudioDiscoveryModes.Assisted && !File.Exists(options.LuaScriptPath) && !File.Exists(options.MoviePath)) {
				SetStatus("Assisted exploration needs an existing Lua script or movie.");
				return;
			}

			_captureActive = true;
			_capturePaused = false;
			SetCaptureState("RECORDING");
			_discoverStartButton.IsEnabled = false;
			_discoverCancelButton.IsEnabled = true;
			_busy = true;
			_discoveryProgress.Value = 0;
			_discoveryCancellation = new CancellationTokenSource();
			SetStatus(HdStudioDiscoveryModes.DisplayName(mode) + " running...");
			Progress<HdStudioDiscoveryProgress> progress = new(p => {
				_discoveryProgress.Value = p.RequestedFrames > 0 ? Math.Clamp(100.0 * p.FramesCaptured / p.RequestedFrames, 0, 100) : 0;
				_discoveryStatusText.Text = p.Message;
				_dockCaptureProgress.Value = _discoveryProgress.Value;
				_dockCaptureStatus.Text = p.Message;
			});
			try {
				HdStudioDiscoveryResult result = await Task.Run(() => HdStudioDiscovery.RunAsync(_project, options, Log, progress, _discoveryCancellation.Token));
				foreach(string error in result.Errors) Log("Discovery error: " + error);
				RefreshFromDisk();
				ShowDiscoverySession(result.Session, result.StopReason);
				SetCaptureState(result.Success ? "COMPLETE" : "IDLE");
				_capturePaused = false;
				SetStatus(result.Success
					? $"Discovery stopped: {result.StopReason} after {result.Session.FramesCaptured:N0} frames."
					: "Discovery stopped with errors or was cancelled. Earlier chunks are preserved.");
			} catch(Exception ex) {
				SetStatus("Discovery failed: " + ex.Message);
				Log("ERROR: " + ex);
				SetCaptureState("IDLE");
			} finally {
				_busy = false;
				_captureActive = false;
				_discoverStartButton.IsEnabled = true;
				_discoverCancelButton.IsEnabled = false;
				_discoveryCancellation?.Dispose();
				_discoveryCancellation = null;
				_pauseCaptureButton.IsEnabled = false;
				_stopCaptureButton.IsEnabled = _capturePaused;
				UpdateStatusBar();
			}
		}

		private void DiscoverStart_Click(object? sender, RoutedEventArgs e) => _ = StartDiscoveryAsync(resume: _capturePaused);

		private void DiscoverCancel_Click(object? sender, RoutedEventArgs e)
		{
			if(_discoveryCancellation == null) return;
			_discoveryCancellation.Cancel();
			_discoveryStatusText.Text = "Cancel requested. Current chunk stops; earlier chunks are saved.";
			_discoverCancelButton.IsEnabled = false;
		}

		//------------------------------------------------------------------ live capture

		private void StartLiveCapture()
		{
			if(_project == null || string.IsNullOrWhiteSpace(_romPath)) {
				SetStatus("Open a ROM first, start playing in Mesen, then press Record.");
				return;
			}
			try {
				string samplingMode = _samplingModeBox.SelectedIndex switch {
					0 => HdStudioSamplingModes.Efficient,
					2 => HdStudioSamplingModes.Exhaustive,
					3 => HdStudioSamplingModes.SingleFrame,
					_ => HdStudioSamplingModes.Balanced
				};
				_liveSession = HdStudioLiveCapture.Start(_project, samplingMode, Log);
				_pausedLiveFrame = null;
				_captureActive = true;
				_capturePaused = false;
				SetCaptureState("LIVE");
				_discoverStartButton.IsEnabled = false;
				_discoverCancelButton.IsEnabled = false;
				_discoveryProgress.IsIndeterminate = true;
				_animationTimer?.Stop();
				_liveTimer?.Stop();
				_liveTimer = new DispatcherTimer() { Interval = TimeSpan.FromMilliseconds(700) };
				_liveTimer.Tick += (_, _) => UpdateLiveCaptureStatus();
				_liveTimer.Start();
				ShowWorkspace("discover");
				_discoveryStatusText.Text = "Live capture is recording the real emulator frames. Play normally in the Mesen window.";
				_gameplayHintText.Text = "Live: play the game, then press Pause capture to select an object in the frozen frame.";
				_dockCaptureMode.Text = "Live gameplay";
				_dockCaptureStatus.Text = "Live capture recording";
				Log("Live capture: session started.");
				SetStatus("Live capture running. Play the game normally in Mesen.");
			} catch(Exception ex) {
				_liveSession = null;
				SetCaptureState("IDLE");
				SetStatus("Live capture could not start: " + ex.Message);
				Log("ERROR: live capture: " + ex);
			}
		}

		private void UpdateLiveCaptureStatus()
		{
			if(_liveSession == null) {
				return;
			}
			HdStudioLiveCapture.LiveStatus? status = HdStudioLiveCapture.ReadStatus(_liveSession);
			if(status == null) {
				return;
			}
			_captureFramesText.Text = status.Samples.ToString("N0");
			_capturePatternsText.Text = status.Patterns.ToString("N0");
			_captureNewText.Text = status.GameplayFrames.ToString("N0") + " live";
			_captureAssetsText.Text = _catalog.Assets.Count.ToString("N0");
			_captureSessionText.Text = "Live gameplay - frame " + status.Frame.ToString("N0");
			_dockCaptureFrames.Text = status.Samples.ToString("N0");
			_dockCaptureChunks.Text = "-";
			_dockCaptureReason.Text = status.Paused ? "paused" : "recording";
			double overhead = status.Samples > 0 ? status.SampleMicros / 1000.0 / status.Samples : 0;
			string evidence = status.OamFallbackSamples > 0
				? $"{status.DisplaySpriteSamples:N0} render-time, {status.OamFallbackSamples:N0} OAM fallback"
				: $"{status.DisplaySpriteSamples:N0} render-time samples";
			string limit = status.PatternLimitReached ? " PATTERN BUDGET REACHED." : "";
			string message = status.Paused
				? $"Live capture paused at frame {status.Frame:N0}. The game keeps running; the frozen frame is ready for picking. {evidence}, {overhead:0.0} ms/sample average.{limit}"
				: $"Live ({status.CaptureMode}): frame {status.Frame:N0}, {status.Samples:N0} samples, {status.SpriteOccurrences:N0} sprite occurrences, {status.Patterns:N0} patterns, {status.BackgroundBlocks:N0} background blocks. {evidence}, {overhead:0.0} ms/sample.{limit}";
			_discoveryStatusText.Text = message;
			_dockCaptureStatus.Text = message;
			if(status.Paused && !_capturePaused) {
				_capturePaused = true;
				SetCaptureState("LIVE-PAUSED");
				_discoveryProgress.IsIndeterminate = false;
			} else if(!status.Paused && _capturePaused && _liveSession != null) {
				_capturePaused = false;
				SetCaptureState("LIVE");
				_discoveryProgress.IsIndeterminate = true;
			}
			if(status.Paused && _pausedLiveFrame == null) {
				LoadLivePauseFrame();
			}
			if(status.Stopped) {
				_liveTimer?.Stop();
			}
		}

		private void UpdateLiveCaptureStatusFromDisk()
		{
			try {
				string path = Path.Combine(HdStudioLiveCapture.GetFolder(_packFolder), "live-status.json");
				if(!File.Exists(path)) return;
				using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
				System.Text.Json.JsonElement root = doc.RootElement;
				int Int(string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out int result) ? result : 0;
				int samples = Int("samples");
				int patterns = Int("patterns");
				int occurrences = Int("spriteOccurrences");
				int blocks = Int("backgroundBlocks");
				int frame = Int("frame");
				_captureFramesText.Text = samples.ToString("N0");
				_capturePatternsText.Text = patterns.ToString("N0");
				_captureAssetsText.Text = _catalog.Assets.Count.ToString("N0");
				_captureNewText.Text = Int("gameplayFrames").ToString("N0") + " live";
				_captureSessionText.Text = "Live gameplay - frame " + frame.ToString("N0");
				_dockCaptureFrames.Text = samples.ToString("N0");
				_dockCaptureMode.Text = "Live gameplay";
				_dockCaptureReason.Text = "paused";
				int displaySamples = Int("displaySpriteSamples");
				int fallbackSamples = Int("oamFallbackSamples");
				long sampleMicros = root.TryGetProperty("sampleMicros", out var micros) && micros.TryGetInt64(out long sm) ? sm : 0;
				double overhead = samples > 0 ? sampleMicros / 1000.0 / samples : 0;
				string message = $"Live capture paused at frame {frame:N0}: {samples:N0} samples, {occurrences:N0} sprite occurrences, {patterns:N0} patterns, {blocks:N0} background blocks. {displaySamples:N0} render-time / {fallbackSamples:N0} OAM fallback, {overhead:0.0} ms/sample.";
				_discoveryStatusText.Text = message;
				_dockCaptureStatus.Text = message;
				_discoveryProgress.IsIndeterminate = false;
				_discoveryProgress.Value = 0;
			} catch { }
		}

		private void EmulatorPause_Click(object? sender, RoutedEventArgs e)
		{
			if(!EmuApi.IsRunning()) {
				SetStatus("No emulator is running. Open a game first.");
				return;
			}
			EmuApi.Pause();
			SetStatus("Emulator paused. This is separate from Pause capture: recording state is unchanged.");
			Log("Emulator paused explicitly from HD Studio (recorder state unchanged).");
		}

		private void EmulatorResume_Click(object? sender, RoutedEventArgs e)
		{
			if(!EmuApi.IsRunning()) {
				SetStatus("No emulator is running. Open a game first.");
				return;
			}
			EmuApi.Resume();
			SetStatus("Emulator resumed. Recording state is unchanged.");
			Log("Emulator resumed explicitly from HD Studio (recorder state unchanged).");
		}

		private async void PauseLiveCapture()
		{
			if(_liveSession == null) return;
			HdStudioLiveCapture.SendCommand(_liveSession, "pause");
			_capturePaused = true;
			SetCaptureState("LIVE-PAUSED");
			SetStatus("Live capture paused. Waiting for the frozen frame...");
			await Task.Delay(800);
			UpdateLiveCaptureStatus();
			LoadLivePauseFrame();
		}

		private void ResumeLiveCapture()
		{
			if(_liveSession == null) return;
			HdStudioLiveCapture.SendCommand(_liveSession, "resume");
			_capturePaused = false;
			_pausedLiveFrame = null;
			_gameplayCanvas.PickMode = false;
			_gameplayCanvas.HardwareBoxes.Clear();
			SetCaptureState("LIVE");
			SetStatus("Live capture resumed. Continue playing in Mesen.");
		}

		private async Task StopLiveCaptureAsync()
		{
			if(_liveSession == null) return;
			HdStudioLiveCapture.SendCommand(_liveSession, "pause");
			await Task.Delay(700);
			HdStudioLiveCapture.SendCommand(_liveSession, "stop");
			await Task.Delay(900);
			HdStudioLiveCapture.Remove(_liveSession);
			_liveTimer?.Stop();
			_liveTimer = null;
			_liveSession = null;
			_captureActive = false;
			_capturePaused = false;
			_discoveryProgress.IsIndeterminate = false;
			SetCaptureState("COMPLETE");
			_discoverStartButton.IsEnabled = true;
			try {
				SetStatus("Live capture stopped. Importing observed assets...");
				int code = await RunHeadlessAsync("--hdstudio", _romPath, "--assets", "--out=" + _packFolder);
				RefreshFromDisk();
				LoadLivePauseFrame();
				int picked = HdStudioPicking.ReadPickedAssetElements(_packFolder).Count;
				SetStatus($"Live capture imported (exit {code}): {_catalog.Assets.Count:N0} editable assets, {picked} manual pick(s). Approved artwork was preserved.");
				_discoveryStatusText.Text = $"Live capture complete: {_catalog.Assets.Count:N0} assets. Frame picking stays available on the frozen frame.";
				Log($"Live capture: imported {_catalog.Assets.Count} assets, {picked} manual picks.");
			} catch(Exception ex) {
				SetStatus("Live capture stopped, but asset import failed: " + ex.Message);
				Log("ERROR: live import: " + ex);
			}
		}

		private void LoadLivePauseFrame()
		{
			_pausedLiveFrame = HdStudioPicking.PauseFrame.Load(_packFolder);
			if(_pausedLiveFrame == null) {
				SetStatus("No paused live frame yet. Wait a moment and press Pause again.");
				return;
			}
			if(!string.IsNullOrWhiteSpace(_pausedLiveFrame.ScreenshotPath) && File.Exists(_pausedLiveFrame.ScreenshotPath)) {
				_gameplayCanvas.FramePath = _pausedLiveFrame.ScreenshotPath;
				_gameplayCanvas.Reload();
				ShowBitmap(_verifyOriginalImage, _pausedLiveFrame.ScreenshotPath);
			}
			_gameplayCanvas.SelectMode = true;
			_gameplayCanvas.PickMode = true;
			_gameplayCanvas.ClearSelection();
			_gameplayCanvas.HardwareBoxes.Clear();
			foreach(var group in _pausedLiveFrame.Sprites.GroupBy(s => s.Oam)) {
				PauseSpriteBox(group.ToList());
			}
			_gameplayCanvas.InvalidateVisual();
			_pickCandidates.Clear();
			_candidateRows.Clear();
			_gameplayCandidateList.ItemsSource = null;
			_gameplayHintText.Text = $"Frame {_pausedLiveFrame.Frame} frozen. Click any object to resolve OAM sprites, palette and CHR patterns.";
			_gameplaySelectionText.Text = $"Live frame {_pausedLiveFrame.Frame}: {_pausedLiveFrame.Sprites.Count} hardware sprite parts, scroll {_pausedLiveFrame.ScrollX},{_pausedLiveFrame.ScrollY}, {_pausedLiveFrame.Mirroring}.";
			_candidateHintText.Text = "Click an object in the frame to list its hardware sprites, composition and nametable cell.";
		}

		private void PauseSpriteBox(List<HdStudioPicking.PauseSprite> parts)
		{
			int x = parts.Min(p => p.X);
			int y = parts.Min(p => p.Y);
			int w = parts.Max(p => p.X + 8) - x;
			int h = parts.Max(p => p.Y + 8) - y;
			_gameplayCanvas.HardwareBoxes.Add((x, y, Math.Max(8, w), Math.Max(8, h), "slot " + parts[0].Oam));
		}

		private void HandleGameplayPointPicked(Point point)
		{
			if(_pausedLiveFrame == null) {
				SetStatus("Pause live capture first; picking needs a frozen frame with OAM and pattern data.");
				return;
			}
			_pickCandidates.Clear();
			_pickCandidates.AddRange(HdStudioPicking.GetCandidates(_pausedLiveFrame, (int)point.X, (int)point.Y, _palette));
			_candidateRows.Clear();
			foreach(HdStudioPicking.PickCandidate candidate in _pickCandidates) {
				if(candidate.Asset == null) continue;
				_candidateRows.Add(new HdStudioCandidateRow() {
					Asset = candidate.Asset,
					Thumbnail = LoadBitmap(HdStudioCatalog.EnsureOriginalPreview(_catalog, candidate.Asset, _palette)),
					Category = candidate.Kind + " - " + candidate.Width + "x" + candidate.Height + " - " + candidate.Confidence.ToString("0.00"),
					Pick = candidate
				});
			}
			_gameplayCandidateList.ItemsSource = _candidateRows.ToList();
			if(_candidateRows.Count > 0) {
				_gameplayCandidateList.SelectedIndex = 0;
				HdStudioPicking.PickCandidate first = _pickCandidates[0];
				_gameplayCanvas.SetSelectionNes(first.X, first.Y, first.Width, first.Height);
			}
			_gameplaySelectionText.Text = _pickCandidates.Count == 0
				? $"No hardware sprite or background cell at {point.X},{point.Y}."
				: $"{_pickCandidates.Count} candidate(s) at {point.X},{point.Y}. The first is the topmost hardware sprite (lowest OAM index).";
			_candidateHintText.Text = _candidateRows.Count == 0
				? "Nothing was drawn at that pixel in the frozen frame."
				: "Choose a hardware sprite, the reconstructed composition, or the background tile. Group selection merges multiple picks.";
		}

		private void GroupCandidates_Click(object? sender, RoutedEventArgs e)
		{
			if(_pausedLiveFrame == null) {
				SetStatus("Pause live capture and pick objects first.");
				return;
			}
			List<HdStudioPicking.PickCandidate> selected = _gameplayCandidateList.SelectedItems?
				.OfType<HdStudioCandidateRow>()
				.Where(r => r.Pick != null)
				.Select(r => r.Pick!)
				.ToList() ?? new List<HdStudioPicking.PickCandidate>();
			if(selected.Count < 2) {
				SetStatus("Select at least two candidate rows (hold Ctrl) to build a manual composition.");
				return;
			}
			HdStudioPicking.PickCandidate merged = new() {
				Kind = "manualComposition",
				Name = "Manual composition (" + selected.Sum(c => c.Sprites.Count + c.Background.Count) + " cells)",
				Detail = "user grouped " + selected.Count + " candidate(s)",
				Confidence = 1.0
			};
			foreach(HdStudioPicking.PickCandidate candidate in selected) {
				merged.Sprites.AddRange(candidate.Sprites);
				merged.Background.AddRange(candidate.Background);
			}
			merged.Sprites.Sort((a, b) => a.Oam != b.Oam ? a.Oam.CompareTo(b.Oam) : a.Part.CompareTo(b.Part));
			HdStudioCatalogAsset asset = HdStudioPicking.BuildManualComposition(merged, _pausedLiveFrame, _palette);
			string path = HdStudioPicking.SavePickedAsset(_packFolder, asset, Log);
			RefreshFromDisk();
			HdStudioCatalogAsset? saved = _catalog.Find(asset.Id);
			if(saved != null) {
				_newAssetIds.Add(saved.Id);
				SelectAsset(saved);
				ShowWorkspace("artwork");
				SetStatus($"Manual composition saved to {asset.Name} ({path}).");
			}
		}

		/// <summary>
		/// Manual composition splitting: every selected hardware cell becomes its own
		/// persistent picked asset. This keeps the original pattern/palette/position
		/// evidence and lets the user edit or replace individual parts without changing
		/// the original game geometry.
		/// </summary>
		private void AnalyzeContext_Click(object? sender, RoutedEventArgs e)
		{
			if(_selectedAsset == null || string.IsNullOrWhiteSpace(_packFolder)) {
				SetStatus("Select an asset after a live capture first.");
				return;
			}
			string path = Path.Combine(_packFolder, HdStudioReport.StudioFolderName, "context-plan.json");
			int code = HdStudioContextPlanner.WritePlan(_packFolder, _selectedAsset.Id, path, Log);
			string? status = null;
			try {
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
				status = doc.RootElement.TryGetProperty("status", out JsonElement s) ? s.GetString() : null;
			} catch { }
			SetStatus(status == null
				? "Context analysis finished (exit " + code + ")."
				: $"Context: {status}. Measure intended/unrelated occurrences in {path}.");
		}

		private void SaveAssetMetadata_Click(object? sender, RoutedEventArgs e)
		{
			if(_selectedAsset == null) {
				SetStatus("Select an asset first.");
				return;
			}
			string name = C<TextBox>("AssetNameBox").Text?.Trim() ?? "";
			string category = C<TextBox>("AssetCategoryBox").Text?.Trim() ?? "";
			if(name.Length == 0) {
				SetStatus("The asset name cannot be empty.");
				return;
			}
			_selectedAsset.Name = name;
			_selectedAsset.CustomCategory = category;
			bool persisted = HdStudioPicking.RenamePickedAsset(_packFolder, _selectedAsset.Id, name, category, Log);
			RefreshAssetRows();
			RefreshSpriteGallery();
			RefreshBackgroundRows();
			SelectAsset(_selectedAsset, navigate: false);
			SetStatus(persisted
				? $"Saved metadata for {name}."
				: "The name is updated in this session. Persistent custom names are available for picked assets; automatic discoveries are regenerated from measured evidence.");
		}

		private void SplitCandidates_Click(object? sender, RoutedEventArgs e)
		{
			if(_pausedLiveFrame == null) {
				SetStatus("Pause live capture and pick an object first.");
				return;
			}
			List<HdStudioCandidateRow> selectedRows = _gameplayCandidateList.SelectedItems?
				.OfType<HdStudioCandidateRow>()
				.Where(r => r.Pick != null).ToList() ?? new List<HdStudioCandidateRow>();
			if(selectedRows.Count == 0) {
				SetStatus("Select one or more candidate rows to split.");
				return;
			}
			int savedCount = 0;
			int part = 1;
			foreach(HdStudioCandidateRow row in selectedRows) {
				HdStudioPicking.PickCandidate source = row.Pick!;
				foreach(HdStudioPicking.PauseSprite sprite in source.Sprites) {
					HdStudioPicking.PickCandidate partCandidate = new() {
						Kind = "hardwareSprite",
						Name = "Split part " + part,
						Detail = $"split from {source.Name}, render slot {sprite.Oam} part {sprite.Part}",
						Confidence = 1.0
					};
					partCandidate.Sprites.Add(sprite);
					HdStudioCatalogAsset asset = HdStudioPicking.BuildManualComposition(partCandidate, _pausedLiveFrame, _palette);
					HdStudioPicking.SavePickedAsset(_packFolder, asset, Log);
					part++;
					savedCount++;
				}
				foreach(HdStudioPicking.PauseBackgroundTile tile in source.Background) {
					HdStudioPicking.PickCandidate partCandidate = new() {
						Kind = "backgroundTile",
						Name = "Split background part " + part,
						Detail = $"split from {source.Name}, nametable {tile.TileX},{tile.TileY}",
						Confidence = 1.0
					};
					partCandidate.Background.Add(tile);
					HdStudioCatalogAsset asset = HdStudioPicking.BuildManualComposition(partCandidate, _pausedLiveFrame, _palette);
					HdStudioPicking.SavePickedAsset(_packFolder, asset, Log);
					part++;
					savedCount++;
				}
			}
			RefreshFromDisk();
			C<TextBlock>("CandidateHintText").Text = $"{savedCount} individual part(s) saved as persistent assets. Each keeps its original pattern and palette.";
			SetStatus($"{savedCount} split asset(s) saved. Select any part in the Asset Library to edit it independently.");
		}

		private void PauseCapture_Click(object? sender, RoutedEventArgs e)
		{
			if(_liveSession != null) {
				PauseLiveCapture();
				return;
			}
			if(_captureActive && _discoveryCancellation != null) {
				_discoveryCancellation.Cancel();
				_capturePaused = true;
				SetCaptureState("PAUSED");
				SetStatus("Headless discovery paused: the current chunk was cancelled, not the emulator. Progress is preserved; Resume starts a new chunk.");
			} else if(_capturePaused) {
				SetStatus("Capture is already paused.");
			}
		}

		private async void ResumeCapture_Click(object? sender, RoutedEventArgs e)
		{
			if(_liveSession != null) {
				ResumeLiveCapture();
				return;
			}
			if(_capturePaused) {
				await StartDiscoveryAsync(resume: true);
			} else {
				SetStatus("No paused capture to resume.");
			}
		}

		private async void StopCapture_Click(object? sender, RoutedEventArgs e)
		{
			if(_liveSession != null) {
				await StopLiveCaptureAsync();
				return;
			}
			_discoveryCancellation?.Cancel();
			_capturePaused = false;
			_captureActive = false;
			SetCaptureState("IDLE");
			UpdateStatusBar();
			SetStatus("Headless discovery stopped. Existing chunks and approved artwork are preserved.");
		}

		private void SetCaptureState(string state)
		{
			_captureState = state;
			_captureStateText.Text = state;
			_captureStateBadge.Classes.Set("warning", state == "RECORDING" || state == "LIVE");
			_captureStateBadge.Classes.Set("info", state == "PAUSED" || state == "LIVE-PAUSED");
			_captureStateBadge.Classes.Set("success", state == "COMPLETE");
			_captureSessionText.Text = state switch {
				"LIVE" => "Live gameplay recording",
				"LIVE-PAUSED" => "Live capture paused",
				"RECORDING" => "Headless discovery running",
				"PAUSED" => "Headless discovery paused",
				"COMPLETE" => "Capture complete",
				_ => "No session yet"
			};
			_captureStateText.Text = state switch {
				"LIVE" => "LIVE",
				"LIVE-PAUSED" => "PAUSED",
				_ => state
			};
			_capturePaused = state == "LIVE-PAUSED" || state == "PAUSED";
			UpdateStatusBar();
		}

		private void ShowDiscoverySession(HdStudioDiscoverySession session, string stopReason)
		{
			List<HdStudioSessionRow> rows = new();
			foreach(HdStudioDiscoverySnapshot snapshot in session.Snapshots) {
				rows.Add(new HdStudioSessionRow() {
					Chunk = "#" + snapshot.Chunk,
					Headline = $"+{snapshot.Frames:N0} frames · {snapshot.Assets:N0} assets (+{snapshot.NewAssets})",
					Detail = $"{snapshot.ChrTilesSeen:N0} patterns (+{snapshot.NewPatterns}) · {snapshot.Seconds:0.0}s · seed {snapshot.Seed}"
				});
			}
			_discoverySessionList.ItemsSource = rows;
			_discoveryStatusText.Text = $"Session: {session.FramesCaptured:N0} of {session.RequestedFrames:N0} frames, {session.Chunks} chunk(s), stopped by {stopReason}.";
			_captureSessionText.Text = $"{session.Chunks} chunk(s) · {session.FramesCaptured:N0} frames";
			_capturePercentText.Text = (session.RequestedFrames > 0 ? Math.Clamp(100.0 * session.FramesCaptured / session.RequestedFrames, 0, 100) : 0).ToString("0") + "%";
			_captureFramesText.Text = session.FramesCaptured.ToString("N0");
			_captureAssetsText.Text = session.LastAssets.ToString("N0");
			_capturePatternsText.Text = session.LastPatterns.ToString("N0");
			_captureNewText.Text = Math.Max(0, session.Snapshots.Sum(s => s.NewAssets)).ToString("N0");
			_dockCaptureProgress.Value = _discoveryProgress.Value;
			_dockCaptureStatus.Text = _discoveryStatusText.Text;
			_dockCaptureMode.Text = HdStudioDiscoveryModes.DisplayName(session.Mode);
			_dockCaptureFrames.Text = session.FramesCaptured.ToString("N0");
			_dockCaptureChunks.Text = session.Chunks.ToString();
			_dockCaptureReason.Text = stopReason;
			System.Text.StringBuilder metrics = new();
			metrics.AppendLine("Mode: " + HdStudioDiscoveryModes.DisplayName(session.Mode));
			metrics.AppendLine("Frames: " + session.FramesCaptured.ToString("N0"));
			metrics.AppendLine("Chunks: " + session.Chunks);
			metrics.AppendLine("Assets: " + session.LastAssets.ToString("N0"));
			metrics.AppendLine("Observed patterns: " + session.LastPatterns.ToString("N0"));
			metrics.AppendLine("Observed tile+palette: " + session.LastSeenCombos.ToString("N0"));
			metrics.AppendLine("Stop reason: " + stopReason);
			metrics.AppendLine();
			metrics.AppendLine("Saturation means the measured counts stopped growing; it is not a completeness proof.");
			_discoveryMetricsText.Text = metrics.ToString();
			_discoveryProgress.Value = session.RequestedFrames > 0 ? Math.Clamp(100.0 * session.FramesCaptured / session.RequestedFrames, 0, 100) : 0;
			_dockCaptureProgress.Value = _discoveryProgress.Value;
		}

		private void LoadDiscoverySession()
		{
			try {
				HdStudioDiscoverySession? session = HdStudioDiscoverySession.Load(_packFolder);
				if(session != null) {
					ShowDiscoverySession(session, session.StopReason);
					SetCaptureState(session.StopReason == "cancelled" ? "PAUSED" : "COMPLETE");
				}
			} catch { }
		}

		private async void CaptureFrame_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null || string.IsNullOrWhiteSpace(_romPath)) {
				SetStatus("Open a game first.");
				return;
			}
			await RunBusy("Capturing a deterministic gameplay frame...", async () => {
				if(!File.Exists(Path.Combine(_packFolder, "hires.txt"))) {
					HdStudioCompileResult build = await Task.Run(() => HdStudioCompiler.Compile(_project, Log));
					if(!build.Success) {
						SetStatus("Build failed before frame capture.");
						return;
					}
				}
				InstallCurrentPack();
				int code = await RunHeadlessAsync("--hdstudio", _romPath, "--verify", "--shots=1", "--frames=600", "--out=" + _packFolder);
				string verify = Path.Combine(_packFolder, HdStudioReport.StudioFolderName, "verify");
				string? original = Directory.Exists(verify) ? Directory.GetFiles(verify, "frame_*_original.png").OrderBy(f => f).LastOrDefault() : null;
				string? hd = Directory.Exists(verify) ? Directory.GetFiles(verify, "frame_*_hd.png").OrderBy(f => f).LastOrDefault() : null;
				if(original != null) {
					_gameplayCanvas.FramePath = original;
					_gameplayCanvas.Reload();
					ShowBitmap(_verifyOriginalImage, original);
					SetCaptureState("PAUSED");
					_gameplayHintText.Text = "Frame paused. Use Select object to mark an 8×8-aligned region.";
					SetStatus("Gameplay frame captured (exit " + code + "). Select an object or inspect the composition.");
				} else {
					SetStatus("Frame capture did not produce an image. See the console.");
				}
				if(hd != null) ShowBitmap(_verifyHdImage, hd);
				if(original != null || hd != null) {
					_verifySummaryText.Text = "Captured from the real emulator at " + verify + ". Use Verify for a multi-frame comparison.";
				}
			});
		}

		private void SelectObject_Click(object? sender, RoutedEventArgs e)
		{
			if(string.IsNullOrWhiteSpace(_gameplayCanvas.FramePath)) {
				SetStatus("Capture a frame first.");
				return;
			}
			_gameplayCanvas.SelectMode = !_gameplayCanvas.SelectMode;
			_selectObjectButton.Classes.Set("active", _gameplayCanvas.SelectMode);
			_gameplayHintText.Text = _gameplayCanvas.SelectMode
				? "Select object active: drag an 8×8-aligned region on the paused frame."
				: "Selection disabled. Browse the frame or capture another one.";
		}

		private void InspectBackground_Click(object? sender, RoutedEventArgs e)
		{
			_candidateFilter = "background";
			_candidateFilterText.Text = "Backgrounds";
			if(_gameplayCanvas.TryGetSelectionNes(out int x, out int y, out int w, out int h)) UpdateGameplayCandidates();
			else SetStatus("Select a region first; background candidates will be filtered.");
		}

		private void InspectSprite_Click(object? sender, RoutedEventArgs e)
		{
			_candidateFilter = "sprite";
			_candidateFilterText.Text = "Sprites and objects";
			if(_gameplayCanvas.TryGetSelectionNes(out int x, out int y, out int w, out int h)) UpdateGameplayCandidates();
			else SetStatus("Select a region first; sprite candidates will be filtered.");
		}

		private string _candidateFilter = "sprite";

		private void UpdateGameplayCandidates()
		{
			_candidateRows.Clear();
			if(_gameplayCanvas.TryGetSelectionNes(out int x, out int y, out int w, out int h)) {
				_gameplaySelectionText.Text = $"Selected {w}×{h} NES pixels at ({x},{y}) · snapped to the original 8×8 grid.";
				IEnumerable<HdStudioCatalogAsset> query = _catalog.Assets.Where(a => a.Tiles.Count > 0);
				if(_candidateFilter == "background") {
					query = query.Where(a => a.Category is "Backgrounds" or "Background tiles" || a.Kind == "backgroundBlock");
				} else {
					query = query.Where(a => a.Category is "Objects" or "Rendered metasprites" || a.Kind is "semanticObject" or "metasprite" or "animationFrame");
				}
				double targetRatio = w / (double)Math.Max(1, h);
				var candidates = query
					.Select(a => new {
						Asset = a,
						Score = Math.Abs(a.Width - w) + Math.Abs(a.Height - h) + Math.Abs(a.Width / (double)Math.Max(1, a.Height) - targetRatio) * 24
					})
					.OrderBy(c => c.Score)
					.Take(10)
					.ToList();
				foreach(var candidate in candidates) {
					_candidateRows.Add(new HdStudioCandidateRow() {
						Asset = candidate.Asset,
						Thumbnail = LoadBitmap(HdStudioCatalog.EnsureOriginalPreview(_catalog, candidate.Asset, _palette)),
						Category = $"{FriendlyCategory(candidate.Asset)} · {candidate.Asset.Width}×{candidate.Asset.Height}"
					});
				}
				_gameplayCandidateList.ItemsSource = _candidateRows.ToList();
				if(_candidateRows.Count > 0) _gameplayCandidateList.SelectedIndex = 0;
				_candidateHintText.Text = _candidateRows.Count == 0 ? "No candidate assets match this region." : "Pick a match to open it in the Artwork Workshop.";
				_gameplayHintText.Text = "Object selected. The region is aligned to the original NES grid.";
			} else {
				_gameplaySelectionText.Text = "No region selected.";
			}
		}

		private void ClearSelection_Click(object? sender, RoutedEventArgs e)
		{
			_gameplayCanvas.ClearSelection();
			_candidateRows.Clear();
			_gameplayCandidateList.ItemsSource = null;
			_gameplaySelectionText.Text = "No region selected.";
			_gameplayHintText.Text = "Capture a frame, then use Select object to mark an 8×8-aligned region.";
		}

		private HdStudioCatalogAsset? EnsurePickedAsset(HdStudioCandidateRow? row)
		{
			if(row == null) {
				return null;
			}
			if(row.Pick?.Asset == null) {
				return row.Asset;
			}
			HdStudioPicking.SavePickedAsset(_packFolder, row.Pick.Asset, Log);
			RefreshFromDisk();
			HdStudioCatalogAsset? saved = _catalog.Find(row.Pick.Asset.Id);
			if(saved != null) {
				_newAssetIds.Add(saved.Id);
				return saved;
			}
			return row.Pick.Asset;
		}

		private void OpenSelectionInWorkshop_Click(object? sender, RoutedEventArgs e)
		{
			HdStudioCandidateRow? row = _gameplayCandidateList.SelectedItem as HdStudioCandidateRow ?? _candidateRows.FirstOrDefault();
			HdStudioCatalogAsset? asset = EnsurePickedAsset(row);
			if(asset == null) {
				SetStatus("No matching asset was found. Pause live capture and click an object first.");
				return;
			}
			SelectAsset(asset);
			ShowWorkspace("artwork");
			SetStatus("Opened " + asset.Name + " in the Artwork Workshop with its original hardware patterns.");
		}

		private void AddDiscovered_Click(object? sender, RoutedEventArgs e)
		{
			HdStudioCandidateRow? row = _gameplayCandidateList.SelectedItem as HdStudioCandidateRow ?? _candidateRows.FirstOrDefault();
			HdStudioCatalogAsset? asset = EnsurePickedAsset(row);
			if(asset == null || !_gameplayCanvas.TryGetSelectionNes(out int x, out int y, out int w, out int h)) {
				SetStatus("Select a gameplay region and a matching asset first.");
				return;
			}
			try {
				//The crop is only a starting artwork preview. The asset itself keeps the
				//mapper-resolved pattern/palette references and can compile a real HD Pack.
				string crop = CropGameplayRegion(x, y, w, h);
				if(crop.Length == 0) {
					SetStatus("The selected region could not be cropped.");
					return;
				}
				SelectAsset(asset);
				SetSourceArtwork(crop);
				ShowWorkspace("artwork");
				SetStatus($"Added {asset.Name} to the workshop. The replacement uses the picked hardware patterns, not a screenshot crop.");
			} catch(Exception ex) {
				SetStatus("Could not add the discovered region: " + ex.Message);
				Log("ERROR: " + ex);
			}
		}

		private string CropGameplayRegion(int x, int y, int w, int h)
		{
			string frame = _gameplayCanvas.FramePath;
			if(string.IsNullOrWhiteSpace(frame) || !File.Exists(frame)) return "";
			using SKBitmap? bitmap = SKBitmap.Decode(frame);
			if(bitmap == null) return "";
			double sx = bitmap.Width / 256.0;
			double sy = bitmap.Height / 240.0;
			SKRectI rect = new(
				Math.Clamp((int)Math.Round(x * sx), 0, bitmap.Width - 1),
				Math.Clamp((int)Math.Round(y * sy), 0, bitmap.Height - 1),
				Math.Clamp((int)Math.Round((x + w) * sx), 1, bitmap.Width),
				Math.Clamp((int)Math.Round((y + h) * sy), 1, bitmap.Height));
			using SKBitmap crop = new(Math.Max(1, rect.Width), Math.Max(1, rect.Height));
			using(SKCanvas canvas = new(crop)) {
				canvas.Clear(SKColors.Transparent);
				canvas.DrawBitmap(bitmap, rect, new SKRect(0, 0, crop.Width, crop.Height));
			}
			string folder = Path.Combine(_packFolder, HdStudioReport.StudioFolderName, "discovery-crops");
			Directory.CreateDirectory(folder);
			string path = Path.Combine(folder, $"region_{DateTime.UtcNow:yyyyMMdd-HHmmss}.png");
			using(FileStream stream = File.Create(path)) {
				crop.Encode(stream, SKEncodedImageFormat.Png, 100);
			}
			return path;
		}

		private void BrowseMovie_Click(object? sender, RoutedEventArgs e) => _ = BrowseInputAsync("Select an input movie", "*.fm2", "*.mmo", "*.bk2");
		private void BrowseScript_Click(object? sender, RoutedEventArgs e) => _ = BrowseInputAsync("Select a Lua input script", "*.lua");

		private async Task BrowseInputAsync(string title, params string[] patterns)
		{
			var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions() {
				Title = title,
				AllowMultiple = false,
				FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Supported files") { Patterns = patterns.ToList() } }
			});
			if(files.Count > 0) {
				if(patterns.Contains("*.lua")) _discoveryScriptBox.Text = files[0].Path.LocalPath;
				else _discoveryMovieBox.Text = files[0].Path.LocalPath;
			}
		}

		//------------------------------------------------------------------ export

		private void UpdateExportPaths()
		{
			string projectFolder = string.IsNullOrWhiteSpace(_packFolder) ? "" : Path.Combine(_packFolder, HdStudioReport.StudioFolderName);
			_exportProjectFolderText.Text = string.IsNullOrWhiteSpace(projectFolder) ? "No project folder loaded." : projectFolder;
			_exportCurrentPackText.Text = string.IsNullOrWhiteSpace(_packFolder) ? "No pack folder loaded." : _packFolder;
			if(string.IsNullOrWhiteSpace(_exportNameBox.Text) && !string.IsNullOrWhiteSpace(_romPath)) {
				_exportNameBox.Text = HdStudioExport.SanitizePackName(Path.GetFileNameWithoutExtension(_romPath));
			}
			string root = _exportRootBox.Text?.Trim() ?? "";
			if(root.Length == 0) root = HdStudioExport.GetDefaultExportRoot();
			string name = HdStudioExport.SanitizePackName(_exportNameBox.Text ?? "");
			string destination = Path.GetFullPath(Path.Combine(root, name));
			_exportDestinationText.Text = destination;
			_exportDestinationHint.Text = string.Equals(root.TrimEnd('\\', '/'), HdStudioExport.GetDefaultExportRoot().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
				? "Default destination: Documents\\RetroArcDo HD Studio\\Exports"
				: "Custom destination — remembered for the next export.";
			_exportSummaryPackName.Text = name;
			_exportSummaryDestination.Text = destination;
			_exportSummaryApproved.Text = (_project?.Replacements.Count(r => r.Approved) ?? 0) + " approved replacements";
			_exportSummaryScale.Text = "Output scale: " + CurrentScale() + "×";
			_exportSummaryFiles.Text = "Files are counted after a successful export.";
		}

		private async void BrowseExportRoot_Click(object? sender, RoutedEventArgs e)
		{
			string start = _exportRootBox.Text?.Trim() ?? HdStudioExport.GetDefaultExportRoot();
			HdStudioFolderWindow dialog = new("Choose the HD Pack destination", start, _settings.Theme.Equals("light", StringComparison.OrdinalIgnoreCase));
			string? path = await dialog.ShowDialog<string?>(this);
			if(!string.IsNullOrWhiteSpace(path)) {
				_exportRootBox.Text = path;
				_exactExportDestination = "";
				UpdateExportPaths();
				SetStatus("Export destination set: " + path);
			}
		}

		private async void ExportSaveAs_Click(object? sender, RoutedEventArgs e)
		{
			string start = ExportDestination();
			HdStudioFolderWindow dialog = new("Save HD Pack As", start, _settings.Theme.Equals("light", StringComparison.OrdinalIgnoreCase));
			string? path = await dialog.ShowDialog<string?>(this);
			if(!string.IsNullOrWhiteSpace(path)) {
				_exactExportDestination = path;
				_exportRootBox.Text = Path.GetDirectoryName(path) ?? path;
				_exportNameBox.Text = Path.GetFileName(path);
				_exportDestinationText.Text = path;
				_exportDestinationHint.Text = "Exact destination selected with Save As.";
				SetStatus("Export destination set: " + path);
			}
		}

		private void UseRomName_Click(object? sender, RoutedEventArgs e)
		{
			if(!string.IsNullOrWhiteSpace(_romPath)) {
				_exactExportDestination = "";
				_exportNameBox.Text = HdStudioExport.SanitizePackName(Path.GetFileNameWithoutExtension(_romPath));
				UpdateExportPaths();
			}
		}

		private void ExportPath_TextChanged(object? sender, TextChangedEventArgs e)
		{
			if(_initialized) UpdateExportPaths();
		}

		private string ExportDestination()
		{
			if(!string.IsNullOrWhiteSpace(_exactExportDestination)) return Path.GetFullPath(_exactExportDestination);
			string root = _exportRootBox.Text?.Trim() ?? "";
			if(root.Length == 0) root = HdStudioExport.GetDefaultExportRoot();
			string name = HdStudioExport.SanitizePackName(_exportNameBox.Text ?? "");
			return Path.GetFullPath(Path.Combine(root, name));
		}

		private async void ExportRun_Click(object? sender, RoutedEventArgs e) => await RunExportAsync(zip: false);

		private async void ExportZip_Click(object? sender, RoutedEventArgs e) => await RunExportAsync(zip: true);

		private async Task RunExportAsync(bool zip)
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
				SetExportSummary(result);
				if(result.Success) {
					_lastExportFolder = result.FinalFolder;
					_settings.ExportRoot = _exportRootBox.Text?.Trim() ?? "";
					_settings.LastExportFolder = _lastExportFolder;
					_settings.LastExportMinimal = options.Minimal;
					_settings.LastExportInstall = options.InstallIntoMesen;
					_settings.LastExportBackup = options.KeepBackup;
					_settings.Save();
					if(zip) {
						string zipPath = ExportZipFile(result.FinalFolder);
						_exportResultText.Text += "\nZIP: " + zipPath;
						_exportSummaryFiles.Text = (result.Files).ToString("N0") + " files · ZIP " + new FileInfo(zipPath).Length.ToString("N0") + " bytes";
					}
					SetStatus("Export completed: " + result.FinalFolder);
				} else {
					SetStatus("Export failed; the previous pack was not modified.");
					ShowBottomTab("validation");
				}
			});
		}

		private void SetExportSummary(HdStudioExportResult result)
		{
			_exportSummaryPackName.Text = string.IsNullOrWhiteSpace(result.FinalFolder) ? "Export" : Path.GetFileName(result.FinalFolder);
			_exportSummaryApproved.Text = result.Replacements.ToString("N0") + " approved replacements";
			_exportSummaryScale.Text = "Output scale: " + CurrentScale() + "×";
			_exportSummaryValidation.Text = result.Success ? "Validation: passed" : "Validation: failed — " + (result.Errors.FirstOrDefault() ?? "see details");
			_exportSummaryFiles.Text = $"{result.Files:N0} files · {result.Images:N0} images · {result.TileEntries:N0} tile entries · {result.Bytes:N0} bytes";
			_exportSummaryDestination.Text = result.FinalFolder;
		}

		private string ExportZipFile(string folder)
		{
			string zipPath = folder.TrimEnd('\\', '/') + ".zip";
			if(File.Exists(zipPath)) File.Delete(zipPath);
			ZipFile.CreateFromDirectory(folder, zipPath, CompressionLevel.Optimal, false);
			return zipPath;
		}

		private static string FormatExportResult(HdStudioExportResult result)
		{
			System.Text.StringBuilder sb = new();
			sb.AppendLine(result.Success ? "Export succeeded." : "Export failed.");
			sb.AppendLine("Destination: " + result.FinalFolder);
			if(!string.IsNullOrWhiteSpace(result.InstalledFolder)) sb.AppendLine("Installed for Mesen: " + result.InstalledFolder);
			sb.AppendLine($"Files: {result.Files:N0}, bytes: {result.Bytes:N0}, images: {result.Images:N0}, tile entries: {result.TileEntries:N0}, replacements: {result.Replacements:N0}");
			if(result.Errors.Count > 0) {
				sb.AppendLine("Errors:");
				foreach(string error in result.Errors) sb.AppendLine("  - " + error);
			}
			if(result.Warnings.Count > 0) {
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
				SetStatus("Could not open the destination: " + ex.Message);
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

		private async void ExportInstall_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null) return;
			await RunBusy("Installing the HD Pack for Mesen...", async () => {
				string source = !string.IsNullOrWhiteSpace(_lastExportFolder) ? _lastExportFolder : _packFolder;
				string target = Path.Combine(ConfigManager.HdPackFolder, Path.GetFileNameWithoutExtension(_romPath));
				await Task.Run(() => CopyDirectoryContents(source, target, new[] { "HdStudio" }));
				SetStatus("Installed into Mesen: " + target);
				_exportResultText.Text = "Installed for Mesen at " + target;
			});
		}

		//------------------------------------------------------------------ preview / verify / project

		private async void PreviewBuildInstall_Click(object? sender, RoutedEventArgs e)
		{
			await BuildAsync("Building and installing the pack for preview...");
			try {
				InstallCurrentPack();
				_previewStatusText.Text = "Pack installed for Mesen: " + ExpectedMesenPackFolder();
				SetStatus("Pack built and installed for preview.");
				ShowWorkspace("preview");
			} catch(Exception ex) {
				_previewStatusText.Text = "Install failed: " + ex.Message;
			}
		}

		private string ExpectedMesenPackFolder() => Path.Combine(ConfigManager.HdPackFolder, Path.GetFileNameWithoutExtension(_romPath));

		private void InstallCurrentPack()
		{
			if(_project == null) return;
			string expected = ExpectedMesenPackFolder();
			if(string.Equals(Path.GetFullPath(expected).TrimEnd('\\', '/'), Path.GetFullPath(_packFolder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return;
			CopyDirectoryContents(_packFolder, expected, new[] { "HdStudio" });
		}

		private static void CopyDirectoryContents(string source, string target, string[] excludedFolders)
		{
			Directory.CreateDirectory(target);
			foreach(string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
				string relative = Path.GetRelativePath(source, file);
				string first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
				if(excludedFolders.Contains(first, StringComparer.OrdinalIgnoreCase)) continue;
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
			if(_project == null || string.IsNullOrWhiteSpace(_romPath)) return;
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
					? "Verification page: " + html + "\nExit code: " + code + ". The page compares identical deterministic frames and reports gameplay-state hashes."
					: "Verification did not produce a report (exit code " + code + "). See the console.";
				_previewStatusText.Text = code == 0 ? "HD Pack active and deterministic gameplay state comparison completed." : "Verification reported a problem. See the report and console.";
				RefreshAssetRows();
				RefreshSpriteGallery();
				UpdateHomeStats();
				SetStatus(code == 0 ? "Verification completed." : "Verification reported a problem.");
			});
		}

		private void LoadLastVerifyImages()
		{
			try {
				string verifyFolder = Path.Combine(_packFolder, HdStudioReport.StudioFolderName, "verify");
				if(!Directory.Exists(verifyFolder)) return;
				string? original = Directory.GetFiles(verifyFolder, "frame_*_original.png").OrderBy(f => f).FirstOrDefault();
				string? hd = Directory.GetFiles(verifyFolder, "frame_*_hd.png").OrderBy(f => f).FirstOrDefault();
				if(original != null) ShowBitmap(_verifyOriginalImage, original);
				if(hd != null) ShowBitmap(_verifyHdImage, hd);
				if(original != null || hd != null) {
					_previewStatusText.Text = "Last verification images loaded. Run Verify for a new deterministic comparison.";
					VerifySummaryTextSet(verifyFolder);
				}
			} catch { }
		}

		private void VerifySummaryTextSet(string folder)
		{
			_verifySummaryText.Text = "Verification folder: " + folder + ". Use Verify to compare the same frames with and without the HD Pack.";
		}

		private static bool IsReplacementVerified(HdStudioReplacement? replacement)
		{
			return replacement != null && replacement.Approved &&
				replacement.Validation.Any(v => v.StartsWith("verified", StringComparison.OrdinalIgnoreCase));
		}

		private bool HasVerifyImages()
		{
			try {
				string verifyFolder = Path.Combine(_packFolder, HdStudioReport.StudioFolderName, "verify");
				return Directory.Exists(verifyFolder) && Directory.EnumerateFiles(verifyFolder, "frame_*_hd.png").Any();
			} catch { return false; }
		}

		private void SaveProject_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null) return;
			PushUndo();
			int scale = Math.Clamp(IntValue(_scaleBox, _project.OutputScale), 1, 10);
			_project.OutputScale = scale;
			foreach(HdStudioReplacement replacement in _project.Replacements) {
				replacement.PackScale = scale;
				replacement.TargetWidth = replacement.OriginalWidth * scale;
				replacement.TargetHeight = replacement.OriginalHeight * scale;
			}
			_project.Save(HdStudioProject.GetDefaultPath(_packFolder));
			_dirty = false;
			RefreshProjectSummary();
			UpdateStatusBar();
			SetStatus("Project saved at " + scale + "×.");
		}

		private void ReloadProject_Click(object? sender, RoutedEventArgs e)
		{
			RefreshFromDisk();
			SetStatus("Project and catalog reloaded.");
		}

		private async void RebuildBase_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null) return;
			string basePath = Path.Combine(_project.ProjectFolder, "base-hires.txt");
			if(File.Exists(basePath)) {
				File.Delete(basePath);
				Log("Base snapshot deleted; it will be recreated from the current pack.");
			}
			await BuildAsync("Re-snapshotting the base pack and rebuilding...");
		}

		private async void Capture_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null || string.IsNullOrWhiteSpace(_romPath)) {
				SetStatus("A ROM and project are required before capturing.");
				return;
			}
			int frames = IntValue(_framesBox, 18000);
			int scale = Math.Clamp(IntValue(_scaleBox, _project.OutputScale), 1, 10);
			await RunBusy("Running headless capture...", async () => {
				SetStatus("Capture running in a separate process...");
				int code = await RunHeadlessAsync("--hdstudio", _romPath, "--generate", "--bgscan", "--frames=" + frames, "--scale=" + scale, "--filter=prescale", "--out=" + _packFolder);
				if(code != 0) {
					SetStatus("Capture failed with exit code " + code + ". See the console.");
					return;
				}
				int assetCode = await RunHeadlessAsync("--hdstudio", _romPath, "--assets", "--out=" + _packFolder);
				if(assetCode != 0) {
					SetStatus("Asset export failed with exit code " + assetCode + ".");
					return;
				}
				RefreshFromDisk();
				SetStatus("Capture and asset export completed.");
			});
		}

		private async void Compile_Click(object? sender, RoutedEventArgs e) => await BuildAsync("Building the standard pack...");

		private async void Validate_Click(object? sender, RoutedEventArgs e)
		{
			if(_project == null) return;
			await RunBusy("Validating project...", async () => {
				HdStudioCompileResult result = await Task.Run(() => HdStudioCompiler.Compile(_project, Log, dryRun: true));
				SetCompileResult(result);
				SetStatus(result.Success ? "Validation passed." : "Validation found errors.");
				ShowBottomTab("validation");
			});
		}

		private void OpenPackFolder_Click(object? sender, RoutedEventArgs e) => OpenFolder(_packFolder);
		private void OpenDocs_Click(object? sender, RoutedEventArgs e) => OpenUrl("https://git.capricorn.co.jp/senziosavino_b/RetroArcDo_HDStudio");
		private void About_Click(object? sender, RoutedEventArgs e) => ShowAbout();
		private void Help_Click(object? sender, RoutedEventArgs e) => ShowHelp();

		private async void ExportRunButton_Click(object? sender, RoutedEventArgs e) => await RunExportAsync(zip: false);

		//------------------------------------------------------------------ file operations

		private async void OpenArchaeology_Click(object? sender, RoutedEventArgs e)
		{
			if(string.IsNullOrWhiteSpace(_romPath)) {
				await OpenRomAsync();
				if(string.IsNullOrWhiteSpace(_romPath)) {
					return;
				}
			}
			HdStudioArchaeologyWindow window = new(_romPath, _packFolder, this);
			await window.ShowDialog(this);
		}

		/// <summary>
		/// Called by the RetroArchaeology window after the artist selects a discovered
		/// asset. The normal Studio catalog, inspector, importer and verifier remain the
		/// only authoring path; archaeology only projects evidence into that catalog.
		/// </summary>
		public void OpenArchaeologyAsset(string romPath, string packFolder, string assetId)
		{
			_romPath = romPath;
			_packFolder = packFolder;
			_palette = ConfigManager.Config.Nes.UserPalette;
			_romText.Text = Path.GetFileName(_romPath);
			_projectText.Text = Path.GetFileNameWithoutExtension(_romPath) + " \u2022 archaeology";
			Title = "RetroArcDo HD Studio - " + Path.GetFileNameWithoutExtension(_romPath);
			RefreshFromDisk();
			ShowWorkspace("artwork");
			HdStudioCatalogAsset? asset = _catalog.Find(assetId);
			if(asset != null) {
				SelectAsset(asset);
			} else {
				SetStatus("Archaeology asset " + assetId + " is not in the catalog yet; press Reload.");
			}
		}

		private async void OpenRom_Click(object? sender, RoutedEventArgs e) => await OpenRomAsync();

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
			if(files.Count == 0) return;
			string path = files[0].Path.LocalPath;
			if(!File.Exists(path)) {
				SetStatus("The selected ROM no longer exists.");
				return;
			}
			OpenRomPath(path);
		}

		private void OpenRomPath(string path)
		{
			_romPath = path;
			_packFolder = Path.Combine(ConfigManager.HdPackFolder, Path.GetFileNameWithoutExtension(path));
			_palette = ConfigManager.Config.Nes.UserPalette;
			_romText.Text = Path.GetFileName(path);
			_projectText.Text = Path.GetFileNameWithoutExtension(path);
			Title = "RetroArcDo HD Studio — " + Path.GetFileNameWithoutExtension(path);
			try {
				RefreshFromDisk();
				ShowWorkspace(_catalog.Assets.Count > 0 ? "artwork" : "discover");
				SetStatus("Opened " + Path.GetFileName(path) + ".");
			} catch(Exception ex) {
				SetStatus("The ROM could not be opened: " + ex.Message);
				Log("ERROR: " + ex);
			}
		}

		private void NewProject_Click(object? sender, RoutedEventArgs e)
		{
			if(string.IsNullOrWhiteSpace(_romPath)) {
				_ = OpenRomAsync();
				return;
			}
			//A project is always tied to the standard Mesen pack folder so the game finds it.
			//Existing projects are loaded, never silently overwritten.
			ReloadProject();
			RefreshFromDisk();
			ShowWorkspace("discover");
			SetStatus("Project ready for " + Path.GetFileNameWithoutExtension(_romPath) + ".");
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
			if(files.Count == 0) return;
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
				_projectText.Text = project.ProjectName + " · " + project.ChrMode;
				Title = "RetroArcDo HD Studio — " + Path.GetFileNameWithoutExtension(_romPath);
				RefreshFromDisk();
				ShowWorkspace("artwork");
				SetStatus("Project opened: " + projectPath);
			} catch(Exception ex) {
				SetStatus("The project could not be opened: " + ex.Message);
				Log("ERROR: " + ex);
			}
		}

		private void OpenExample_Click(object? sender, RoutedEventArgs e)
		{
			string[] candidates = {
				Path.Combine(Program.OriginalFolder, "examples", "hdstudio_test.nes"),
				Path.Combine(AppContext.BaseDirectory, "examples", "hdstudio_test.nes"),
				Path.Combine(AppContext.BaseDirectory, "..", "..", "test-roms", "hdstudio_test.nes"),
				@"D:\MesenHighWork\test-roms\hdstudio_test.nes",
				@"D:\MesenHigh\test-roms\hdstudio_test.nes"
			};
			string? rom = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
			if(rom == null) {
				SetStatus("The synthetic example is not installed next to this build. Use Open NES Game instead.");
				return;
			}
			OpenRomPath(rom);
			SetStatus("Synthetic example opened. The example pack is a legal generated NES ROM project.");
		}

		private void Window_Drop(object? sender, DragEventArgs e)
		{
			string? path = e.DataTransfer.TryGetFiles()?.Select(f => f.Path.LocalPath).FirstOrDefault(File.Exists);
			if(path == null) return;
			string extension = Path.GetExtension(path);
			if(extension.Equals(".nes", StringComparison.OrdinalIgnoreCase) ||
				extension.Equals(".fds", StringComparison.OrdinalIgnoreCase) ||
				extension.Equals(".unf", StringComparison.OrdinalIgnoreCase) ||
				extension.Equals(".unif", StringComparison.OrdinalIgnoreCase)) {
				OpenRomPath(path);
				return;
			}
			if(_selectedAsset == null) {
				HdStudioCatalogAsset? first = _assetRows.FirstOrDefault()?.Asset;
				if(first != null) SelectAsset(first);
			}
			SetSourceArtwork(path);
			ShowWorkspace("artwork");
		}

		private void Window_KeyDown(object? sender, KeyEventArgs e)
		{
			bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
			if(ctrl && e.Key == Key.O) {
				if(e.KeyModifiers.HasFlag(KeyModifiers.Shift)) OpenProject_Click(sender, e); else OpenRom_Click(sender, e);
				e.Handled = true;
			} else if(ctrl && e.Key == Key.S) {
				SaveProject_Click(sender, e);
				e.Handled = true;
			} else if(ctrl && e.Key == Key.Z) {
				Undo_Click(sender, e);
				e.Handled = true;
			} else if(ctrl && e.Key == Key.Y) {
				Redo_Click(sender, e);
				e.Handled = true;
			} else if(e.Key == Key.Escape) {
				_gameplayCanvas.SelectMode = false;
				_selectObjectButton.Classes.Set("active", false);
			}
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
			foreach(string arg in args) info.ArgumentList.Add(arg);
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

		//------------------------------------------------------------------ sheet slicing

		private async void BrowseSheet_Click(object? sender, RoutedEventArgs e)
		{
			var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions() {
				Title = "Select a sprite sheet",
				AllowMultiple = false,
				FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Images") { Patterns = new List<string>() { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" } } }
			});
			if(files.Count > 0) _sheetPathBox.Text = files[0].Path.LocalPath;
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
					_sliceStatusText.Text = $"{_lastSliceFrames.Count} frames sliced into {folder}.";
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
			if(_lastSliceFrames.Count == 0) SlicePreview_Click(sender, e);
			if(_lastSliceFrames.Count == 0) return;
			if(_sequenceAsset == null) {
				SetStatus("Select an animation sequence first.");
				return;
			}
			List<HdStudioCatalogAsset> frames = _sequenceAsset.Kind == "animationSequence"
				? _catalog.Assets.Where(a => a.Kind == "animationFrame" && a.Tiles.Count > 0 &&
					(string.Equals(a.ParentId, _sequenceAsset.Id, StringComparison.OrdinalIgnoreCase) ||
					 string.Equals(a.AnimationId, _sequenceAsset.Id, StringComparison.OrdinalIgnoreCase)))
					.OrderBy(a => a.FrameIndex).ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToList()
				: new() { _sequenceAsset };
			if(frames.Count == 0) {
				SetStatus("The selected sequence has no captured frames with tile occurrences.");
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
						Id = id, AssetId = asset.Id, AssetName = asset.Name, AssetKind = asset.Kind,
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
			RefreshAssetRows();
			RefreshSpriteGallery();
			RefreshReplacements();
			BuildAnimationTimeline(_sequenceAsset);
			_sliceStatusText.Text = $"Assigned {assigned} sliced frame(s) as drafts.";
			SetStatus($"Assigned {assigned} animation frame(s) as drafts.");
		}

		//------------------------------------------------------------------ settings actions

		private void Theme_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			if(!_initialized) return;
			_settings.Theme = "dark";
			SetStatus("Dark appearance is the validated theme for v0.6 Alpha.");
		}


		private void DefaultGrid_Click(object? sender, RoutedEventArgs e)
		{
			_settings.ShowTileGrid = _defaultGridCheck.IsChecked == true;
			_settings.Save();
			SetEditorGridDefaults(_settings.ShowTileGrid, _settings.ShowCheckerboard);
			RefreshEditorCanvases();
		}

		private void DefaultChecker_Click(object? sender, RoutedEventArgs e)
		{
			_settings.ShowCheckerboard = _defaultCheckerCheck.IsChecked == true;
			_settings.Save();
			SetEditorGridDefaults(_settings.ShowTileGrid, _settings.ShowCheckerboard);
			RefreshEditorCanvases();
		}

		//------------------------------------------------------------------ helper functions

		private int CurrentScale() => Math.Clamp(_project?.OutputScale ?? IntValue(_scaleBox, 4), 1, 10);

		private static int IntValue(NumericUpDown box, int fallback)
		{
			try {
				return box.Value.HasValue ? (int)Math.Round((double)box.Value.Value) : fallback;
			} catch {
				return fallback;
			}
		}

		private static string AnchorValue(int index)
		{
			return index switch {
				0 => "top", 1 => "left", 3 => "right", 4 => "bottom", _ => "center"
			};
		}

		private HdStudioReplacement? FindReplacement(string assetId)
		{
			if(_project == null) return null;
			return _project.Replacements
				.Where(r => string.Equals(r.AssetId, assetId, StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(r => r.Approved)
				.ThenByDescending(r => r.UpdatedAtUtc, StringComparer.Ordinal)
				.FirstOrDefault();
		}

		private void RefreshReplacements()
		{
			_replacementRows.Clear();
			if(_project != null) _replacementRows.AddRange(_project.Replacements.OrderBy(r => r.AssetName, StringComparer.OrdinalIgnoreCase));
			_replacementList.ItemsSource = _replacementRows
				.Select(r => $"{(r.Approved ? "APPROVED" : "DRAFT")}  ·  {r.AssetName}  ·  {r.Cells.Count} tiles{(r.AllowSharedReplacement ? "  ·  shared" : "")}")
				.ToList();
			if(_replacementRows.Count > 0) {
				_replacementList.SelectedIndex = 0;
				_selectedReplacement = _replacementRows[0];
			} else {
				_selectedReplacement = null;
				_replacementInfoText.Text = "No replacements yet. Select an asset, load artwork and use Approve & Apply.";
			}
			UpdateStatusBar();
			UpdateHomeStats();
		}

		private List<HdStudioCatalogAsset> SharedAssets(HdStudioCatalogAsset asset)
		{
			if(asset.Tiles.Count == 0) return new();
			return HdStudioCatalog.FindAssetsSharingKeys(_catalog, asset.Id, asset.Tiles.Select(t => (HdStudioCatalog.GetTileKey(t), t.Palette)));
		}

		private bool HasSharedRisk(HdStudioCatalogAsset asset)
		{
			if(_sharedRiskCache.TryGetValue(asset.Id, out bool cached)) return cached;
			bool risk = SharedAssets(asset).Count > 0;
			_sharedRiskCache[asset.Id] = risk;
			return risk;
		}

		private string DescribeSource(string path)
		{
			try {
				using SKBitmap? bitmap = SKBitmap.Decode(path);
				return bitmap == null ? "—" : $"{bitmap.Width}×{bitmap.Height} px";
			} catch {
				return "—";
			}
		}

		private Bitmap? LoadRowThumbnail(HdStudioAssetRow row)
		{
			QueueThumbnail(row, false);
			return null;
		}

		private Bitmap? LoadRowReplacementThumbnail(HdStudioAssetRow row)
		{
			QueueThumbnail(row, true);
			return null;
		}

		private void QueueThumbnail(HdStudioAssetRow row, bool replacement)
		{
			string key = row.Asset.Id + (replacement ? ":replacement" : ":original");
			if(!_thumbnailRequests.Add(key)) return;
			HdStudioCatalog catalog = _catalog;
			HdStudioCatalogAsset asset = row.Asset;
			uint[] palette = _palette;
			HdStudioReplacement? replacementData = replacement ? FindReplacement(asset.Id) : null;
			_ = Task.Run(async () => {
				await _thumbnailGate.WaitAsync();
				try {
					string path = "";
					if(replacement) {
						if(replacementData == null) return;
						path = _project?.ResolvePath(replacementData.SourceImage) ?? replacementData.SourceImage;
						if(!File.Exists(path) && !string.IsNullOrWhiteSpace(replacementData.Sheet)) {
							path = Path.Combine(_packFolder, replacementData.Sheet);
						}
					} else {
						path = HdStudioCatalog.EnsureOriginalPreview(catalog, asset, palette);
					}
					if(string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
					Dispatcher.UIThread.Post(() => {
						Bitmap? bitmap = LoadBitmap(path);
						if(replacement) row.SetReplacementThumbnail(bitmap);
						else row.SetThumbnail(bitmap);
					});
				} catch {
					//A single unreadable preview must never stop the catalog.
				} finally {
					_thumbnailRequests.Remove(key);
					_thumbnailGate.Release();
				}
			});
		}

		private Bitmap? LoadBitmap(string path)
		{
			if(string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
			if(_bitmapCache.TryGetValue(path, out Bitmap? cached)) return cached;
			try {
				Bitmap bitmap = new(path);
				_bitmapCache[path] = bitmap;
				return bitmap;
			} catch {
				return null;
			}
		}

		private void ShowBitmap(Image target, string path)
		{
			target.Source = LoadBitmap(path);
		}

		private Geometry? IconGeometry(string key)
		{
			return this.TryFindResource(key, out object? value) ? value as Geometry : null;
		}

		private IBrush Brush(string key)
		{
			return this.TryFindResource(key, out object? value) && value is IBrush brush ? brush : Brushes.Gray;
		}

		private void MarkDirty()
		{
			_dirty = true;
			UpdateStatusBar();
		}

		private void SetStatus(string message)
		{
			if(Dispatcher.UIThread.CheckAccess()) {
				_statusOperationText.Text = message;
			} else {
				Dispatcher.UIThread.Post(() => _statusOperationText.Text = message);
			}
		}

		private void Log(string message)
		{
			if(Dispatcher.UIThread.CheckAccess()) AppendLog(message);
			else Dispatcher.UIThread.Post(() => AppendLog(message));
		}

		private void AppendLog(string message)
		{
			string text = (_logBox.Text ?? "") + message + Environment.NewLine;
			if(text.Length > 400000) text = text.Substring(text.Length - 300000);
			_logBox.Text = text;
			_logBox.CaretIndex = text.Length;
			_statusOperationText.Text = message.Length > 160 ? message.Substring(0, 160) : message;
		}

		private void OpenFolder(string folder)
		{
			try {
				Directory.CreateDirectory(folder);
				Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
			} catch(Exception ex) {
				SetStatus("Could not open folder: " + ex.Message);
			}
		}

		private void OpenUrl(string url)
		{
			try {
				Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
			} catch(Exception ex) {
				SetStatus("Could not open the link: " + ex.Message);
			}
		}

		private void ShowImagePreview(string path, string title)
		{
			try {
				bool light = _settings.Theme.Equals("light", StringComparison.OrdinalIgnoreCase);
				Window window = new() {
					Title = title,
					Width = 640,
					Height = 560,
					MinWidth = 420,
					MinHeight = 360,
					WindowStartupLocation = WindowStartupLocation.CenterOwner,
					Background = new SolidColorBrush(light ? Color.Parse("#F3F5F8") : Color.Parse("#181B20")),
					Foreground = new SolidColorBrush(light ? Color.Parse("#1B2430") : Color.Parse("#F0F2F5"))
				};
				Image image = new() { Source = LoadBitmap(path), Stretch = Stretch.Uniform, Margin = new Thickness(16) };
				Border checker = new() { Background = new SolidColorBrush(light ? Color.Parse("#E9EDF2") : Color.Parse("#1D2127")), CornerRadius = new CornerRadius(8), Child = image };
				TextBlock info = new() { Text = "Exact composed replacement · " + path, FontSize = 10.5, Opacity = 0.7, Margin = new Thickness(16, 0, 16, 12), TextWrapping = TextWrapping.Wrap };
				Button close = new() { Content = "Close", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(16, 0, 16, 14) };
				close.Click += (_, _) => window.Close();
				window.Opened += (_, _) => StudioWindowTheme.Apply(window, !light);
				window.Content = new DockPanel() {
					LastChildFill = true,
					Children = { close, info, checker }
				};
				DockPanel.SetDock(close, Avalonia.Controls.Dock.Bottom);
				DockPanel.SetDock(info, Avalonia.Controls.Dock.Bottom);
				window.ShowDialog(this);
			} catch(Exception ex) {
				SetStatus("Could not open preview: " + ex.Message);
			}
		}

		private void ShowHelp()
		{
			ShowInfoWindow("User Guide",
				"RetroArcDo HD Studio v" + Version + "\n\n" +
				"1. Open a NES game or the synthetic example.\n" +
				"2. Use Discovery to record gameplay. Press Capture frame to pause on a still frame, then Select object to inspect an 8×8-aligned region and open its match in the Artwork Workshop.\n" +
				"3. Choose a sprite or background in the Asset Library. Drag artwork onto the replacement canvas, or import a PNG/JPEG/WebP.\n" +
				"4. Position, scale, crop, mirror and anchor the artwork. The original logical grid and game geometry never change.\n" +
				"5. Preview checks the exact composition. Approve & Apply compiles the standard HD Pack.\n" +
				"6. Use Game Preview to build, install and play in the real Mesen emulator, or Verify to compare deterministic frames.\n" +
				"7. Export HD Pack to the default Documents\\RetroArcDo HD Studio\\Exports folder or any folder you choose.\n\n" +
				"Keyboard: Ctrl+O open game · Ctrl+Shift+O open project · Ctrl+S save · Ctrl+Z undo · Ctrl+Y redo.\n" +
				"Mouse: wheel zooms the canvas; drag moves artwork; corner handle resizes; crop mode shows edge handles.");
		}

		private void ShowAbout()
		{
			ShowInfoWindow("About RetroArcDo HD Studio",
				"RetroArcDo HD Studio v" + Version + "\n" +
				"A graphical remastering workshop for NES games, built on the Mesen emulator.\n\n" +
				"• Discovers original CHR/CHR-RAM graphics without a game-specific profile.\n" +
				"• Composes arbitrary user artwork into the original 8×8 tile grid.\n" +
				"• Preserves ROM bytes, tile placement, animation timing and gameplay.\n" +
				"• Exports standard Mesen HD Packs that also load in stock Mesen.\n\n" +
				"Original NES geometry is preserved; only appearance changes.");
		}

		private void ShowInfoWindow(string title, string body)
		{
			bool light = _settings.Theme.Equals("light", StringComparison.OrdinalIgnoreCase);
			Window window = new() {
				Title = title,
				Width = 680,
				Height = 560,
				MinWidth = 520,
				MinHeight = 380,
				WindowStartupLocation = WindowStartupLocation.CenterOwner,
				Background = new SolidColorBrush(light ? Color.Parse("#F3F5F8") : Color.Parse("#181B20")),
				Foreground = new SolidColorBrush(light ? Color.Parse("#1B2430") : Color.Parse("#F0F2F5"))
			};
			TextBlock heading = new() { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(18, 16, 18, 8) };
			TextBox text = new() {
				Text = body,
				AcceptsReturn = true,
				IsReadOnly = true,
				TextWrapping = TextWrapping.Wrap,
				Background = Brushes.Transparent,
				BorderThickness = new Thickness(0),
				Margin = new Thickness(14, 0, 14, 10),
				Foreground = window.Foreground
			};
			Button close = new() { Content = "Close", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(16, 0, 16, 14) };
			close.Click += (_, _) => window.Close();
			window.Opened += (_, _) => StudioWindowTheme.Apply(window, !light);
			DockPanel panel = new() { LastChildFill = true, Children = { heading, close, text } };
			DockPanel.SetDock(heading, Avalonia.Controls.Dock.Top);
			DockPanel.SetDock(close, Avalonia.Controls.Dock.Bottom);
			window.Content = panel;
			window.ShowDialog(this);
		}
	}
}


