using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Mesen.Utilities;
using System.Collections.Generic;

namespace Mesen.Windows
{
	public partial class HdStudioConflictWindow : Window
	{
		public sealed class SharedAssetRow
		{
			public string Name { get; set; } = "";
			public string Category { get; set; } = "";
		}

		private TextBlock AssetNameText => this.GetControl<TextBlock>("AssetNameText");
		private TextBlock AssetInfoText => this.GetControl<TextBlock>("AssetInfoText");
		private ListBox SharedList => this.GetControl<ListBox>("SharedList");
		private TextBlock SharedCountText => this.GetControl<TextBlock>("SharedCountText");

		public bool AllowShared { get; private set; }
		public HdStudioConflictWindow() : this("Asset", "", new List<SharedAssetRow>(), false) { }

		public HdStudioConflictWindow(string assetName, string info, IReadOnlyList<SharedAssetRow> shared, bool lightTheme)
		{
			AvaloniaXamlLoader.Load(this);
			RequestedThemeVariant = lightTheme ? ThemeVariant.Light : ThemeVariant.Dark;
			Opened += (_, _) => {
				StudioWindowTheme.Apply(this, !lightTheme);
				Avalonia.Threading.Dispatcher.UIThread.Post(() => StudioWindowTheme.Apply(this, !lightTheme), Avalonia.Threading.DispatcherPriority.Background);
			};
			AssetNameText.Text = assetName;
			AssetInfoText.Text = info;
			SharedList.ItemsSource = shared;
			SharedCountText.Text = shared.Count + (shared.Count == 1 ? " other asset" : " other assets");
		}

		private void Cancel_Click(object? sender, RoutedEventArgs e)
		{
			AllowShared = false;
			Close(false);
		}

		private void Allow_Click(object? sender, RoutedEventArgs e)
		{
			AllowShared = true;
			Close(true);
		}
	}
}

