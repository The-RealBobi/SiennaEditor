using System.ComponentModel;
using Avalonia;
using CFGBINManager;

namespace SiennaEditor;

internal sealed class RecordBranch : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool expanded;
    public RecordChoice? Record { get; }
    public RecordBranch? Parent { get; }
    public bool HasChildren { get; }
    public bool IsClosing { get; }
    public int Depth { get; }
    public Thickness Indent => new(Depth * 16, 0, 0, 0);
    public string Heading { get; }
    private string search = "";
    private bool directMatch;
    public Avalonia.Controls.Documents.InlineCollection HeadingParts => SearchHighlight.Create(Heading, search);
    public double HeadingOpacity => search.Length > 0 && !directMatch ? 0.65 : 1;
    public string SearchDescription => search.Length > 0 ? directMatch ? "Matching entry: " + Heading : "Context parent: " + Heading : Heading;
    public void SetSearch(string query, bool matches)
    {
        search = query; directMatch = matches;
        PropertyChanged?.Invoke(this, new(nameof(HeadingParts)));
        PropertyChanged?.Invoke(this, new(nameof(HeadingOpacity)));
        PropertyChanged?.Invoke(this, new(nameof(SearchDescription)));
    }
    public string? ListName { get; }
    public string Key => Record is null ? "list:" + ListName : "row:" + Record.Ordinal;
    public string Toggle => expanded ? "▾" : "▸";
    public string ToggleName => (expanded ? "Collapse " : "Expand ") + Heading;
    public override string ToString() => Heading;
    public bool IsExpanded
    {
        get => expanded;
        set
        {
            if (expanded == value) return;
            expanded = value;
            PropertyChanged?.Invoke(this, new(nameof(Toggle)));
            PropertyChanged?.Invoke(this, new(nameof(ToggleName)));
        }
    }

    public RecordBranch(CfgRecordNode node, RecordChoice? record, RecordBranch? parent, string? blockEnd, bool isClosing, int occurrence)
    {
        ListName = node.ListName; Record = record; Parent = parent; Depth = parent is null ? 0 : parent.Depth + 1;
        IsClosing = isClosing;
        HasChildren = node.Children.Any(child => child.Row?.Name != blockEnd);
        Heading = record?.Row.Name ?? node.ListName ?? "Records";
        if (record is not null && blockEnd is not null && blockEnd.EndsWith("_END", StringComparison.Ordinal))
        {
            string name = blockEnd[..^4];
            if (record.Row.Name == name + "_BGN" || record.Row.Name == name + "_BEG" || record.Row.Name == name + "_BEGIN" || record.Row.Name == name + "_START")
                Heading = name;
        }
        if (record is not null) Heading += $"_{occurrence}";
        expanded = parent is null;
    }
}
