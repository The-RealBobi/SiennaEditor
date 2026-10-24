using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CFGBINManager;

namespace SiennaEditor;

internal static class CommandLine
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly HashSet<string> Commands = ["inspect", "export", "import", "export-batch", "merge"];

    public static bool IsCommand(string[] args) => args.Length > 0 && (Commands.Contains(args[0]) || args[0].StartsWith('-')
        || !File.Exists(args[0]) && Path.GetExtension(args[0]).Length == 0);

    public static int Run(string[] args)
    {
        ConnectConsole();
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (args is ["--version"]) { Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3)); return 0; }
            if (args.Any(arg => arg is "--help" or "-h")) { Console.WriteLine(Help); return 0; }
            if (!Commands.Contains(args[0])) throw new ArgumentException("Unknown command. Use --help to list commands.");
            string command = args[0];
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var paths = new List<string>(); bool recursive = false, endOptions = false;
            var allowed = command switch
            {
                "export-batch" => new[] { "--structure", "--profile", "--output" },
                "merge" => new[] { "--structure", "--profile", "--output", "--report", "--decisions" },
                _ => new[] { "--structure", "--profile" }
            };
            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];
                if (!endOptions && arg == "--") { endOptions = true; continue; }
                if (!endOptions && arg == "--recursive" && command == "export-batch")
                { if (recursive) throw new ArgumentException("Duplicate --recursive option."); recursive = true; continue; }
                if (!endOptions && arg.StartsWith('-'))
                {
                    if (!allowed.Contains(arg)) throw new ArgumentException("Unknown option: " + arg);
                    if (++i == args.Length || args[i].StartsWith("--")) throw new ArgumentException("Missing value for " + arg);
                    if (!values.TryAdd(arg, args[i])) throw new ArgumentException("Duplicate option: " + arg);
                }
                else paths.Add(Path.GetFullPath(arg));
            }
            int count = command is "export" or "import" or "merge" ? 2 : 1;
            if (paths.Count != count) throw new ArgumentException($"{command} requires {count} path argument(s). Use --help for syntax.");
            var catalog = LoadCatalog(values.GetValueOrDefault("--structure"), values.GetValueOrDefault("--profile"));
            var protectedPaths = Directory.Exists(DocumentFiles.DataDirectory())
                ? Directory.EnumerateFiles(DocumentFiles.DataDirectory(), "*", SearchOption.TopDirectoryOnly).Select(Path.GetFullPath).ToHashSet(PathComparer)
                : new HashSet<string>(PathComparer);
            if (values.TryGetValue("--structure", out string? structure)) protectedPaths.Add(Path.GetFullPath(structure));
            void Write(string path, byte[] bytes)
            {
                path = Path.GetFullPath(path);
                if (protectedPaths.Contains(path) || PathComparer.Equals(Path.GetDirectoryName(path), Path.GetFullPath(DocumentFiles.DataDirectory())))
                    throw new IOException("The destination is reserved for Structure catalogs. Choose another folder.");
                DocumentFiles.WriteNew(path, bytes);
            }
            if (command == "export-batch")
            {
                string destination = values.GetValueOrDefault("--output") ?? paths[0];
                var result = DocumentFiles.ExportBatch(paths[0], destination, recursive, catalog, protectedPaths,
                    update => Console.Error.WriteLine($"{update.Visited}/{update.Total} {update.File}"), cancellation.Token);
                Console.WriteLine(result);
                foreach (string failure in result.Failures) Console.Error.WriteLine("[EXPORT] " + failure);
                return result.Cancelled ? 130 : result.Failures.Count > 0 ? 1 : 0;
            }
            var document = DocumentFiles.Read(paths[0], catalog);
            if (command == "inspect")
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    document.Format, Profile = catalog.ProfileName, Rows = (document.Format == BinaryFormat.T2b ? document.Rows : document.Lists.SelectMany(list => list.Rows)).Count(),
                    Entries = (document.Format == BinaryFormat.T2b ? document.Rows : document.Lists.SelectMany(list => list.Rows)).GroupBy(row => row.Name).Select(group => new { Name = group.Key, Count = group.Count() })
                }, JsonOptions));
                return 0;
            }
            if (command is "export" or "import")
            {
                if (command == "export" ? !paths[1].EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) : !DocumentFiles.IsBinaryFile(paths[1]))
                    throw new ArgumentException(command == "export" ? "Export output must end in .cfg." : "Import output must use a supported BIN extension.");
                Write(paths[1], command == "export" ? DocumentFiles.Export(document) : DocumentFiles.Import(document));
                Console.WriteLine("Created " + paths[1]); return 0;
            }
            var file2 = DocumentFiles.Read(paths[1], catalog);
            var keys = DocumentFiles.MergeKeys(document, file2);
            var plan = CfgMergePlan.Analyze(document, file2, keys);
            string identity = JsonSerializer.Serialize(new
            {
                File1 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paths[0]))),
                File2 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paths[1]))),
                Catalog = catalog.ToText(), catalog.ProfileName, Keys = keys, plan.Conflicts
            }, JsonOptions);
            string planId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            string report = JsonSerializer.Serialize(new
            {
                PlanId = planId, File1 = paths[0], File2 = paths[1], Keys = keys, plan.MatchedRows,
                File1OnlyRows = plan.CurrentOnlyRows, File2OnlyRows = plan.AddedRows,
                Conflicts = plan.Conflicts.Select(conflict => new
                {
                    conflict.Id, conflict.Location, conflict.Field, File1 = conflict.Current, File2 = conflict.Incoming,
                    File1Target = conflict.CurrentTarget, File2Target = conflict.IncomingTarget
                }),
                Decisions = plan.Conflicts.ToDictionary(conflict => conflict.Id.ToString(CultureInfo.InvariantCulture), _ => (string?)null),
                Version = typeof(Program).Assembly.GetName().Version?.ToString(3)
            }, JsonOptions);
            string? output = values.GetValueOrDefault("--output"), reportPath = values.GetValueOrDefault("--report");
            if (values.ContainsKey("--decisions") && output is null) throw new ArgumentException("--decisions requires --output.");
            if (reportPath is not null && (output is not null && PathComparer.Equals(Path.GetFullPath(reportPath), Path.GetFullPath(output))
                || values.TryGetValue("--decisions", out string? decisionsPath) && PathComparer.Equals(Path.GetFullPath(reportPath), Path.GetFullPath(decisionsPath))))
                throw new ArgumentException("Report, decisions and binary output must have different destinations.");
            if (reportPath is not null) Write(reportPath, Encoding.UTF8.GetBytes(report));
            else if (output is null) Console.WriteLine(report);
            if (output is null) return 0;
            if (!DocumentFiles.IsBinaryFile(output)) throw new ArgumentException("Merged output must use a supported BIN extension.");
            var resolutions = values.TryGetValue("--decisions", out string? decisionFile)
                ? ReadDecisions(decisionFile, planId, plan) : new Dictionary<int, MergeSide>();
            if (resolutions.Count != plan.Conflicts.Count)
            { Console.Error.WriteLine("[MERGE] Unresolved conflicts. Export a --report, fill Decisions with File 1 or File 2, then pass it with --decisions."); return 3; }
            if (cancellation.IsCancellationRequested) return 130;
            Write(output, DocumentFiles.Import(plan.Build(resolutions)));
            Console.WriteLine("Created " + Path.GetFullPath(output)); return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException or OverflowException or JsonException)
        { Console.Error.WriteLine("[CLI] " + error.Message); return error is ArgumentException ? 2 : 1; }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StructureCatalog LoadCatalog(string? path, string? profile)
    {
        string text;
        if (path is not null) text = File.ReadAllText(path, Encoding.UTF8);
        else
        {
            string data = DocumentFiles.DataDirectory();
            string preferred = Path.Combine(data, "structure.cfg");
            if (File.Exists(preferred)) text = File.ReadAllText(preferred, Encoding.UTF8);
            else
            {
                string[] candidates = Directory.Exists(data) ? Directory.EnumerateFiles(data).Where(file => file.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)).ToArray() : [];
                if (candidates.Length > 1) throw new ArgumentException("Multiple catalogs found. Choose one using --structure.");
                if (candidates.Length == 1) text = File.ReadAllText(candidates[0], Encoding.UTF8);
                else
                {
                    using var stream = typeof(Program).Assembly.GetManifestResourceStream("structure.cfg") ?? throw new IOException("The default Structure is missing.");
                    using var reader = new StreamReader(stream, Encoding.UTF8, true); text = reader.ReadToEnd();
                }
            }
        }
        var root = StructureCatalog.Parse(text);
        if (profile is not null)
        {
            if (!root.ProfileNames.Contains(profile, StringComparer.Ordinal))
                throw new ArgumentException("Unknown profile '" + profile + "'. Available profiles: " + string.Join(", ", root.ProfileNames));
            return root.ForProfile(profile);
        }
        if (root.ProfileNames.Count > 1) throw new ArgumentException("This catalog contains several games. Select one using --profile: " + string.Join(", ", root.ProfileNames));
        return root.ProfileNames.Count == 1 ? root.ForProfile(root.ProfileNames[0]) : root;
    }

    private static Dictionary<int, MergeSide> ReadDecisions(string path, string planId, CfgMergePlan plan)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("PlanId", out var identity) || identity.ValueKind != JsonValueKind.String || identity.GetString() != planId)
            throw new InvalidDataException("The decisions do not belong to this merge analysis. Generate a fresh report.");
        if (!root.TryGetProperty("Decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The report must contain a Decisions object.");
        var result = new Dictionary<int, MergeSide>();
        var ids = plan.Conflicts.Select(conflict => conflict.Id).ToHashSet();
        foreach (var decision in decisions.EnumerateObject())
        {
            if (!int.TryParse(decision.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int id) || !ids.Contains(id))
                throw new InvalidDataException("Unknown conflict ID: " + decision.Name);
            if (decision.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Choose File 1 or File 2 for conflict " + id);
            var side = decision.Value.GetString() switch { "File 1" => MergeSide.Current, "File 2" => MergeSide.Incoming, _ => throw new InvalidDataException("Choose File 1 or File 2 for conflict " + id) };
            if (!result.TryAdd(id, side)) throw new InvalidDataException("Duplicate conflict ID: " + id);
        }
        return result;
    }

    private static void ConnectConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        nint output = GetStdHandle(-11), error = GetStdHandle(-12);
        bool outputRedirected = GetFileType(output) is 1 or 3, errorRedirected = GetFileType(error) is 1 or 3;
        // Console attachment can replace inherited file/pipe handles from native launchers.
        _ = AttachConsole(uint.MaxValue);
        if (outputRedirected) _ = SetStdHandle(-11, output);
        if (errorRedirected) _ = SetStdHandle(-12, error);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int handle, nint value);

    private const string Help = """
        Sienna Editor
        Without a command, opens the GUI. A BIN/CFG path opens it in the editor.

        inspect <input>                         Print format and entry counts as JSON
        export <input> <output.cfg>              Export readable CFG
        import <input.cfg> <output.bin>          Rebuild binary from CFG
        export-batch <folder> [--recursive] [--output <folder>]
        merge <file1> <file2> [--report <report.json>]
        merge <file1> <file2> --decisions <report.json> --output <result.bin>

        Options: --structure <catalog.cfg> --profile <game>
        --help / -h: Show help. --version: Print version. --: End options.
        Merge report defaults to stdout. Fill its Decisions with "File 1" or "File 2".
        File 1 ordering is retained. Entries exclusive to either file are kept.
        Outputs never overwrite existing files. Batch skips existing CFG outputs.
        Exit codes: 0 success, 1 operation failed, 2 invalid arguments,
                    3 unresolved merge conflicts, 130 cancelled batch/merge.
        """;
}
