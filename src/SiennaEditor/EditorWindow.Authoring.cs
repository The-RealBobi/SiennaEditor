using CFGBINManager;

namespace SiennaEditor;

public sealed partial class EditorWindow
{
    private async Task MoveAuthoredEntryAsync(int start, int count, int destination, int parentOrdinal)
    {
        ApplyFields();
        var original = document!;
        var oldPositions = original.Rows.Select((row, index) => (row, index)).ToDictionary(item => item.row, item => item.index);
        var originalParents = RowParents(original);
        var candidateRoot = StructureCatalog.Parse(catalogRoot.ToText());
        var candidate = catalog.ProfileName is null ? candidateRoot : candidateRoot.ForProfile(catalog.ProfileName);
        var rules = candidate.Children.ToList();
        var source = original.Rows[start];
        var oldParent = originalParents[source];
        var newParent = parentOrdinal < 0 ? null : original.Rows[parentOrdinal];
        if (oldParent is not null && !ReferenceEquals(oldParent, newParent) && !IsBlockEntry(oldParent.Name))
        {
            var previous = new ChildRule(oldParent.Name, source.Name);
            if (rules.Contains(previous) && !active.AuthoredChildren.Contains(previous))
                throw new InvalidOperationException("That relationship belongs to the loaded Structure. Use a separate Structure to author different relationships.");
            rules.Remove(previous);
        }
        if (newParent is not null && !ReferenceEquals(oldParent, newParent) && !IsBlockEntry(newParent.Name))
            rules.Add(new ChildRule(newParent.Name, source.Name));
        candidate.SetChildRules(rules);
        bool changedRules = !candidate.Children.SequenceEqual(catalog.Children);
        byte[] bytes = await Task.Run(() =>
        {
            var copy = CfgTextCodec.Read(CfgTextCodec.Write(original), candidate);
            var copiedRows = copy.Rows.ToArray();
            copy.MoveT2bRows(start, count, destination);
            var after = RowParents(copy);
            CfgRow? expected = parentOrdinal < 0 ? null : copiedRows[parentOrdinal];
            if (!ReferenceEquals(after[copiedRows[start]], expected)) throw new InvalidOperationException("This hierarchy requires compatible entry names or an explicit list block.");
            foreach (var item in originalParents)
            {
                if (ReferenceEquals(item.Key, source)) continue;
                CfgRow? previous = item.Value is null ? null : copiedRows[oldPositions[item.Value]];
                if (!ReferenceEquals(after[copiedRows[oldPositions[item.Key]]], previous))
                    throw new InvalidOperationException("That relation would also reparent other entries with the same names. Use distinct entry names or explicit list blocks.");
            }
            byte[] result = BinCodec.Write(copy);
            _ = BinCodec.Read(result, candidate).GetRecordTree();
            return result;
        });
        if (changedRules)
        {
            foreach (var session in sessions.Where(session => !ReferenceEquals(session, active) && ReferenceEquals(session.Catalog, catalog) && session.Document is not null))
            {
                var loaded = session.Document!;
                var relabeled = CfgTextCodec.Read(CfgTextCodec.Write(loaded), candidate);
                if (!ParentPositions(loaded).SequenceEqual(ParentPositions(relabeled)))
                    throw new InvalidOperationException("That Structure relation would change the hierarchy in another open file. Choose a separate Structure for this new document.");
            }
            var file = structureFiles.Single(file => ReferenceEquals(file.Root, catalogRoot));
            string text = candidateRoot.ToText(); _ = StructureCatalog.Parse(text);
            var addedRules = candidate.Children.Except(catalog.Children).ToArray();
            SaveCatalogText(file, text); catalog.SetChildRules(candidate.Children);
            active.AuthoredChildren.IntersectWith(candidate.Children); active.AuthoredChildren.UnionWith(addedRules);
        }
        document = BinCodec.Read(bytes, catalog); MarkDirty(); RebuildRecords();
        FocusOrdinal(destination > start + count ? destination - count : start > destination ? destination : start);
        Status.Text = changedRules ? "Branch moved. Child relationships saved to the Structure." : "Branch moved; counts and references recalculated.";
    }

    private static (int Row, int Parent)[] ParentPositions(CfgDocument source)
    {
        var indices = source.Rows.Select((row, index) => (row, index)).ToDictionary(item => item.row, item => item.index);
        return RowParents(source).Select(item => (Row: indices[item.Key], Parent: item.Value is null ? -1 : indices[item.Value])).OrderBy(item => item.Row).ToArray();
    }
}
