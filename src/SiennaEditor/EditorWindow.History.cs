using Avalonia.Interactivity;
using CFGBINManager;

namespace SiennaEditor;

public sealed partial class EditorWindow
{
    private EditorSnapshot CaptureSnapshot() => new(CfgTextCodec.Write(document!), active.AuthoredChildren.ToArray(),
        current?.Ordinal ?? -1, active.History.Revision);

    private static void ClearHistoryForCatalogChange(EditorSession session)
        => session.History.Reset(session.Dirty);

    private async void UndoClicked(object? sender, RoutedEventArgs args) => await RunAsync("Undo", UndoAsync);
    private async void RedoClicked(object? sender, RoutedEventArgs args) => await RunAsync("Redo", RedoAsync);

    private async Task UndoAsync()
    {
        ApplyFields();
        if (active.History.Undo is not { } target) { Status.Text = "Nothing to undo."; return; }
        var before = CaptureSnapshot();
        await RestoreSnapshotAsync(target);
        active.History.CompleteUndo(before);
        UpdateTitle(); UpdateControls(); Status.Text = "Undone.";
    }

    private async Task RedoAsync()
    {
        ApplyFields();
        if (active.History.Redo is not { } target) { Status.Text = "Nothing to redo."; return; }
        var before = CaptureSnapshot();
        await RestoreSnapshotAsync(target);
        active.History.CompleteRedo(before);
        UpdateTitle(); UpdateControls(); Status.Text = "Redone.";
    }

    private async Task RestoreSnapshotAsync(EditorSnapshot snapshot)
    {
        var root = StructureCatalog.Parse(catalogRoot.ToText());
        var selected = catalog.ProfileName is null ? root : root.ForProfile(catalog.ProfileName);
        var rules = catalog.Children.Where(rule => !active.AuthoredChildren.Contains(rule) || snapshot.AuthoredChildren.Contains(rule)).ToList();
        foreach (var rule in snapshot.AuthoredChildren)
            if (!rules.Contains(rule)) rules.Add(rule);
        selected.SetChildRules(rules);
        var restored = await Task.Run(() => CfgTextCodec.Read(snapshot.Text, selected));
        _ = restored.GetRecordTree();
        bool changedRules = !catalog.Children.SequenceEqual(rules);
        if (changedRules)
        {
            foreach (var session in sessions.Where(session => !ReferenceEquals(session, active)
                && ReferenceEquals(session.Catalog, catalog) && session.Document?.Format == BinaryFormat.T2b))
            {
                var other = CfgTextCodec.Read(CfgTextCodec.Write(session.Document!), selected);
                if (!ParentPositions(session.Document!).SequenceEqual(ParentPositions(other)))
                    throw new InvalidOperationException("This history step would change the hierarchy in another open file. Close that file before restoring this step.");
            }
            var file = structureFiles.Single(file => ReferenceEquals(file.Root, catalogRoot));
            SaveCatalogText(file, root.ToText());
        }
        catalog.SetChildRules(rules);
        document = CfgTextCodec.Read(snapshot.Text, catalog);
        active.AuthoredChildren.Clear(); active.AuthoredChildren.UnionWith(snapshot.AuthoredChildren);
        RebuildRecords(); FocusOrdinal(snapshot.SelectedOrdinal);
    }
}
