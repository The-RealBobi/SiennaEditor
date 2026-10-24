using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using CFGBINManager;

namespace SiennaEditor;

internal sealed record RecordChoice(int Ordinal, CfgRow Row, string? ListName, int ListIndex);

public sealed partial class EditorWindow : Window
{
    private static readonly FilePickerFileType DocumentFileType = new("BIN and CFG files")
    { Patterns = ["*.bin", "*.cfg", "*.objbin", "*.cfgbin", "*.clobin", "*.mevbin", "*.fxbin", "*.fxbiny", "*bin"] };
    private readonly ObservableCollection<EditorSession> sessions = [];
    private EditorSession active;
    private StructureCatalog catalogRoot { get => active.CatalogRoot; set => active.CatalogRoot = value; }
    private StructureCatalog catalog { get => active.Catalog; set => active.Catalog = value; }
    private CfgDocument? document { get => active.Document; set => active.Document = value; }
    private string sourcePath { get => active.SourcePath; set => active.SourcePath = value; }
    private string catalogName { get => active.CatalogName; set => active.CatalogName = value; }
    private bool dirty { get => active.Dirty; set => active.Dirty = value; }
    private List<RecordChoice> allRecords = [];
    private List<RecordChoice> matching = [];
    private List<RecordBranch> branches = [];
    private List<RecordBranch> visibleBranches = [];
    private HashSet<RecordBranch> includedBranches = [];
    private readonly ObservableCollection<FieldEdit> editors = [];
    private RecordChoice? current;
    private bool changing = true;
    private bool busy;
    private bool allowClose;
    private bool HasChanges => dirty || editors.Any(edit => edit.IsChanged || edit.IsLabelChanged);

    public EditorWindow()
    {
        LoadStructures();
        var choice = structureChoices[0];
        active = new EditorSession(choice.File.Root, choice.Catalog, Path.GetFileName(choice.File.Path))
        { Document = CfgDocument.CreateT2b(choice.Catalog), IsAuthoring = true };
        InitializeComponent();
        Tabs.ItemsSource = sessions;
        sessions.Add(active); Tabs.SelectedItem = active;
        Fields.ItemsSource = editors;
        Profiles.SelectionChanged += ProfileChanged;
        AddContext.AddHandler(PointerPressedEvent, AddContextPressed, RoutingStrategies.Tunnel);
        AddContext.AddHandler(KeyDownEvent, AddContextKeyDown, RoutingStrategies.Tunnel);
        SetProfiles(); RebuildRecords(); UpdateControls();
        Status.Text = catalogLoadErrors.Length == 0 ? "Add an entry to start, or open a file." : "Some Structures were not loaded: " + catalogLoadErrors;
        changing = false;
        Closed += (_, _) => { searchDelay?.Cancel(); searchDelay?.Dispose(); };
        Closing += ClosingWindow;
        KeyDown += WindowKeyDown;
        Records.AddHandler(PointerPressedEvent, DragEntryPressed, RoutingStrategies.Tunnel);
        Records.AddHandler(PointerMovedEvent, DragEntryMoved, RoutingStrategies.Tunnel);
        Records.AddHandler(PointerReleasedEvent, DragEntryReleased, RoutingStrategies.Tunnel);
    }

    private async Task<bool> RunAsync(string operation, Func<Task> action)
    {
        if (busy) return false;
        busy = true; Workspace.IsEnabled = false; Profiles.IsEnabled = false; Status.Text = operation + "…";
        try { await action(); return true; }
        catch (Exception error) { Status.Text = $"{operation} failed: {error.Message}"; return false; }
        finally { Workspace.IsEnabled = true; Profiles.IsEnabled = true; busy = false; UpdateControls(); UpdateTitle(); }
    }

