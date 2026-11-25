using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Mesen.HdStudio;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Mesen.Controls
{
	/// <summary>
	/// Interactive pixel-artwork surface used by the HD Studio Artwork Workshop.
	///
	/// The control draws either the original NES appearance or the exact composed replacement
	/// (the same HdStudioArtwork pipeline used by the pack compiler) and provides pan, wheel
	/// zoom, drag-to-position, corner scaling and crop handles. It never edits the ROM, the
	/// original tile geometry or the animation timing: it only changes the user's adaptation
	/// settings, which are persisted in the project file.
	/// </summary>
	public class HdStudioArtworkCanvas : Control
	{
		public enum CanvasMode
		{
			Original,
			Replacement
		}

		public CanvasMode Mode { get; set; } = CanvasMode.Replacement;
		public bool LightTheme { get; set; }
		public bool ShowCheckerboard { get; set; } = true;
		public bool ShowTileGrid { get; set; } = true;
		public bool ShowOriginalBounds { get; set; } = true;
		public bool CropMode { get; set; }

		public string OriginalImagePath { get; set; } = "";
		public string SourceImagePath { get; set; } = "";
		public HdStudioAdaptationSettings? Settings { get; set; }
		public int LogicalWidth { get; set; } = 16;
		public int LogicalHeight { get; set; } = 16;
		public int OutputScale { get; set; } = 4;
		public int TargetWidth => Math.Max(1, LogicalWidth * Math.Max(1, OutputScale));
		public int TargetHeight => Math.Max(1, LogicalHeight * Math.Max(1, OutputScale));

		public double ViewZoom { get; private set; } = 1;
		public event EventHandler? SettingsChanged;
		public event EventHandler? ViewZoomChanged;

		private Bitmap? _originalBitmap;
		private string _loadedOriginal = "";
		private Bitmap? _composedBitmap;
		private HdStudioArtwork.ArtworkImage? _source;
		private string _loadedSource = "";
		private int _sourceHash;
		private bool _dragging;
		private DragKind _dragKind = DragKind.None;
		private Point _dragStart;
		private int _startOffsetX;
		private int _startOffsetY;
		private int _startZoom;
		private int _startCropLeft;
		private int _startCropTop;
		private int _startCropRight;
		private int _startCropBottom;
		private double _panX;
		private double _panY;

		private enum DragKind
		{
			None,
			Pan,
			MoveArtwork,
			Scale,
			CropLeft,
			CropTop,
			CropRight,
			CropBottom
		}

		public HdStudioArtworkCanvas()
		{
			ClipToBounds = true;
			Focusable = true;
			RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
		}

		public void ResetView()
		{
			ViewZoom = 1;
			_panX = 0;
			_panY = 0;
			ViewZoomChanged?.Invoke(this, EventArgs.Empty);
			InvalidateVisual();
		}

		public void ZoomBy(double factor)
		{
			ViewZoom = Math.Clamp(ViewZoom * factor, 0.5, 12);
			ViewZoomChanged?.Invoke(this, EventArgs.Empty);
			InvalidateVisual();
		}

		public void FitToView()
		{
			ResetView();
		}

		public void InvalidateSettings()
		{
			_composedBitmap?.Dispose();
			_composedBitmap = null;
			InvalidateVisual();
		}

		private Color C(string dark, string light)
		{
			string value = LightTheme ? light : dark;
			if(!value.StartsWith("#", StringComparison.Ordinal)) {
				value = value switch {
					"StudioCanvas" => LightTheme ? "#DDE3EA" : "#14171B",
					"StudioPanel" => LightTheme ? "#FFFFFF" : "#22262D",
					"StudioPanelAlt" => LightTheme ? "#E9EDF2" : "#1D2127",
					"StudioElevated" => LightTheme ? "#E1E7EE" : "#2D333C",
					"StudioBorder" => LightTheme ? "#C9D2DC" : "#3B424C",
					"StudioBorderStrong" => LightTheme ? "#AAB6C3" : "#4B545F",
					"StudioText" => LightTheme ? "#1B2430" : "#F0F2F5",
					"StudioTextSecondary" => LightTheme ? "#556273" : "#A5AFBD",
					"StudioTextTertiary" => LightTheme ? "#788697" : "#748091",
					"StudioAccent" => LightTheme ? "#D97726" : "#F28C38",
					"StudioInfo" => LightTheme ? "#2E6DB4" : "#5796DF",
					"StudioWarning" => LightTheme ? "#A87400" : "#E9B44C",
					"StudioSuccess" => LightTheme ? "#248A5C" : "#45B883",
					"StudioError" => LightTheme ? "#C33D3D" : "#E16464",
					_ => "#FF00FF"
				};
			}
			return Color.Parse(value);
		}
		private IBrush B(string dark, string light) => new SolidColorBrush(C(dark, light));
		private IPen P(string dark, string light, double width = 1) => new Pen(B(dark, light), width);

		protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
		{
			base.OnPointerWheelChanged(e);
			double factor = e.Delta.Y > 0 ? 1.18 : 1 / 1.18;
			ViewZoom = Math.Clamp(ViewZoom * factor, 0.5, 12);
			ViewZoomChanged?.Invoke(this, EventArgs.Empty);
			InvalidateVisual();
			e.Handled = true;
		}

		protected override void OnPointerPressed(PointerPressedEventArgs e)
		{
			base.OnPointerPressed(e);
			if(!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) {
				return;
			}
			Focus();
			_dragStart = e.GetPosition(this);
			_dragging = true;
			_dragKind = Mode == CanvasMode.Original ? DragKind.Pan : HitTest(_dragStart);
			if(Settings != null) {
				_startOffsetX = Settings.OffsetX;
				_startOffsetY = Settings.OffsetY;
				_startZoom = Settings.ZoomPercent;
				_startCropLeft = Settings.CropLeft;
				_startCropTop = Settings.CropTop;
				_startCropRight = Settings.CropRight;
				_startCropBottom = Settings.CropBottom;
			}
			e.Pointer.Capture(this);
			e.Handled = true;
		}

		protected override void OnPointerMoved(PointerEventArgs e)
		{
			base.OnPointerMoved(e);
			if(!_dragging) {
				UpdateCursor(e.GetPosition(this));
				return;
			}
			Point point = e.GetPosition(this);
			double dx = point.X - _dragStart.X;
			double dy = point.Y - _dragStart.Y;
			switch(_dragKind) {
				case DragKind.Pan:
					_panX += dx;
					_panY += dy;
					_dragStart = point;
					break;
				case DragKind.MoveArtwork:
					if(Settings != null) {
						double displayScale = DisplayScale();
						Settings.OffsetX = _startOffsetX + (int)Math.Round(dx / Math.Max(0.1, displayScale));
						Settings.OffsetY = _startOffsetY + (int)Math.Round(dy / Math.Max(0.1, displayScale));
					}
					break;
				case DragKind.Scale:
					if(Settings != null) {
						double basis = Math.Max(24, Math.Min(LogicalWidth, LogicalHeight) * 3.0);
						float change = (float)((dx + dy) / basis);
						Settings.ZoomPercent = Math.Clamp((int)Math.Round(_startZoom * (1 + change)), 10, 400);
					}
					break;
				case DragKind.CropLeft:
					if(Settings != null) Settings.CropLeft = Math.Clamp(_startCropLeft + (int)Math.Round(dx / Math.Max(0.1, DisplayScale())), 0, Math.Max(0, LogicalWidth - 1 - Settings.CropRight));
					break;
				case DragKind.CropRight:
					if(Settings != null) Settings.CropRight = Math.Clamp(_startCropRight - (int)Math.Round(dx / Math.Max(0.1, DisplayScale())), 0, Math.Max(0, LogicalWidth - 1 - Settings.CropLeft));
					break;
				case DragKind.CropTop:
					if(Settings != null) Settings.CropTop = Math.Clamp(_startCropTop + (int)Math.Round(dy / Math.Max(0.1, DisplayScale())), 0, Math.Max(0, LogicalHeight - 1 - Settings.CropBottom));
					break;
				case DragKind.CropBottom:
					if(Settings != null) Settings.CropBottom = Math.Clamp(_startCropBottom - (int)Math.Round(dy / Math.Max(0.1, DisplayScale())), 0, Math.Max(0, LogicalHeight - 1 - Settings.CropTop));
					break;
			}
			InvalidateSettings();
			SettingsChanged?.Invoke(this, EventArgs.Empty);
			e.Handled = true;
		}

		protected override void OnPointerReleased(PointerReleasedEventArgs e)
		{
			base.OnPointerReleased(e);
			_dragging = false;
			_dragKind = DragKind.None;
			e.Pointer.Capture(null);
			e.Handled = true;
		}

		private void UpdateCursor(Point point)
		{
			if(Mode == CanvasMode.Original) {
				Cursor = new Cursor(StandardCursorType.SizeAll);
				return;
			}
			Cursor = new Cursor(CropMode ? StandardCursorType.Cross : StandardCursorType.Hand);
			DragKind kind = HitTest(point);
			if(CropMode && (kind == DragKind.CropLeft || kind == DragKind.CropRight)) {
				Cursor = new Cursor(StandardCursorType.SizeWestEast);
			} else if(CropMode && (kind == DragKind.CropTop || kind == DragKind.CropBottom)) {
				Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
			} else if(kind == DragKind.Scale) {
				Cursor = new Cursor(StandardCursorType.BottomRightCorner);
			}
		}

		private DragKind HitTest(Point point)
		{
			Rect rect = ArtRect();
			const double handle = 10;
			if(CropMode) {
				if(Math.Abs(point.Y - rect.Top) < handle) return DragKind.CropTop;
				if(Math.Abs(point.Y - rect.Bottom) < handle) return DragKind.CropBottom;
				if(Math.Abs(point.X - rect.Left) < handle) return DragKind.CropLeft;
				if(Math.Abs(point.X - rect.Right) < handle) return DragKind.CropRight;
			}
			Point br = rect.BottomRight;
			if(Math.Abs(point.X - br.X) < handle + 4 && Math.Abs(point.Y - br.Y) < handle + 4) {
				return DragKind.Scale;
			}
			if(rect.Contains(point)) {
				return DragKind.MoveArtwork;
			}
			return DragKind.Pan;
		}

		private double DisplayScale()
		{
			double availableW = Math.Max(40, Bounds.Width - 44);
			double availableH = Math.Max(40, Bounds.Height - 44);
			double baseScale = Math.Min(availableW / TargetWidth, availableH / TargetHeight);
			return Math.Max(0.05, baseScale * ViewZoom);
		}

		private Rect ArtRect()
		{
			double scale = DisplayScale();
			double w = TargetWidth * scale;
			double h = TargetHeight * scale;
			double x = (Bounds.Width - w) / 2 + _panX;
			double y = (Bounds.Height - h) / 2 + _panY;
			return new Rect(x, y, w, h);
		}

		public override void Render(DrawingContext context)
		{
			base.Render(context);
			context.FillRectangle(B("StudioCanvas", "StudioCanvas"), Bounds);
			DrawBackdrop(context);

			Rect art = ArtRect();
			if(Mode == CanvasMode.Original) {
				DrawOriginal(context, art);
			} else {
				DrawReplacement(context, art);
			}
			DrawFrameLabels(context, art);
		}

		private void DrawBackdrop(DrawingContext context)
		{
			//A calm vignette-like inner border gives the canvas a professional editing surface
			//without gradients or decorative noise.
			Rect outer = new Rect(8, 8, Math.Max(1, Bounds.Width - 16), Math.Max(1, Bounds.Height - 16));
			context.DrawRectangle(null, P("StudioBorder", "StudioBorder"), outer, 6, 6);
			for(double x = 24; x < Bounds.Width; x += 32) {
				for(double y = 24; y < Bounds.Height; y += 32) {
					context.FillRectangle(B("StudioPanelAlt", "StudioPanelAlt"), new Rect(x, y, 1, 1));
				}
			}
		}

		private void DrawOriginal(DrawingContext context, Rect art)
		{
			if(!EnsureOriginalBitmap()) {
				DrawEmpty(context, art, "No original preview", "Select a discovered asset to reconstruct its NES appearance.");
				return;
			}
			DrawChecker(context, art);
			context.DrawImage(_originalBitmap!, art);
			if(ShowOriginalBounds) {
				DrawTileGrid(context, art, false);
				context.DrawRectangle(null, P("StudioInfo", "StudioInfo", 1.2), art, 1, 1);
			}
		}

		private void DrawReplacement(DrawingContext context, Rect art)
		{
			if(ShowCheckerboard) {
				DrawChecker(context, art);
			}
			if(!EnsureComposedBitmap()) {
				DrawEmpty(context, art, "Drop artwork to begin", "PNG · JPEG · WebP · 8×8 grid preserved");
				context.DrawRectangle(null, P("StudioAccent", "StudioAccent", 1.2), art, 1, 1);
				return;
			}
			context.DrawImage(_composedBitmap!, art);
			if(ShowTileGrid) {
				DrawTileGrid(context, art, true);
			}
			context.DrawRectangle(null, P("StudioAccent", "StudioAccent", 1.4), art, 1, 1);
			DrawAnchor(context, art);
			DrawHandles(context, art);
		}

		private void DrawEmpty(DrawingContext context, Rect art, string title, string subtitle)
		{
			Color canvas = C("StudioCanvas", "StudioCanvas");
			Color panel = C("StudioPanel", "StudioPanel");
			context.FillRectangle(new SolidColorBrush(panel), art);
			FormattedText text = new FormattedText(title, System.Globalization.CultureInfo.InvariantCulture,
				FlowDirection.LeftToRight, new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold), 14,
				B("StudioTextSecondary", "StudioTextSecondary"));
			FormattedText sub = new FormattedText(subtitle, System.Globalization.CultureInfo.InvariantCulture,
				FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11,
				B("StudioTextTertiary", "StudioTextTertiary"));
			context.DrawText(text, new Point(art.Center.X - text.Width / 2, art.Center.Y - 18));
			context.DrawText(sub, new Point(art.Center.X - sub.Width / 2, art.Center.Y + 4));
		}

		private void DrawChecker(DrawingContext context, Rect art)
		{
			IBrush light = B("StudioPanelAlt", "StudioPanelAlt");
			IBrush dark = B("StudioElevated", "StudioElevated");
			double cell = Math.Max(6, Math.Min(14, art.Width / 24));
			context.FillRectangle(light, art);
			int columns = (int)Math.Ceiling(art.Width / cell);
			int rows = (int)Math.Ceiling(art.Height / cell);
			for(int y = 0; y < rows; y++) {
				for(int x = 0; x < columns; x++) {
					if(((x + y) & 1) == 1) {
						Rect r = new Rect(art.X + x * cell, art.Y + y * cell, cell, cell);
						context.FillRectangle(dark, r.Intersect(art));
					}
				}
			}
		}

		private void DrawTileGrid(DrawingContext context, Rect art, bool replacement)
		{
			double cell = art.Width / Math.Max(1, LogicalWidth / 8.0);
			IPen minor = P("StudioBorderStrong", "StudioBorderStrong", 0.6);
			IPen major = P("StudioInfo", "StudioInfo", 0.8);
			for(int i = 1; i < Math.Max(1, LogicalWidth / 8); i++) {
				double x = art.X + i * cell;
				context.DrawLine(i % 2 == 0 ? major : minor, new Point(x, art.Top), new Point(x, art.Bottom));
			}
			for(int i = 1; i < Math.Max(1, LogicalHeight / 8); i++) {
				double y = art.Y + i * cell;
				context.DrawLine(i % 2 == 0 ? major : minor, new Point(art.Left, y), new Point(art.Right, y));
			}
			//8x8 NES pixels are the real editing grid. At low zoom the minor lines are hidden
			//so the artwork remains readable.
			if(art.Width > 180) {
				double logical = art.Width / Math.Max(1, LogicalWidth);
				if(logical >= 3.5) {
					for(int i = 1; i < LogicalWidth; i++) {
						double x = art.X + i * logical;
						context.DrawLine(P("StudioBorder", "StudioBorder", 0.35), new Point(x, art.Top), new Point(x, art.Bottom));
					}
					for(int i = 1; i < LogicalHeight; i++) {
						double y = art.Y + i * logical;
						context.DrawLine(P("StudioBorder", "StudioBorder", 0.35), new Point(art.Left, y), new Point(art.Right, y));
					}
				}
			}
		}

		private void DrawAnchor(DrawingContext context, Rect art)
		{
			if(Settings == null) {
				return;
			}
			string anchor = (Settings.Anchor ?? "center").ToLowerInvariant();
			double cx = anchor switch { "left" => art.Left, "right" => art.Right, _ => art.Center.X };
			double cy = anchor switch { "top" => art.Top, "bottom" => art.Bottom, _ => art.Center.Y };
			IPen pen = P("StudioWarning", "StudioWarning", 1.2);
			context.DrawLine(pen, new Point(cx - 7, cy), new Point(cx + 7, cy));
			context.DrawLine(pen, new Point(cx, cy - 7), new Point(cx, cy + 7));
			context.DrawEllipse(null, pen, new Point(cx, cy), 11, 11);
		}

		private void DrawHandles(DrawingContext context, Rect art)
		{
			const double s = 9;
			IBrush fill = B("StudioAccent", "StudioAccent");
			IBrush inner = B("StudioPanel", "StudioPanel");
			foreach(Point p in new[] { art.TopLeft, art.TopRight, art.BottomLeft, art.BottomRight }) {
				Rect r = new Rect(p.X - s / 2, p.Y - s / 2, s, s);
				context.FillRectangle(inner, r);
				context.DrawRectangle(null, new Pen(fill, 1.5), r, 1, 1);
			}
			if(CropMode) {
				IPen cropPen = P("StudioWarning", "StudioWarning", 2);
				context.DrawLine(cropPen, new Point(art.Left, art.Top + art.Height / 2 - 12), new Point(art.Left, art.Top + art.Height / 2 + 12));
				context.DrawLine(cropPen, new Point(art.Right, art.Top + art.Height / 2 - 12), new Point(art.Right, art.Top + art.Height / 2 + 12));
				context.DrawLine(cropPen, new Point(art.Left + art.Width / 2 - 12, art.Top), new Point(art.Left + art.Width / 2 + 12, art.Top));
				context.DrawLine(cropPen, new Point(art.Left + art.Width / 2 - 12, art.Bottom), new Point(art.Left + art.Width / 2 + 12, art.Bottom));
			}
			context.DrawEllipse(null, P("StudioAccent", "StudioAccent"), art.BottomRight, 16, 16);
		}

		private void DrawFrameLabels(DrawingContext context, Rect art)
		{
			string label = Mode == CanvasMode.Original ? "ORIGINAL · NES 8×8 GRID" : "HD REPLACEMENT · " + TargetWidth + "×" + TargetHeight;
			FormattedText text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
				FlowDirection.LeftToRight, new Typeface("Cascadia Mono, Consolas"), 9.5,
				B("StudioTextTertiary", "StudioTextTertiary"));
			context.FillRectangle(B("StudioPanelAlt", "StudioPanelAlt"), new Rect(art.Left, Math.Max(4, art.Top - 20), text.Width + 12, 16));
			context.DrawText(text, new Point(art.Left + 6, Math.Max(6, art.Top - 18)));
		}

		private bool EnsureOriginalBitmap()
		{
			if(string.IsNullOrWhiteSpace(OriginalImagePath) || !File.Exists(OriginalImagePath)) {
				_originalBitmap?.Dispose();
				_originalBitmap = null;
				_loadedOriginal = "";
				return false;
			}
			if(_loadedOriginal == OriginalImagePath && _originalBitmap != null) {
				return true;
			}
			_originalBitmap?.Dispose();
			try {
				_originalBitmap = new Bitmap(OriginalImagePath);
				_loadedOriginal = OriginalImagePath;
				return true;
			} catch {
				_originalBitmap = null;
				return false;
			}
		}

		private bool EnsureComposedBitmap()
		{
			if(Settings == null || string.IsNullOrWhiteSpace(SourceImagePath) || !File.Exists(SourceImagePath)) {
				return false;
			}
			int hash = Settings.GetHashCode() ^ SourceImagePath.GetHashCode() ^ TargetWidth ^ TargetHeight;
			if(_composedBitmap != null && _source != null && _sourceHash == hash) {
				return true;
			}
			try {
				if(_source == null || _loadedSource != SourceImagePath) {
					_source = HdStudioArtwork.LoadImage(SourceImagePath);
					_loadedSource = SourceImagePath;
				}
				HdStudioArtwork.ArtworkImage copy = new() {
					Width = _source.Width,
					Height = _source.Height,
					Pixels = (SKColor[])_source.Pixels.Clone()
				};
				if(Settings.RemoveBackground) {
					HdStudioArtwork.RemoveBackground(copy, Settings.BackgroundTolerance);
				}
				if(Settings.KeepLargestComponent) {
					HdStudioArtwork.KeepLargestComponent(copy);
				}
				SKRectI bounds = Settings.Crop ? HdStudioArtwork.AlphaBounds(copy) : new SKRectI(0, 0, copy.Width, copy.Height);
				SKColor[] pixels = HdStudioArtwork.Compose(copy, bounds, TargetWidth, TargetHeight, Settings);
				_composedBitmap?.Dispose();
				_composedBitmap = ToBitmap(pixels, TargetWidth, TargetHeight);
				_sourceHash = hash;
				return true;
			} catch {
				return false;
			}
		}

		private static Bitmap ToBitmap(SKColor[] pixels, int width, int height)
		{
			WriteableBitmap bitmap = new(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
			using(ILockedFramebuffer frame = bitmap.Lock()) {
				int[] buffer = new int[width * height];
				for(int i = 0; i < buffer.Length; i++) {
					SKColor c = pixels[i];
					buffer[i] = (c.Alpha << 24) | (c.Red << 16) | (c.Green << 8) | c.Blue;
				}
				Marshal.Copy(buffer, 0, frame.Address, buffer.Length);
			}
			return bitmap;
		}
	}
}
