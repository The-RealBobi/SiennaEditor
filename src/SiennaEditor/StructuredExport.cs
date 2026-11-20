using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using CFGBINManager;
using ValueKind = CFGBINManager.ValueKind;

namespace SiennaEditor;

internal static class StructuredExport
{
    private static readonly JsonSerializerOptions StringOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly JsonSerializerOptions Options = new(StringOptions)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonElement NegativeZero = JsonSerializer.Deserialize<JsonElement>("-0.0");

    private sealed record ExportDocument(int SchemaVersion, string Format, ushort? BinaryVersion, bool BigEndian,
        int? TextEncoding, int? ValueWidth, ExportRow[] Rows, ExportList[] Lists);
    private sealed record ExportList(string Name, string Type, ExportRow[] Rows);
    private sealed record ExportRow(string Name, uint NameHash, ExportField[] Fields);
    private sealed record ExportField(string Name, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label,
        ValueKind Type, object? Value);
    private sealed record ExportValue(ValueKind Type, object? Value);
    private sealed record ExportRange(uint Start, uint Count);

    public static byte[] Encode(CfgDocument document, DocumentExportFormat format)
    {
        var model = new ExportDocument(1, document.Format == BinaryFormat.T2b ? "T2B" : "RDBNP",
            document.BinaryVersion, document.IsBigEndian, document.TextEncodingCode, document.T2bValueWidth,
            document.Rows.Select(row => Row(row, document)).ToArray(),
            document.Lists.Select(list => new ExportList(list.Name, list.TypeName, list.Rows.Select(row => Row(row, document)).ToArray())).ToArray());
        if (format == DocumentExportFormat.Json) return JsonSerializer.SerializeToUtf8Bytes(model, Options);
        var tree = JsonSerializer.SerializeToElement(model, Options);
        return format switch
        {
            DocumentExportFormat.Xml => WriteXml(tree),
            DocumentExportFormat.Yaml => WriteYaml(tree),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    private static ExportRow Row(CfgRow row, CfgDocument document)
    {
        var definitions = document.Catalog.GetFields(document.Format, row.Name, row.Values.Count);
        var fields = row.Values.Select((value, index) =>
        {
            string position = index.ToString(CultureInfo.InvariantCulture);
            string name = index < row.FieldNames.Count ? row.FieldNames[index] : position;
            string? label = document.Catalog.GetFieldLabel(document.Format, row.Name, row.Values.Count, position)
                ?? (document.Format == BinaryFormat.T2b && definitions.Count == row.Values.Count ? definitions[index].Name : null);
            return new ExportField(name, label == name ? null : label, value.Kind, Content(value));
        }).ToArray();
        return new(row.Name, row.NameHash, fields);
    }

    private static object? Content(CfgValue value) => value.Kind switch
    {
        ValueKind.String => value.Text,
        ValueKind.Signed => value.AsInt64(),
        ValueKind.Unsigned or ValueKind.Hash32 or ValueKind.Hash64 => value.AsUInt64(),
        ValueKind.Boolean => value.AsBoolean(),
        ValueKind.Float32 => Float(value.AsSingle(), value.Bits, 8),
        ValueKind.Float64 => Float(value.AsDouble(), value.Bits, 16),
        ValueKind.Reference16 or ValueKind.Reference32 => Range(value),
        ValueKind.Vector or ValueKind.Array or ValueKind.Structure =>
            value.Items.Select(item => new ExportValue(item.Kind, Content(item))).ToArray(),
        ValueKind.Opaque => $"0x{value.Bits:X}",
        _ => throw new InvalidDataException($"Unsupported export type: {value.Kind}.")
    };

    private static object Float<T>(T number, ulong bits, int digits) where T : struct, System.Numerics.IFloatingPointIeee754<T>
        => T.IsNaN(number) ? "NaN (0x" + bits.ToString("X" + digits, CultureInfo.InvariantCulture) + ")" :
            T.IsPositiveInfinity(number) ? "Infinity" : T.IsNegativeInfinity(number) ? "-Infinity" :
            T.IsZero(number) && T.IsNegative(number) ? NegativeZero : number;

    private static ExportRange Range(CfgValue value)
    {
        var (start, count) = value.AsRange();
        return new(start, count);
    }

    private static byte[] WriteXml(JsonElement tree)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false), Indent = true, NewLineHandling = NewLineHandling.Entitize
        }))
        {
            writer.WriteStartDocument();
            Element("document", tree);
            writer.WriteEndDocument();

            void Element(string name, JsonElement value)
            {
                writer.WriteStartElement(name);
                writer.WriteAttributeString("type", value.ValueKind switch
                {
                    JsonValueKind.True or JsonValueKind.False => "boolean",
                    _ => value.ValueKind.ToString().ToLowerInvariant()
                });
                switch (value.ValueKind)
                {
                    case JsonValueKind.Object:
                        foreach (var property in value.EnumerateObject()) Element(property.Name, property.Value);
                        break;
                    case JsonValueKind.Array:
                        foreach (var item in value.EnumerateArray()) Element("item", item);
                        break;
                    case JsonValueKind.String:
                        string text = value.GetString()!;
                        // XML 1.0 cannot carry every CFG character; retain such strings as JSON literals.
                        if (text.EnumerateRunes().Any(rune => rune.IsBmp && !XmlConvert.IsXmlChar((char)rune.Value)))
                        {
                            writer.WriteAttributeString("encoding", "json-string");
                            writer.WriteString(value.GetRawText());
                        }
                        else writer.WriteString(text);
                        break;
                    case JsonValueKind.Null:
                        break;
                    default:
                        writer.WriteString(value.GetRawText());
                        break;
                }
                writer.WriteEndElement();
            }
        }
        return stream.ToArray();
    }

    private static byte[] WriteYaml(JsonElement tree)
    {
        var output = new StringBuilder();
        Block(tree, 0);
        return Encoding.UTF8.GetBytes(output.ToString());

        void Block(JsonElement value, int indent)
        {
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject())
                {
                    output.Append(' ', indent).Append(property.Name).Append(':');
                    Value(property.Value, indent);
                }
            else
                foreach (var item in value.EnumerateArray())
                {
                    output.Append(' ', indent).Append('-');
                    Value(item, indent);
                }
        }

        void Value(JsonElement value, int indent)
        {
            if (value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Any()
                || value.ValueKind == JsonValueKind.Array && value.GetArrayLength() != 0)
            {
                output.Append('\n');
                Block(value, indent + 2);
            }
            else
            {
                output.Append(' ');
                if (value.ValueKind == JsonValueKind.String) AppendYamlString(output, value.GetString()!);
                else if (value.ValueKind == JsonValueKind.Number)
                {
                    string number = value.GetRawText();
                    int exponent = number.IndexOfAny(['e', 'E']);
                    // Keep exponent notation numeric in YAML 1.1 readers as well as YAML 1.2.
                    if (exponent >= 0 && !number.Contains('.')) output.Append(number.AsSpan(0, exponent)).Append(".0").Append(number.AsSpan(exponent));
                    else output.Append(number);
                }
                else output.Append(value.GetRawText());
                output.Append('\n');
            }
        }
    }

    private static void AppendYamlString(StringBuilder output, string value)
    {
        output.Append('"');
        // JSON quoting is also valid YAML quoting for BMP characters. YAML's \U handles non-BMP code points.
        int start = 0;
        for (int i = 0; i + 1 < value.Length; i++)
        {
            if (!char.IsSurrogatePair(value[i], value[i + 1])) continue;
            QuoteSegment(start, i - start);
            output.Append("\\U").Append(char.ConvertToUtf32(value, i).ToString("X8", CultureInfo.InvariantCulture));
            i++;
            start = i + 1;
        }
        QuoteSegment(start, value.Length - start);
        output.Append('"');

        void QuoteSegment(int offset, int length)
        {
            if (length == 0) return;
            string json = JsonSerializer.Serialize(value.Substring(offset, length), StringOptions);
            output.Append(json, 1, json.Length - 2);
        }
    }
}
