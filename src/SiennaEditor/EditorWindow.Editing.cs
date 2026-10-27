using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CFGBINManager;

namespace SiennaEditor;

public sealed partial class EditorWindow
{
    private CancellationTokenSource? searchDelay;
    private static bool Matches(RecordChoice item, string text) => (text.Length == 0 || item.Row.Name.Contains(text, StringComparison.Ordinal) || item.Row.Values.Any(value => ValueText.Contains(value, text)));

    private async void SearchChanged(object? sender, TextChangedEventArgs args)
    {
        if (changing || (Search.Text ?? "") == active.Search) return;
        active.Search = Search.Text ?? "";
        await SearchAsync(TimeSpan.FromMilliseconds(180));
    }

    private async Task SearchAsync(TimeSpan delay)
    {
        searchDelay?.Cancel(); searchDelay?.Dispose();
        searchDelay = new CancellationTokenSource();
        var token = searchDelay.Token;
        try
        {
            await Task.Delay(delay, token);
            while (busy) await Task.Delay(25, token);
            if (!token.IsCancellationRequested) await FindRecordsAsync(token);
        }
        catch (OperationCanceledException) { }
    }
    private void FieldChanged() { UpdateTitle(); UpdateControls(); }
    private void NullFieldClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is MenuItem { DataContext: FieldEdit field } && field.IsString) field.IsNull = !field.IsNull;
    }
    private void RestoreFieldClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is MenuItem { DataContext: FieldEdit field }) field.Reset();
    }

    private void RecordPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(Records).Properties.IsRightButtonPressed || args.Source is not Visual source) return;
        var item = source.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (item?.DataContext is not RecordBranch branch) return;
        try { ApplyFields(); if (Records.SelectedItems?.Contains(branch) != true) Records.SelectedItem = branch; }
        catch (Exception error) { args.Handled = true; Status.Text = "Edit not applied: " + error.Message; }
    }

    private void FocusOrdinal(int ordinal)
    {
        if (branches.FirstOrDefault(branch => branch.Record?.Ordinal == ordinal) is { } branch) FocusBranch(branch);
    }

    private List<RecordChoice> SelectedRecords()
    {
        if (Records.SelectedItem is not RecordBranch selected) return [];
        int index = branches.IndexOf(selected);
        return branches.Skip(index).TakeWhile((branch, offset) => offset == 0 || branch.Depth > selected.Depth)
            .Where(branch => branch.Record is not null).Select(branch => branch.Record!).ToList();
    }

    private async Task RemoveSelectedRecordsAsync()
    {
        if (document is null) throw new InvalidOperationException("Open a file first.");
        ApplyFields();
        var selection = (Records.SelectedItems?.OfType<RecordBranch>() ?? []).ToHashSet();
        var selected = new List<RecordChoice>();
        int selectedDepth = int.MaxValue;
        foreach (var branch in branches)
        {
            if (branch.Depth <= selectedDepth) selectedDepth = int.MaxValue;
            if (selection.Contains(branch)) selectedDepth = Math.Min(selectedDepth, branch.Depth);
            if (selectedDepth != int.MaxValue && branch.Record is not null) selected.Add(branch.Record);
        }
        if (selected.Count == 0) throw new InvalidOperationException("Select entries to remove.");
        if (!await AskAsync("Remove entries", $"Remove {selected.Count:N0} entries, including contained children?", "Remove")) return;
        await EditDocumentAsync(copy =>
        {
            var rows = selected.Select(item => item.ListName is null ? copy.Rows[item.ListIndex] : copy.GetList(item.ListName).Rows[item.ListIndex]).ToArray();
            copy.RemoveRows(rows);
        }, selected[0].Ordinal, "Entries removed; counts and references recalculated.");
    }

    private async Task EditDocumentAsync(Action<CfgDocument> edit, int focusOrdinal, string message)
    {
        if (document is null) throw new InvalidOperationException("Open a file first.");
        ApplyFields();
        var checkpoint = CaptureSnapshot();
        CfgDocument rebuilt = await Task.Run(() =>
        {
            var copy = CfgTextCodec.Read(CfgTextCodec.Write(document), catalog);
            edit(copy);
            var result = BinCodec.Read(BinCodec.Write(copy), catalog);
            _ = result.GetRecordTree();
            return result;
        });
        active.History.Record(checkpoint);
        document = rebuilt; MarkDirty(); RebuildRecords();
        FocusOrdinal(Math.Min(focusOrdinal, allRecords.Count - 1));
        Status.Text = message;
    }

    private async Task<string?> AskNameAsync(string initial)
    {
        var dialog = new Window { Title = "Add entry", Width = 480, Height = 160, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var name = new TextBox { Text = initial };
        var add = new Button { Content = "Add" }; var cancel = new Button { Content = "Cancel" };
        add.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text)) dialog.Close(name.Text.Trim()); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children =
        { new TextBlock { Text = "Record name" }, name, new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { add, cancel } } } };
        return await dialog.ShowDialog<string?>(this);
    }

    private enum EntryPlacement { Root, Sibling, Child }

    private async Task AddRecordAsync(EntryPlacement placement)
    {
        if (document is null) throw new InvalidOperationException("Open or create a file before adding entries.");
        ApplyFields();
        var branch = placement == EntryPlacement.Root ? null : Records.SelectedItem as RecordBranch;
        bool child = placement == EntryPlacement.Child;
        if (child && branch is null) throw new InvalidOperationException("Select the parent entry or list first.");
        if (document.Format == BinaryFormat.Rdbnp)
        {
            string listName = branch?.ListName ?? throw new InvalidOperationException("Select a list or entry first.");
            if (child && branch!.Record is not null) throw new InvalidOperationException("This entry has no native child records. Select a list to add an entry inside it.");
            if (!child && branch!.Record is null) throw new InvalidOperationException("Select an entry to add a sibling, or choose As child to add inside this list.");
            var list = document.GetList(listName);
            CfgRow template = branch.Record?.Row ?? list.Rows.FirstOrDefault() ?? throw new InvalidOperationException("This empty native list has no entry template. Open a file containing this entry type.");
            int index = child ? list.Rows.Count : branch.Record!.ListIndex + 1;
            int ordinal = allRecords.TakeWhile(item => item.ListName != listName).Count() + index;
            await EditDocumentAsync(copy =>
            {
                var row = template.Clone();
                for (int i = 0; i < row.Values.Count; i++) row.SetValue(i, EmptyValue(row.Values[i]));
                copy.InsertListRow(listName, index, row);
            }, ordinal, child ? "Child entry added to " + listName + "." : "Entry added at the same level.");
            return;
        }

        var parent = child ? branch : branch?.Parent;
        string? parentName = parent?.Record?.Row.Name;
        var childNames = parentName is null ? [] : catalog.Counts.Where(rule => rule.Start == parentName).Select(rule => rule.Child)
            .Concat(catalog.Children.Where(rule => rule.Parent == parentName).Select(rule => rule.Child)).Distinct(StringComparer.Ordinal).ToArray();
        if (child && childNames.Length == 0) throw new InvalidOperationException("The Structure defines no child relationship for this entry. Load a Structure that describes its children.");
        bool closing = branch?.Record is { } selected && IsClosingEntry(selected.Row.Name);
        string initial = child || closing ? childNames.FirstOrDefault() ?? "NEW_ENTRY" : branch?.Record?.Row.Name ?? "NEW_ENTRY";
        string? name = await AskNameAsync(initial);
        if (name is null) { Status.Text = "Add cancelled."; return; }
        name = catalog.ResolveEntry(BinaryFormat.T2b, name);
        int insertion = child ? branch!.Record!.Ordinal + 1 : closing ? branch!.Record!.Ordinal
            : placement == EntryPlacement.Root ? RootInsertion() : SelectedRecords().LastOrDefault()?.Ordinal + 1 ?? document.Rows.Count;
        int parentOrdinal = parent?.Record?.Ordinal ?? -1;
        await EditDocumentAsync(copy =>
        {
            var before = RowParents(copy);
            CfgRow? expectedParent = parentOrdinal < 0 ? null : copy.Rows[parentOrdinal];
            CfgRow Create(string tag)
            {
                var template = copy.Rows.FirstOrDefault(row => row.Name == tag);
                var values = template?.Values.Select(EmptyValue).ToArray();
                if (values is null && catalog.Counts.Any(rule => rule.Start == tag && rule.Position == 0)) values = [CfgValue.Signed(0)];
                return copy.CreateT2bRow(tag, values ?? []);
            }
            var added = Create(name);
            copy.InsertRow(insertion, added);
            string? end = copy.GetBlockEnd(name);
            if (!string.IsNullOrEmpty(end)) copy.InsertRow(insertion + 1, Create(end));
            var after = RowParents(copy);
            if (!after.TryGetValue(added, out var actualParent) || !ReferenceEquals(actualParent, expectedParent))
                throw new InvalidOperationException("The Structure cannot place this entry at the requested level. Choose a compatible entry name or load the matching Structure.");
            if (before.Any(item => !after.TryGetValue(item.Key, out var newParent) || !ReferenceEquals(item.Value, newParent)))
                throw new InvalidOperationException("Adding this entry would change the parent of an existing entry. Choose a compatible entry name.");
        }, insertion, child ? "Entry added as a child." : placement == EntryPlacement.Root ? "Root entry added." : "Entry added at the same level.");
    }

    private static Dictionary<CfgRow, CfgRow?> RowParents(CfgDocument source)
    {
        var parents = new Dictionary<CfgRow, CfgRow?>();
        var pending = new Stack<(CfgRecordNode Node, CfgRow? Parent)>();
        foreach (var root in source.GetRecordTree()) pending.Push((root, null));
        while (pending.TryPop(out var item))
        {
            if (item.Node.Row is { } row) parents.Add(row, item.Parent);
            foreach (var nested in item.Node.Children) pending.Push((nested, item.Node.Row));
        }
        return parents;
    }

    private async void AddContextPressed(object? sender, PointerPressedEventArgs args)
    {
        if (sender is not MenuItem menu || !args.GetCurrentPoint(menu).Properties.IsLeftButtonPressed || args.Source is not Visual source) return;
        if (source.GetVisualAncestors().Prepend(source).OfType<MenuItem>().FirstOrDefault() != menu) return;
        args.Handled = true; Records.ContextMenu?.Close();
        await RunAsync("Add entry", () => AddRecordAsync(EntryPlacement.Sibling));
    }

    private async void AddContextKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key is not (Key.Enter or Key.Space) || !ReferenceEquals(args.Source, sender)) return;
        args.Handled = true; Records.ContextMenu?.Close();
        await RunAsync("Add entry", () => AddRecordAsync(EntryPlacement.Sibling));
    }

    private static CfgValue EmptyValue(CfgValue value) => value.Kind switch
    {
        ValueKind.String => CfgValue.String(""),
        ValueKind.Array => value with { Items = value.Items.Select(EmptyValue).ToArray() },
        ValueKind.Structure or ValueKind.Vector => value with { Items = value.Items.Select(EmptyValue).ToArray() },
        _ => value with { Bits = 0 }
    };
    private bool IsClosingEntry(string name) => catalog.Counts.Any(rule => rule.End.Length > 0 && rule.End == name)
        || document!.Rows.Any(row => document.GetBlockEnd(row.Name) == name);
    private bool IsBlockEntry(string name) => document!.GetBlockEnd(name) is not null;

    private int RootInsertion()
    {
        var last = document!.GetRecordTree().LastOrDefault();
        return last?.Row is { } row && catalog.Counts.Any(rule => rule.Start == row.Name && rule.End.Length == 0) ? last.RowIndex : document.Rows.Count;
    }
    private async void AddRootClicked(object? sender, RoutedEventArgs args) => await RunAsync("Add root entry", () => AddRecordAsync(EntryPlacement.Root));
    private async void AddSiblingClicked(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        await RunAsync("Add entry", () => AddRecordAsync(EntryPlacement.Sibling));
    }
    private async void AddChildClicked(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        await RunAsync("Add child entry", () => AddRecordAsync(EntryPlacement.Child));
    }
    private async void AddValueClicked(object? sender, RoutedEventArgs args) => await RunAsync("Add value", async () =>
    {
        if (current is null) throw new InvalidOperationException("Select a record first.");
        int ordinal = current.Ordinal, index = current.Row.Values.Count;
        await EditDocumentAsync(copy => copy.InsertT2bValue(copy.Rows[ordinal], index, CfgValue.Signed(0)), ordinal, "Value added.");
    });
    private async void RemoveValueClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is not MenuItem { DataContext: FieldEdit field } || !field.CanRemove || current is null) return;
        int ordinal = current.Ordinal;
        await RunAsync("Remove value", () => EditDocumentAsync(copy => copy.RemoveT2bValue(copy.Rows[ordinal], field.RootIndex), ordinal, "Value removed."));
    }

}
