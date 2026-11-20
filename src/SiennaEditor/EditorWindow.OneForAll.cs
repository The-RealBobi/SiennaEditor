using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using CFGBINManager;

namespace SiennaEditor;

public sealed partial class EditorWindow
{
    private async void OneForAllClicked(object? sender, RoutedEventArgs args)
    {
        if (busy) return;
        try { ApplyFields(); }
        catch (Exception error) { Status.Text = "Cannot open OneForAll: " + error.Message; return; }

        var dialog = new Window
        {
            Title = "OneForAll", Width = 660, Height = 510, MinWidth = 525, MinHeight = 370,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground, FontFamily = FontFamily
        };
        AutomationProperties.SetAutomationId(dialog, "oneforall");
        var pickerType = new FilePickerFileType("batchCFG files") { Patterns = ["*.batchcfg"] };
        var sourceList = new StackPanel { Spacing = 5 };
        using var closeIconStream = AssetLoader.Open(new Uri("avares://Sienna Editor/Assets/close.png"));
        using var closeIcon = new Bitmap(closeIconStream);
        var addFiles = new Button { Content = "Add files" };
        var addOpen = new Button { Content = "Add open tabs" };
        var pack = new Button { Content = "Create batchCFG" };
        var unpack = new Button { Content = "Extract files", IsEnabled = false };
        var close = new Button { Content = "Close" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var batchFile = new TextBox { IsReadOnly = true, PlaceholderText = "Select or drop a batchCFG" };
        var browse = new Button { Content = "Browse" };
        DocumentExportFormat[] formats = [DocumentExportFormat.Cfg, DocumentExportFormat.Bin, DocumentExportFormat.Json, DocumentExportFormat.Xml, DocumentExportFormat.Yaml];
        var format = new ComboBox { ItemsSource = formats.Select(DocumentFiles.ExportLabel).ToArray(), SelectedIndex = 0 };
        AutomationProperties.SetName(format, "Output format");
        AutomationProperties.SetAutomationId(format, "oneforall-format");
        AutomationProperties.SetAutomationId(sourceList, "oneforall-sources");
        AutomationProperties.SetAutomationId(pack, "oneforall-pack");
        AutomationProperties.SetAutomationId(unpack, "oneforall-unpack");
        AutomationProperties.SetAutomationId(batchFile, "oneforall-input");
        var packing = new Grid { Margin = new Thickness(14), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12 };
        var add = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { addFiles, addOpen } };
        var sources = new ScrollViewer { Content = sourceList };
        var packingFooter = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { pack } };
        packing.Children.Add(add);
        AddAtRow(packing, sources, 1);
        AddAtRow(packing, packingFooter, 2);

