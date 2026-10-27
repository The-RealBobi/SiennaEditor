using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CFGBINManager;

namespace SiennaEditor;

internal sealed class MergeDecision(MergeConflict conflict, Action changed) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public MergeConflict Conflict { get; } = conflict;
    public override string ToString() => Conflict.Location + "." + Conflict.Field;
    private string? choice;
    public string? Choice
    {
        get => choice;
        set { if (choice == value) return; choice = value; PropertyChanged?.Invoke(this, new(nameof(Choice))); changed(); }
    }
}

internal sealed class MergeTreeRow(MergeEntry entry, MergeTreeRow? parent) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public MergeEntry Entry { get; } = entry;
    public MergeTreeRow? Parent { get; } = parent;
    public int Depth { get; } = parent is null ? 0 : parent.Depth + 1;
    public bool HasChildren { get; set; }
    public bool HasConflicts { get; set; } = entry.State == MergeEntryState.Conflict;
    public bool HasAdditions { get; set; } = entry.State is MergeEntryState.File1Only or MergeEntryState.File2Only;
    public bool HasChanges { get; set; } = entry.State != MergeEntryState.Matched
        || entry.Fields.Any(field => field.AutomaticValue is { } value && (field.File1?.Bits != value.Bits || field.File2?.Bits != value.Bits));
    private bool expanded = parent is null;
    public bool IsExpanded
    {
        get => expanded;
        set { if (expanded == value) return; expanded = value; PropertyChanged?.Invoke(this, new(nameof(Toggle))); }
    }
    public string Toggle => expanded ? "▾" : "▸";
    public override string ToString() => Entry.Label;
}

internal sealed record MergeFieldRow(MergeField Field, MergeEntryState State, MergeDecision? Decision)
{
    public override string ToString() => Field.Name;
}

