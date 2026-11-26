using Avalonia.Media;
using Avalonia.Media.Imaging;
using Mesen.HdStudio;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Mesen.HdStudio
{
	/// <summary>Small view models used by the Studio UI. They are presentation-only and hold
	/// no emulator state; every action still goes through the shared HdStudio services.</summary>
	internal sealed class HdStudioAssetRow : INotifyPropertyChanged
	{
		public HdStudioCatalogAsset Asset { get; init; } = null!;
		public Func<HdStudioAssetRow, Bitmap?>? ThumbnailLoader { get; set; }
		public Func<HdStudioAssetRow, Bitmap?>? ReplacementThumbnailLoader { get; set; }
		public string Name => Asset.Name;
		public string Category { get; set; } = "";
		public string Detail { get; set; } = "";
		public Geometry? StatusGeometry { get; set; }
		public IBrush? StatusBrush { get; set; }
		public string StatusTip { get; set; } = "";
		public bool IsNew { get; set; }
		public bool HasReplacement { get; set; }
		public bool Locked { get; set; }

		private Bitmap? _thumbnail;
		private bool _thumbnailLoaded;
		private Bitmap? _replacementThumbnail;
		private bool _replacementLoaded;
		private double _thumbnailSize = 128;
		private bool _showReplacement;

		public Bitmap? Thumbnail
		{
			get {
				if(!_thumbnailLoaded) {
					_thumbnailLoaded = true;
					_thumbnail = ThumbnailLoader?.Invoke(this);
				}
				return _thumbnail;
			}
		}

		public Bitmap? ReplacementThumbnail
		{
			get {
				if(!_replacementLoaded) {
					_replacementLoaded = true;
					_replacementThumbnail = ReplacementThumbnailLoader?.Invoke(this);
				}
				return _replacementThumbnail;
			}
		}

		public double ThumbnailSize
		{
			get => _thumbnailSize;
			set {
				if(Math.Abs(_thumbnailSize - value) < 0.1) return;
				_thumbnailSize = value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(CardSize));
				OnPropertyChanged(nameof(ReplacementWidth));
			}
		}

		public void SetThumbnail(Bitmap? bitmap)
		{
			_thumbnailLoaded = true;
			_thumbnail = bitmap;
			OnPropertyChanged(nameof(Thumbnail));
		}

		public void SetReplacementThumbnail(Bitmap? bitmap)
		{
			_replacementLoaded = true;
			_replacementThumbnail = bitmap;
			OnPropertyChanged(nameof(ReplacementThumbnail));
		}

		public double CardSize => _thumbnailSize + 6;
		public double ReplacementWidth => _thumbnailSize * 0.52;

		public bool ShowReplacement
		{
			get => _showReplacement;
			set {
				if(_showReplacement == value) return;
				_showReplacement = value;
				OnPropertyChanged();
			}
		}

		public event PropertyChangedEventHandler? PropertyChanged;
		private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}

	internal sealed class HdStudioFrameRow
	{
		public HdStudioCatalogAsset Asset { get; init; } = null!;
		public Bitmap? Thumbnail { get; init; }
		public string Name { get; init; } = "";
		public string FrameLabel { get; init; } = "";
		public Geometry? StatusGeometry { get; init; }
		public IBrush? StatusBrush { get; init; }
		public string StatusTip { get; init; } = "";
		public string Detail { get; init; } = "";
	}

	internal sealed class HdStudioSessionRow
	{
		public string Chunk { get; init; } = "";
		public string Headline { get; init; } = "";
		public string Detail { get; init; } = "";
	}

	internal sealed class HdStudioRecentRow
	{
		public string Path { get; init; } = "";
		public string Name { get; init; } = "";
		public string Game { get; init; } = "";
		public string When { get; init; } = "";
	}

	internal sealed class HdStudioRomRow : INotifyPropertyChanged
	{
		public string Name { get; init; } = "";
		public string Path { get; init; } = "";
		public string Mapper { get; init; } = "";
		public string ChrType { get; init; } = "";
		public string ProjectStatus { get; init; } = "";
		public string CaptureStatus { get; init; } = "";
		public string ReplacementStatus { get; init; } = "";
		public string HdPackStatus { get; init; } = "";
		public bool HasProject { get; init; }
		public bool HasHdPack { get; init; }
		public string Detail => $"{Mapper} · {ChrType} · {CaptureStatus} · {ReplacementStatus}";
		private Bitmap? _thumbnail;
		private bool _loaded;
		public Bitmap? Thumbnail {
			get {
				if(!_loaded) {
					_loaded = true;
					_thumbnail = Load();
				}
				return _thumbnail;
			}
		}
		public Func<HdStudioRomRow, Bitmap?>? ThumbnailLoader { get; set; }
		private Bitmap? Load() => ThumbnailLoader?.Invoke(this);
		public void SetThumbnail(Bitmap? bitmap)
		{
			_loaded = true;
			_thumbnail = bitmap;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
		}
		public event PropertyChangedEventHandler? PropertyChanged;
	}

	internal sealed class HdStudioBatchRow : INotifyPropertyChanged
	{
		public string Name { get; init; } = "";
		public string Path { get; init; } = "";
		public string Sha256 { get; init; } = "";
		public string Mapper { get; init; } = "";
		public string ChrType { get; init; } = "";
		public bool Eligible { get; init; } = true;
		private bool _selected;
		private string _status = "Queued";
		private string _detail = "";
		public bool IsSelected {
			get => _selected;
			set { if(_selected != value) { _selected = value; OnPropertyChanged(); } }
		}
		public string Status {
			get => _status;
			set { if(_status != value) { _status = value; OnPropertyChanged(); } }
		}
		public string Detail {
			get => _detail;
			set { if(_detail != value) { _detail = value; OnPropertyChanged(); } }
		}
		public string SelectionLabel => Mapper + " · " + ChrType;
		public event PropertyChangedEventHandler? PropertyChanged;
		private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}

	internal sealed class HdStudioCandidateRow
	{
		public HdStudioCatalogAsset Asset { get; init; } = null!;
		public Bitmap? Thumbnail { get; init; }
		public string Name => Asset.Name;
		public string Category { get; init; } = "";
		public HdStudioPicking.PickCandidate? Pick { get; init; }
	}
}
