using System.Buffers.Binary;
using System.Text;

namespace SiennaEditor;

internal static class ExportProvenance
{
    internal static readonly string Identifier = $"SE_v{typeof(EditorWindow).Assembly.GetName().Version!.ToString(3)}";
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes(Identifier);

    internal static void MarkT2b(Span<byte> data)
    {
        if (data.Length < 48 || !data[^16..^12].SequenceEqual("\x01t2b"u8))
            return;
        var footer = data.Length - 16;
        var stringsEnd = (long)Read(data, 4) + Read(data, 8);
        var nameSection = (stringsEnd + 15) & ~15L;
        if (nameSection > footer - 16)
            return;
        var section = (int)nameSection;
        var namesEnd = nameSection + Read(data, section + 8) + Read(data, section + 12);
        if (nameSection + Read(data, section) != footer || namesEnd < nameSection + 16 ||
            namesEnd > footer - Marker.Length)
            return;
        var padding = data[(int)namesEnd..footer];
        if (padding.IndexOfAnyExcept((byte)0xFF) < 0)
            Marker.CopyTo(padding);
    }

    private static uint Read(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}
