using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using CFGBINManager;

namespace SiennaEditor;

public sealed partial class EditorWindow
{
    private RecordBranch? dragSource;
    private RecordBranch? dragTarget;
    private ListBoxItem? dragMarker;
    private Point dragStart;
    private bool draggingEntry;
    private bool dropAsChild;

    private static ListBoxItem? EntryItem(Visual source) => source.GetVisualAncestors().Prepend(source).OfType<ListBoxItem>().FirstOrDefault();

    private void DragEntryPressed(object? sender, PointerPressedEventArgs args)
    {
        ClearEntryDrag();
        if (busy || !args.GetCurrentPoint(Records).Properties.IsLeftButtonPressed || args.Source is not Visual source) return;
        if (Records.SelectedItems?.Count > 1 || (args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Shift)) != 0) return;
        if (source.GetVisualAncestors().Prepend(source).OfType<Button>().Any()) return;
        if (EntryItem(source)?.DataContext is not RecordBranch { Record: not null } branch) return;
        dragSource = branch; dragStart = args.GetPosition(Records);
    }

    private void DragEntryMoved(object? sender, PointerEventArgs args)
    {
        if (dragSource is null || busy || !args.GetCurrentPoint(Records).Properties.IsLeftButtonPressed) return;
        var point = args.GetPosition(Records);
        if (!draggingEntry && Math.Abs(point.X - dragStart.X) < 6 && Math.Abs(point.Y - dragStart.Y) < 6) return;
        draggingEntry = true; args.Pointer.Capture(Records); args.Handled = true;
        UpdateEntryDrop(point);
    }

    private void UpdateEntryDrop(Point point)
    {
        dragMarker?.Classes.Remove("drop-child"); dragMarker?.Classes.Remove("drop-sibling");
        dragMarker = null; dragTarget = null;
        if (!new Rect(Records.Bounds.Size).Contains(point)) return;
        var item = Records.GetVisualsAt(point).Select(EntryItem).FirstOrDefault(item => item?.DataContext is RecordBranch);
        if (item?.DataContext is not RecordBranch target || ReferenceEquals(target, dragSource)) return;
        for (var parent = target.Parent; parent is not null; parent = parent.Parent)
            if (ReferenceEquals(parent, dragSource)) return;
        dropAsChild = point.X > target.Depth * 16 + 38;
        dragTarget = target; dragMarker = item;
        item.Classes.Add(dropAsChild ? "drop-child" : "drop-sibling");
        Status.Text = dropAsChild ? "Move as child of " + target.Heading : "Move after " + target.Heading + " at the same level";
    }

    private async void DragEntryReleased(object? sender, PointerReleasedEventArgs args)
    {
        var released = args.GetPosition(Records);
        if (dragSource is not null && (draggingEntry || Math.Abs(released.X - dragStart.X) >= 6 || Math.Abs(released.Y - dragStart.Y) >= 6))
        { draggingEntry = true; UpdateEntryDrop(released); }
        var source = dragSource; var target = dragTarget; bool child = dropAsChild, wasDragging = draggingEntry;
        ClearEntryDrag();
        if (!wasDragging) return;
        args.Pointer.Capture(null);
        args.Handled = true;
        if (source is null || target is null) { Status.Text = "Move cancelled. Drop below an entry to choose its level."; return; }
        await RunAsync("Move entry", () => MoveEntryAsync(source, target, child));
    }

    private void ClearEntryDrag()
    {
        dragMarker?.Classes.Remove("drop-child"); dragMarker?.Classes.Remove("drop-sibling");
        dragSource = null; dragTarget = null; dragMarker = null; draggingEntry = false;
    }

    private async Task MoveEntryAsync(RecordBranch source, RecordBranch target, bool child)
    {
        if (document is null || source.Record is null) throw new InvalidOperationException("Select a movable entry.");
        if (document.Format == BinaryFormat.Rdbnp)
        {
            if (source.ListName != target.ListName) throw new InvalidOperationException("Native list schemas cannot be changed by dragging. Move entries within the same list.");
            if (child && target.Record is not null) throw new InvalidOperationException("This native entry has no owned child records. Drop at the same level or onto its list.");
            if (!child && target.Record is null) throw new InvalidOperationException("Native lists cannot be reordered as entries. Drop to the right to move inside the list.");
            string list = source.ListName!;
            int destination = target.Record is null ? document.GetList(list).Rows.Count : target.Record.ListIndex + 1;
            int focus = source.Record.Ordinal - source.Record.ListIndex + (destination > source.Record.ListIndex ? destination - 1 : destination);
            await EditDocumentAsync(copy => copy.MoveListRows(list, source.Record.ListIndex, 1, destination), focus, "Entry moved; native references recalculated.");
            return;
        }
        if (target.Record is null) throw new InvalidOperationException("Select a destination entry.");
        var moving = BranchRows(source);
        int start = moving[0].Ordinal, count = moving.Count;
        var parent = child ? target : target.Parent;
        bool closing = IsClosingEntry(target.Record.Row.Name);
        if (child && closing) throw new InvalidOperationException("A closing entry cannot contain children.");
        int destinationIndex = child ? target.Record.Ordinal + 1 : closing ? target.Record.Ordinal : BranchRows(target)[^1].Ordinal + 1;
        if (active.IsAuthoring)
        {
            await MoveAuthoredEntryAsync(start, count, destinationIndex, parent?.Record?.Ordinal ?? -1);
            return;
        }
        if (destinationIndex >= start && destinationIndex <= start + count)
        {
            if (!ReferenceEquals(source.Parent, parent)) throw new InvalidOperationException("The Structure cannot represent that level change at this position. Choose a compatible destination.");
            Status.Text = "The entry is already at that position."; return;
        }
        int parentOrdinal = parent?.Record?.Ordinal ?? -1;
        int focusOrdinal = destinationIndex > start ? destinationIndex - count : destinationIndex;
        await EditDocumentAsync(copy =>
        {
            var before = RowParents(copy);
            var root = copy.Rows[start];
            CfgRow? expectedParent = parentOrdinal < 0 ? null : copy.Rows[parentOrdinal];
            copy.MoveT2bRows(start, count, destinationIndex);
            var after = RowParents(copy);
            if (!ReferenceEquals(after[root], expectedParent))
                throw new InvalidOperationException("The Structure cannot preserve this parent-child relationship in the BIN. Choose a compatible destination.");
            if (before.Any(item => !ReferenceEquals(item.Key, root) && !ReferenceEquals(item.Value, after[item.Key])))
                throw new InvalidOperationException("This move would change the parent of another entry. Move its complete branch to a compatible destination.");
        }, focusOrdinal, "Branch moved; supported counts and references recalculated.");
    }

    private List<RecordChoice> BranchRows(RecordBranch selected)
    {
        int index = branches.IndexOf(selected);
        return branches.Skip(index).TakeWhile((branch, offset) => offset == 0 || branch.Depth > selected.Depth)
            .Where(branch => branch.Record is not null).Select(branch => branch.Record!).ToList();
    }
}
