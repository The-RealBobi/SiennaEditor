using CFGBINManager;

namespace SiennaEditor;

internal static class TextReplacement
{
    public static IEnumerable<(CfgRow Row, int Index, CfgValue Value)> Find(CfgDocument document, string find, string replacement)
    {
        if (find.Length == 0) throw new ArgumentException("Enter the text to find.");
        if (replacement.Contains('\0')) throw new ArgumentException("Replacement text cannot contain a NUL character.");
        if (find == replacement) yield break;

        var rows = document.Format == BinaryFormat.T2b ? document.Rows : document.Lists.SelectMany(list => list.Rows);
        foreach (var row in rows)
            for (int index = 0; index < row.Values.Count; index++)
            {
                var original = row.Values[index];
                var updated = ReplaceValue(original, find, replacement);
                if (!ReferenceEquals(original, updated)) yield return (row, index, updated);
            }
    }

    public static int Apply(CfgDocument document, string find, string replacement)
    {
        int changed = 0;
        foreach (var (row, index, value) in Find(document, find, replacement))
        {
            if (document.Format == BinaryFormat.T2b) document.SetT2bValue(row, index, value);
            else row.SetValue(index, value);
            changed++;
        }
        return changed;
    }

    private static CfgValue ReplaceValue(CfgValue value, string find, string replacement)
    {
        if (value.Kind == ValueKind.String && value.Text is { } text && text.Contains(find, StringComparison.Ordinal))
            return value with { Text = text.Replace(find, replacement, StringComparison.Ordinal) };

        CfgValue[]? items = null;
        for (int i = 0; i < value.Items.Count; i++)
        {
            var original = value.Items[i];
            var updated = ReplaceValue(original, find, replacement);
            if (ReferenceEquals(updated, original)) continue;
            items ??= value.Items.ToArray();
            items[i] = updated;
        }
        return items is null ? value : value with { Items = items };
    }
}
