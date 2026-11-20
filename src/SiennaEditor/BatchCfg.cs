using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CFGBINManager;

namespace SiennaEditor;

internal sealed record BatchDocumentEntry(string RelativePath, CfgDocument Document)
{
    public bool? IsModified { get; init; }
}

internal static class BatchCfg
{
    public const string Header = "L5-CFGBATCH 2";
    public const string LegacyHeader = "L5-CFGBATCH 1";
    private sealed record FileHeader(string Path, int Catalog)
    {
        public string? BaselineSha256 { get; init; }
    }
    private static readonly JsonSerializerOptions HeaderJson = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, RespectRequiredConstructorParameters = true };
    private static readonly DocumentExportFormat[] Formats = Enum.GetValues<DocumentExportFormat>();

    public static string Write(IReadOnlyList<BatchDocumentEntry> entries, CancellationToken token)
    {
        if (entries.Count == 0) throw new InvalidDataException("Select at least one document.");
        var output = new StringBuilder(Header + "\n");
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var catalogs = new Dictionary<StructureCatalog, int>(ReferenceEqualityComparer.Instance);
        var declarations = new Dictionary<(string? Profile, string Text), int>();
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (!catalogs.TryGetValue(entry.Document.Catalog, out int catalogIndex))
            {
                var key = (Profile: entry.Document.Catalog.ProfileName, Text: entry.Document.Catalog.ToText());
                if (!declarations.TryGetValue(key, out catalogIndex))
                {
                    catalogIndex = declarations.Count;
                    declarations.Add(key, catalogIndex);
                    output.Append("\ncatalog ").Append(JsonSerializer.Serialize(key.Profile)).Append('\n')
                        .Append(key.Text.TrimEnd('\r', '\n')).Append("\nendcatalog\n");
                }
                catalogs.Add(entry.Document.Catalog, catalogIndex);
            }

            string requested = NormalizeRelativePath(entry.RelativePath);
            if (requested.Contains('/')) throw new InvalidDataException("Use only the document name when creating a batchCFG.");
            string name = requested;
            string extension = requested.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase) ? requested[^8..] : Path.GetExtension(requested);
            string stem = requested[..(requested.Length - extension.Length)];
            for (int i = 2; OutputNames(name).Any(usedNames.Contains); i++) name = $"{stem}-{i}{extension}";
            usedNames.UnionWith(OutputNames(name));
            string cfg = CfgTextCodec.WriteReadable(entry.Document);
            output.Append("\nfile ").Append(JsonSerializer.Serialize(new FileHeader(name, catalogIndex) { BaselineSha256 = Fingerprint(CfgTextCodec.Read(cfg, entry.Document.Catalog)) }, HeaderJson)).Append('\n')
                .Append(cfg.TrimEnd('\r', '\n')).Append("\nendfile\n");
        }
        return output.ToString();

        static IEnumerable<string> OutputNames(string name) => Formats.Select(format => NameKey(DocumentFiles.ExportFileName(name, format)));
    }

    public static IReadOnlyList<BatchDocumentEntry> Read(string text, CancellationToken token)
    {
        using var input = new StringReader(text);
        if (input.ReadLine()?.TrimStart('\uFEFF').TrimEnd() != Header)
            throw new InvalidDataException("Unsupported batchCFG version.");
        var catalogs = new List<StructureCatalog>();
        var entries = new List<BatchDocumentEntry>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (line.StartsWith("catalog ", StringComparison.Ordinal))
                {
                    string? profile = JsonSerializer.Deserialize<string>(line.AsSpan(8));
                    var root = StructureCatalog.Parse(ReadBlock("endcatalog"));
                    if (profile is null && root.ProfileNames.Count != 0)
                        throw new InvalidDataException("A catalog with game profiles must name the selected profile.");
                    catalogs.Add(profile is null ? root : root.ForProfile(profile));
                }
                else if (line.StartsWith("file ", StringComparison.Ordinal))
                {
                    var file = JsonSerializer.Deserialize<FileHeader>(line.AsSpan(5), HeaderJson)
                        ?? throw new InvalidDataException("Missing batchCFG file header.");
                    if (file.Path is null || (uint)file.Catalog >= catalogs.Count)
                        throw new InvalidDataException("A batchCFG file needs a path and a preceding catalog.");
                    string path = NormalizeRelativePath(file.Path);
                    if (!paths.Add(NameKey(path))) throw new InvalidDataException($"Duplicate batchCFG path: {path}.");
                    try
                    {
                        var document = CfgTextCodec.Read(ReadBlock("endfile"), catalogs[file.Catalog]);
                        _ = document.GetRecordTree();
                        if (file.BaselineSha256 is { } baseline && (baseline.Length != 64 || !baseline.All(Uri.IsHexDigit)))
                            throw new InvalidDataException("Invalid change-tracking fingerprint.");
                        entries.Add(new(path, document)
                        {
                            IsModified = file.BaselineSha256 is { } hash
                                ? !string.Equals(hash, Fingerprint(document), StringComparison.OrdinalIgnoreCase)
                                : null
                        });
                    }
                    catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException or OverflowException)
                    {
                        throw new InvalidDataException($"{path}: {error.Message}", error);
                    }
                }
                else throw new InvalidDataException("Expected a catalog or file block in batchCFG.");
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException)
            {
                throw new InvalidDataException("Invalid batchCFG header: " + error.Message, error);
            }
        }
        if (entries.Count == 0) throw new InvalidDataException("The batchCFG does not contain any documents.");
        return entries;

        string ReadBlock(string terminator)
        {
            var block = new StringBuilder();
            while (input.ReadLine() is { } content)
            {
                token.ThrowIfCancellationRequested();
                if (content.TrimEnd() == terminator) return block.ToString();
                block.AppendLine(content);
            }
            throw new InvalidDataException($"A batchCFG block is missing {terminator}.");
        }
    }

    public static async Task<int> ExtractAsync(IReadOnlyList<BatchDocumentEntry> entries, string destination, DocumentExportFormat format,
        Func<IReadOnlyList<string>, Task<bool>> confirmOverwrite, CancellationToken token)
    {
        if (entries.Count == 0) throw new InvalidDataException("The batchCFG does not contain any documents.");
        destination = ResolveDirectory(destination);
        string data = DocumentFiles.DataDirectory();
        data = Directory.Exists(data) ? ResolveDirectory(data) : Path.GetFullPath(data);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputs = new List<(BatchDocumentEntry Entry, string Target, bool Replace)>(entries.Count);
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            string relative = NormalizeRelativePath(entry.RelativePath);
            var parts = relative.Split('/');
            parts[^1] = NormalizeRelativePath(DocumentFiles.ExportFileName(parts[^1], format));
            string target = Path.Combine(destination, Path.Combine(parts));
            string key = NameKey(target);
            if (!targets.Add(key) || directories.Contains(key))
                throw new IOException($"More than one batch document uses the destination '{string.Join('/', parts)}'.");
            if (string.Equals(target, data, StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(Path.TrimEndingDirectorySeparator(data) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The output folder is reserved for Structure catalogs.");
            for (string parent = Path.GetDirectoryName(target)!; parent != destination; parent = Path.GetDirectoryName(parent)!)
            {
                if (targets.Contains(NameKey(parent)))
                    throw new IOException($"A batch document would replace a folder needed by '{string.Join('/', parts)}'.");
                directories.Add(NameKey(parent));
            }
            var attributes = ValidateDestination(target, destination, allowExistingTarget: true);
            if (attributes?.HasFlag(FileAttributes.Directory) == true)
                throw new IOException($"A folder already uses this output name: {target}");
            outputs.Add((entry, target, attributes.HasValue));
        }

        var replacements = outputs.Where(output => output.Replace).Select(output => Path.GetRelativePath(destination, output.Target)).ToArray();
        if (replacements.Length != 0 && !await confirmOverwrite(replacements).ConfigureAwait(false))
            throw new OperationCanceledException();
        token.ThrowIfCancellationRequested();

        string staging = Path.Combine(destination, ".sienna-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var fingerprints = new byte[outputs.Count][];
        bool preserveStaging = false;
        try
        {
            for (int i = 0; i < outputs.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var output = outputs[i];
                try
                {
                    byte[] bytes = DocumentFiles.EncodeExport(output.Entry.Document, format);
                    fingerprints[i] = SHA256.HashData(bytes);
                    DocumentFiles.WriteNew(StagedFile(i), bytes);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
                {
                    throw new IOException($"{output.Entry.RelativePath}: {error.Message}", error);
                }
            }
            token.ThrowIfCancellationRequested();
            var createdDirectories = new List<string>();
            int published = 0;
            try
            {
                foreach (var output in outputs) CheckOutput(output.Target, output.Replace);
                foreach (var output in outputs)
                {
                    CreateParent(Path.GetDirectoryName(output.Target)!);
                    CheckOutput(output.Target, output.Replace);
                    if (output.Replace) File.Replace(StagedFile(published), output.Target, BackupFile(published));
                    else File.Move(StagedFile(published), output.Target, overwrite: false);
                    published++;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                try
                {
                    for (int i = 0; i < published; i++)
                    {
                        string target = outputs[i].Target;
                        ValidateDestination(target, destination, allowExistingTarget: true);
                        if (File.Exists(target))
                        {
                            byte[] current;
                            using (var stream = File.OpenRead(target)) current = SHA256.HashData(stream);
                            if (!current.AsSpan().SequenceEqual(fingerprints[i]))
                                throw new IOException($"A published file was changed during the export: {target}");
                        }
                        if (outputs[i].Replace) File.Move(BackupFile(i), target, overwrite: true);
                        else File.Delete(target);
                    }
                    foreach (string directory in createdDirectories.AsEnumerable().Reverse())
                    {
                        ValidateDestination(directory, destination, allowExistingTarget: true);
                        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                    }
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    preserveStaging = true;
                    throw new IOException($"Export failed: {error.Message} Could not restore every output: {rollbackError.Message} Recovery files: {staging}", error);
                }
                if (published < outputs.Count && File.Exists(BackupFile(published)))
                {
                    preserveStaging = true;
                    throw new IOException($"Export failed: {error.Message} Recovery files: {staging}", error);
                }
                throw;
            }
            return published;

            void CreateParent(string parent)
            {
                var parents = new Stack<string>();
                while (!Directory.Exists(parent))
                {
                    parents.Push(parent);
                    parent = Path.GetDirectoryName(parent)!;
                }
                while (parents.TryPop(out string? directory))
                {
                    Directory.CreateDirectory(directory);
                    createdDirectories.Add(directory);
                }
            }
        }
        finally { if (!preserveStaging) Directory.Delete(staging, recursive: true); }

        void CheckOutput(string target, bool replace)
        {
            var attributes = ValidateDestination(target, destination, allowExistingTarget: replace);
            if (replace && (!attributes.HasValue || attributes.Value.HasFlag(FileAttributes.Directory)))
                throw new IOException($"The file selected for replacement has changed: {target}");
        }

        string BackupFile(int index) => StagedFile(index) + ".original";
        string StagedFile(int index) => Path.Combine(staging, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static string NormalizeRelativePath(string path)
    {
        string[] parts = path.Split('/');
        foreach (string part in parts)
        {
            string first = part.Split('.')[0].TrimEnd().ToUpperInvariant();
            bool reserved = first is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || first.Length == 4 && (first.StartsWith("COM", StringComparison.Ordinal) || first.StartsWith("LPT", StringComparison.Ordinal))
                    && "123456789¹²³".Contains(first[3]);
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || reserved
                || part.Any(character => char.IsControl(character) || "\\:\"<>|?*".Contains(character)))
                throw new InvalidDataException($"Unsafe batchCFG path: '{path}'. Use relative names separated by '/'.");
        }
        return path;
    }

    private static string Fingerprint(CfgDocument document) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(CfgTextCodec.Write(document).ReplaceLineEndings("\n"))));

    private static string NameKey(string name) => name.Normalize(NormalizationForm.FormC);

    private static string ResolveDirectory(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (!directory.Exists) throw new DirectoryNotFoundException($"Folder does not exist: {path}");
        return Path.TrimEndingDirectorySeparator(Resolve(directory));
        static string Resolve(DirectoryInfo folder)
        {
            // Windows link queries do not accept a volume or share root.
            if (folder.Parent is not { } parent) return folder.FullName;
            if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint)
                && folder.ResolveLinkTarget(returnFinalTarget: true) is DirectoryInfo resolved)
                return Resolve(resolved);
            return Path.Combine(Resolve(parent), folder.Name);
        }
    }

    private static FileAttributes? ValidateDestination(string target, string root, bool allowExistingTarget)
    {
        if (!Directory.Exists(root)
            || Path.GetDirectoryName(root) is not null && File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"The output folder has changed: {root}");
        FileAttributes? targetAttributes = null;
        for (string path = target; path != root; path = Path.GetDirectoryName(path)!)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"Cannot export through a linked path: {path}");
            if (path == target ? !allowExistingTarget : !attributes.HasFlag(FileAttributes.Directory))
                throw new IOException($"The destination already exists: {path}");
            if (path == target) targetAttributes = attributes;
        }
        return targetAttributes;
    }
}
