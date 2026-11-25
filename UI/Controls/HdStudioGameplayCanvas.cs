using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mesen.Controls
{
	/// <summary>
	/// Paused gameplay surface. Shows a real emulator frame, overlays the 8x8 tile grid and
	/// lets the user mark a region. The region is expressed in NES pixels, not screen pixels,
	/// so the result can be matched against the discovered asset library.
	/// </summary>
	public class HdStudioGameplayCanvas : Control
	{
		public bool LightTheme { get; set; }
		public string FramePath { get; set; } = "";
		public int NesWidth { get; set; } = 256;
		public int NesHeight { get; set; } = 240;
		public bool SelectMode { get; set; }
		public bool PickMode { get; set; }
		public Rect SelectedRegion { get; private set; }
		public event EventHandler? RegionSelected;
		public event EventHandler<Point>? PointPicked;
		public List<(int X, int Y, int W, int H, string Label)> HardwareBoxes { get; } = new();

		private Bitmap? _bitmap;
		private string _loaded = "";
		private bool _dragging;
		private Point _start;
		private double _zoom = 1;

		public HdStudioGameplayCanvas()
		{
			ClipToBounds = true;
			Focusable = true;
			RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
		}

		public void Reload()
		{
			_bitmap?.Dispose();
			_bitmap = null;
			_loaded = "";
			InvalidateVisual();
		}

		public void ClearSelection()
		{
			SelectedRegion = default;
			InvalidateVisual();
		}

		public void SetSelectionNes(int x, int y, int w, int h)
		{
			x = Math.Clamp(x, 0, NesWidth - 8);
			y = Math.Clamp(y, 0, NesHeight - 8);
			w = Math.Clamp(w, 8, NesWidth - x);
			h = Math.Clamp(h, 8, NesHeight - y);
			SelectedRegion = NesToRect(new Rect(x, y, w, h));
			RegionSelected?.Invoke(this, EventArgs.Empty);
			InvalidateVisual();
		}

		public void Fit()
		{
			_zoom = 1;
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

		protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
		{
			base.OnPointerWheelChanged(e);
			_zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), 0.6, 6);
			InvalidateVisual();
			e.Handled = true;
		}

		protected override void OnPointerPressed(PointerPressedEventArgs e)
		{
			base.OnPointerPressed(e);
			if((!SelectMode && !PickMode) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) {
				return;
			}			Point p = e.GetPosition(this);
			if(!FrameRect().Contains(p)) {
				return;
			}
			_dragging = true;
			_start = p;
			SelectedRegion = default;
			e.Pointer.Capture(this);
			e.Handled = true;
		}

		protected override void OnPointerMoved(PointerEventArgs e)
		{
			base.OnPointerMoved(e);
			if(!_dragging) {
				return;
			}
			Point p = e.GetPosition(this);
			SelectedRegion = Normalize(_start, p);
			InvalidateVisual();
			e.Handled = true;
		}

		protected override void OnPointerReleased(PointerReleasedEventArgs e)
		{
			base.OnPointerReleased(e);
			if(!_dragging) {
				return;
			}
			_dragging = false;
			e.Pointer.Capture(null);
			Point end = e.GetPosition(this);
			if(PickMode && (Math.Abs(end.X - _start.X) < 3 && Math.Abs(end.Y - _start.Y) < 3)) {
				//Hardware-aware pick: a click resolves the sprite/OAM and nametable cell at
				//this exact NES pixel instead of an 8x8-aligned approximate region.
				if(FrameRect().Contains(end)) {
					Rect frame = FrameRect();
					double px = (end.X - frame.X) / Math.Max(0.001, frame.Width / NesWidth);
					double py = (end.Y - frame.Y) / Math.Max(0.001, frame.Height / NesHeight);
					PointPicked?.Invoke(this, new Point(Math.Clamp((int)Math.Round(px), 0, NesWidth - 1),
						Math.Clamp((int)Math.Round(py), 0, NesHeight - 1)));
				}
				InvalidateVisual();
				e.Handled = true;
				return;
			}
			//Snap the selection to the original 8x8 grid. The game object is never modified;
			//this only improves the usefulness of the inspection region.
			RectToNes(SelectedRegion, out int x, out int y, out int w, out int h);
			x = Math.Clamp((x / 8) * 8, 0, NesWidth - 8);
			y = Math.Clamp((y / 8) * 8, 0, NesHeight - 8);
			w = Math.Clamp(((w + 7) / 8) * 8, 8, NesWidth - x);
			h = Math.Clamp(((h + 7) / 8) * 8, 8, NesHeight - y);
			SelectedRegion = NesToRect(new Rect(x, y, w, h));
			RegionSelected?.Invoke(this, EventArgs.Empty);
			InvalidateVisual();
			e.Handled = true;
		}

		private static Rect Normalize(Point a, Point b)
		{
			return new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
		}

		private Rect FrameRect()
		{
			double availableW = Math.Max(40, Bounds.Width - 28);
			double availableH = Math.Max(40, Bounds.Height - 28);
			double scale = Math.Min(availableW / NesWidth, availableH / NesHeight) * _zoom;
			scale = Math.Max(0.2, scale);
			double w = NesWidth * scale;
			double h = NesHeight * scale;
			return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
		}

		private Rect NesToRect(Rect nes)
		{
			Rect frame = FrameRect();
			double sx = frame.Width / NesWidth;
			double sy = frame.Height / NesHeight;
			return new Rect(frame.X + nes.X * sx, frame.Y + nes.Y * sy, nes.Width * sx, nes.Height * sy);
		}

		private void RectToNes(Rect screen, out int x, out int y, out int w, out int h)
		{
			Rect frame = FrameRect();
			double sx = frame.Width / NesWidth;
			double sy = frame.Height / NesHeight;
			x = (int)Math.Round((screen.X - frame.X) / Math.Max(0.001, sx));
			y = (int)Math.Round((screen.Y - frame.Y) / Math.Max(0.001, sy));
			w = (int)Math.Round(screen.Width / Math.Max(0.001, sx));
			h = (int)Math.Round(screen.Height / Math.Max(0.001, sy));
			x = Math.Clamp(x, 0, NesWidth - 8);
			y = Math.Clamp(y, 0, NesHeight - 8);
			w = Math.Clamp(w, 8, NesWidth - x);
			h = Math.Clamp(h, 8, NesHeight - y);
		}

		public bool TryGetSelectionNes(out int x, out int y, out int w, out int h)
		{
			x = y = w = h = 0;
			if(SelectedRegion.Width <= 0 || SelectedRegion.Height <= 0) {
				return false;
			}
			RectToNes(SelectedRegion, out x, out y, out w, out h);
			return true;
		}

		public override void Render(DrawingContext context)
		{
			base.Render(context);
			context.FillRectangle(B("StudioCanvas", "StudioCanvas"), Bounds);
			Rect frame = FrameRect();
			context.FillRectangle(B("StudioPanelAlt", "StudioPanelAlt"), frame);
			if(string.IsNullOrWhiteSpace(FramePath) || !System.IO.File.Exists(FramePath)) {
				DrawCentered(context, "Capture a frame to inspect gameplay", frame.Center);
				return;
			}
			if(_loaded != FramePath || _bitmap == null) {
				_bitmap?.Dispose();
				try { _bitmap = new Bitmap(FramePath); } catch { _bitmap = null; }
				_loaded = FramePath;
			}
			if(_bitmap != null) {
				context.DrawImage(_bitmap, frame);
			} else {
				DrawCentered(context, "The captured frame could not be decoded", frame.Center);
			}

			//Original logical boundaries: 8x8 NES cells and a brighter 16x16 object guide.
			double cell = frame.Width / NesWidth;
			IPen minor = new Pen(B("StudioPanel", "StudioPanel"), 0.28);
			IPen major = new Pen(B("StudioInfo", "StudioInfo"), 0.45);
			for(int x = 8; x < NesWidth; x += 8) {
				context.DrawLine(x % 16 == 0 ? major : minor, new Point(frame.X + x * cell, frame.Top), new Point(frame.X + x * cell, frame.Bottom));
			}
			for(int y = 8; y < NesHeight; y += 8) {
				context.DrawLine(y % 16 == 0 ? major : minor, new Point(frame.Left, frame.Y + y * cell), new Point(frame.Right, frame.Y + y * cell));
			}

			//Hardware-aware overlay: exact OAM footprints from the paused frame. These are
			//derived from the emulator's OAM, not from image recognition.
			foreach((int hx, int hy, int hw, int hh, string label) in HardwareBoxes) {
				Rect hr = NesToRect(new Rect(hx, hy, hw, hh)).Intersect(frame);
				if(hr.Width <= 0 || hr.Height <= 0) {
					continue;
				}
				context.DrawRectangle(null, new Pen(B("StudioInfo", "StudioInfo"), 1), hr, 2, 2);
				if(!string.IsNullOrEmpty(label) && hw >= 16) {
					FormattedText tag = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
						new Typeface("Cascadia Mono, Consolas"), 9, B("StudioInfo", "StudioInfo"));
					context.DrawText(tag, new Point(hr.X + 2, Math.Max(frame.Top, hr.Top - 12)));
				}
			}

			if(SelectedRegion.Width > 0 && SelectedRegion.Height > 0) {
				Rect r = SelectedRegion.Intersect(frame);
				context.FillRectangle(new SolidColorBrush(Color.FromArgb(38, 242, 140, 56)), r);
				context.DrawRectangle(null, new Pen(B("StudioAccent", "StudioAccent"), 2), r, 1, 1);
				foreach(Point p in new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight }) {
					context.FillRectangle(B("StudioPanel", "StudioPanel"), new Rect(p.X - 4, p.Y - 4, 8, 8));
					context.DrawRectangle(null, new Pen(B("StudioAccent", "StudioAccent"), 1.5), new Rect(p.X - 4, p.Y - 4, 8, 8));
				}
				RectToNes(SelectedRegion, out int nx, out int ny, out int nw, out int nh);
				FormattedText label = new FormattedText($"NES {nx},{ny}  {nw}×{nh}", CultureInfo.InvariantCulture,
					FlowDirection.LeftToRight, new Typeface("Cascadia Mono, Consolas"), 10, B("StudioText", "StudioText"));
				Rect bg = new(r.X, Math.Max(frame.Top, r.Top - 22), label.Width + 10, 18);
				context.FillRectangle(B("StudioAccent", "StudioAccent"), bg);
				context.DrawText(label, new Point(bg.X + 5, bg.Y + 3));
			}

			//Corner brackets make the emulator frame read as an intentional viewport.
			IPen bracket = new Pen(B("StudioBorderStrong", "StudioBorderStrong"), 1.6);
			const double len = 16;
			context.DrawLine(bracket, frame.TopLeft, new Point(frame.Left + len, frame.Top));
			context.DrawLine(bracket, frame.TopLeft, new Point(frame.Left, frame.Top + len));
			context.DrawLine(bracket, frame.TopRight, new Point(frame.Right - len, frame.Top));
			context.DrawLine(bracket, frame.TopRight, new Point(frame.Right, frame.Top + len));
			context.DrawLine(bracket, frame.BottomLeft, new Point(frame.Left + len, frame.Bottom));
			context.DrawLine(bracket, frame.BottomLeft, new Point(frame.Left, frame.Bottom - len));
			context.DrawLine(bracket, frame.BottomRight, new Point(frame.Right - len, frame.Bottom));
			context.DrawLine(bracket, frame.BottomRight, new Point(frame.Right, frame.Bottom - len));
		}

		private void DrawCentered(DrawingContext context, string text, Point center)
		{
			FormattedText ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
				new Typeface("Segoe UI"), 12, B("StudioTextSecondary", "StudioTextSecondary"));
			context.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
		}
	}
}
