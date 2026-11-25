using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Mesen.HdStudio;
using Mesen.Utilities;
using System.Collections.Generic;
using System.Linq;

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
		private ListBox RuleList => this.GetControl<ListBox>("RuleList");

		public bool AllowShared { get; private set; }
		public string ChosenRule { get; private set; } = "";
		public bool KeepOriginal { get; private set; }

		public HdStudioConflictWindow() : this("Asset", "", new List<SharedAssetRow>(), false) { }

		internal HdStudioConflictWindow(string assetName, string info, IReadOnlyList<SharedAssetRow> shared, bool lightTheme,
			IReadOnlyList<HdStudioContextResolver.Proposal>? proposals = null)
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

			List<HdStudioContextResolver.Proposal> rules = proposals?.ToList() ?? new List<HdStudioContextResolver.Proposal>();
			RuleList.ItemsSource = rules;
			if(rules.Count > 0) {
				RuleList.SelectedIndex = 0;
			} else {
				RuleList.IsVisible = false;
			}
		}

		private void Cancel_Click(object? sender, RoutedEventArgs e)
		{
			AllowShared = false;
			Close(false);
		}

		private void Allow_Click(object? sender, RoutedEventArgs e)
		{
			AllowShared = true;
			ChosenRule = "";
			KeepOriginal = false;
			Close(true);
		}

		private void ApplyRule_Click(object? sender, RoutedEventArgs e)
		{
			if(RuleList.SelectedItem is HdStudioContextResolver.Proposal proposal) {
				if(proposal.IsPreserve) {
					AllowShared = false;
					ChosenRule = "";
					KeepOriginal = true;
					Close(false);
					return;
				}
				if(proposal.IsShared) {
					AllowShared = true;
					ChosenRule = "";
					KeepOriginal = false;
					Close(true);
					return;
				}
				AllowShared = false;
				ChosenRule = proposal.Rule;
				KeepOriginal = false;
				Close(true);
				return;
			}
			AllowShared = false;
			Close(false);
		}
	}
}
