using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CFGBINManager;

namespace SiennaEditor;

internal sealed record RecordChoice(int Ordinal, CfgRow Row, string? ListName, int ListIndex)
{
    public string Heading => $"{Ordinal + 1:N0}  {Row.Name}";
    public string Preview => Row.Values.Count == 0 ? "No fields" : ValueText.Format(Row.Values[0]).Replace('\n', ' ');
}

public sealed partial class EditorWindow : Window
{
    private const int PageSize = 200;
    private StructureCatalog catalogRoot;
    private StructureCatalog catalog;
    private CfgDocument? document;
    private string sourcePath = "";
    private string catalogName = "Bundled Structure";
    private List<RecordChoice> allRecords = [];
    private List<RecordChoice> matching = [];
    private readonly ObservableCollection<FieldEdit> editors = [];
    private RecordChoice? current;
    private int page;
    private bool changing;
    private bool busy;
    private bool dirty;
    private bool allowClose;
    private bool HasChanges => dirty || editors.Any(edit => edit.IsChanged);

    public EditorWindow()
    {
        InitializeComponent();
        using var stream = typeof(EditorWindow).Assembly.GetManifestResourceStream("structure.cfg")
            ?? throw new InvalidOperationException("The bundled Structure is missing. Rebuild Sienna Editor with CFGBINManager.");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        catalogRoot = StructureCatalog.Parse(reader.ReadToEnd());
        catalog = catalogRoot;
        Fields.ItemsSource = editors;
        SetProfiles();
        Previous.IsEnabled = Next.IsEnabled = false;
        Closing += ClosingWindow;
        KeyDown += WindowKeyDown;
    }

    private async Task RunAsync(string operation, Func<Task> action)
    {
        if (busy) return;
        busy = true; Workspace.IsEnabled = false; Status.Text = operation + "…";
        try { await action(); }
        catch (Exception error) { Status.Text = $"{operation} failed: {error.Message}"; }
        finally { Workspace.IsEnabled = true; busy = false; }
    }

    public Task OpenPathAsync(string path) => RunAsync("Open file", async () =>
    {
        if (!await CanReplaceAsync()) return;
        var opened = await Task.Run(() => Path.GetExtension(path).Equals(".cfg", StringComparison.OrdinalIgnoreCase)
            ? CfgTextCodec.Read(File.ReadAllText(path, Encoding.UTF8), catalog) : BinCodec.Read(File.ReadAllBytes(path), catalog));
        document = opened; sourcePath = Path.GetFullPath(path); dirty = false;
        RebuildRecords(); UpdateTitle();
        Status.Text = "Opened " + Path.GetFileName(sourcePath);
    });

