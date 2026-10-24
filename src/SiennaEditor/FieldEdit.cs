using System.ComponentModel;
using CFGBINManager;

namespace SiennaEditor;

internal sealed class FieldEdit : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private readonly Action changed;
    private string text;
    private string selectedKind;
    private bool isNull;
    public int RootIndex { get; }
    public IReadOnlyList<int> Path { get; }
    public CfgValue Original { get; }
    public int Width { get; }
    private string label;
    private string originalLabel;
    public string Label
    {
        get => label;
        set { if (label == value) return; label = value; Notify(nameof(Label)); changed(); }
    }
    public bool IsLabelChanged => label != originalLabel;
    public bool CanEditLabel { get; }
    public Avalonia.Controls.GridLength TypeWidth { get; }
    public void AcceptLabel() { originalLabel = label; }
    public IReadOnlyList<string> Kinds { get; }
    public bool CanChangeKind { get; }
    public bool CanRemove { get; }
    public bool IsString => selectedKind == "String";
    public bool CanEdit => !isNull && Original.Kind is not (ValueKind.Array or ValueKind.Structure or ValueKind.Vector);
    public bool IsChanged => selectedKind != DisplayKind(Original.Kind) || text != ValueText.Format(Original) || isNull != (Original.Kind == ValueKind.String && Original.Text is null);
    public string SelectedKind
    {
        get => selectedKind;
        set
        {
            if (selectedKind == value || !Kinds.Contains(value)) return;
            selectedKind = value; isNull = false;
            text = ValueText.Format(Template());
            Notify(nameof(SelectedKind), nameof(Text), nameof(IsString), nameof(IsNull), nameof(CanEdit));
            changed();
        }
    }
    public string Text
    {
        get => text;
        set { if (text == value) return; text = value; Notify(nameof(Text)); changed(); }
    }
    public bool IsNull
    {
        get => isNull;
        set { if (isNull == value) return; isNull = value; Notify(nameof(IsNull), nameof(CanEdit)); changed(); }
    }
    public FieldEdit(int rootIndex, IReadOnlyList<int> path, CfgValue value, int width, string label, bool canRemove, bool canChangeKind, bool canEditLabel, Action changed)
    {
        RootIndex = rootIndex; Path = path; Original = value; Width = width; this.label = label; originalLabel = label; CanRemove = canRemove; CanEditLabel = canEditLabel; this.changed = changed;
        CanChangeKind = canChangeKind;
        TypeWidth = new Avalonia.Controls.GridLength(canChangeKind ? 100 : 0);
        selectedKind = DisplayKind(value.Kind);
        Kinds = canChangeKind ? ["Int", "String", "Float", "Raw"] : [selectedKind];
        text = ValueText.Format(value); isNull = IsString && value.Text is null;
    }
    public void Reset()
    {
        selectedKind = DisplayKind(Original.Kind); text = ValueText.Format(Original); isNull = Original.Kind == ValueKind.String && Original.Text is null;
        Notify(nameof(SelectedKind), nameof(Text), nameof(IsString), nameof(IsNull), nameof(CanEdit)); changed();
    }
    public CfgValue Parse() => IsChanged ? ValueText.Parse(Template(), Width, text, isNull) : Original;
    private CfgValue Template()
    {
        if (selectedKind == DisplayKind(Original.Kind)) return Original;
        uint bits = Original.Kind == ValueKind.String ? 0 : unchecked((uint)Original.Bits);
        return selectedKind switch
        {
            "Int" => CfgValue.Signed(unchecked((int)bits)),
            "Float" => new CfgValue(ValueKind.Float32, bits, null, []),
            "Raw" => new CfgValue(ValueKind.Opaque, bits, null, []),
            _ => CfgValue.String(ValueText.Format(Original))
        };
    }
    private static string DisplayKind(ValueKind kind) => kind switch
    { ValueKind.Signed => "Int", ValueKind.Float32 => "Float", ValueKind.Float64 => "Double", ValueKind.Opaque => "Raw", _ => kind.ToString() };
    private void Notify(params string[] names) { foreach (string name in names) PropertyChanged?.Invoke(this, new(name)); }
}
