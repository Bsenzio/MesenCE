using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Mesen.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mesen.Windows
{
	public partial class HdStudioFolderWindow : Window
	{
		public sealed class FolderEntry
		{
			public string Name { get; set; } = "";
			public string FullPath { get; set; } = "";
		}

		private TextBlock HeaderText => this.GetControl<TextBlock>("HeaderText");
		private TextBlock PathText => this.GetControl<TextBlock>("PathText");
		private ListBox FolderList => this.GetControl<ListBox>("FolderList");
		private TextBlock FolderCountText => this.GetControl<TextBlock>("FolderCountText");
		private TextBlock SelectionText => this.GetControl<TextBlock>("SelectionText");
		private TextBlock ValidationText => this.GetControl<TextBlock>("ValidationText");
		private Button ChooseButton => this.GetControl<Button>("ChooseButton");
		private Button UpButton => this.GetControl<Button>("UpButton");
		public string SelectedPath { get; private set; } = "";
		private string _current = "";
		private readonly string _initial;
		private readonly bool _light;

		public HdStudioFolderWindow() : this("Choose a folder", "", false) { }

		public HdStudioFolderWindow(string title, string initialPath, bool lightTheme)
		{
			_initial = initialPath;
			_light = lightTheme;
			AvaloniaXamlLoader.Load(this);
			Title = title;
			RequestedThemeVariant = lightTheme ? ThemeVariant.Light : ThemeVariant.Dark;
			HeaderText.Text = title;
			Opened += (_, _) => {
				StudioWindowTheme.Apply(this, !lightTheme);
				Avalonia.Threading.Dispatcher.UIThread.Post(() => StudioWindowTheme.Apply(this, !lightTheme), Avalonia.Threading.DispatcherPriority.Background);
			};
			Opened += (_, _) => Browse(Directory.Exists(initialPath) ? initialPath : DefaultStart());
			KeyDown += (_, e) => {
				if(e.Key == Key.Escape) Close();
			};
		}

		private static string DefaultStart()
		{
			string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
			if(Directory.Exists(documents)) return documents;
			string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			return Directory.Exists(home) ? home : AppContext.BaseDirectory;
		}

		private void Browse(string path)
		{
			try {
				string full = Path.GetFullPath(path);
				if(!Directory.Exists(full)) {
					Directory.CreateDirectory(full);
				}
				_current = full;
				PathText.Text = full;
				List<FolderEntry> folders;
				try {
					folders = new DirectoryInfo(full).EnumerateDirectories()
						.Where(d => (d.Attributes & FileAttributes.System) == 0)
						.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
						.Select(d => new FolderEntry { Name = d.Name, FullPath = d.FullName })
						.ToList();
				} catch {
					folders = new List<FolderEntry>();
				}
				FolderList.ItemsSource = folders;
				FolderCountText.Text = folders.Count + (folders.Count == 1 ? " folder" : " folders");
				SelectionText.Text = full;
				ValidationText.Text = CanWrite(full) ? "This folder is available." : "This folder is not writable. Choose another location.";
				ValidationText.Classes.Set("error", !CanWrite(full));
				ValidationText.Classes.Set("muted", CanWrite(full));
				ChooseButton.IsEnabled = CanWrite(full);
				UpButton.IsEnabled = Path.GetDirectoryName(full) != null;
			} catch(Exception ex) {
				ValidationText.Text = "This folder cannot be opened: " + ex.Message;
				ValidationText.Classes.Set("error", true);
				ChooseButton.IsEnabled = false;
			}
		}

		private static bool CanWrite(string path)
		{
			try {
				if(!Directory.Exists(path)) return false;
				string probe = Path.Combine(path, ".retroarcdo-write-probe");
				File.WriteAllText(probe, "");
				File.Delete(probe);
				return true;
			} catch {
				return false;
			}
		}

		private void FolderList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
		{
			if(FolderList.SelectedItem is FolderEntry entry) {
				SelectionText.Text = entry.FullPath;
			}
		}

		private void FolderList_DoubleTapped(object? sender, TappedEventArgs e)
		{
			if(FolderList.SelectedItem is FolderEntry entry) {
				Browse(entry.FullPath);
			}
		}

		private void Up_Click(object? sender, RoutedEventArgs e)
		{
			string? parent = Path.GetDirectoryName(_current);
			if(parent != null) Browse(parent);
		}

		private void Refresh_Click(object? sender, RoutedEventArgs e) => Browse(_current);

		private void GoDocuments_Click(object? sender, RoutedEventArgs e) => Browse(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

		private void GoPictures_Click(object? sender, RoutedEventArgs e) => Browse(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));

		private void GoDesktop_Click(object? sender, RoutedEventArgs e) => Browse(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

		private void GoHome_Click(object? sender, RoutedEventArgs e) => Browse(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

		private void Close_Click(object? sender, RoutedEventArgs e) => Close();

		private void Choose_Click(object? sender, RoutedEventArgs e)
		{
			if(FolderList.SelectedItem is FolderEntry entry) {
				SelectedPath = entry.FullPath;
			} else {
				SelectedPath = _current;
			}
			Close(SelectedPath);
		}

		private async void NewFolder_Click(object? sender, RoutedEventArgs e)
		{
			string? name = await PromptNameAsync("New folder", "Folder name");
			if(string.IsNullOrWhiteSpace(name)) {
				return;
			}
			try {
				string target = Path.Combine(_current, name.Trim());
				Directory.CreateDirectory(target);
				Browse(target);
			} catch(Exception ex) {
				ValidationText.Text = "The folder could not be created: " + ex.Message;
				ValidationText.Classes.Set("error", true);
			}
		}

		private async System.Threading.Tasks.Task<string?> PromptNameAsync(string title, string label)
		{
			Window dialog = new() {
				Title = title,
				Width = 380,
				Height = 170,
				CanResize = false,
				WindowStartupLocation = WindowStartupLocation.CenterOwner,
				Background = new SolidColorBrush(_light ? Color.Parse("#FFFFFF") : Color.Parse("#22262D")),
				Foreground = new SolidColorBrush(_light ? Color.Parse("#1B2430") : Color.Parse("#F0F2F5"))
			};
			TextBox box = new() { Text = "New folder", Margin = new Thickness(0, 8, 0, 0) };
			Button ok = new() { Content = "Create", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
			Button cancel = new() { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 12, 0, 0) };
			ok.Click += (_, _) => dialog.Close(box.Text);
			cancel.Click += (_, _) => dialog.Close(null);
			dialog.Content = new StackPanel() {
				Margin = new Thickness(16),
				Children = {
					new TextBlock() { Text = label, FontWeight = FontWeight.SemiBold },
					box,
					new StackPanel() {
						Orientation = Orientation.Horizontal,
						HorizontalAlignment = HorizontalAlignment.Right,
						Children = { cancel, ok }
					}
				}
			};
			return await dialog.ShowDialog<string?>(this);
		}
	}
}

