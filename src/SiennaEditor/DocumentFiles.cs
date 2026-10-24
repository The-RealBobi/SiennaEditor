using System.Diagnostics;
using System.Text;
using CFGBINManager;

namespace SiennaEditor;

internal sealed record BatchProgress(int Visited, int Total, string File);
internal sealed record BatchResult(int Saved, int Skipped, int Visited, int Total, IReadOnlyList<string> Failures, bool Cancelled)
{
    public override string ToString() => $"{(Cancelled ? "Cancelled" : "Finished")}: {Saved:N0} exported, {Skipped:N0} skipped, {Failures.Count:N0} failed ({Visited:N0} / {Total:N0} files).";
}

internal static class DocumentFiles
{
    public static string DataDirectory()
    {
        string? executable = Environment.ProcessPath;
        string applicationDirectory = executable is null || Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? AppContext.BaseDirectory : Path.GetDirectoryName(executable)!;
        string resources = Path.GetFullPath(Path.Combine(applicationDirectory, "..", "Resources"));
        return Path.Combine(OperatingSystem.IsMacOS() && Directory.Exists(resources) ? resources : applicationDirectory, "data");
    }

    public static bool IsBinaryFile(string path) => !path.EndsWith(".lua.bin", StringComparison.OrdinalIgnoreCase)
        && (Path.GetExtension(path).EndsWith("bin", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".fxbiny", StringComparison.OrdinalIgnoreCase));

    public static CfgDocument Read(string path, StructureCatalog catalog)
    {
        if (!IsBinaryFile(path) && !Path.GetExtension(path).Equals(".cfg", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a supported BIN or CFG file. Lua bytecode is not supported.");
        var document = Path.GetExtension(path).Equals(".cfg", StringComparison.OrdinalIgnoreCase)
            ? CfgTextCodec.Read(File.ReadAllText(path, Encoding.UTF8), catalog) : BinCodec.Read(File.ReadAllBytes(path), catalog);
        _ = document.GetRecordTree();
        return document;
    }

    public static IReadOnlyList<MergeKey> MergeKeys(CfgDocument file1, CfgDocument file2)
        => CfgMergePlan.GetKeyOptions(file1, file2).Where(option => option.SuggestedPosition.HasValue)
            .Select(option => new MergeKey(option.Scope, option.Entry, option.SuggestedPosition!.Value)).ToArray();

    public static byte[] Export(CfgDocument document)
    {
        string text = CfgTextCodec.WriteReadable(document);
        _ = CfgTextCodec.Read(text, document.Catalog).GetRecordTree();
        return Encoding.UTF8.GetBytes(text);
    }

    public static byte[] Import(CfgDocument document)
    {
        byte[] bytes = BinCodec.Write(document);
        _ = BinCodec.Read(bytes, document.Catalog).GetRecordTree();
        return bytes;
    }

    public static void WriteNew(string path, byte[] bytes)
    {
        path = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static BatchResult ExportBatch(string source, string destination, bool recursive, StructureCatalog catalog,
        IReadOnlySet<string> protectedPaths, Action<BatchProgress> progress, CancellationToken token)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (pathComparer.Equals(destination, Path.TrimEndingDirectorySeparator(Path.GetFullPath(DataDirectory()))))
            throw new IOException("The output folder is reserved for Structure catalogs.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string outputPrefix = Path.TrimEndingDirectorySeparator(destination) + Path.DirectorySeparatorChar;
        bool nestedOutput = !destination.Equals(source, comparison) && outputPrefix.StartsWith(Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar, comparison);
        var files = Directory.EnumerateFiles(source, "*", new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(IsBinaryFile).Where(path => !nestedOutput || !Path.GetFullPath(path).StartsWith(outputPrefix, comparison)).Order(StringComparer.Ordinal).ToArray();
        var targets = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var failures = new List<string>(); int saved = 0, skipped = 0, visited = 0;
        long lastUpdate = Stopwatch.GetTimestamp();
        foreach (string file in files)
        {
            if (token.IsCancellationRequested) break;
            string relative = Path.GetRelativePath(source, file);
            string name = Path.GetFileNameWithoutExtension(relative);
            string target = Path.Combine(destination, Path.GetDirectoryName(relative) ?? "", name.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) ? name : name + ".cfg");
            try
            {
                if (!targets.Add(target)) throw new IOException("Another input produces the same CFG filename.");
                if (File.Exists(target) || protectedPaths.Contains(target)) skipped++;
                else
                {
                    byte[] bytes = Export(Read(file, catalog));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    WriteNew(target, bytes); saved++;
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
            { failures.Add(relative + ": " + error.Message); }
            visited++;
            if (Stopwatch.GetElapsedTime(lastUpdate).TotalMilliseconds >= 150 || visited == files.Length)
            { progress(new(visited, files.Length, relative)); lastUpdate = Stopwatch.GetTimestamp(); }
        }
        return new(saved, skipped, visited, files.Length, failures.AsReadOnly(), token.IsCancellationRequested);
    }
}
