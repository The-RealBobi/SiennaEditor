using System.ComponentModel;
using CFGBINManager;

namespace SiennaEditor;

internal sealed class EditorSession : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public CfgDocument? Document { get; set; }
    public StructureCatalog CatalogRoot { get; set; }
    public StructureCatalog Catalog { get; set; }
    public string CatalogName { get; set; }
    public string SourcePath { get; set; } = "";
    public bool Dirty { get; set; }
    public bool UseTags { get; set; } = true;
    public string Search { get; set; } = "";
    public bool IsAuthoring { get; set; }
    public HashSet<ChildRule> AuthoredChildren { get; } = [];
    public bool NavigationSaved { get; set; }
    public int SelectedOrdinal { get; set; } = -1;
    public HashSet<string> Expanded { get; } = [];
    private string title = "Untitled";
    public string Title
    {
        get => title;
        set { if (title == value) return; title = value; PropertyChanged?.Invoke(this, new(nameof(Title))); }
    }
    public EditorSession(StructureCatalog root, StructureCatalog catalog, string catalogName)
    { CatalogRoot = root; Catalog = catalog; CatalogName = catalogName; }
    public override string ToString() => Title;
}