public sealed partial class EditorWindow
{
    private async void ConflictMergeClicked(object? sender, RoutedEventArgs args)
    {
        if (busy) return;
        try { ApplyFields(); }
        catch (Exception error) { Status.Text = "Cannot merge: " + error.Message; return; }
        var chosenRoot = catalogRoot; var chosenCatalog = catalog; string chosenName = catalogName;
        var dialog = new Window
        {
            Title = "ConflictMerge", Width = 1320, Height = 820, MinWidth = 1100, MinHeight = 620,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground, FontFamily = FontFamily
        };
        var currentPath = new TextBox { IsReadOnly = true, PlaceholderText = "File 1" };
        var incomingPath = new TextBox { IsReadOnly = true, PlaceholderText = "File 2" };
        var pickCurrent = new Button { Content = "Browse…" }; var pickIncoming = new Button { Content = "Browse…" };
        Grid FileRow(string label, TextBox text, Button browse)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*,Auto"), ColumnSpacing = 10 };
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(text, 1); row.Children.Add(text); Grid.SetColumn(browse, 2); row.Children.Add(browse); return row;
        }
        var inputRows = new StackPanel { Spacing = 10, Children = { FileRow("File 1", currentPath, pickCurrent), FileRow("File 2", incomingPath, pickIncoming) } };
        var summary = new TextBlock { Text = "Select File 1 and File 2. The result keeps entries exclusive to either file.", TextWrapping = TextWrapping.Wrap };
        var analyze = new Button { Content = "Analyze", IsEnabled = false };
        var create = new Button { Content = "Create merged file", IsEnabled = false };
        var close = new Button { Content = "Close" };
        var useCurrent = new Button { Content = "Use File 1 for all", IsEnabled = false };
        var useIncoming = new Button { Content = "Use File 2 for all", IsEnabled = false };
        var removeSelected = new Button { Content = "Remove selected", IsEnabled = false };
        AutomationProperties.SetAutomationId(removeSelected, "merge-remove-selected");
        var search = new TextBox { PlaceholderText = "Search entries and values", MinWidth = 240 };
        ToolTip.SetTip(search, "Literal search. Case-sensitive; spaces are significant.");
        AutomationProperties.SetAutomationId(search, "merge-search");
        AutomationProperties.SetName(search, "Search merge entries and values");
        var unresolvedOnly = new CheckBox { Content = "Unresolved conflicts only", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(unresolvedOnly, "merge-unresolved-only");
        var conflictBrush = new SolidColorBrush(Color.Parse("#E7C55B"));
        var additionBrush = new SolidColorBrush(Color.Parse("#75C998"));
        var changeTree = new ListBox { SelectionMode = SelectionMode.Multiple };
        AutomationProperties.SetAutomationId(changeTree, "merge-entries");
        var detailsTitle = new TextBlock { Text = "Select an entry to inspect its fields.", TextWrapping = TextWrapping.Wrap, FontSize = 15 };
        var conflictList = new ListBox();
        conflictList.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        changeTree.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        IReadOnlyList<MergeKey> keys = [];
        var decisions = new List<MergeDecision>();
        var decisionsById = new Dictionary<int, MergeDecision>();
        var treeRows = new List<MergeTreeRow>();
        var excludedEntries = new HashSet<MergeEntry>(ReferenceEqualityComparer.Instance);
        var excludedConflicts = new HashSet<int>();
        CfgDocument? currentFile = null, incomingFile = null; CfgMergePlan? plan = null;
        bool working = false, settingAll = false, compatible = false, refreshingTree = false;
        var expansionBeforeFilter = new Dictionary<MergeTreeRow, bool>();
        var fieldsByRow = new Dictionary<MergeTreeRow, MergeFieldRow[]>();
        bool IsFiltering() => !string.IsNullOrEmpty(search.Text) || unresolvedOnly.IsChecked == true;
        IEnumerable<MergeDecision> ActiveDecisions() => decisions.Where(item => !excludedConflicts.Contains(item.Conflict.Id));
        void UpdateDecisions()
        {
            if (settingAll || plan is null) return;
            var activeDecisions = ActiveDecisions().ToArray();
            int resolved = activeDecisions.Count(item => item.Choice is not null);
            summary.Text = $"{plan.MatchedRows:N0} matched · {plan.AddedRows:N0} File 2 only · {plan.CurrentOnlyRows:N0} File 1 only rows. {resolved:N0} / {activeDecisions.Length:N0} conflicts resolved. {excludedEntries.Count:N0} entries removed from result.";
            create.IsEnabled = !working && resolved == activeDecisions.Length;
            removeSelected.IsEnabled = !working && changeTree.SelectedItems?.Count > 0;
            if (unresolvedOnly.IsChecked == true) RefreshTree(null);
        }
        void ClearAnalysis()
        {
            plan = null; decisions.Clear(); decisionsById.Clear(); conflictList.ItemsSource = null;
            excludedEntries.Clear(); excludedConflicts.Clear(); removeSelected.IsEnabled = false;
            expansionBeforeFilter.Clear(); fieldsByRow.Clear(); treeRows.Clear(); changeTree.ItemsSource = null; detailsTitle.Text = "Select an entry to inspect its fields.";
            create.IsEnabled = false; useCurrent.IsEnabled = false; useIncoming.IsEnabled = false;
        }
        void SetWorking(bool value)
        {
            working = value; inputRows.IsEnabled = !value;
            analyze.IsEnabled = !value && compatible;
            close.IsEnabled = !value; create.IsEnabled = !value && plan is not null && ActiveDecisions().All(item => item.Choice is not null);
            useCurrent.IsEnabled = !value && ActiveDecisions().Any(); useIncoming.IsEnabled = !value && ActiveDecisions().Any();
            removeSelected.IsEnabled = !value && plan is not null && changeTree.SelectedItems?.Count > 0;
            conflictList.IsEnabled = !value;
            changeTree.IsEnabled = !value; search.IsEnabled = !value; unresolvedOnly.IsEnabled = !value;
        }
        IEnumerable<MergeFieldRow> Fields(MergeTreeRow row) => fieldsByRow[row].Where(item => item.Field.ConflictId is not int id || !excludedConflicts.Contains(id));
        string DisplayValue(MergeFieldRow item, MergeSide side)
        {
            var value = side == MergeSide.Current ? item.Field.File1 : item.Field.File2;
            var target = side == MergeSide.Current ? item.Decision?.Conflict.CurrentTarget : item.Decision?.Conflict.IncomingTarget;
            return value is null ? "—" : MergeValueText(value) + (target is null ? "" : Environment.NewLine + "Target: " + MergeValueText(target));
        }
        bool MatchesText(string value) => value.Contains(search.Text ?? "", StringComparison.Ordinal);
        bool MatchesEntry(MergeTreeRow row) => MatchesText(row.Entry.Name) || MatchesText(row.Entry.Label);
        bool MatchesField(MergeFieldRow item) => MatchesText(item.Field.Name)
            || MatchesText(DisplayValue(item, MergeSide.Current)) || MatchesText(DisplayValue(item, MergeSide.Incoming));
        bool IsUnresolved(MergeFieldRow item) => item.Decision is { Choice: null } decision && !excludedConflicts.Contains(decision.Conflict.Id);
        void ShowFields(MergeTreeRow row)
        {
            detailsTitle.Text = row.Entry.Label;
            ToolTip.SetTip(detailsTitle, row.Entry.Location);
            conflictList.ItemsSource = Fields(row).Where(item => (unresolvedOnly.IsChecked != true || IsUnresolved(item))
                && (MatchesEntry(row) || MatchesField(item))).ToArray();
        }
        HashSet<MergeTreeRow> FilteredRows()
        {
            var included = new HashSet<MergeTreeRow>();
            foreach (var row in treeRows.Where(row => row.HasChanges && !row.Entry.IsClosing && !excludedEntries.Contains(row.Entry)))
            {
                bool entryMatches = MatchesEntry(row);
                bool matches = unresolvedOnly.IsChecked == true
                    ? Fields(row).Any(item => IsUnresolved(item) && (entryMatches || MatchesField(item)))
                    : entryMatches || Fields(row).Any(MatchesField);
                if (!matches) continue;
                for (MergeTreeRow? ancestor = row; ancestor is not null; ancestor = ancestor.Parent)
                    if (!included.Add(ancestor)) break;
            }
            return included;
        }
        void RefreshTree(MergeTreeRow? selected)
        {
            var selection = changeTree.SelectedItems?.OfType<MergeTreeRow>().ToArray() ?? [];
            foreach (var row in treeRows)
            {
                row.HasConflicts = row.Entry.Fields.Any(field => field.ConflictId is int id && !excludedConflicts.Contains(id));
                row.HasAdditions = !excludedEntries.Contains(row.Entry) && row.Entry.State is MergeEntryState.File1Only or MergeEntryState.File2Only;
            }
            foreach (var row in treeRows.AsEnumerable().Reverse().Where(row => !excludedEntries.Contains(row.Entry)))
                if (row.Parent is { } parent) { parent.HasConflicts |= row.HasConflicts; parent.HasAdditions |= row.HasAdditions; }
            var included = FilteredRows();
            var parentsWithChildren = included.Select(row => row.Parent).ToHashSet();
            foreach (var row in treeRows) row.HasChildren = parentsWithChildren.Contains(row);
            var shown = new HashSet<MergeTreeRow>(); var visible = new List<MergeTreeRow>();
            foreach (var row in treeRows)
                if (included.Contains(row) && !excludedEntries.Contains(row.Entry) && (row.Parent is null || shown.Contains(row.Parent) && row.Parent.IsExpanded))
                { shown.Add(row); visible.Add(row); }
            refreshingTree = true;
            try
            {
                changeTree.ItemsSource = visible;
                foreach (var row in selection.Where(visible.Contains))
                    if (changeTree.SelectedItems?.Contains(row) != true) changeTree.SelectedItems?.Add(row);
                if (selected is not null && visible.Contains(selected) && changeTree.SelectedItems?.Contains(selected) != true) changeTree.SelectedItems?.Add(selected);
            }
            finally { refreshingTree = false; }
            if (changeTree.SelectedItem is MergeTreeRow current) ShowFields(current);
            else
            {
                conflictList.ItemsSource = null;
                detailsTitle.Text = visible.Count == 0 ? "No entries match the filters." : "Select an entry to inspect its fields.";
            }
            removeSelected.IsEnabled = !working && plan is not null && changeTree.SelectedItems?.Count > 0;
        }
        void FiltersChanged()
        {
            if (plan is null) return;
            if (IsFiltering())
            {
                if (expansionBeforeFilter.Count == 0)
                {
                    foreach (var row in treeRows) expansionBeforeFilter.Add(row, row.IsExpanded);
                }
                foreach (var row in FilteredRows()) if (row.Parent is { } parent) parent.IsExpanded = true;
            }
            else
            {
                foreach (var item in expansionBeforeFilter) item.Key.IsExpanded = item.Value;
                expansionBeforeFilter.Clear();
            }
            RefreshTree(null);
        }
        search.TextChanged += (_, _) => FiltersChanged();
        unresolvedOnly.IsCheckedChanged += (_, _) => FiltersChanged();
        void BuildTree()
        {
            treeRows.Clear();
            var pending = new Stack<(MergeEntry Entry, MergeTreeRow? Parent)>();
            foreach (var entry in plan!.Entries.Reverse()) pending.Push((entry, null));
            while (pending.TryPop(out var item))
            {
                var parent = item.Parent;
                while (parent?.Entry.IsClosing == true) parent = parent.Parent;
                var row = new MergeTreeRow(item.Entry, parent); treeRows.Add(row);
                foreach (var child in item.Entry.Children.Reverse()) pending.Push((child, row));
            }
            var children = treeRows.ToLookup(row => row.Parent);
            fieldsByRow.Clear();
            foreach (var row in treeRows)
                fieldsByRow.Add(row, row.Entry.Fields.Select(field => (Field: field, row.Entry.State))
                    .Concat(children[row].Where(child => child.Entry.IsClosing).SelectMany(child => child.Entry.Fields
                        .Select(field => (Field: field with { Name = "End / " + field.Name }, child.Entry.State))))
                    .Select(item => new MergeFieldRow(item.Field, item.State, item.Field.ConflictId is int id ? decisionsById[id] : null)).ToArray());
            foreach (var row in treeRows.AsEnumerable().Reverse())
                if (row.Parent is { } parent)
                {
                    parent.HasChanges |= row.HasChanges; parent.HasConflicts |= row.HasConflicts; parent.HasAdditions |= row.HasAdditions;
                    if (row.HasChanges && !row.Entry.IsClosing) parent.HasChildren = true;
                }
            var selected = treeRows.FirstOrDefault(row => row.Entry.State == MergeEntryState.Conflict && !row.Entry.IsClosing)
                ?? treeRows.FirstOrDefault(row => row.HasChanges && !row.Entry.IsClosing);
            for (var parent = selected?.Parent; parent is not null; parent = parent.Parent) parent.IsExpanded = true;
            RefreshTree(selected);
            if (IsFiltering()) FiltersChanged();
            if (changeTree.SelectedItem is MergeTreeRow current) changeTree.ScrollIntoView(current);
            else if (selected is null && !IsFiltering()) detailsTitle.Text = "The files have no changes.";
        }
        async Task RemoveSelectedAsync()
        {
            if (working || plan is null) return;
            var selection = changeTree.SelectedItems?.OfType<MergeTreeRow>().ToArray() ?? [];
            if (selection.Length == 0) return;
            var removed = new HashSet<MergeEntry>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<MergeEntry>(selection.Select(row => row.Entry));
            while (pending.TryPop(out var entry))
                if (removed.Add(entry)) foreach (var child in entry.Children) pending.Push(child);
            if (!await AskAsync(dialog, "Remove from result", $"Remove {removed.Count:N0} entries from the merged result, including contained children?", "Remove")) return;
            excludedEntries.UnionWith(removed);
            foreach (var field in removed.SelectMany(entry => entry.Fields))
                if (field.ConflictId is int id) excludedConflicts.Add(id);
            changeTree.SelectedItems?.Clear();
            RefreshTree(null);
            if (changeTree.ItemsSource?.Cast<MergeTreeRow>().FirstOrDefault() is { } first) changeTree.SelectedItem = first;
            else { conflictList.ItemsSource = null; detailsTitle.Text = "No changed entries remain."; }
            UpdateDecisions();
        }
        removeSelected.Click += async (_, _) => await RemoveSelectedAsync();
        var removeMenu = new MenuItem { Header = "Remove selected" };
        removeMenu.Click += async (_, _) => await RemoveSelectedAsync();
        changeTree.ContextMenu = new ContextMenu { ItemsSource = new[] { removeMenu } };
        changeTree.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(changeTree).Properties.IsRightButtonPressed || e.Source is not Visual source) return;
            if (EntryItem(source)?.DataContext is MergeTreeRow row && changeTree.SelectedItems?.Contains(row) != true) changeTree.SelectedItem = row;
        };
        changeTree.SelectionChanged += (_, _) =>
        {
            if (refreshingTree) return;
            if (plan is not null && changeTree.SelectedItem is MergeTreeRow row) ShowFields(row);
            removeSelected.IsEnabled = !working && plan is not null && changeTree.SelectedItems?.Count > 0;
        };
        changeTree.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Delete || e.Key == Key.Back && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            { e.Handled = true; await RemoveSelectedAsync(); return; }
            if (changeTree.SelectedItem is not MergeTreeRow row || e.Key is not (Avalonia.Input.Key.Left or Avalonia.Input.Key.Right)) return;
            e.Handled = true;
            if (e.Key == Avalonia.Input.Key.Right)
            {
                if (row.HasChildren && !row.IsExpanded) { row.IsExpanded = true; RefreshTree(row); }
                else if (changeTree.ItemsSource?.Cast<MergeTreeRow>().FirstOrDefault(child => child.Parent == row) is { } child) changeTree.SelectedItem = child;
            }
            else if (row.HasChildren && row.IsExpanded) { row.IsExpanded = false; RefreshTree(row); }
            else if (row.Parent is not null) changeTree.SelectedItem = row.Parent;
        };
        changeTree.ItemTemplate = new FuncDataTemplate<MergeTreeRow>((row, _) =>
        {
            if (row is null) return new TextBlock();
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*"), Margin = new Thickness(row.Depth * 14, 0, 0, 0) };
            var toggle = new Button { Width = 22, Height = 26, Padding = new Thickness(0), Background = Brushes.Transparent, IsVisible = row.HasChildren };
            toggle.Bind(ContentControl.ContentProperty, new Binding(nameof(MergeTreeRow.Toggle)) { Source = row });
            AutomationProperties.SetName(toggle, "Expand or collapse " + row.Entry.Label);
            toggle.Click += (_, e) => { e.Handled = true; row.IsExpanded = !row.IsExpanded; RefreshTree(row); };
            grid.Children.Add(toggle);
            var label = new TextBlock { Text = row.Entry.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = row.HasConflicts ? conflictBrush : row.HasAdditions ? additionBrush : Foreground,
                TextDecorations = row.HasConflicts ? TextDecorations.Underline : null };
            string state = row.HasConflicts ? "Conflicts" : row.Entry.State == MergeEntryState.File1Only ? "File 1 only" : row.Entry.State == MergeEntryState.File2Only ? "File 2 only" : "Changes in child entries";
            ToolTip.SetTip(label, row.Entry.Location + Environment.NewLine + state); AutomationProperties.SetName(label, row.Entry.Label + " — " + state);
            Grid.SetColumn(label, 1); grid.Children.Add(label); return grid;
        });
        async Task Pick(bool isCurrent)
        {
            if (working) return;
            try
            {
                var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                { Title = isCurrent ? "Select File 1" : "Select File 2", AllowMultiple = false, FileTypeFilter = [DocumentFileType, FilePickerFileTypes.All] });
                if (files.Count != 1) return;
                string path = files[0].TryGetLocalPath() ?? throw new IOException("Choose a local file.");
                SetWorking(true); ClearAnalysis(); compatible = false; summary.Text = "Loading file…";
                var loaded = await Task.Run(() => DocumentFiles.Read(path, chosenCatalog));
                if (isCurrent) { currentFile = loaded; currentPath.Text = path; }
                else { incomingFile = loaded; incomingPath.Text = path; }
                keys = [];
                if (currentFile is not null && incomingFile is not null)
                {
                    keys = await Task.Run(() => DocumentFiles.MergeKeys(currentFile, incomingFile));
                    compatible = true;
                    summary.Text = "Ready to analyze.";
                }
                else summary.Text = "Select the other file to continue.";
            }
            catch (Exception error) { analyze.IsEnabled = false; summary.Text = "Cannot load merge inputs: " + error.Message; }
            finally { SetWorking(false); }
        }
        pickCurrent.Click += async (_, _) => await Pick(true); pickIncoming.Click += async (_, _) => await Pick(false);
        analyze.Click += async (_, _) =>
        {
            if (working || currentFile is null || incomingFile is null) return;
            SetWorking(true); ClearAnalysis(); summary.Text = "Analyzing entries and references…";
            try
            {
                plan = await Task.Run(() => CfgMergePlan.Analyze(currentFile, incomingFile, keys));
                decisions.AddRange(plan.Conflicts.Select(conflict => new MergeDecision(conflict, UpdateDecisions)));
                foreach (var decision in decisions) decisionsById.Add(decision.Conflict.Id, decision);
                BuildTree(); UpdateDecisions();
            }
            catch (Exception error) { summary.Text = "Cannot analyze these files: " + error.Message; }
            finally { SetWorking(false); }
        };
        void ChooseAll(string side)
        { settingAll = true; foreach (var decision in ActiveDecisions()) decision.Choice = side; settingAll = false; UpdateDecisions(); }
        useCurrent.Click += (_, _) => ChooseAll("File 1"); useIncoming.Click += (_, _) => ChooseAll("File 2");
        conflictList.ItemTemplate = new FuncDataTemplate<MergeFieldRow>((item, _) =>
        {
            if (item is null) return new TextBlock();
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("1.5*,2*,2*,135"), ColumnSpacing = 10, Margin = new Thickness(4, 5) };
            var label = new TextBlock { Text = item.Field.Name, TextWrapping = TextWrapping.Wrap, MaxHeight = 72, VerticalAlignment = VerticalAlignment.Center,
                Foreground = item.Decision is not null ? conflictBrush : item.State is MergeEntryState.File1Only or MergeEntryState.File2Only ? additionBrush : Foreground,
                TextDecorations = item.Decision is not null ? TextDecorations.Underline : null };
            ToolTip.SetTip(label, item.Field.Name); row.Children.Add(label);
            var currentValue = new TextBox { Text = DisplayValue(item, MergeSide.Current), IsReadOnly = true, MaxHeight = 72, TextWrapping = TextWrapping.Wrap };
            var incomingValue = new TextBox { Text = DisplayValue(item, MergeSide.Incoming), IsReadOnly = true, MaxHeight = 72, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(currentValue, 1); row.Children.Add(currentValue); Grid.SetColumn(incomingValue, 2); row.Children.Add(incomingValue);
            Control resolution;
            if (item.Decision is { } decision)
            {
                var choice = new ComboBox { ItemsSource = new[] { "File 1", "File 2" }, PlaceholderText = "Choose…", HorizontalAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(choice, "Keep value for " + decision.Conflict.Location + "." + item.Field.Name);
                choice.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(MergeDecision.Choice)) { Source = decision, Mode = BindingMode.TwoWay }); resolution = choice;
            }
            else resolution = new TextBlock { Text = item.Field.AutomaticValue is { } automatic ? excludedEntries.Count > 0 ? "Updated when merged" : "Automatic: " + MergeValueText(automatic)
                : item.State == MergeEntryState.File1Only ? "Added from File 1" : item.State == MergeEntryState.File2Only ? "Added from File 2" : "Unchanged",
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                Foreground = item.State is MergeEntryState.File1Only or MergeEntryState.File2Only ? additionBrush : Foreground };
            Grid.SetColumn(resolution, 3); row.Children.Add(resolution); return row;
        });
        create.Click += async (_, _) =>
        {
            if (working || plan is null || ActiveDecisions().Any(item => item.Choice is null)) return;
            var choices = ActiveDecisions().ToDictionary(item => item.Conflict.Id, item => item.Choice == "File 1" ? MergeSide.Current : MergeSide.Incoming);
            SetWorking(true); summary.Text = "Rebuilding and validating the merged file…";
            try
            {
                var excluded = excludedEntries.ToArray();
                var merged = await Task.Run(() => CfgTextCodec.Read(CfgTextCodec.Write(plan.Build(choices, excluded)), chosenCatalog));
                _ = merged.GetRecordTree(); working = false; dialog.Close(merged);
            }
            catch (Exception error) { summary.Text = "Cannot create the merged file: " + error.Message; }
            finally { SetWorking(false); }
        };
        close.Click += (_, _) => dialog.Close();
        dialog.Closing += (_, e) => { if (working) { e.Cancel = true; summary.Text = "Wait until the current operation finishes."; } };
        var conflictHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("1.5*,2*,2*,135"), ColumnSpacing = 10 };
        string[] headers = ["Field", "File 1", "File 2", "Result"];
        for (int i = 0; i < headers.Length; i++) { var label = new TextBlock { Text = headers[i] }; Grid.SetColumn(label, i); conflictHeader.Children.Add(label); }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { analyze, useCurrent, useIncoming, removeSelected } };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { create, close } };
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Children =
        { new TextBlock { Text = "Conflicts", Foreground = conflictBrush, TextDecorations = TextDecorations.Underline }, new TextBlock { Text = "Additions", Foreground = additionBrush } } };
        var treePane = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 8 };
        treePane.Children.Add(legend); Grid.SetRow(changeTree, 1); treePane.Children.Add(changeTree);
        var detailsPane = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 8 };
        detailsPane.Children.Add(detailsTitle); Grid.SetRow(conflictHeader, 1); detailsPane.Children.Add(conflictHeader); Grid.SetRow(conflictList, 2); detailsPane.Children.Add(conflictList);
        var comparison = new Grid { ColumnDefinitions = new ColumnDefinitions("310,6,*"), ColumnSpacing = 8 };
        comparison.Children.Add(treePane); var splitter = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1); comparison.Children.Add(splitter); Grid.SetColumn(detailsPane, 2); comparison.Children.Add(detailsPane);
        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        filters.Children.Add(search); Grid.SetColumn(unresolvedOnly, 1); filters.Children.Add(unresolvedOnly);
        var body = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"), RowSpacing = 12 };
        Control[] sections = [inputRows, actions, summary, filters, comparison, footer];
        for (int i = 0; i < sections.Length; i++) { Grid.SetRow(sections[i], i); body.Children.Add(sections[i]); }
        dialog.Content = body;
        var result = await dialog.ShowDialog<CfgDocument?>(this);
        if (result is null) return;
        try
        {
            var session = new EditorSession(chosenRoot, chosenCatalog, chosenName) { Document = result, Dirty = true };
            sessions.Add(session); ActivateSession(session);
            Status.Text = "Merged file created. Review the new tab and use Save as… to choose its destination.";
        }
        catch (Exception error) { Status.Text = "Cannot open the merged result: " + error.Message; }
    }

    private static string MergeValueText(CfgValue value) => value.Kind == ValueKind.String ? value.Text is null ? "<null>" : "\"" + value.Text + "\""
        : value.Items.Count > 0 ? "[" + string.Join(", ", value.Items.Select(MergeValueText)) + "]" : ValueText.Format(value);
}
