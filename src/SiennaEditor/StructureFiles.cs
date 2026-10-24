using System.Text;
using CFGBINManager;

namespace SiennaEditor;

internal sealed class StructureFile
{
    public string Path { get; }
    public string Text { get; set; }
    public StructureCatalog Root { get; }
    public StructureFile(string path, string text, StructureCatalog root) { Path = path; Text = text; Root = root; }
}

internal sealed record StructureChoice(StructureFile File, StructureCatalog Catalog)
{
    public override string ToString() => System.IO.Path.GetFileName(File.Path) + " — " + (Catalog.ProfileName ?? "Custom");
}

public sealed partial class EditorWindow
{
    private readonly List<StructureFile> structureFiles = [];
    private readonly List<StructureChoice> structureChoices = [];
    private string catalogLoadErrors = "";
    private readonly string dataDirectory = DocumentFiles.DataDirectory();

    private void LoadStructures()
    {
        Directory.CreateDirectory(dataDirectory);
        if (!Directory.EnumerateFiles(dataDirectory).Where(path => Path.GetExtension(path).Equals(".cfg", StringComparison.OrdinalIgnoreCase)).Any())
        {
            using var stream = typeof(EditorWindow).Assembly.GetManifestResourceStream("structure.cfg") ?? throw new IOException("The default Structure is missing.");
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            AtomicWrite(Path.Combine(dataDirectory, "structure.cfg"), Encoding.UTF8.GetBytes(reader.ReadToEnd()));
        }
        var errors = new List<string>();
        foreach (string path in Directory.EnumerateFiles(dataDirectory).Where(path => Path.GetExtension(path).Equals(".cfg", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            try { RegisterStructure(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
            { errors.Add(Path.GetFileName(path) + ": " + error.Message); }
        }
        catalogLoadErrors = string.Join("; ", errors);
        if (structureChoices.Count == 0) throw new InvalidDataException("No valid Structure catalog was found in data. " + catalogLoadErrors);
    }

    private StructureChoice RegisterStructure(string path)
    {
        string text = File.ReadAllText(path, Encoding.UTF8);
        var root = StructureCatalog.Parse(text);
        var file = new StructureFile(Path.GetFullPath(path), text, root);
        var choices = root.ProfileNames.Count == 0 ? new[] { new StructureChoice(file, root) }
            : root.ProfileNames.Select(name => new StructureChoice(file, root.ForProfile(name))).ToArray();
        structureFiles.Add(file); structureChoices.AddRange(choices);
        return choices[0];
    }

    private void ApplyLabels()
    {
        if (current is null || document is null) return;
        var changed = editors.Where(field => field.IsLabelChanged).ToArray();
        if (changed.Length == 0) return;
        var labels = changed.Select(field => new StructureLabel(document.Format, current.Row.Name, current.Row.Values.Count,
            string.Join(".", new[] { field.RootIndex }.Concat(field.Path)), field.Label)).ToArray();
        var file = structureFiles.Single(file => ReferenceEquals(file.Root, catalogRoot));
        var candidateRoot = StructureCatalog.Parse(catalogRoot.ToText());
        var candidate = catalog.ProfileName is null ? candidateRoot : candidateRoot.ForProfile(catalog.ProfileName);
        candidate.SetFieldLabels(labels);
        string text = candidateRoot.ToText();
        _ = StructureCatalog.Parse(text);
        SaveCatalogText(file, text);
        catalog.SetFieldLabels(labels);
        foreach (var field in changed) field.AcceptLabel();
        Status.Text = "Field names saved to " + Path.GetFileName(file.Path) + ".";
        UpdateTitle();
    }

    private static void SaveCatalogText(StructureFile file, string text)
    {
        if (File.ReadAllText(file.Path, Encoding.UTF8) != file.Text)
            throw new IOException("This Structure was changed outside the editor. Reopen the application to reload it before editing it.");
        try { AtomicWrite(file.Path, Encoding.UTF8.GetBytes(text)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new IOException("Cannot save the Structure. Make the data directory writable and try again. " + error.Message, error); }
        file.Text = text;
    }

    private void FieldNameLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (changing) return;
        try { ApplyLabels(); }
        catch (Exception error) { Status.Text = "Field name not saved: " + error.Message; }
    }
}
