using CFGBINManager;

namespace SiennaEditor;

internal sealed record EditorSnapshot(string Text, ChildRule[] AuthoredChildren, int SelectedOrdinal, long Revision);

internal sealed class DocumentHistory
{
    private readonly List<EditorSnapshot> undo = [];
    private readonly List<EditorSnapshot> redo = [];
    private long nextRevision;
    private long savedRevision;
    public long Revision { get; private set; }
    public bool IsDirty => Revision != savedRevision;
    public EditorSnapshot? Undo => undo.LastOrDefault();
    public EditorSnapshot? Redo => redo.LastOrDefault();

    public void Record(EditorSnapshot before)
    {
        undo.Add(before); redo.Clear(); Revision = ++nextRevision;
        Trim();
    }

    public void CompleteUndo(EditorSnapshot current)
    {
        Revision = undo[^1].Revision;
        undo.RemoveAt(undo.Count - 1); redo.Add(current); Trim();
    }

    public void CompleteRedo(EditorSnapshot current)
    {
        Revision = redo[^1].Revision;
        redo.RemoveAt(redo.Count - 1); undo.Add(current); Trim();
    }

    public void MarkSaved() => savedRevision = Revision;
    public void MarkUnsaved() { if (Revision == savedRevision) savedRevision = -1; }
    public void Reset(bool dirty)
    {
        undo.Clear(); redo.Clear(); Revision = nextRevision = 0; savedRevision = dirty ? -1 : 0;
    }

    private void Trim()
    {
        // Keep at least the most recent operation even when a large document exceeds the history budget.
        long characters = undo.Sum(item => (long)item.Text.Length) + redo.Sum(item => (long)item.Text.Length);
        while (undo.Count + redo.Count > 1 && (characters > 32L * 1024 * 1024 || undo.Count + redo.Count > 100))
        {
            var list = undo.Count > 0 ? undo : redo;
            characters -= list[0].Text.Length; list.RemoveAt(0);
        }
    }
}
