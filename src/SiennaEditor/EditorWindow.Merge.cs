using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
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
        var conflictBrush = new SolidColorBrush(Color.Parse("#E7C55B"));
        var additionBrush = new SolidColorBrush(Color.Parse("#75C998"));
        var changeTree = new ListBox();
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
        CfgDocument? currentFile = null, incomingFile = null; CfgMergePlan? plan = null;
        bool working = false, settingAll = false, compatible = false;
        void UpdateDecisions()
        {
            if (settingAll || plan is null) return;
            int resolved = decisions.Count(item => item.Choice is not null);
            summary.Text = $"{plan.MatchedRows:N0} matched · {plan.AddedRows:N0} File 2 only · {plan.CurrentOnlyRows:N0} File 1 only rows. {resolved:N0} / {decisions.Count:N0} conflicts resolved.";
            create.IsEnabled = resolved == decisions.Count;
        }
        void ClearAnalysis()
        {
            plan = null; decisions.Clear(); decisionsById.Clear(); conflictList.ItemsSource = null;
            treeRows.Clear(); changeTree.ItemsSource = null; detailsTitle.Text = "Select an entry to inspect its fields.";
            create.IsEnabled = false; useCurrent.IsEnabled = false; useIncoming.IsEnabled = false;
        }
        void SetWorking(bool value)
        {
            working = value; inputRows.IsEnabled = !value;
            analyze.IsEnabled = !value && compatible;
            close.IsEnabled = !value; create.IsEnabled = !value && plan is not null && decisions.All(item => item.Choice is not null);
            useCurrent.IsEnabled = !value && decisions.Count > 0; useIncoming.IsEnabled = !value && decisions.Count > 0;
            conflictList.IsEnabled = !value;
            changeTree.IsEnabled = !value;
        }
        void ShowFields(MergeTreeRow row)
        {
            detailsTitle.Text = row.Entry.Label;
            ToolTip.SetTip(detailsTitle, row.Entry.Location);
            var fields = row.Entry.Fields.Select(field => (Field: field, row.Entry.State))
                .Concat(treeRows.Where(child => child.Parent == row && child.Entry.IsClosing).SelectMany(child => child.Entry.Fields.Select(field => (Field: field with { Name = "End / " + field.Name }, child.Entry.State))));
            conflictList.ItemsSource = fields.Select(item => new MergeFieldRow(item.Field, item.State,
                item.Field.ConflictId is int id ? decisionsById[id] : null)).ToArray();
        }
        void RefreshTree(MergeTreeRow? selected)
        {
            var shown = new HashSet<MergeTreeRow>(); var visible = new List<MergeTreeRow>();
            foreach (var row in treeRows)
                if (row.HasChanges && !row.Entry.IsClosing && (row.Parent is null || shown.Contains(row.Parent) && row.Parent.IsExpanded))
                { shown.Add(row); visible.Add(row); }
            changeTree.ItemsSource = visible;
            if (selected is not null && visible.Contains(selected)) changeTree.SelectedItem = selected;
        }
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
            if (selected is not null) { ShowFields(selected); changeTree.ScrollIntoView(selected); }
            else detailsTitle.Text = "The files have no changes.";
        }
        changeTree.SelectionChanged += (_, _) => { if (changeTree.SelectedItem is MergeTreeRow row) ShowFields(row); };
        changeTree.KeyDown += (_, e) =>
        {
            if (changeTree.SelectedItem is not MergeTreeRow row || e.Key is not (Avalonia.Input.Key.Left or Avalonia.Input.Key.Right)) return;
            e.Handled = true;
            if (e.Key == Avalonia.Input.Key.Right)
            {
                if (row.HasChildren && !row.IsExpanded) { row.IsExpanded = true; RefreshTree(row); }
                else if (treeRows.FirstOrDefault(child => child.Parent == row && child.HasChanges && !child.Entry.IsClosing) is { } child) changeTree.SelectedItem = child;
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
        { settingAll = true; foreach (var decision in decisions) decision.Choice = side; settingAll = false; UpdateDecisions(); }
        useCurrent.Click += (_, _) => ChooseAll("File 1"); useIncoming.Click += (_, _) => ChooseAll("File 2");
        conflictList.ItemTemplate = new FuncDataTemplate<MergeFieldRow>((item, _) =>
        {
            if (item is null) return new TextBlock();
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("1.5*,2*,2*,135"), ColumnSpacing = 10, Margin = new Thickness(4, 5) };
            var label = new TextBlock { Text = item.Field.Name, TextWrapping = TextWrapping.Wrap, MaxHeight = 72, VerticalAlignment = VerticalAlignment.Center,
                Foreground = item.Decision is not null ? conflictBrush : item.State is MergeEntryState.File1Only or MergeEntryState.File2Only ? additionBrush : Foreground,
                TextDecorations = item.Decision is not null ? TextDecorations.Underline : null };
            ToolTip.SetTip(label, item.Field.Name); row.Children.Add(label);
            var currentValue = new TextBox { Text = item.Field.File1 is { } left ? MergeValueText(left) + (item.Decision?.Conflict.CurrentTarget is { } a ? Environment.NewLine + "Target: " + MergeValueText(a) : "") : "—", IsReadOnly = true, MaxHeight = 72, TextWrapping = TextWrapping.Wrap };
            var incomingValue = new TextBox { Text = item.Field.File2 is { } right ? MergeValueText(right) + (item.Decision?.Conflict.IncomingTarget is { } b ? Environment.NewLine + "Target: " + MergeValueText(b) : "") : "—", IsReadOnly = true, MaxHeight = 72, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(currentValue, 1); row.Children.Add(currentValue); Grid.SetColumn(incomingValue, 2); row.Children.Add(incomingValue);
            Control resolution;
            if (item.Decision is { } decision)
            {
                var choice = new ComboBox { ItemsSource = new[] { "File 1", "File 2" }, PlaceholderText = "Choose…", HorizontalAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(choice, "Keep value for " + decision.Conflict.Location + "." + item.Field.Name);
                choice.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(MergeDecision.Choice)) { Source = decision, Mode = BindingMode.TwoWay }); resolution = choice;
            }
            else resolution = new TextBlock { Text = item.Field.AutomaticValue is { } automatic ? "Automatic: " + MergeValueText(automatic)
                : item.State == MergeEntryState.File1Only ? "Added from File 1" : item.State == MergeEntryState.File2Only ? "Added from File 2" : "Unchanged",
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                Foreground = item.State is MergeEntryState.File1Only or MergeEntryState.File2Only ? additionBrush : Foreground };
            Grid.SetColumn(resolution, 3); row.Children.Add(resolution); return row;
        });
        create.Click += async (_, _) =>
        {
            if (working || plan is null || decisions.Any(item => item.Choice is null)) return;
            var choices = decisions.ToDictionary(item => item.Conflict.Id, item => item.Choice == "File 1" ? MergeSide.Current : MergeSide.Incoming);
            SetWorking(true); summary.Text = "Rebuilding and validating the merged file…";
            try
            {
                var merged = await Task.Run(() => CfgTextCodec.Read(CfgTextCodec.Write(plan.Build(choices)), chosenCatalog));
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
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { analyze, useCurrent, useIncoming } };
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
        var body = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"), RowSpacing = 12 };
        Control[] sections = [inputRows, actions, summary, comparison, footer];
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