    public Task<bool> OpenPathAsync(string path) => RunAsync("Open file", async () =>
    {
        ApplyFields();
        var fullPath = Path.GetFullPath(path);
        var existing = sessions.FirstOrDefault(session => string.Equals(session.SourcePath, fullPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        if (existing is not null) { ActivateSession(existing); Status.Text = "Already open: " + Path.GetFileName(fullPath); return; }
        var opened = await Task.Run(() => DocumentFiles.Read(path, catalog));
        var session = new EditorSession(catalogRoot, catalog, catalogName) { Document = opened, SourcePath = fullPath };
        sessions.Add(session); ActivateSession(session);
        Status.Text = "Opened " + Path.GetFileName(sourcePath);
    });

    private async void OpenClicked(object? sender, RoutedEventArgs args) => await PickFilesAsync();

    private static bool IsSupportedDrop(IStorageItem item) => item is IStorageFile
        && item.TryGetLocalPath() is not null
        && (DocumentFiles.IsBinaryFile(item.Name) || item.Name.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase));

    private void FileDragOver(object? sender, DragEventArgs args)
    {
        args.DragEffects = !busy && args.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
    }

    private async void FileDrop(object? sender, DragEventArgs args)
    {
        args.Handled = true;
        if (busy) return;
        var files = args.DataTransfer.TryGetFiles()?.Where(IsSupportedDrop).Select(item => item.TryGetLocalPath()!).ToArray() ?? [];
        if (files.Length == 0) { Status.Text = "Drop local BIN or CFG files. Lua files and folders are not supported."; return; }
        foreach (string path in files)
            if (!await OpenPathAsync(path)) break;
    }

    private async Task PickFilesAsync()
    {
        if (busy) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open BIN or CFG", AllowMultiple = true,
                FileTypeFilter = [DocumentFileType, FilePickerFileTypes.All]
            });
            foreach (var file in files)
                if (file.TryGetLocalPath() is string path) await OpenPathAsync(path);
                else Status.Text = "Choose a local file so it can be opened safely.";
        }
        catch (Exception error) { Status.Text = "Open failed: " + error.Message; }
    }

    private void SetProfiles()
    {
        changing = true;
        Profiles.ItemsSource = structureChoices;
        Profiles.SelectedItem = structureChoices.FirstOrDefault(choice => ReferenceEquals(choice.Catalog, catalog));
        changing = false;
    }

    private async void StructureClicked(object? sender, RoutedEventArgs args)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add Structures to data", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Structure catalogs") { Patterns = ["*.cfg"] }]
        });
        if (files.Count == 0) return;
        await RunAsync("Add Structures", async () =>
        {
            ApplyFields();
            foreach (var selectedFile in files)
            {
                using var stream = await selectedFile.OpenReadAsync(); using var reader = new StreamReader(stream, Encoding.UTF8, true);
                string text = await reader.ReadToEndAsync(); _ = StructureCatalog.Parse(text);
                string name = Path.GetFileNameWithoutExtension(selectedFile.Name), target = Path.Combine(dataDirectory, name + ".cfg");
                for (int number = 2; File.Exists(target); number++) target = Path.Combine(dataDirectory, name + "-" + number.ToString(CultureInfo.InvariantCulture) + ".cfg");
                AtomicWrite(target, Encoding.UTF8.GetBytes(text)); RegisterStructure(target);
            }
            SetProfiles(); Status.Text = "Structures added to data. Choose a catalog and game in Settings.";
        });
    }

    private async void ProfileChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (changing || Profiles.SelectedItem is not StructureChoice choice || ReferenceEquals(choice.Catalog, catalog)) return;
        await RunAsync("Select Structure", async () =>
        {
            try
            {
                ApplyFields();
                var selected = choice.Catalog;
                CfgDocument? relabeled = document is null ? null : await Task.Run(() => CfgTextCodec.Read(CfgTextCodec.Write(document), selected));
                _ = relabeled?.GetRecordTree();
                catalogRoot = choice.File.Root; catalog = selected; catalogName = Path.GetFileName(choice.File.Path);
                document = relabeled; RebuildRecords(); Status.Text = "Structure: " + choice;
            }
            finally { SetProfiles(); }
        });
    }

    private void RebuildRecords()
    {
        changing = true; current = null; editors.Clear(); Records.ItemsSource = null;
        allRecords = [];
        if (document is not null)
        {
            if (document.Format == BinaryFormat.T2b)
                allRecords.AddRange(document.Rows.Select((row, i) => new RecordChoice(i, row, null, i)));
            else
                foreach (var list in document.Lists)
                    for (int i = 0; i < list.Rows.Count; i++) allRecords.Add(new RecordChoice(allRecords.Count, list.Rows[i], list.Name, i));
        }
        changing = false;
        matching = allRecords.Where(item => Matches(item, Search.Text?.Trim() ?? "")).ToList();
        branches = [];
        if (document is not null)
        {
            var byRow = allRecords.ToDictionary(item => item.Row);
            var occurrences = new Dictionary<(RecordBranch? Parent, string Name), int>();
            var pending = new Stack<(CfgRecordNode Node, RecordBranch? Parent)>();
            foreach (var node in document.GetRecordTree().Reverse()) pending.Push((node, null));
            while (pending.TryPop(out var next))
            {
                var row = next.Node.Row;
                string? blockEnd = row is not null && document.Format == BinaryFormat.T2b ? document.GetBlockEnd(row.Name) : null;
                bool closing = row is not null && next.Parent?.Record is { } parent && document.Format == BinaryFormat.T2b
                    && document.GetBlockEnd(parent.Row.Name) == row.Name;
                var key = (next.Parent, row?.Name ?? next.Node.ListName ?? "");
                int occurrence = occurrences.GetValueOrDefault(key);
                if (!closing) occurrences[key] = occurrence + 1;
                var branch = new RecordBranch(next.Node, row is null ? null : byRow[row], next.Parent, blockEnd, closing, occurrence);
                branches.Add(branch);
                foreach (var child in next.Node.Children.Reverse()) pending.Push((child, branch));
            }
        }
        IncludeMatches(!string.IsNullOrWhiteSpace(Search.Text)); ShowPage();
        UpdateControls();
    }

    private async Task FilterAsync()
    {
        ApplyFields();
        string text = Search.Text?.Trim() ?? "";
        active.Search = Search.Text ?? "";
        matching = await Task.Run(() => allRecords.Where(item => Matches(item, text)).ToList());
        IncludeMatches(text.Length > 0); ShowPage();
        Status.Text = $"{matching.Count:N0} matching records; parent records remain visible.";
    }

    private void IncludeMatches(bool filtering)
    {
        var matches = matching.ToHashSet();
        includedBranches = filtering ? branches.Where(branch => branch.Record is not null && matches.Contains(branch.Record)).ToHashSet() : branches.ToHashSet();
        for (int i = branches.Count - 1; i >= 0; i--)
        {
            var branch = branches[i];
            if (includedBranches.Contains(branch) && branch.Parent is not null)
            {
                includedBranches.Add(branch.Parent);
                if (filtering) branch.Parent.IsExpanded = true;
            }
        }
    }

    private void RefreshVisibleBranches()
    {
        var shown = new HashSet<RecordBranch>();
        visibleBranches = [];
        foreach (var branch in branches)
            if (includedBranches.Contains(branch) && (branch.Parent is null || shown.Contains(branch.Parent) && branch.Parent.IsExpanded))
            { shown.Add(branch); if (!branch.IsClosing) visibleBranches.Add(branch); }
    }

    private void ShowPage()
    {
        changing = true; current = null; editors.Clear();
        RefreshVisibleBranches();
        Records.ItemsSource = visibleBranches;
        Records.SelectedIndex = -1;
        changing = false;
        if (visibleBranches.Count > 0) Records.SelectedIndex = 0;
        UpdateControls();
    }

    private void FocusBranch(RecordBranch branch)
    {
        if (branch.IsClosing && branch.Parent is { } owner) branch = owner;
        for (var parent = branch.Parent; parent is not null; parent = parent.Parent) parent.IsExpanded = true;
        ShowPage(); Records.SelectedItem = branch; Records.ScrollIntoView(branch);
    }

    private void ToggleBranch(RecordBranch branch)
    {
        ApplyFields(); branch.IsExpanded = !branch.IsExpanded;
        FocusBranch(branch);
    }

    private async void ToggleClicked(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (sender is Button { DataContext: RecordBranch branch })
            await RunAsync("Toggle records", () => { ToggleBranch(branch); Status.Text = "Ready"; return Task.CompletedTask; });
    }

    private async void TreeKeyDown(object? sender, KeyEventArgs args)
    {
        if (Records.SelectedItem is not RecordBranch branch || args.Key is not (Key.Left or Key.Right)) return;
        args.Handled = true;
        await RunAsync("Navigate records", () =>
        {
            ApplyFields();
            if (args.Key == Key.Right)
            {
                if (branch.HasChildren && !branch.IsExpanded) ToggleBranch(branch);
                else if (branches.FirstOrDefault(child => child.Parent == branch && !child.IsClosing && includedBranches.Contains(child)) is { } child) FocusBranch(child);
            }
            else if (branch.HasChildren && branch.IsExpanded) ToggleBranch(branch);
            else if (branch.Parent is not null) FocusBranch(branch.Parent);
            Status.Text = "Ready"; return Task.CompletedTask;
        });
    }

    private void RecordChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (changing) return;
        try
        {
            ApplyFields(); current = (Records.SelectedItem as RecordBranch)?.Record; BuildFields();
        }
        catch (Exception error)
        {
            changing = true; Records.SelectedItem = branches.FirstOrDefault(branch => branch.Record == current); changing = false;
            Status.Text = "Edit not applied: " + error.Message;
        }
    }

    private void BuildFields()
    {
        editors.Clear();
        if (current is null || document is null) { UpdateControls(); return; }
        var row = current.Row;
        var definitions = catalog.GetFields(document.Format, row.Name, row.Values.Count);
        var types = document.Types;
        IReadOnlyList<CfgField> native = current.ListName is null ? [] : document.GetListFields(current.ListName);
        for (int i = 0; i < row.Values.Count; i++)
        {
            string name = active.UseTags && document.Format == BinaryFormat.T2b && definitions.Count == row.Values.Count ? definitions[i].Name : row.FieldNames[i];
            if (active.UseTags && document.Format == BinaryFormat.Rdbnp && definitions.SingleOrDefault(field => field.Name == name)?.Alias is string alias)
                name = alias + " (" + name + ")";
            CfgField? descriptor = i < native.Count ? native[i] : null;
            Add(row.Values[i], i, [], name, descriptor, 0);
        }
        UpdateControls();

        string DisplayLabel(int root, int[] path, string fallback) => active.UseTags ? catalog.GetFieldLabel(document.Format, row.Name, row.Values.Count, string.Join(".", new[] { root }.Concat(path))) ?? fallback : fallback;

        void Add(CfgValue value, int rootIndex, int[] path, string label, CfgField? descriptor, int componentWidth)
        {
            if (value.Kind is ValueKind.Array or ValueKind.Structure or ValueKind.Vector)
            {
                if (value.Items.Count == 0)
                    editors.Add(new FieldEdit(rootIndex, path, value, 0, DisplayLabel(rootIndex, path, label), false, false, active.UseTags, FieldChanged));
                CfgType? structure = value.Kind == ValueKind.Structure && descriptor is not null && descriptor.TypeCode < types.Count ? types[descriptor.TypeCode] : null;
                for (int i = 0; i < value.Items.Count; i++)
                {
                    CfgField? child = structure is not null && i < structure.Fields.Count ? structure.Fields[i] : value.Kind == ValueKind.Array ? descriptor : null;
                    string suffix = structure is not null ? "." + structure.Fields[i].Name : $"[{i}]";
                    Add(value.Items[i], rootIndex, [.. path, i], label + suffix, child, value.Kind == ValueKind.Vector ? 4 : 0);
                }
                return;
            }
            int width = document.Format == BinaryFormat.T2b ? 4 : descriptor?.PrimitiveSize ?? componentWidth;
            editors.Add(new FieldEdit(rootIndex, path, value, width, DisplayLabel(rootIndex, path, label), document.Format == BinaryFormat.T2b && path.Length == 0, active.IsAuthoring && document.Format == BinaryFormat.T2b && path.Length == 0, active.UseTags, FieldChanged));
        }
    }

    private void ApplyFields()
    {
        if (current is null || !editors.Any(field => field.IsChanged || field.IsLabelChanged)) return;
        var changes = new Dictionary<int, CfgValue>();
        foreach (var field in editors.Where(field => field.IsChanged))
        {
            CfgValue parsed;
            try { parsed = field.Parse(); }
            catch (Exception error) when (error is FormatException or OverflowException or ArgumentException)
            { throw new FormatException($"{field.Label}: {error.Message}", error); }
            CfgValue root = changes.GetValueOrDefault(field.RootIndex, current.Row.Values[field.RootIndex]);
            changes[field.RootIndex] = ValueText.Replace(root, field.Path, 0, parsed);
        }
        ApplyLabels();
        foreach (var change in changes)
            if (document!.Format == BinaryFormat.T2b) document.SetT2bValue(current.Row, change.Key, change.Value);
            else current.Row.SetValue(change.Key, change.Value);
        if (changes.Count > 0) MarkDirty();
        BuildFields();
    }

    private void MarkDirty() { dirty = true; UpdateTitle(); }
    private void UpdateTitle()
    {
        active.Title = (HasChanges ? "* " : "") + (sourcePath.Length == 0 ? "Untitled" : Path.GetFileName(sourcePath));
        Title = document is null ? "Sienna Editor" : active.Title + " — Sienna Editor";
    }

    private async Task SaveAsync(bool cfg)
    {
        if (document is null) { Status.Text = "Open a file before saving."; return; }
        ApplyFields();
        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = cfg ? "Export readable CFG" : "Save reconstructed BIN", ShowOverwritePrompt = true,
            SuggestedFileName = sourcePath.Length == 0 ? (cfg ? "Untitled.cfg" : "Untitled.cfg.bin") : Path.GetFileNameWithoutExtension(sourcePath) + (cfg ? ".cfg" : ".edited.bin"),
            FileTypeChoices = [new FilePickerFileType(cfg ? "Readable CFG" : "BIN file") { Patterns = [cfg ? "*.cfg" : "*.bin"] }]
        });
        if (target is null) { Status.Text = "Save cancelled."; return; }
        string path = target.TryGetLocalPath() ?? throw new IOException("Choose a local destination for atomic saving.");
        await WriteDocumentAsync(path, cfg, !cfg);
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ".sienna-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task ChangeRowAsync(bool remove)
    {
        if (document is null || current is null) throw new InvalidOperationException("Select a record first.");
        ApplyFields(); var selected = SelectedRecords();
        if (remove && !await AskAsync("Remove records", $"Remove {selected.Count} record(s), including contained children?", "Remove")) return;
        int ordinal = current.Ordinal, insertion = selected[^1].ListIndex + 1;
        string? listName = current.ListName;
        int focus = remove ? ordinal : selected[^1].Ordinal + 1;
        await EditDocumentAsync(copy =>
        {
            if (listName is null)
            {
                if (remove) foreach (var row in selected.AsEnumerable().Reverse()) copy.RemoveRow(row.ListIndex);
                else
                {
                    var clones = selected.Select(item => copy.Rows[item.ListIndex].Clone()).ToArray();
                    foreach (var row in clones) copy.InsertRow(insertion++, row);
                }
            }
            else
            {
                if (remove) foreach (var row in selected.AsEnumerable().Reverse()) copy.RemoveListRow(listName, row.ListIndex);
                else
                {
                    var clones = selected.Select(item => copy.GetList(listName).Rows[item.ListIndex].Clone()).ToArray();
                    foreach (var row in clones) copy.InsertListRow(listName, insertion++, row);
                }
            }
        }, focus, remove ? "Records removed; supported counts and references recalculated." : "Branch duplicated. Edit identifiers where uniqueness is required.");
    }

    private async Task<bool> AskAsync(string title, string message, string accept)
    {
        var dialog = new Window { Title = title, Width = 470, Height = 190, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var yes = new Button { Content = accept };
        var no = new Button { Content = "Keep editing" };
        yes.Click += (_, _) => dialog.Close(true); no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 18, Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { no, yes } } } };
        return await dialog.ShowDialog<bool>(this);
    }

    private async void ClosingWindow(object? sender, WindowClosingEventArgs args)
    {
        if (allowClose) return;
        if (busy) { args.Cancel = true; Status.Text = "Wait for the current operation to finish before closing."; return; }
        try { ApplyLabels(); }
        catch (Exception error) { args.Cancel = true; Status.Text = "Cannot close: " + error.Message; return; }
        int modified = sessions.Count(session => ReferenceEquals(session, active) ? HasChanges : session.Dirty);
        if (modified == 0) return;
        args.Cancel = true;
        if (await AskAsync("Unsaved changes", $"Close Sienna Editor and discard changes in {modified} file(s)?", "Discard and close")) { allowClose = true; Close(); }
    }

    private async void WindowKeyDown(object? sender, KeyEventArgs args)
    {
        if (busy || (args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0) return;
        if (args.Key == Key.S) { args.Handled = true; await RunAsync("Save", async () => { await SaveCurrentAsync(); }); }
        else if (args.Key == Key.E && args.KeyModifiers.HasFlag(KeyModifiers.Shift)) { args.Handled = true; ExportBatchClicked(this, new RoutedEventArgs()); }
        else if (args.Key == Key.N) { args.Handled = true; await NewDocumentAsync(); }
        else if (args.Key == Key.O) { args.Handled = true; await PickFilesAsync(); }
        else if (args.Key == Key.W) { args.Handled = true; await CloseSessionAsync(active); }
    }

    private async void SearchKeyDown(object? sender, KeyEventArgs args) { if (args.Key == Key.Enter) { searchDelay?.Cancel(); args.Handled = true; await RunAsync("Find records", FilterAsync); } }
    private async void SaveBinClicked(object? sender, RoutedEventArgs args) => await RunAsync("Save BIN", () => SaveAsync(false));
    private async void ExportCfgClicked(object? sender, RoutedEventArgs args) => await RunAsync("Export CFG", () => SaveAsync(true));
    private async void DuplicateClicked(object? sender, RoutedEventArgs args) => await RunAsync("Duplicate record", () => ChangeRowAsync(false));
    private async void DeleteClicked(object? sender, RoutedEventArgs args) => await RunAsync("Delete record", () => ChangeRowAsync(true));
    private async void SchemaClicked(object? sender, RoutedEventArgs args)
    {
        if (document is null) { Status.Text = "Open a file to inspect its schema."; return; }
        var dialog = new Window { Title = "Sienna Editor — Native schema", Width = 820, Height = 640, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Content = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            Text = JsonSerializer.Serialize(new { Format = document.Format.ToString(), document.BinaryVersion, document.IsBigEndian, document.T2bInfo, Types = document.Types,
                Records = allRecords.Select(item => new { item.Row.Name, Fields = item.Row.Values.Count }).Distinct() }, new JsonSerializerOptions { WriteIndented = true }) };
        await dialog.ShowDialog(this);
    }
}