    private async void OpenClicked(object? sender, RoutedEventArgs args)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open BIN or CFG", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("BIN and CFG files") { Patterns = ["*.bin", "*.cfg", "*.objbin", "*.cfgbin", "*bin"] }, FilePickerFileTypes.All]
        });
        if (files.Count == 1)
        {
            string? path = files[0].TryGetLocalPath();
            if (path is null) Status.Text = "Choose a local file so it can be opened safely.";
            else await OpenPathAsync(path);
        }
    }

    private async Task<bool> CanReplaceAsync() => !HasChanges || await AskAsync("Unsaved changes", "Discard the current changes and open another file?", "Discard changes");

    private void SetProfiles()
    {
        changing = true;
        Profiles.ItemsSource = catalogRoot.ProfileNames.Count == 0 ? new[] { "Generic" } : catalogRoot.ProfileNames;
        Profiles.SelectedItem = catalog.ProfileName ?? "Generic";
        changing = false;
    }

    private async void StructureClicked(object? sender, RoutedEventArgs args)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Structure catalog", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Structure catalog") { Patterns = ["*.cfg"] }]
        });
        if (files.Count != 1) return;
        await RunAsync("Load Structure", async () =>
        {
            ApplyFields();
            using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            StructureCatalog root = StructureCatalog.Parse(await reader.ReadToEndAsync());
            string? profile = root.ProfileNames.Count > 1 ? await PickProfileAsync(root.ProfileNames) : root.ProfileName;
            if (root.ProfileNames.Count > 1 && profile is null) { Status.Text = "Structure selection cancelled."; return; }
            StructureCatalog selected = profile is null ? root : root.ForProfile(profile);
            CfgDocument? relabeled = document is null ? null : await Task.Run(() => CfgTextCodec.Read(CfgTextCodec.Write(document), selected));
            catalogRoot = root; catalog = selected; document = relabeled; catalogName = files[0].Name;
            SetProfiles(); RebuildRecords();
            Status.Text = "Loaded " + catalogName;
        });
    }

    private async void ProfileChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (changing || Profiles.SelectedItem is not string profile || profile == (catalog.ProfileName ?? "Generic")) return;
        await RunAsync("Select profile", async () =>
        {
            try
            {
                ApplyFields();
                StructureCatalog selected = catalogRoot.ForProfile(profile);
                CfgDocument? relabeled = document is null ? null : await Task.Run(() => CfgTextCodec.Read(CfgTextCodec.Write(document), selected));
                catalog = selected; document = relabeled; RebuildRecords(); Status.Text = "Profile: " + profile;
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
        Types.ItemsSource = new[] { "All records" }.Concat(allRecords.Select(item => item.Row.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)).ToArray();
        Types.SelectedIndex = 0; page = 0; changing = false;
        matching = allRecords.ToList(); ShowPage();
        FileTitle.Text = sourcePath.Length == 0 ? "Open a BIN or CFG file to begin" : Path.GetFileName(sourcePath);
        FileInfo.Text = document is null ? catalogName : $"{document.Format} · {allRecords.Count:N0} records · {catalogName} / {catalog.ProfileName ?? "Generic"}";
    }

    private async Task FilterAsync()
    {
        ApplyFields();
        string text = Search.Text?.Trim() ?? "", type = Types.SelectedItem as string ?? "All records";
        matching = await Task.Run(() => allRecords.Where(item => (type == "All records" || item.Row.Name == type)
            && (text.Length == 0 || item.Row.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || item.Row.Values.Any(value => ValueText.Contains(value, text)))).ToList());
        page = 0; ShowPage(); Status.Text = $"{matching.Count:N0} matching records";
    }

    private void ShowPage()
    {
        changing = true; current = null; editors.Clear();
        Records.ItemsSource = matching.Skip(page * PageSize).Take(PageSize).ToArray();
        Records.SelectedIndex = -1;
        Previous.IsEnabled = page > 0; Next.IsEnabled = (page + 1) * PageSize < matching.Count;
        PageInfo.Text = matching.Count == 0 ? "No records" : $"{page * PageSize + 1:N0}–{Math.Min((page + 1) * PageSize, matching.Count):N0} / {matching.Count:N0}";
        changing = false;
        if (matching.Count > 0) Records.SelectedIndex = 0;
    }

    private void RecordChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (changing) return;
        try
        {
            ApplyFields(); current = Records.SelectedItem as RecordChoice; BuildFields();
        }
        catch (Exception error)
        {
            changing = true; Records.SelectedItem = current; changing = false;
            Status.Text = "Edit not applied: " + error.Message;
        }
    }

    private void BuildFields()
    {
        editors.Clear();
        if (current is null || document is null) { RowInfo.Text = "Select a record to inspect its fields."; return; }
        var row = current.Row;
        var definitions = catalog.GetFields(document.Format, row.Name, row.Values.Count);
        var types = document.Types;
        IReadOnlyList<CfgField> native = current.ListName is null ? [] : document.GetListFields(current.ListName);
        for (int i = 0; i < row.Values.Count; i++)
        {
            string name = document.Format == BinaryFormat.T2b && definitions.Count == row.Values.Count ? definitions[i].Name : row.FieldNames[i];
            if (document.Format == BinaryFormat.Rdbnp && definitions.SingleOrDefault(field => field.Name == name)?.Alias is string alias)
                name = alias + " (" + name + ")";
            CfgField? descriptor = i < native.Count ? native[i] : null;
            Add(row.Values[i], i, [], $"[{i}] {name}", descriptor, 0);
        }
        RowInfo.Text = $"Record {current.Ordinal + 1:N0} · {row.Name} · {row.Values.Count} fields" + (current.ListName is null ? "" : " · " + current.ListName)
            + "\nEdits apply when you select another record. Revert restores unapplied edits.";

        void Add(CfgValue value, int rootIndex, int[] path, string label, CfgField? descriptor, int componentWidth)
        {
            if (value.Kind is ValueKind.Array or ValueKind.Structure or ValueKind.Vector)
            {
                if (value.Items.Count == 0)
                    editors.Add(new FieldEdit(rootIndex, path, value, 0, label, UpdateTitle));
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
            editors.Add(new FieldEdit(rootIndex, path, value, width, label, UpdateTitle));
        }
    }

    private void ApplyFields()
    {
        if (current is null || !editors.Any(field => field.IsChanged)) return;
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
        foreach (var change in changes) current.Row.SetValue(change.Key, change.Value);
        MarkDirty(); BuildFields();
    }

    private void MarkDirty() { dirty = true; UpdateTitle(); }
    private void UpdateTitle() => Title = $"{(HasChanges ? "* " : "")}{(sourcePath.Length == 0 ? "" : Path.GetFileName(sourcePath) + " — ")}Sienna Editor";

    private async Task SaveAsync(bool cfg)
    {
        if (document is null) { Status.Text = "Open a file before saving."; return; }
        ApplyFields();
        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = cfg ? "Export readable CFG" : "Save reconstructed BIN", ShowOverwritePrompt = true,
            SuggestedFileName = Path.GetFileNameWithoutExtension(sourcePath) + (cfg ? ".cfg" : ".edited.bin"),
            FileTypeChoices = [new FilePickerFileType(cfg ? "Readable CFG" : "BIN file") { Patterns = [cfg ? "*.cfg" : "*.bin"] }]
        });
        if (target is null) { Status.Text = "Save cancelled."; return; }
        string path = target.TryGetLocalPath() ?? throw new IOException("Choose a local destination for atomic saving.");
        byte[] bytes = await Task.Run(() => cfg ? Encoding.UTF8.GetBytes(CfgTextCodec.Write(document)) : BinCodec.Write(document));
        CfgDocument? saved = cfg ? null : await Task.Run(() => BinCodec.Read(bytes, catalog));
        await Task.Run(() => AtomicWrite(path, bytes));
        if (saved is not null) { document = saved; dirty = false; sourcePath = path; RebuildRecords(); UpdateTitle(); }
        Status.Text = $"Saved {Path.GetFileName(path)} · {bytes.Length:N0} bytes";
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
        if (document is null || current is null) { Status.Text = "Select a record first."; return; }
        ApplyFields();
        if (remove && !await AskAsync("Delete record", $"Delete record {current.Ordinal + 1}: {current.Row.Name}?", "Delete record")) return;
        int ordinal = current.Ordinal;
        CfgDocument replacement = await Task.Run(() =>
        {
            CfgDocument copy = CfgTextCodec.Read(CfgTextCodec.Write(document), catalog);
            if (current.ListName is null)
            {
                if (remove) copy.RemoveRow(current.ListIndex);
                else copy.InsertRow(current.ListIndex + 1, copy.Rows[current.ListIndex].Clone());
            }
            else
            {
                if (remove) copy.RemoveListRow(current.ListName, current.ListIndex);
                else copy.InsertListRow(current.ListName, current.ListIndex + 1, copy.GetList(current.ListName).Rows[current.ListIndex].Clone());
            }
            return BinCodec.Read(BinCodec.Write(copy), catalog);
        });
        document = replacement; MarkDirty(); RebuildRecords();
        page = Math.Min(ordinal / PageSize, Math.Max(0, (matching.Count - 1) / PageSize)); ShowPage();
        Records.SelectedIndex = Math.Min(ordinal % PageSize, Math.Min(PageSize, matching.Count - page * PageSize) - 1);
        Status.Text = remove ? "Record deleted; supported references and counts recalculated." : "Record duplicated; edit its identifier when uniqueness is required.";
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

    private async Task<string?> PickProfileAsync(IReadOnlyList<string> profiles)
    {
        var dialog = new Window { Title = "Select Structure profile", Width = 420, Height = 180, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var choice = new ComboBox { ItemsSource = profiles, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var select = new Button { Content = "Use profile", IsEnabled = false };
        choice.SelectionChanged += (_, _) => select.IsEnabled = choice.SelectedItem is string;
        select.Click += (_, _) => dialog.Close(choice.SelectedItem as string);
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children = { choice, select } };
        return await dialog.ShowDialog<string?>(this);
    }

    private async void ClosingWindow(object? sender, WindowClosingEventArgs args)
    {
        if (allowClose || !HasChanges && !busy) return;
        args.Cancel = true;
        if (busy) { Status.Text = "Wait for the current operation to finish before closing."; return; }
        if (await AskAsync("Unsaved changes", "Close Sienna Editor and discard unsaved changes?", "Discard and close")) { allowClose = true; Close(); }
    }

    private async void WindowKeyDown(object? sender, KeyEventArgs args)
    {
        if ((args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0) return;
        if (args.Key == Key.S) { args.Handled = true; await RunAsync("Save BIN", () => SaveAsync(false)); }
    }

    private async void FindClicked(object? sender, RoutedEventArgs args) => await RunAsync("Find records", FilterAsync);
    private async void FilterChanged(object? sender, SelectionChangedEventArgs args) { if (!changing) await RunAsync("Filter records", FilterAsync); }
    private async void SearchKeyDown(object? sender, KeyEventArgs args) { if (args.Key == Key.Enter) { args.Handled = true; await RunAsync("Find records", FilterAsync); } }
    private async void PreviousClicked(object? sender, RoutedEventArgs args) => await RunAsync("Previous records", () => { ApplyFields(); page--; ShowPage(); Status.Text = "Ready"; return Task.CompletedTask; });
    private async void NextClicked(object? sender, RoutedEventArgs args) => await RunAsync("Next records", () => { ApplyFields(); page++; ShowPage(); Status.Text = "Ready"; return Task.CompletedTask; });
    private async void ApplyClicked(object? sender, RoutedEventArgs args) => await RunAsync("Apply row", () => { ApplyFields(); Status.Text = "Row applied. Save BIN or export CFG to keep changes."; return Task.CompletedTask; });
    private void RevertClicked(object? sender, RoutedEventArgs args) { BuildFields(); UpdateTitle(); Status.Text = "Unapplied row edits reverted."; }
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
