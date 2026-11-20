using System.Diagnostics;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using CFGBINManager;

namespace SiennaEditor;

public sealed partial class EditorWindow
{
    private void TabContextPressed(object? sender, PointerPressedEventArgs args)
    {
        if (args.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed && DocumentTab(args.Source) is not null)
            args.Handled = true;
    }

    private ListBoxItem? DocumentTab(object? source)
        => (source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } tab && Tabs.IndexFromContainer(tab) >= 0 ? tab : null;

    private void TabContextRequested(object? sender, ContextRequestedEventArgs args)
    {
        if (busy || DocumentTab(args.Source) is not { DataContext: EditorSession session } tab) return;

        var duplicate = new MenuItem { Header = "Duplicate", IsEnabled = session.Document is not null };
        var reveal = new MenuItem
        {
            Header = OperatingSystem.IsMacOS() ? "Show in Finder" : OperatingSystem.IsWindows() ? "Show in File Explorer" : "Show in File Manager",
            IsEnabled = File.Exists(session.SourcePath)
        };
        AutomationProperties.SetAutomationId(duplicate, "duplicate-tab");
        AutomationProperties.SetAutomationId(reveal, "reveal-tab");
        duplicate.Click += async (_, _) => await RunAsync("Duplicate tab", () => DuplicateSessionAsync(session));
        reveal.Click += async (_, _) => await RunAsync("Show file", () => RevealSessionAsync(session));

        var menu = new ContextMenu { Items = { duplicate, reveal } };
        if (!args.TryGetPosition(tab, out _)) menu.Placement = PlacementMode.Bottom;
        args.Handled = true;
        menu.Open(tab);
    }

    private async Task DuplicateSessionAsync(EditorSession session)
    {
        int index = sessions.IndexOf(session);
        if (index < 0 || session.Document is null) return;
        ApplyFields();
        if (ReferenceEquals(session, active)) StoreNavigation();

        var original = session.Document;
        var copy = await Task.Run(() => CfgTextCodec.Read(CfgTextCodec.Write(original), session.Catalog));
        string name = session.SourcePath.Length == 0 ? session.UnsavedName : Path.GetFileNameWithoutExtension(session.SourcePath);
        var duplicate = new EditorSession(session.CatalogRoot, session.Catalog, session.CatalogName)
        {
            Document = copy, UnsavedName = name + " copy", Dirty = true, IsAuthoring = session.IsAuthoring, UseTags = session.UseTags,
            Search = session.Search, NavigationSaved = session.NavigationSaved, SelectedOrdinal = session.SelectedOrdinal
        };
        duplicate.Expanded.UnionWith(session.Expanded);
        sessions.Insert(index + 1, duplicate);
        ActivateSession(duplicate);
    }

    private async Task RevealSessionAsync(EditorSession session)
    {
        string path = session.SourcePath;
        if (!File.Exists(path)) throw new FileNotFoundException("The file is no longer at its original location.", path);

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
        {
            bool mac = OperatingSystem.IsMacOS();
            var start = new ProcessStartInfo(mac ? "/usr/bin/open" : "explorer.exe");
            start.ArgumentList.Add(mac ? "-R" : "/select,");
            start.ArgumentList.Add(path);
            using var process = Process.Start(start) ?? throw new IOException("The file manager did not start.");
            if (mac)
            {
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) throw new IOException("Finder could not reveal the file.");
            }
        }
        else if (!await Launcher.LaunchDirectoryInfoAsync(new FileInfo(path).Directory!))
            throw new IOException("The file manager could not open the containing folder.");
    }
}
