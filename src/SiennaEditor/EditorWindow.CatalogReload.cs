using System.Text;
using CFGBINManager;

namespace SiennaEditor;

public sealed partial class EditorWindow
{
    private string ActiveStructureDescription()
    {
        var file = structureFiles.FirstOrDefault(file => ReferenceEquals(file.Root, catalogRoot));
        return "Catalog: " + (file?.Path ?? "Unavailable") + "\nProfile: " + (catalog.ProfileName ?? "Custom");
    }

    private async Task ReloadStructuresAsync()
    {
        if (editors.Any(field => field.IsLabelChanged))
            throw new InvalidOperationException("Finish or restore the pending field name edits before reloading Structures.");
        var pending = editors.Where(field => field.IsChanged).Select(field => (Field: field, Value: field.Parse())).ToArray();
        var selected = current;
        var openSessions = sessions.Contains(active) ? sessions.ToArray() : sessions.Append(active).ToArray();
        var originals = openSessions.Select(session => (Session: session,
            File: structureFiles.SingleOrDefault(file => ReferenceEquals(file.Root, session.CatalogRoot))
                ?? throw new InvalidOperationException("The active Structure catalog is unavailable for " + session.Title + "."),
            Text: session.Document is null ? null : CfgTextCodec.Write(session.Document))).ToArray();
        var replacement = await Task.Run(() =>
        {
            var files = new List<StructureFile>();
            var choices = new List<StructureChoice>();
            foreach (string path in Directory.EnumerateFiles(dataDirectory).Where(path => Path.GetExtension(path).Equals(".cfg", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
            {
                try
                {
                    string text = File.ReadAllText(path, Encoding.UTF8);
                    var root = StructureCatalog.Parse(text);
                    var file = new StructureFile(Path.GetFullPath(path), text, root);
                    files.Add(file);
                    if (root.ProfileNames.Count == 0) choices.Add(new StructureChoice(file, root));
                    else choices.AddRange(root.ProfileNames.Select(name => new StructureChoice(file, root.ForProfile(name))));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
                { throw new InvalidDataException(Path.GetFileName(path) + ": " + error.Message, error); }
            }
            if (choices.Count == 0) throw new InvalidDataException("No Structure catalogs were found in data.");
            var documents = new List<(EditorSession Session, StructureChoice Choice, CfgDocument? Document)>();
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var original in originals)
            {
                var choice = choices.SingleOrDefault(choice => string.Equals(choice.File.Path, original.File.Path, comparison)
                    && string.Equals(choice.Catalog.ProfileName, original.Session.Catalog.ProfileName, StringComparison.Ordinal))
                    ?? throw new InvalidDataException("The Structure catalog or profile used by " + original.Session.Title + " is missing: " + original.File.Path + " — " + (original.Session.Catalog.ProfileName ?? "Custom") + ".");
                CfgDocument? rebound = original.Text is null ? null : CfgTextCodec.Read(original.Text, choice.Catalog);
                if (rebound is not null)
                {
                    if (ReferenceEquals(original.Session, active) && selected is not null && pending.Length > 0)
                    {
                        var row = selected.ListName is null ? rebound.Rows[selected.ListIndex] : rebound.GetList(selected.ListName).Rows[selected.ListIndex];
                        foreach (var edit in pending)
                        {
                            CfgValue value = ValueText.Replace(row.Values[edit.Field.RootIndex], edit.Field.Path, 0, edit.Value);
                            if (rebound.Format == BinaryFormat.T2b) rebound.SetT2bValue(row, edit.Field.RootIndex, value);
                            else row.SetValue(edit.Field.RootIndex, value);
                        }
                    }
                    _ = rebound.GetRecordTree();
                    _ = BinCodec.Write(rebound);
                }
                documents.Add((original.Session, choice, rebound));
            }
            return (Files: files, Choices: choices, Documents: documents);
        });
        searchDelay?.Cancel();
        StoreNavigation();
        structureFiles.Clear(); structureFiles.AddRange(replacement.Files);
        structureChoices.Clear(); structureChoices.AddRange(replacement.Choices);
        catalogLoadErrors = "";
        foreach (var item in replacement.Documents)
        {
            item.Session.CatalogRoot = item.Choice.File.Root;
            item.Session.Catalog = item.Choice.Catalog;
            item.Session.CatalogName = Path.GetFileName(item.Choice.File.Path);
            item.Session.Document = item.Document;
            item.Session.AuthoredChildren.IntersectWith(item.Choice.Catalog.Children);
            ClearHistoryForCatalogChange(item.Session);
        }
        if (pending.Length > 0) active.Dirty = true;
        SetProfiles(); RebuildRecords();
        foreach (var branch in branches) branch.IsExpanded = active.Expanded.Contains(branch.Key);
        IncludeMatches(active.Search.Length > 0); ShowPage(); FocusOrdinal(active.SelectedOrdinal);
        Status.Text = $"Reloaded {replacement.Files.Count} Structure catalog(s) for {openSessions.Length} session(s). Unsaved edits preserved. Undo and Redo history cleared.";
    }
}
