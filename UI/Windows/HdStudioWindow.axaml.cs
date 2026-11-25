using Avalonia.Controls;
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
using System.IO;
using System.Linq;
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

		public HdStudioWindow() : this("", "") { }

		public HdStudioWindow(string romPath, string packFolder)
		{
			_romPath = romPath ?? "";
			_packFolder = packFolder ?? "";
			AvaloniaXamlLoader.Load(this);
			BindControls();
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
			_footerText.Text = "RetroArcDo HD Studio - " + (typeof(HdStudioWindow).Assembly.GetName().Version?.ToString() ?? "dev") + " - original NES geometry is preserved; only appearance changes.";
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
				SetStatus("No semantic asset catalog yet. Run Capture to build one, or export assets from an existing capture.");
			} else {
				SetStatus($"{_catalog.Assets.Count} assets loaded from {_catalog.AssetsPath}");
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
			string search = _assetSearchBox.Text?.Trim() ?? "";
			string category = SelectedCategory();
			IEnumerable<HdStudioCatalogAsset> query = _catalog.Assets;
			if(category != "All assets") {
				query = query.Where(a => string.Equals(a.Category, category, StringComparison.OrdinalIgnoreCase));
			}
			if(search.Length > 0) {
				query = query.Where(a =>
					a.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
					a.Label.Contains(search, StringComparison.OrdinalIgnoreCase) ||
					a.Id.Contains(search, StringComparison.OrdinalIgnoreCase));
			}
			_assetRows.AddRange(query.OrderBy(a => a.Category).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase));
			_assetList.ItemsSource = _assetRows.Select(a => FormatAssetRow(a)).ToList();
			_assetCountText.Text = _assetRows.Count + " assets";
			if(_assetRows.Count > 0) {
				_assetList.SelectedIndex = 0;
			} else {
				_selectedAsset = null;
				_originalImage.Source = null;
				_assetInfoText.Text = _catalog.Assets.Count == 0 ? "No assets captured yet." : "No asset matches the filter.";
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

		private string SelectedCategory()
		{
			return (_categoryBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All assets";
		}

		private static string FormatAssetRow(HdStudioCatalogAsset asset)
		{
			string status = string.IsNullOrWhiteSpace(asset.ValidationStatus) ? "" : ", " + asset.ValidationStatus;
			string tiles = asset.Tiles.Count == 1 ? "1 tile" : asset.Tiles.Count + " tiles";
			return $"{asset.Name}  [{asset.Kind}, {tiles}{status}]";
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
				_tabs.SelectedIndex = result.Success ? _tabs.SelectedIndex : 5;
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
				int code = await RunHeadlessAsync("--hdstudio", _romPath, "--generate", "--frames=" + frames, "--scale=" + scale, "--filter=prescale", "--out=" + _packFolder);
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

		private void OpenWorkshop_Click(object? sender, RoutedEventArgs e)
		{
			if(_selectedAsset != null) {
				_tabs.SelectedIndex = 0;
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
			_tabs.SelectedIndex = 0;
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
			_tabs.SelectedIndex = 0;
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
