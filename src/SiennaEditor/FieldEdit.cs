using System.ComponentModel;
using CFGBINManager;

namespace SiennaEditor;

internal sealed class FieldEdit : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private readonly Action changed;
    private string text;
    private bool isNull;
    public int RootIndex { get; }
    public IReadOnlyList<int> Path { get; }
    public CfgValue Original { get; }
    public int Width { get; }
    public string Label { get; }
    public string Kind => Original.Kind.ToString();
    public bool IsString => Original.Kind == ValueKind.String;
    public bool CanEdit => !isNull && Original.Kind is not (ValueKind.Array or ValueKind.Structure or ValueKind.Vector);
    public bool IsChanged => text != ValueText.Format(Original) || isNull != (IsString && Original.Text is null);
    public string Text
    {
        get => text;
        set { if (text == value) return; text = value; PropertyChanged?.Invoke(this, new(nameof(Text))); changed(); }
    }
    public bool IsNull
    {
        get => isNull;
        set { if (isNull == value) return; isNull = value; PropertyChanged?.Invoke(this, new(nameof(IsNull))); PropertyChanged?.Invoke(this, new(nameof(CanEdit))); changed(); }
    }
    public FieldEdit(int rootIndex, IReadOnlyList<int> path, CfgValue value, int width, string label, Action changed)
    {
        RootIndex = rootIndex; Path = path; Original = value; Width = width; Label = label; this.changed = changed;
        text = ValueText.Format(value); isNull = IsString && value.Text is null;
    }
    public CfgValue Parse() => IsChanged ? ValueText.Parse(Original, Width, text, isNull) : Original;
}
