using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CFGBINManager;

namespace SiennaEditor;

public sealed partial class EditorWindow
{
    private bool canAddSibling;

    private void RecordMenuOpening(object? sender, CancelEventArgs args)
    {
        var branch = Records.SelectedItem as RecordBranch;
        bool single = !busy && Records.SelectedItems?.Count == 1;
        canAddSibling = false;
        bool canAddChild = false, canDuplicate = false;
        if (single && branch is not null && document is { } source)
        {
            if (source.Format == BinaryFormat.T2b && branch.Record is { } entry)
            {
                canAddSibling = canDuplicate = !branch.IsClosing;
                canAddChild = !branch.IsClosing && ChildEntryNames(entry.Row.Name).Any();
            }
            else if (source.Format == BinaryFormat.Rdbnp && branch.ListName is { } listName)
            {
                var list = source.GetList(listName);
                if (branch.Record is { } row)
                {
                    canAddSibling = StructureAllows(copy => InsertNativeEntry(copy, listName, row.ListIndex + 1, row.Row));
                    canDuplicate = StructureAllows(copy => DuplicateRecords(copy, [row]));
                }
                else if (list.Rows.FirstOrDefault() is { } template)
                    canAddChild = StructureAllows(copy => InsertNativeEntry(copy, listName, list.Rows.Count, template));
            }
        }
        AddContext.IsEnabled = canAddSibling || canAddChild;
        AddChildAction.IsEnabled = canAddChild;
        RenameRecordAction.IsEnabled = CanRenameRecord(branch);
        DuplicateRecordAction.IsEnabled = canDuplicate;
        DeleteRecordsAction.IsEnabled = CanRemoveSelectedRecords();
    }

    private bool StructureAllows(Action<CfgDocument> change)
    {
        if (document is not { } source) return false;
        try
        {
            var copy = CfgTextCodec.Read(CfgTextCodec.Write(source), catalog);
            change(copy);
            _ = BinCodec.Write(copy);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or OverflowException)
        {
            return false;
        }
    }

    private bool CanRemoveSelectedRecords()
    {
        if (busy || document is null) return false;
        var selected = SelectedRowsToRemove();
        if (selected.Count == 0) return false;
        bool needsValidation = document.Format == BinaryFormat.Rdbnp || selected.Any(entry =>
            catalog.RowIndices.Any(rule => rule.Target == entry.Row.Name) || catalog.Ranges.Any(rule => rule.Target == entry.Row.Name));
        return !needsValidation || StructureAllows(copy => RemoveRecords(copy, selected));
    }

    private bool CanRemoveValue(int index)
    {
        if (document?.Format != BinaryFormat.T2b || current is not { } entry) return false;
        string name = entry.Row.Name;
        return !catalog.Counts.Any(rule => rule.Start == name && rule.Position >= index)
            && !catalog.RowIndices.Any(rule => rule.Source == name && rule.Position >= index)
            && !catalog.Ranges.Any(rule => rule.Source == name && (rule.StartPosition >= index || rule.CountPosition >= index));
    }

    private bool CanRenameRecord(RecordBranch? branch) => !busy && document?.Format == BinaryFormat.T2b
        && Records.SelectedItems?.Count == 1 && ReferenceEquals(branch, Records.SelectedItem)
        && branch is { IsClosing: false, Record: { } entry }
        && !IsBlockEntry(entry.Row.Name) && !IsLinkedEntryName(entry.Row.Name);

    private bool IsLinkedEntryName(string name) =>
        catalog.Counts.Any(rule => rule.Start == name || rule.End == name || rule.Child == name)
        || catalog.RowIndices.Any(rule => rule.Source == name || rule.Target == name)
        || catalog.Ranges.Any(rule => rule.Source == name || rule.Target == name);

    private void RenameRecordClicked(object? sender, RoutedEventArgs args) => BeginRecordRename();

    private void BeginRecordRename()
    {
        if (Records.SelectedItem is not RecordBranch branch || !CanRenameRecord(branch)) return;
        Records.ScrollIntoView(branch);
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(Records.SelectedItem, branch) || !CanRenameRecord(branch)) return;
            var input = Records.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(box => box.Classes.Contains("entry-rename") && ReferenceEquals(box.DataContext, branch));
            if (input is null) return;
            input.Text = branch.Record!.Row.Name;
            branch.IsRenaming = true;
            input.Focus();
            input.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private async void RenameRecordKeyDown(object? sender, KeyEventArgs args)
    {
        if (sender is not TextBox { DataContext: RecordBranch { Record: { } entry } branch } input
            || args.Key is not (Key.Enter or Key.Escape)) return;
        args.Handled = true;
        string oldName = entry.Row.Name;
        string newName = input.Text?.Trim() ?? "";
        if (args.Key == Key.Escape || string.Equals(newName, oldName, StringComparison.Ordinal))
        {
            branch.IsRenaming = false;
            Records.ContainerFromItem(branch)?.Focus();
            return;
        }
        if (!CanRenameRecord(branch)) return;
        newName = catalog.ResolveEntry(BinaryFormat.T2b, newName);
        if (string.IsNullOrEmpty(newName) || newName.Contains('\0') || IsLinkedEntryName(newName)
            || IsBlockEntry(newName) || IsClosingEntry(newName))
        {
            Status.Text = "Enter a nonempty name that is available for an entry.";
            return;
        }
        branch.IsRenaming = false;
        Records.ContainerFromItem(branch)?.Focus();
        await RunAsync("Rename entry", () => EditDocumentAsync(copy =>
        {
            var before = ParentPositions(copy);
            int ordinal = entry.Ordinal;
            var replacement = copy.CreateT2bRow(newName, copy.Rows[ordinal].Values);
            copy.RemoveRow(ordinal);
            copy.InsertRow(ordinal, replacement);
            if (!ParentPositions(copy).SequenceEqual(before))
                throw new InvalidOperationException("That name would change the entry hierarchy. Choose a name compatible with its current location.");
        }, entry.Ordinal));
    }

    private void RenameRecordLostFocus(object? sender, RoutedEventArgs args)
    {
        if (sender is TextBox { DataContext: RecordBranch branch }) branch.IsRenaming = false;
    }
}
