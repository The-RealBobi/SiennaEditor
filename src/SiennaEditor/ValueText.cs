using System.Globalization;
using CFGBINManager;

namespace SiennaEditor;

internal static class ValueText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Format(CfgValue value) => value.Kind switch
    {
        ValueKind.String => value.Text ?? "",
        ValueKind.Signed => value.AsInt64().ToString(Invariant),
        ValueKind.Unsigned or ValueKind.Hash32 or ValueKind.Hash64 => value.Bits.ToString(Invariant),
        ValueKind.Opaque => $"0x{value.Bits:X8}",
        ValueKind.Boolean => value.AsBoolean() ? "true" : "false",
        ValueKind.Float32 => float.IsNaN(value.AsSingle()) ? $"bits:0x{value.Bits:X8}" : value.AsSingle().ToString("R", Invariant),
        ValueKind.Float64 => double.IsNaN(value.AsDouble()) ? $"bits:0x{value.Bits:X16}" : value.AsDouble().ToString("R", Invariant),
        ValueKind.Reference16 or ValueKind.Reference32 => $"{value.AsRange().Start}, {value.AsRange().Count}",
        _ => $"{value.Kind} [{value.Items.Count}]"
    };

    public static bool Contains(CfgValue value, string text) => Format(value).Contains(text, StringComparison.OrdinalIgnoreCase)
        || value.Items.Any(child => Contains(child, text));

    public static CfgValue Parse(CfgValue original, int width, string text, bool isNull)
    {
        if (original.Kind == ValueKind.String)
        {
            if (!isNull && text.Contains('\0')) throw new FormatException("Strings cannot contain a NUL character.");
            return original with { Text = isNull ? null : text };
        }
        text = text.Trim();
        switch (original.Kind)
        {
            case ValueKind.Boolean:
                return CfgValue.Boolean(text switch { "true" or "1" => true, "false" or "0" => false, _ => throw new FormatException("Enter true or false.") });
            case ValueKind.Signed:
                long signed;
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    ulong bits = Unsigned(text);
                    CheckWidth(bits, width);
                    signed = width switch { 1 => unchecked((sbyte)bits), 2 => unchecked((short)bits), 4 => unchecked((int)bits), _ => unchecked((long)bits) };
                }
                else signed = long.Parse(text, NumberStyles.Integer, Invariant);
                if (width is 1 or 2 or 4)
                {
                    long maximum = (1L << (width * 8 - 1)) - 1;
                    if (signed < -maximum - 1 || signed > maximum) throw new OverflowException($"Value must fit a signed {width * 8}-bit field.");
                }
                return CfgValue.Signed(signed);
            case ValueKind.Unsigned:
            case ValueKind.Hash32:
            case ValueKind.Hash64:
            case ValueKind.Opaque:
                ulong number = Unsigned(text);
                CheckWidth(number, original.Kind == ValueKind.Hash32 ? 4 : original.Kind == ValueKind.Hash64 ? 8 : width);
                return original with { Bits = number };
            case ValueKind.Float32:
            case ValueKind.Float64:
                if (text.StartsWith("bits:", StringComparison.OrdinalIgnoreCase))
                {
                    ulong bits = Unsigned(text[5..]);
                    CheckWidth(bits, original.Kind == ValueKind.Float32 ? 4 : 8);
                    return original with { Bits = bits };
                }
                return original.Kind == ValueKind.Float32 ? CfgValue.Float(float.Parse(text, NumberStyles.Float, Invariant)) : CfgValue.Double(double.Parse(text, NumberStyles.Float, Invariant));
            case ValueKind.Reference16:
            case ValueKind.Reference32:
                var parts = text.Split(',');
                if (parts.Length != 2) throw new FormatException("Enter a range as start, count.");
                ulong start = Unsigned(parts[0].Trim()), count = Unsigned(parts[1].Trim());
                return original.Kind == ValueKind.Reference16 ? CfgValue.Range16(checked((ushort)start), checked((ushort)count)) : CfgValue.Range32(checked((uint)start), checked((uint)count));
            default:
                throw new InvalidOperationException("Edit the individual components of this value.");
        }
    }

    private static ulong Unsigned(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? ulong.Parse(text[2..], NumberStyles.AllowHexSpecifier, Invariant) : ulong.Parse(text, NumberStyles.Integer, Invariant);

    private static void CheckWidth(ulong bits, int width)
    {
        if (width is 1 or 2 or 4 && bits >= 1UL << (width * 8)) throw new OverflowException($"Value must fit an unsigned {width * 8}-bit field.");
    }

    public static CfgValue Replace(CfgValue root, IReadOnlyList<int> path, int depth, CfgValue replacement)
    {
        if (depth == path.Count) return replacement;
        var children = root.Items.ToArray();
        children[path[depth]] = Replace(children[path[depth]], path, depth + 1, replacement);
        return root with { Items = children };
    }
}
