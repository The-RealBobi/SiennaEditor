using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CFGBINManager;

namespace SiennaEditor;

public sealed partial class EditorWindow
{
    private async void ExportBatchClicked(object? sender, RoutedEventArgs args)
    {
        if (busy) return;
        try { ApplyFields(); }
        catch (Exception error) { Status.Text = "Cannot export: " + error.Message; return; }
        var dialog = new Window
        {
            Title = "Export batch", Width = 620, Height = 520, MinWidth = 560, MinHeight = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground, FontFamily = FontFamily
        };
        var input = new TextBox { IsReadOnly = true, PlaceholderText = "Select a folder containing BIN files" };
        var output = new TextBox { IsReadOnly = true, PlaceholderText = "Select an output folder" };
        var browse = new Button { Content = "Browse…" };
        var browseOutput = new Button { Content = "Browse…" };
        var recursive = new CheckBox { Content = "Recursive batch" };
        var custom = new CheckBox { Content = "Custom output folder" };
        var outputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, IsVisible = false };
        outputRow.Children.Add(output); Grid.SetColumn(browseOutput, 1); outputRow.Children.Add(browseOutput);
        custom.IsCheckedChanged += (_, _) => outputRow.IsVisible = custom.IsChecked == true;
        var sourceRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        sourceRow.Children.Add(input); Grid.SetColumn(browse, 1); sourceRow.Children.Add(browse);
        var options = new StackPanel { Spacing = 12, Children = { sourceRow, recursive, custom, outputRow } };
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 7 };
        var summary = new TextBlock { Text = "Export BIN files to readable CFG. Existing files are skipped.", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var details = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 120 };
        var export = new Button { Content = "Export", IsEnabled = false };
        var close = new Button { Content = "Close" };
        bool running = false; CancellationTokenSource? cancellation = null;
        async Task PickFolder(TextBox destination)
        {
            try
            {
                var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select folder", AllowMultiple = false });
                if (folders.Count == 1) destination.Text = folders[0].TryGetLocalPath() ?? throw new IOException("Choose a local folder.");
                export.IsEnabled = !string.IsNullOrEmpty(input.Text);
            }
            catch (Exception error) { summary.Text = "Cannot select folder: " + error.Message; }
        }
        browse.Click += async (_, _) => await PickFolder(input);
        browseOutput.Click += async (_, _) => await PickFolder(output);
        close.Click += (_, _) => { if (running) cancellation?.Cancel(); else dialog.Close(); };
        dialog.Closing += (_, e) => { if (running) { e.Cancel = true; cancellation?.Cancel(); summary.Text = "Cancelling after the current file…"; } };
        export.Click += async (_, _) =>
        {
            if (running) return;
            if (custom.IsChecked == true && string.IsNullOrEmpty(output.Text)) { summary.Text = "Select the output folder first."; return; }
            string source = Path.GetFullPath(input.Text!);
            string destination = Path.GetFullPath(custom.IsChecked == true ? output.Text! : source);
            bool recurse = recursive.IsChecked == true;
            var root = StructureCatalog.Parse(catalogRoot.ToText());
            var selected = catalog.ProfileName is null ? root : root.ForProfile(catalog.ProfileName);
            var protectedPaths = sessions.Select(session => session.SourcePath).Concat(structureFiles.Select(file => file.Path))
                .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            running = true; busy = true; options.IsEnabled = false; export.IsEnabled = false; close.Content = "Cancel";
            cancellation = new CancellationTokenSource(); var token = cancellation.Token;
            try
            {
                var result = await Task.Run(() => DocumentFiles.ExportBatch(source, destination, recurse, selected, protectedPaths,
                    update => Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        progress.Maximum = Math.Max(1, update.Total); progress.Value = update.Visited;
                        summary.Text = $"{update.Visited:N0} / {update.Total:N0} · {update.File}";
                    }).GetAwaiter().GetResult(), token));
                summary.Text = result.ToString();
                details.Text = string.Join(Environment.NewLine, result.Failures);
                Status.Text = summary.Text;
            }
            catch (Exception error) { summary.Text = "Batch export failed: " + error.Message; }
            finally { running = false; busy = false; options.IsEnabled = true; export.IsEnabled = true; close.Content = "Close"; cancellation.Dispose(); cancellation = null; }
        };
        var body = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"), RowSpacing = 14 };
        body.Children.Add(new TextBlock { Text = "Structure: " + catalogName + " — " + (catalog.ProfileName ?? "Custom"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        Grid.SetRow(options, 1); body.Children.Add(options); Grid.SetRow(progress, 2); body.Children.Add(progress);
        Grid.SetRow(summary, 3); body.Children.Add(summary); Grid.SetRow(details, 4); body.Children.Add(details);
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 10, Children = { export, close } };
        Grid.SetRow(buttons, 5); body.Children.Add(buttons); dialog.Content = body;
        await dialog.ShowDialog(this);
    }

}
