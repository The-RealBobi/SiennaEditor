using System.Security.Cryptography;
using CFGBINManager;

namespace SiennaEditor;

internal static partial class LegacyBatch
{
    private enum CfgStringEncoding { Sjis = 0, Utf8 = 1, Utf8_2 = 256, Utf8_3 = 257 }
    private enum CfgHashKind { Crc32Standard, Crc32Jam }
    private sealed record CfgMetadata(CfgStringEncoding Encoding, int ValueWidth, CfgHashKind Hash)
    {
        public void Validate()
        {
            if (ValueWidth is not (4 or 8)) throw new BatchCfgException("value_width must be 4 or 8.");
        }
    }
    private sealed record CfgEntry(string? Name, uint? RawCrc, IReadOnlyList<CfgValue> Values);
    private sealed record BatchRoot(string Alias, string AbsolutePath);
    private sealed record BatchFile(string RootAlias, string RelativePath, string SourceSha256, CfgMetadata Metadata, IReadOnlyList<CfgEntry> Entries);
    private sealed record BatchDocument(IReadOnlyList<BatchRoot> Roots, IReadOnlyList<BatchFile> Files);
    private sealed class BatchCfgException(string message, int? line = null, int? column = null)
        : FormatException(line is null ? message : $"Line {line}, column {column}: {message}");

    internal static async Task<IReadOnlyList<BatchDocumentEntry>> ReadAsync(string text, StructureCatalog catalog,
        Func<string, string, Task<string?>> chooseRoot, CancellationToken token)
    {
        var batch = await Task.Run(() => Parse(text), token);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in batch.Roots)
            if (BatchCfg.NormalizeRelativePath(root.Alias).Contains('/') || !aliases.Add(root.Alias))
                throw new InvalidDataException($"Invalid or repeated root alias: {root.Alias}.");
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in batch.Files)
        {
            if (!sources.Add(file.RootAlias + "/" + BatchCfg.NormalizeRelativePath(file.RelativePath)))
                throw new InvalidDataException($"Repeated source: {file.RelativePath}.");
            if (file.SourceSha256.Length != 64 || !file.SourceSha256.All(Uri.IsHexDigit))
                throw new InvalidDataException($"Invalid source hash: {file.RelativePath}.");
        }
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in batch.Roots.Where(root => batch.Files.Any(file => file.RootAlias == root.Alias)))
        {
            token.ThrowIfCancellationRequested();
            var choice = await chooseRoot(root.Alias, root.AbsolutePath);
            if (choice is null) throw new OperationCanceledException();
            roots.Add(root.Alias, Path.GetFullPath(choice));
        }

        return await Task.Run<IReadOnlyList<BatchDocumentEntry>>(() => batch.Files.Select(file =>
        {
            token.ThrowIfCancellationRequested();
            string relative = BatchCfg.NormalizeRelativePath(file.RelativePath);
            string source = roots[file.RootAlias];
            string mirrorPath = file.RootAlias + "/" + relative;
            try
            {
                foreach (string part in relative.Split('/'))
                {
                    source = Path.Combine(source, part);
                    if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Linked source paths are not supported.");
                }
                byte[] originalBytes = File.ReadAllBytes(source);
                if (!Convert.ToHexString(SHA256.HashData(originalBytes)).Equals(file.SourceSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The original file changed after export (SHA-256 mismatch).");

                var metadata = file.Metadata;
                int encoding = (int)metadata.Encoding;
                bool jam = metadata.Hash == CfgHashKind.Crc32Jam;
                var original = ValidateOriginal(originalBytes, catalog, metadata.ValueWidth, encoding);

                var rows = file.Entries.Select(entry => new CfgRow(entry.Name ?? $"HASH_{entry.RawCrc:X8}", entry.RawCrc ?? 0, [], entry.Values));
                var document = CfgDocument.CreateT2b(catalog, metadata.ValueWidth, encoding, jam, rows);
                _ = document.GetRecordTree();
                return new BatchDocumentEntry(mirrorPath, document)
                {
                    IsModified = original is null ? null : !SameRows(original, document)
                };
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException)
            {
                throw new InvalidDataException($"{mirrorPath}: {error.Message}", error);
            }
        }).ToArray(), token);
    }

    private static bool SameRows(CfgDocument original, CfgDocument edited)
    {
        if (original.Rows.Count != edited.Rows.Count || original.T2bUsesJamCrc != edited.T2bUsesJamCrc)
            return false;
        return original.Rows.Zip(edited.Rows).All(pair => pair.First.NameHash == pair.Second.NameHash
            && pair.First.Values.Count == pair.Second.Values.Count
            && pair.First.Values.Zip(pair.Second.Values).All(values =>
                values.First.Kind == values.Second.Kind && values.First.Bits == values.Second.Bits
                && (values.First.Text ?? "") == (values.Second.Text ?? "")));
    }

    private static CfgDocument? ValidateOriginal(byte[] bytes, StructureCatalog catalog, int width, int encoding)
    {
        if (bytes.Length < 48 || !bytes.AsSpan(bytes.Length - 16, 4).SequenceEqual("\x01t2b"u8))
            throw new InvalidDataException("The original is not a T2B file.");
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(bytes.Length - 10)) != encoding)
            throw new InvalidDataException("The T2B encoding disagrees with the original file.");
        if (TryRead(width) is { } original) return original;
        if (TryRead(width == 4 ? 8 : 4) is not null) throw new InvalidDataException("The T2B value width disagrees with the original file.");
        return null;

        // The old importer used the SHA-verified source only as provenance; some of its native
        // name table layouts are not readable by the current codec. Rebuild those from the v1 rows.
        CfgDocument? TryRead(int candidate)
        {
            try { return BinCodec.Read(bytes, catalog, candidate); }
            catch (InvalidDataException) { return null; }
        }
    }

}
