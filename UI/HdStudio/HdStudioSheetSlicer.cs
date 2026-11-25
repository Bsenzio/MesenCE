using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace Mesen.HdStudio
{
	internal sealed class HdStudioSliceOptions
	{
		public string ImagePath = "";
		public string OutputFolder = "";
		public int Columns;
		public int Rows;
		public int FrameWidth;
		public int FrameHeight;
		public int MarginX;
		public int MarginY;
		public int SpacingX;
		public int SpacingY;
		public bool TrimTransparent;
		public string NamePrefix = "frame";
	}

	internal sealed class HdStudioSliceFrame
	{
		public int Index;
		public int Row;
		public int Column;
		public int X;
		public int Y;
		public int Width;
		public int Height;
		public string Path = "";
	}

	/// <summary>
	/// Deterministic sprite-sheet slicing. The slicer is shared by the Studio Animations tab
	/// and the CLI, and always reports the exact source rectangle of every frame so a manual
	/// correction can be reproduced later. It never changes the original sheet.
	/// </summary>
	internal static class HdStudioSheetSlicer
	{
		public static List<HdStudioSliceFrame> Slice(HdStudioSliceOptions options, Action<string>? log = null)
		{
			if(string.IsNullOrWhiteSpace(options.ImagePath) || !File.Exists(options.ImagePath)) {
				throw new FileNotFoundException("The sprite sheet was not found.", options.ImagePath);
			}
			if(options.Columns < 0 || options.Rows < 0 || options.FrameWidth < 0 || options.FrameHeight < 0 ||
				options.MarginX < 0 || options.MarginY < 0 || options.SpacingX < 0 || options.SpacingY < 0) {
				throw new ArgumentException("Sheet grid values cannot be negative.");
			}

			HdStudioArtwork.ArtworkImage sheet = HdStudioArtwork.LoadImage(options.ImagePath);
			int columns = options.Columns;
			int rows = options.Rows;
			int frameWidth = options.FrameWidth;
			int frameHeight = options.FrameHeight;

			if(columns == 0 && frameWidth == 0) {
				columns = 1;
			}
			if(rows == 0 && frameHeight == 0) {
				rows = 1;
			}
			if(frameWidth == 0) {
				frameWidth = (sheet.Width - options.MarginX * 2 - options.SpacingX * (columns - 1)) / Math.Max(1, columns);
			}
			if(frameHeight == 0) {
				frameHeight = (sheet.Height - options.MarginY * 2 - options.SpacingY * (rows - 1)) / Math.Max(1, rows);
			}
			if(columns == 0) {
				columns = (sheet.Width - options.MarginX * 2 + options.SpacingX) / Math.Max(1, frameWidth + options.SpacingX);
			}
			if(rows == 0) {
				rows = (sheet.Height - options.MarginY * 2 + options.SpacingY) / Math.Max(1, frameHeight + options.SpacingY);
			}
			if(columns <= 0 || rows <= 0 || frameWidth <= 0 || frameHeight <= 0) {
				throw new ArgumentException("The sheet grid produces no usable frames.");
			}
			if(options.MarginX * 2 + options.SpacingX * (columns - 1) + frameWidth * columns > sheet.Width ||
				options.MarginY * 2 + options.SpacingY * (rows - 1) + frameHeight * rows > sheet.Height) {
				throw new ArgumentException($"The grid ({columns}x{rows}, {frameWidth}x{frameHeight}, margin {options.MarginX}/{options.MarginY}, spacing {options.SpacingX}/{options.SpacingY}) exceeds the {sheet.Width}x{sheet.Height} sheet.");
			}

			string output = options.OutputFolder;
			if(string.IsNullOrWhiteSpace(output)) {
				output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.ImagePath)) ?? ".", "slices");
			}
			Directory.CreateDirectory(output);

			List<HdStudioSliceFrame> frames = new();
			int index = 0;
			for(int row = 0; row < rows; row++) {
				for(int column = 0; column < columns; column++) {
					int x = options.MarginX + column * (frameWidth + options.SpacingX);
					int y = options.MarginY + row * (frameHeight + options.SpacingY);
					SKColor[] pixels = new SKColor[frameWidth * frameHeight];
					for(int py = 0; py < frameHeight; py++) {
						Array.Copy(sheet.Pixels, (y + py) * sheet.Width + x, pixels, py * frameWidth, frameWidth);
					}
					int left = 0, top = 0, right = frameWidth, bottom = frameHeight;
					if(options.TrimTransparent) {
						left = frameWidth;
						top = frameHeight;
						right = 0;
						bottom = 0;
						for(int py = 0; py < frameHeight; py++) {
							for(int px = 0; px < frameWidth; px++) {
								if(pixels[py * frameWidth + px].Alpha > 0) {
									left = Math.Min(left, px);
									top = Math.Min(top, py);
									right = Math.Max(right, px + 1);
									bottom = Math.Max(bottom, py + 1);
								}
							}
						}
						if(right <= left || bottom <= top) {
							left = top = 0;
							right = frameWidth;
							bottom = frameHeight;
						}
					}
					int outWidth = right - left;
					int outHeight = bottom - top;
					uint[] argb = new uint[outWidth * outHeight];
					for(int py = 0; py < outHeight; py++) {
						for(int px = 0; px < outWidth; px++) {
							argb[py * outWidth + px] = HdStudioArtwork.ToArgb(pixels[(top + py) * frameWidth + left + px]);
						}
					}
					string path = Path.Combine(output, $"{options.NamePrefix}_{index:000}.png");
					HdStudioPng.Write(path, argb, outWidth, outHeight);
					frames.Add(new HdStudioSliceFrame() {
						Index = index,
						Row = row,
						Column = column,
						X = x + left,
						Y = y + top,
						Width = outWidth,
						Height = outHeight,
						Path = path
					});
					index++;
				}
			}
			log?.Invoke($"Sheet slice: {frames.Count} frames ({columns}x{rows}, {frameWidth}x{frameHeight}) -> {output}");
			return frames;
		}

		public static (int Columns, int Rows, int FrameWidth, int FrameHeight) DescribeGrid(HdStudioSliceOptions options)
		{
			List<HdStudioSliceFrame> frames = Slice(new HdStudioSliceOptions() {
				ImagePath = options.ImagePath,
				OutputFolder = Path.Combine(Path.GetTempPath(), "hdstudio-slice-describe-" + Guid.NewGuid().ToString("N")),
				Columns = options.Columns,
				Rows = options.Rows,
				FrameWidth = options.FrameWidth,
				FrameHeight = options.FrameHeight,
				MarginX = options.MarginX,
				MarginY = options.MarginY,
				SpacingX = options.SpacingX,
				SpacingY = options.SpacingY,
				TrimTransparent = options.TrimTransparent,
				NamePrefix = "probe"
			}, null);
			try {
				return (frames.Max(f => f.Column) + 1, frames.Max(f => f.Row) + 1, frames[0].Width, frames[0].Height);
			} finally {
				try {
					Directory.Delete(Path.GetDirectoryName(frames[0].Path)!, true);
				} catch { }
			}
		}
	}
}