        var unpacking = new Grid { Margin = new Thickness(14), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12 };
        var input = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { batchFile, browse } };
        Grid.SetColumn(browse, 1);
        var modifiedOnly = new CheckBox { Content = "Export only modified files" };
        AutomationProperties.SetAutomationId(modifiedOnly, "oneforall-modified-only");
        ToolTip.SetTip(modifiedOnly, "Skip files that match the batch's original contents.");
        var unpackingFooter = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { modifiedOnly, format, unpack } };
        var guide = new TextBlock
        {
            Text = "New batches only need a destination. For a batch from the old Batch Editor, first select the folder containing the original CFG files.",
            TextWrapping = TextWrapping.Wrap
        };
        unpacking.Children.Add(new StackPanel { Spacing = 8, Children = { input, guide } });
        AddAtRow(unpacking, unpackingFooter, 2);
        var tabs = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem { Header = "CFGs to batchCFG", Content = packing },
                new TabItem { Header = "batchCFG to CFGs", Content = unpacking }
            }
        };
        var root = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("*,Auto,Auto"), RowSpacing = 8, Children = { tabs } };
        AddAtRow(root, status, 1);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        AddAtRow(root, close, 2);
        dialog.Content = root;

        var sourcesInOrder = new List<(string Name, string? Path, EditorSession? Session, CheckBox Include)>();
        var pathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var openSessions = sessions.Where(session => session.Document is not null).ToArray();
        var selectedCatalog = catalog;
        CancellationTokenSource? cancellation = null;

        void UpdateActions()
        {
            pack.IsEnabled = sourcesInOrder.Any(item => item.Include.IsChecked == true);
            unpack.IsEnabled = !string.IsNullOrEmpty(batchFile.Text);
            addOpen.IsEnabled = openSessions.Any(session => sourcesInOrder.All(item => !ReferenceEquals(item.Session, session)));
        }

        void AddSource(EditorSession? session, string? path)
        {
            if (sourcesInOrder.Any(item => session is not null && ReferenceEquals(session, item.Session)
                || path is not null && pathComparer.Equals(path, item.Path))) return;
            string name = path is not null ? Path.GetFileName(path) : "Untitled.cfg";
            var include = new CheckBox { Content = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis }, IsChecked = true };
            AutomationProperties.SetName(include, name);
            if (path is not null) ToolTip.SetTip(include, path);
            var item = (name, path, session, include);
            sourcesInOrder.Add(item);
            var remove = new Button { Content = new Image { Source = closeIcon, Width = 14, Height = 14 } };
            AutomationProperties.SetName(remove, "Remove " + name);
            ToolTip.SetTip(remove, "Remove " + name);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { include, remove } };
            Grid.SetColumn(remove, 1);
            include.IsCheckedChanged += (_, _) => UpdateActions();
            remove.Click += (_, _) => { sourceList.Children.Remove(row); sourcesInOrder.Remove(item); UpdateActions(); };
            sourceList.Children.Add(row);
        }

        void AddOpen()
        {
            foreach (var session in openSessions)
                AddSource(session, session.SourcePath.Length == 0 ? null : session.SourcePath);
            UpdateActions();
        }

        void AddFiles(IEnumerable<IStorageItem> selection)
        {
            int count = 0, unsupported = 0;
            foreach (var file in selection)
            {
                if (!IsSupportedDrop(file)) { unsupported++; continue; }
                string path = Path.GetFullPath(file.TryGetLocalPath()!);
                var session = openSessions.FirstOrDefault(tab => pathComparer.Equals(tab.SourcePath, path));
                int before = sourcesInOrder.Count;
                AddSource(session, path);
                if (sourcesInOrder.Count > before) count++;
            }
            UpdateActions();
            status.Text = unsupported == 0 ? $"{count:N0} documents added."
                : $"{count:N0} documents added. Skipped {unsupported:N0} items; choose local BIN or CFG files.";
        }

        void SetBatchFile(IReadOnlyList<IStorageItem> selection)
        {
            if (selection.Count != 1 || selection[0] is not IStorageFile file
                || !file.Name.EndsWith(".batchcfg", StringComparison.OrdinalIgnoreCase) || file.TryGetLocalPath() is not string path)
            {
                status.Text = "Choose one local .batchcfg file.";
                return;
            }
            batchFile.Text = Path.GetFullPath(path);
            UpdateActions();
            status.Text = "";
        }

        async Task RunOneForAll(string failure, Func<CancellationToken, Task> operation)
        {
            if (cancellation is not null) return;
            using var source = new CancellationTokenSource();
            cancellation = source;
            tabs.IsEnabled = false; close.Content = "Cancel"; status.Text = "";
            try { await operation(source.Token); }
            catch (OperationCanceledException) { status.Text = "Cancelled."; }
            catch (Exception error) { status.Text = $"{failure}: {error.Message}"; }
            finally
            {
                cancellation = null; tabs.IsEnabled = true; close.Content = "Close";
                UpdateActions();
            }
        }

        addOpen.Click += (_, _) => { status.Text = ""; AddOpen(); };
        addFiles.Click += async (_, _) => await RunOneForAll("Cannot add documents", async token =>
        {
            var files = await dialog.StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "Add BIN or CFG files", AllowMultiple = true, FileTypeFilter = [DocumentFileType, FilePickerFileTypes.All]
            });
            token.ThrowIfCancellationRequested();
            if (files.Count > 0) AddFiles(files);
        });
        browse.Click += async (_, _) => await RunOneForAll("Cannot open batchCFG", async token =>
        {
            var files = await dialog.StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "Select batchCFG", AllowMultiple = false, FileTypeFilter = [pickerType]
            });
            token.ThrowIfCancellationRequested();
            if (files.Count > 0) SetBatchFile(files);
        });
        DragDrop.SetAllowDrop(tabs, true);
        tabs.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = cancellation is null && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        tabs.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            e.Handled = true;
            if (cancellation is not null || e.DataTransfer.TryGetFiles() is not { } files) return;
            try
            {
                if (tabs.SelectedIndex == 1) SetBatchFile(files);
                else AddFiles(files);
            }
            catch (Exception error) { status.Text = "Cannot add dropped files: " + error.Message; }
        });
        pack.Click += async (_, _) => await RunOneForAll("Cannot export batchCFG", async token =>
        {
            var selected = sourcesInOrder.Where(item => item.Include.IsChecked == true).ToArray();
            if (selected.Length == 0) return;
            var file = await dialog.StorageProvider.SaveFilePickerAsync(new()
            {
                Title = "Export batchCFG", SuggestedFileName = "documents.batchcfg", DefaultExtension = "batchcfg",
                FileTypeChoices = [pickerType], ShowOverwritePrompt = false
            });
            if (file is null) return;
            string path = file.TryGetLocalPath() ?? throw new IOException("Choose a local file.");
            if (!path.EndsWith(".batchcfg", StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a filename ending in .batchcfg.");
            bool replace = File.Exists(path);
            if (replace && !await AskAsync(dialog, "Replace batchCFG?",
                    $"'{Path.GetFileName(path)}' already exists. Replace it with the new batch?", "Replace", "Cancel"))
                throw new OperationCanceledException();
            status.Text = "Preparing documents…";
            await Task.Run(() =>
            {
                var entries = new List<BatchDocumentEntry>();
                foreach (var item in selected)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var document = item.Session?.Document ?? DocumentFiles.Read(item.Path!, selectedCatalog);
                        entries.Add(new(item.Name, document));
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
                    { throw new IOException($"{item.Name}: {error.Message}", error); }
                }
                var bytes = System.Text.Encoding.UTF8.GetBytes(BatchCfg.Write(entries, token));
                token.ThrowIfCancellationRequested();
                if (replace)
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                        throw new IOException("Choose a regular file to replace.");
                    AtomicWrite(path, bytes);
                }
                else DocumentFiles.WriteNew(path, bytes);
            }, token);
            status.Text = $"Exported {selected.Length:N0} documents to {Path.GetFileName(path)}.";
        });
        unpack.Click += async (_, _) => await RunOneForAll("Cannot extract batchCFG", async token =>
        {
            if (string.IsNullOrEmpty(batchFile.Text) || format.SelectedIndex < 0) return;
            string path = batchFile.Text;
            DocumentExportFormat selected = formats[format.SelectedIndex];
            status.Text = "Reading batchCFG…";
            string text = await File.ReadAllTextAsync(path, token);
            using var reader = new StringReader(text);
            bool legacy = reader.ReadLine()?.TrimStart('\uFEFF').TrimEnd() == BatchCfg.LegacyHeader;
            var entries = legacy
                ? await LegacyBatch.ReadAsync(text, selectedCatalog, PickLegacyRoot, token)
                : await Task.Run(() => BatchCfg.Read(text, token), token);
            int skipped = 0;
            if (modifiedOnly.IsChecked == true)
            {
                if (entries.FirstOrDefault(entry => entry.IsModified is null) is { } unknown)
                    throw new InvalidDataException(legacy
                        ? $"Changes cannot be verified for '{unknown.RelativePath}'. Turn off Export only modified files to extract this batch."
                        : "This batch was created without change tracking. Turn off Export only modified files to extract it.");
                int originalCount = entries.Count;
                entries = entries.Where(entry => entry.IsModified == true).ToArray();
                skipped = originalCount - entries.Count;
                if (entries.Count == 0)
                {
                    status.Text = "No files were modified.";
                    return;
                }
            }
            status.Text = "Choose where to save the extracted files.";
            var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new()
            {
                Title = "Choose where to save the extracted files", AllowMultiple = false,
                SuggestedStartLocation = await BatchFolder()
            });
            if (folders.Count == 0) { status.Text = "Cancelled."; return; }
            token.ThrowIfCancellationRequested();
            string destination = folders[0].TryGetLocalPath() ?? throw new IOException("Choose a local folder.");
            status.Text = "Preparing output files…";
            int saved = await Task.Run(() => BatchCfg.ExtractAsync(entries, destination, selected, files =>
                Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    string names = string.Join("\n", files.Take(4));
                    if (files.Count > 4) names += $"\n…and {files.Count - 4} more";
                    return AskAsync(dialog, "Replace existing files?",
                        $"Files already exist in the destination. Replace these files?\n\n{names}", "Replace", "Cancel");
                }), token), token);
            status.Text = skipped == 0 ? $"Exported {saved:N0} documents." : $"Exported {saved:N0} documents. Skipped {skipped:N0} unchanged.";

            async Task<IStorageFolder?> BatchFolder()
            {
                string? folder = Path.GetDirectoryName(path);
                if (folder is null || Path.GetDirectoryName(folder) is null) return null;
                try { return await dialog.StorageProvider.TryGetFolderFromPathAsync(folder); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
            }

            async Task<string?> PickLegacyRoot(string alias, string hint)
            {
                token.ThrowIfCancellationRequested();
                status.Text = $"Choose the folder containing the original files for {alias}. This can be a different folder from the one used to create the batch.";
                if (!string.IsNullOrWhiteSpace(hint)) status.Text += $" Original location: {hint}";
                var roots = await dialog.StorageProvider.OpenFolderPickerAsync(new()
                {
                    Title = $"Locate original files for {alias} (old batchCFG)", AllowMultiple = false,
                    SuggestedStartLocation = await BatchFolder()
                });
                token.ThrowIfCancellationRequested();
                return roots.Count == 1 ? roots[0].TryGetLocalPath() : null;
            }
        });
        close.Click += (_, _) => { if (cancellation is null) dialog.Close(); else cancellation.Cancel(); };
        dialog.Closing += (_, e) => { if (cancellation is not null) { cancellation.Cancel(); e.Cancel = true; } };
        AddOpen();
        await dialog.ShowDialog(this);
    }

    private static void AddAtRow(Grid grid, Control control, int row)
    {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }
}
