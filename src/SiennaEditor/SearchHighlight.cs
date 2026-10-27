using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace SiennaEditor;

internal static class SearchHighlight
{
    public static InlineCollection Create(string text, string query)
    {
        var parts = new InlineCollection();
        int start = 0;
        while (query.Length > 0 && text.IndexOf(query, start, StringComparison.Ordinal) is int index && index >= 0)
        {
            if (index > start) parts.Add(new Run(text[start..index]));
            parts.Add(new Run(text.Substring(index, query.Length)) { FontWeight = FontWeight.Bold, TextDecorations = TextDecorations.Underline });
            start = index + query.Length;
        }
        if (start < text.Length) parts.Add(new Run(text[start..]));
        return parts;
    }
}
