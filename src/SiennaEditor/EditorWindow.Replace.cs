using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CFGBINManager;

namespace SiennaEditor;

public sealed partial class EditorWindow
{
    private sealed record TextHit(int Ordinal, int Field, int[] Path, Match Match);

    private readonly DispatcherTimer findDelay = new() { Interval = TimeSpan.FromMilliseconds(175) };
    private List<TextHit> textHits = [];
    private int hitIndex = -1;
    private CfgDocument? searchedDocument;

    private void FindReplaceClicked(object? sender, RoutedEventArgs args)
    {
        if (busy || document is null) return;
        if (!FindPanel.IsVisible)
        {
            if (string.IsNullOrEmpty(FindText.Text)) FindText.Text = Search.Text;
            FindPanel.IsVisible = true;
            ShowReplace.IsChecked = true;
            RefreshFindResults();
        }
        FindText.Focus();
        FindText.SelectAll();
    }

    private void FindTextChanged(object? sender, TextChangedEventArgs args) => FindQueryChanged();
    private void FindOptionChanged(object? sender, RoutedEventArgs args) => FindQueryChanged();

    private void FindQueryChanged()
    {
        hitIndex = -1;
        QueueFindResults();
    }

    private void ReplaceVisibilityChanged(object? sender, RoutedEventArgs args)
    {
        if (ReplacementRow is not null) ReplacementRow.IsVisible = ShowReplace.IsChecked == true;
    }

    private void QueueFindResults()
    {
        if (FindPanel?.IsVisible != true) return;
        findDelay.Stop();
        findDelay.Start();
    }

    private Regex CreateFindPattern()
    {
        string query = FindText.Text ?? "";
        string pattern = UseRegex.IsChecked == true ? query : Regex.Escape(query);
        if (WholeWord.IsChecked == true) pattern = $@"(?<!\w)(?:{pattern})(?!\w)";
        var options = RegexOptions.CultureInvariant;
        if (MatchCase.IsChecked != true) options |= RegexOptions.IgnoreCase;
        return new Regex(pattern, options, TimeSpan.FromMilliseconds(200));
    }

    private void RefreshFindResults()
    {
        findDelay.Stop();
        var previous = ReferenceEquals(searchedDocument, document) && hitIndex >= 0 ? textHits[hitIndex] : null;
        searchedDocument = document;
        textHits.Clear();
        hitIndex = -1;
        FindMessage.IsVisible = false;
        if (document is not null && !string.IsNullOrEmpty(FindText.Text))
        {
            try
            {
                var pattern = CreateFindPattern();
                foreach (var record in allRecords)
                    for (int i = 0; i < record.Row.Values.Count; i++)
                        Scan(record.Row.Values[i], record.Ordinal, i, [], pattern);

                if (previous is not null)
                    hitIndex = textHits.FindIndex(hit => hit.Ordinal == previous.Ordinal && hit.Field == previous.Field
                        && hit.Path.SequenceEqual(previous.Path) && hit.Match.Index == previous.Match.Index);
            }
            catch (Exception error) when (error is ArgumentException or RegexMatchTimeoutException)
            {
                textHits.Clear();
                FindMessage.Text = error is RegexMatchTimeoutException ? "That regular expression took too long." : error.Message;
                FindMessage.IsVisible = true;
            }
        }
        UpdateFindControls();

        void Scan(CfgValue value, int ordinal, int field, int[] path, Regex pattern)
        {
            if (value.Kind == ValueKind.String && value.Text is { } text)
                foreach (Match match in pattern.Matches(text))
                    textHits.Add(new TextHit(ordinal, field, path, match));
            for (int i = 0; i < value.Items.Count; i++) Scan(value.Items[i], ordinal, field, [.. path, i], pattern);
        }
    }

    private void UpdateFindControls()
    {
        FindPrevious.IsEnabled = FindNext.IsEnabled = ReplaceAll.IsEnabled = textHits.Count > 0;
        ReplaceCurrent.IsEnabled = hitIndex >= 0;
        FindCount.Text = textHits.Count switch
        {
            0 => string.IsNullOrEmpty(FindText.Text) ? "" : "No matches",
            _ when hitIndex < 0 => $"{textHits.Count:N0} matches",
            _ => $"{hitIndex + 1:N0} / {textHits.Count:N0}"
        };
    }

    private void NavigateFind(int direction)
    {
        if (busy) return;
        try { ApplyFields(); }
        catch (Exception error) { Status.Text = error.Message; return; }
        RefreshFindResults();
        if (textHits.Count == 0) return;
        hitIndex = hitIndex < 0
            ? (direction > 0 ? 0 : textHits.Count - 1)
            : (hitIndex + direction + textHits.Count) % textHits.Count;
        RevealHit(textHits[hitIndex]);
        UpdateFindControls();
    }

