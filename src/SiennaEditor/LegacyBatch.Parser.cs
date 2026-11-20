// Adapted from CfgBinBatchEditor, GPL-3.0-or-later.
using CFGBINManager;
using System.Globalization;
using System.Text;

namespace SiennaEditor;

internal static partial class LegacyBatch
{
    private static BatchDocument Parse(string text) => new Parser(text).Parse();

    private sealed class Parser(string text)
    {
        private readonly Lexer _lexer = new(text);
        public BatchDocument Parse()
        {
            var header = _lexer.Next();
            if (header.Kind != TokenKind.Identifier || header.Text != "L5-CFGBATCH") Error("Expected L5-CFGBATCH header.", header);
            var version = _lexer.Next();
            if (version.Kind != TokenKind.Number || version.Text != "1") Error("Only L5-CFGBATCH version 1 is supported.", version);
            var roots = new List<BatchRoot>(); var files = new List<BatchFile>();
            while (_lexer.Peek().Kind != TokenKind.End)
            {
                var keyword = ExpectIdentifier();
                if (keyword.Text == "root") roots.Add(ParseRoot());
                else if (keyword.Text == "file") files.Add(ParseFile());
                else Error($"Unexpected token '{keyword.Text}'.", keyword);
            }
            if (files.Count == 0) throw new BatchCfgException("The batch has no files.");
            foreach (var file in files)
            {
                if (!roots.Any(root => root.Alias == file.RootAlias)) throw new BatchCfgException($"Unknown root alias '{file.RootAlias}'.");
            }
            return new BatchDocument(roots, files);
        }
        private BatchRoot ParseRoot()
        {
            var alias = Expect(TokenKind.String).Text; var path = Expect(TokenKind.String).Text;
            return new BatchRoot(alias, path);
        }
        private BatchFile ParseFile()
        {
            var root = Expect(TokenKind.String).Text; var relative = Expect(TokenKind.String).Text; Expect("{");
            ExpectIdentifier("format"); var encoding = ParseNamed("encoding"); var width = ParseNamed("value_width"); var hash = ParseNamed("hash");
            var metadata = new CfgMetadata(ParseEncoding(encoding), int.Parse(width, CultureInfo.InvariantCulture), ParseHash(hash)); metadata.Validate();
            ExpectIdentifier("source_sha256"); var sha = Expect(TokenKind.String).Text;
            var entries = new List<CfgEntry>();
            while (!Try("}")) { ExpectIdentifier("entry"); entries.Add(ParseEntry(metadata.ValueWidth)); }
            return new BatchFile(root, relative, sha, metadata, entries);
        }
        private CfgEntry ParseEntry(int width)
        {
            string? name = null; uint? crc = null; var identity = _lexer.Next();
            if (identity.Kind == TokenKind.String) name = identity.Text;
            else if (identity.Kind == TokenKind.Identifier && identity.Text == "crc32") { Expect("("); var hex = _lexer.Next(); if (!hex.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || !uint.TryParse(hex.Text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)) throw new BatchCfgException("Invalid CRC32 value.", hex.Line, hex.Column); crc = parsed; Expect(")"); }
            else Error("Expected entry name or crc32(0x...).", identity);
            Expect("{"); var values = new List<CfgValue>();
            while (!Try("}"))
            {
                var kind = ExpectIdentifier(); var value = _lexer.Next();
                values.Add(kind.Text switch
                {
                    "string" when value.Kind == TokenKind.String => CfgValue.String(value.Text),
                    "int" when long.TryParse(value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => CfgValue.Signed(integer),
                    "float" when double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating) => width == 4 ? CfgValue.Float((float)floating) : CfgValue.Double(floating),
                    _ => throw new BatchCfgException($"Invalid {kind.Text} value.", value.Line, value.Column)
                });
            }
            return new CfgEntry(name, crc, values);
        }
        private string ParseNamed(string name) { ExpectIdentifier(name); Expect("="); return _lexer.Next().Text; }
        private static CfgStringEncoding ParseEncoding(string value) => value switch { "sjis" => CfgStringEncoding.Sjis, "utf8" => CfgStringEncoding.Utf8, "utf8_2" => CfgStringEncoding.Utf8_2, "utf8_3" => CfgStringEncoding.Utf8_3, _ => throw new BatchCfgException($"Unknown encoding '{value}'.") };
        private static CfgHashKind ParseHash(string value) => value switch { "crc32_standard" => CfgHashKind.Crc32Standard, "crc32_jam" => CfgHashKind.Crc32Jam, _ => throw new BatchCfgException($"Unknown hash '{value}'.") };
        private Token ExpectIdentifier(string? expected = null) { var token = Expect(TokenKind.Identifier); if (expected is not null && token.Text != expected) Error($"Expected '{expected}'.", token); return token; }
        private Token Expect(TokenKind kind) { var token = _lexer.Next(); if (token.Kind != kind) Error($"Expected {kind}.", token); return token; }
        private void Expect(string text) { var token = _lexer.Next(); if (token.Kind != TokenKind.Symbol || token.Text != text) Error($"Expected '{text}'.", token); }
        private bool Try(string text) { if (_lexer.Peek() is not { Kind: TokenKind.Symbol } token || token.Text != text) return false; _lexer.Next(); return true; }
        private static void Error(string message, Token token) => throw new BatchCfgException(message, token.Line, token.Column);
    }
    private enum TokenKind { Identifier, String, Number, Symbol, End }
    private readonly record struct Token(TokenKind Kind, string Text, int Line, int Column);
    private sealed class Lexer(string text)
    {
        private int _index; private int _line = 1; private int _column = 1; private Token? _peek;
        public Token Peek() => _peek ??= Read(); public Token Next() { var token = Peek(); _peek = null; return token; }
        private Token Read()
        {
            Skip(); if (_index >= text.Length) return new(TokenKind.End, string.Empty, _line, _column); var line = _line; var column = _column; var current = text[_index];
            if ("{}()=".Contains(current)) { Advance(); return new(TokenKind.Symbol, current.ToString(), line, column); }
            if (current == '"') return ReadString(line, column);
            var start = _index; while (_index < text.Length && !char.IsWhiteSpace(text[_index]) && !"{}()=\";".Contains(text[_index])) Advance(); if (_index == start) throw new BatchCfgException("Unexpected character.", line, column); var value = text[start.._index];
            return new(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || char.IsDigit(value.FirstOrDefault()) || value.StartsWith('-') ? TokenKind.Number : TokenKind.Identifier, value, line, column);
        }
        private Token ReadString(int line, int column)
        {
            Advance(); var builder = new StringBuilder(); while (_index < text.Length && text[_index] != '"') { var c = text[_index]; Advance(); if (c != '\\') { builder.Append(c); continue; } if (_index >= text.Length) break; c = text[_index]; Advance(); builder.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '\\' => '\\', '"' => '"', _ => throw new BatchCfgException($"Unknown escape \\{c}.", _line, _column) }); }
            if (_index >= text.Length) throw new BatchCfgException("Unterminated string.", line, column); Advance(); return new(TokenKind.String, builder.ToString(), line, column);
        }
        private void Skip() { while (_index < text.Length) { if (char.IsWhiteSpace(text[_index])) { Advance(); continue; } if (text[_index] == ';') { while (_index < text.Length && text[_index] != '\n') Advance(); continue; } if (text[_index] == '/' && _index + 1 < text.Length && text[_index + 1] == '/') { while (_index < text.Length && text[_index] != '\n') Advance(); continue; } break; } }
        private void Advance() { if (text[_index++] == '\n') { _line++; _column = 1; } else _column++; }
    }
}