    private void RevealHit(TextHit hit)
    {
        if (!string.IsNullOrEmpty(Search.Text) && !matching.Any(record => record.Ordinal == hit.Ordinal))
        {
            searchDelay?.Cancel();
            active.Search = "";
            Search.Text = "";
            matching = allRecords;
            IncludeMatches(false);
            ShowPage();
        }
        FocusOrdinal(hit.Ordinal);
        Dispatcher.UIThread.Post(() =>
        {
            if (!FindPanel.IsVisible || current?.Ordinal != hit.Ordinal) return;
            var box = Fields.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(control =>
                control.Classes.Contains("field-value") && control.DataContext is FieldEdit field
                && field.RootIndex == hit.Field && field.Path.SequenceEqual(hit.Path));
            if (box is null || box.Text is not { } text || hit.Match.Index + hit.Match.Length > text.Length) return;
            box.BringIntoView();
            box.SelectionStart = hit.Match.Index;
            box.SelectionEnd = hit.Match.Index + hit.Match.Length;
        }, DispatcherPriority.Loaded);
    }

    private async Task ReplaceHitsAsync(bool all)
    {
        await RunAsync("Replace", async () =>
        {
            ApplyFields();
            RefreshFindResults();
            if (textHits.Count == 0 || (!all && hitIndex < 0)) return;
            string replacement = ReplacementText.Text ?? "";
            if (replacement.Contains('\0')) throw new ArgumentException("Replacement text cannot contain a NUL character.");
            bool useRegex = UseRegex.IsChecked == true;
            IEnumerable<TextHit> selected = all ? textHits : [textHits[hitIndex]];
            bool preserveCase = PreserveCase.IsChecked == true;
            var hits = selected.Select(hit =>
                {
                    string value = useRegex ? hit.Match.Result(replacement) : replacement;
                    return (Hit: hit, Replacement: preserveCase ? MatchCapitalization(value, hit.Match.Value) : value);
                })
                .Where(item => item.Hit.Match.Value != item.Replacement).ToArray();
            if (hits.Length == 0)
            {
                Status.Text = "No text fields would change.";
                return;
            }
            var changes = hits.GroupBy(item => (item.Hit.Ordinal, item.Hit.Field)).ToArray();
            int ordinal = current?.Ordinal ?? hits[0].Hit.Ordinal;
            await EditDocumentAsync(copy =>
            {
                var rows = (copy.Format == BinaryFormat.T2b ? copy.Rows : copy.Lists.SelectMany(list => list.Rows)).ToArray();
                foreach (var group in changes)
                {
                    var row = rows[group.Key.Ordinal];
                    int index = group.Key.Field;
                    var original = row.Values[index];
                    var updated = original;
                    foreach (var (hit, replacementValue) in group.Reverse())
                    {
                        var value = updated;
                        foreach (int child in hit.Path) value = value.Items[child];
                        string text = value.Text!;
                        string result = text[..hit.Match.Index] + replacementValue + text[(hit.Match.Index + hit.Match.Length)..];
                        updated = ValueText.Replace(updated, hit.Path, 0, value with { Text = result });
                    }
                    if (copy.Format == BinaryFormat.T2b) copy.SetT2bValue(row, index, updated);
                    else row.SetValue(index, updated);
                }
            }, ordinal);
            hitIndex = -1;
            RefreshFindResults();
            Status.Text = $"Replaced {hits.Length:N0} {(hits.Length == 1 ? "match" : "matches")}.";
        });
    }

    private static string MatchCapitalization(string replacement, string match)
    {
        if (match.Any(char.IsUpper) && !match.Any(char.IsLower)) return replacement.ToUpperInvariant();
        if (match.Any(char.IsLower) && !match.Any(char.IsUpper)) return replacement.ToLowerInvariant();
        if (match.Length > 0 && char.IsUpper(match[0]) && !match.Skip(1).Any(char.IsUpper))
        {
            int first = 0;
            while (first < replacement.Length && !char.IsLetter(replacement[first])) first++;
            if (first < replacement.Length)
                return replacement[..first] + char.ToUpperInvariant(replacement[first]) + replacement[(first + 1)..].ToLowerInvariant();
        }
        return replacement;
    }

    private void FindPreviousClicked(object? sender, RoutedEventArgs args) => NavigateFind(-1);
    private void FindNextClicked(object? sender, RoutedEventArgs args) => NavigateFind(1);
    private async void ReplaceCurrentClicked(object? sender, RoutedEventArgs args) => await ReplaceHitsAsync(false);
    private async void ReplaceAllClicked(object? sender, RoutedEventArgs args) => await ReplaceHitsAsync(true);
    private void FindCloseClicked(object? sender, RoutedEventArgs args) => CloseFindPanel();

    private void CloseFindPanel()
    {
        findDelay.Stop();
        FindPanel.IsVisible = false;
        textHits.Clear();
        hitIndex = -1;
        searchedDocument = null;
        Records.Focus();
    }

    private void FindPanelKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape) { args.Handled = true; CloseFindPanel(); }
        else if (args.Key == Key.Enter && (FindText.IsFocused || ReplacementText.IsFocused))
        {
            args.Handled = true;
            NavigateFind(args.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        }
    }
}
