using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CFGBINManager;
using Avalonia.Platform.Storage;

namespace SiennaEditor;

public sealed partial class EditorWindow
{
    private readonly ComboBox Profiles = new() { MinWidth = 260, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private Window? settingsWindow;

    private async void SettingsClicked(object? sender, RoutedEventArgs args)
    {
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        var dialog = new Window
        {
            Title = "Settings", Width = 570, Height = 540, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background, Foreground = Foreground, FontFamily = FontFamily, FontSize = FontSize
        };
        settingsWindow = dialog;
        var tags = new CheckBox { Content = "Use field tags", IsChecked = active.UseTags };
        bool restoringTags = false;
        tags.IsCheckedChanged += async (_, _) =>
        {
            if (restoringTags) return;
            await RunAsync("Field tags", () => { ApplyFields(); active.UseTags = tags.IsChecked == true; BuildFields(); return Task.CompletedTask; });
            restoringTags = true; tags.IsChecked = active.UseTags; restoringTags = false;
        };
        var load = new Button { Content = "Add Structures…" };
        var reload = new Button { Content = "Reload Structures" };
        ToolTip.SetTip(reload, "Read Structure catalogs from data and apply them to every open file. Unsaved document edits are preserved; Undo and Redo history is cleared.");
        var structure = new TextBlock { Text = ActiveStructureDescription(), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.7 };
        var schema = new Button { Content = "Inspect native schema…", IsEnabled = document is not null };
        var settingsStatus = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, MaxHeight = 80 };
        var statusBinding = settingsStatus.Bind(TextBlock.TextProperty, Status.GetObservable(TextBlock.TextProperty));
        var close = new Button { Content = "Close", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        Avalonia.Automation.AutomationProperties.SetAutomationId(reload, "reload-structures");
        Avalonia.Automation.AutomationProperties.SetAutomationId(structure, "active-structure");
        Avalonia.Automation.AutomationProperties.SetAutomationId(Profiles, "structure-profiles");
        Avalonia.Automation.AutomationProperties.SetAutomationId(dialog, "settings");
        load.Click += (_, _) => { dialog.Close(); StructureClicked(this, new RoutedEventArgs()); };
        schema.Click += (_, _) => { dialog.Close(); SchemaClicked(this, new RoutedEventArgs()); };
        close.Click += (_, _) => dialog.Close();
        var content = new StackPanel
        {
            Margin = new Avalonia.Thickness(22), Spacing = 14, Children =
            {
                new TextBlock { Text = "Tags and field names", FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 16 },
                structure, tags, Profiles,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Children = { load, reload } },
                schema, settingsStatus, close
            }
        };
        reload.Click += async (_, _) =>
        {
            await RunAsync("Reload Structures", ReloadStructuresAsync, [Workspace, content]);
            structure.Text = ActiveStructureDescription();
        };
        void UpdateStructureDescription(object? source, SelectionChangedEventArgs change)
        {
            structure.Text = Profiles.SelectedItem is StructureChoice choice
                ? "Catalog: " + choice.File.Path + "\nProfile: " + (choice.Catalog.ProfileName ?? "Custom")
                : ActiveStructureDescription();
        }
        Profiles.SelectionChanged += UpdateStructureDescription;
        dialog.Content = content;
        dialog.Closed += (_, _) =>
        {
            Profiles.SelectionChanged -= UpdateStructureDescription;
            statusBinding.Dispose();
            if (Profiles.Parent is Panel panel) panel.Children.Remove(Profiles);
            dialog.Content = null; settingsWindow = null;
        };
        await dialog.ShowDialog(this);
    }

    private void StoreNavigation()
    {
        active.Search = Search.Text ?? "";
        active.SelectedOrdinal = current?.Ordinal ?? -1;
        active.NavigationSaved = true;
        active.Expanded.Clear();
        foreach (var branch in branches.Where(branch => branch.IsExpanded)) active.Expanded.Add(branch.Key);
    }

    private void ActivateSession(EditorSession session)
    {
        if (ReferenceEquals(active, session)) return;
        searchDelay?.Cancel(); ApplyFields(); StoreNavigation();
        active = session;
        changing = true; Tabs.SelectedItem = session; Search.Text = session.Search; changing = false;
        SetProfiles(); RebuildRecords();
        matching = allRecords.Where(item => Matches(item, session.Search)).ToList();
        if (session.NavigationSaved)
            foreach (var branch in branches) branch.IsExpanded = session.Expanded.Contains(branch.Key);
        IncludeMatches(session.Search.Length > 0); ShowPage();
        if (branches.FirstOrDefault(branch => branch.Record?.Ordinal == session.SelectedOrdinal) is { } selected) FocusBranch(selected);
        UpdateTitle(); UpdateControls();
    }

    private void TabChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (changing || Tabs.SelectedItem is not EditorSession selected || ReferenceEquals(active, selected)) return;
        try
        {
            if (busy) throw new InvalidOperationException("Wait for the current operation before switching files.");
            ActivateSession(selected);
        }
        catch (Exception error)
        {
            changing = true; Tabs.SelectedItem = sessions.Contains(active) ? active : null; changing = false;
            Status.Text = "Cannot switch file: " + error.Message;
        }
    }

    private async Task CloseSessionAsync(EditorSession session)
    {
        if (busy || !sessions.Contains(session)) return;
        bool selected = ReferenceEquals(active, session);
        if (selected)
        {
            try { ApplyFields(); }
            catch (Exception error) { Status.Text = "Cannot close file: " + error.Message; return; }
        }
        if ((selected ? HasChanges : session.Dirty) && !await AskAsync("Unsaved changes", $"Discard changes and close {Path.GetFileName(session.SourcePath)}?", "Discard and close")) return;
        int index = sessions.IndexOf(session);
        changing = true; sessions.Remove(session); changing = false;
        if (selected)
        {
            editors.Clear(); current = null; dirty = false;
            if (sessions.Count > 0) ActivateSession(sessions[Math.Min(index, sessions.Count - 1)]);
            else
            {
                active = new EditorSession(session.CatalogRoot, session.Catalog, session.CatalogName);
                changing = true; Search.Text = ""; changing = false;
                RebuildRecords(); SetProfiles(); UpdateTitle();
                Status.Text = "Open a file from File → Open";
            }
        }
        else { changing = true; Tabs.SelectedItem = active; changing = false; }
    }

    private async void CloseTabClicked(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (sender is Button { DataContext: EditorSession session }) await CloseSessionAsync(session);
    }
    private async void CloseActiveClicked(object? sender, RoutedEventArgs args) => await CloseSessionAsync(active);
    private void ExitClicked(object? sender, RoutedEventArgs args) => Close();

    private void UpdateControls()
    {
        bool previous = changing; changing = true;
        RecordsCount.Text = matching.Count == allRecords.Count ? $"{allRecords.Count:N0}" : $"{matching.Count:N0} / {allRecords.Count:N0}";
        FieldHeader.ColumnDefinitions = new ColumnDefinitions(active.IsAuthoring ? "100,220,*" : "0,220,*");
        TypeHeader.IsVisible = active.IsAuthoring;
        RootAdd.IsEnabled = document?.Format == BinaryFormat.T2b;
        AddValue.IsEnabled = document?.Format == BinaryFormat.T2b && current is not null && current.Row.Values.Count < 255;
        UndoAction.IsEnabled = document is not null && (active.History.Undo is not null || editors.Any(field => field.IsChanged));
        RedoAction.IsEnabled = active.History.Redo is not null && !editors.Any(field => field.IsChanged);
        changing = previous;
    }

    private async Task WriteDocumentAsync(string path, bool cfg, bool markSaved)
    {
        if (document is null) throw new InvalidOperationException("Open a file before saving.");
        ApplyFields(); int ordinal = current?.Ordinal ?? -1;
        CheckDestination(path, cfg);
        byte[] bytes = await Task.Run(() => cfg ? Encoding.UTF8.GetBytes(CfgTextCodec.WriteReadable(document)) : BinCodec.Write(document));
        CfgDocument saved = await Task.Run(() => cfg ? CfgTextCodec.Read(Encoding.UTF8.GetString(bytes), catalog) : BinCodec.Read(bytes, catalog));
        _ = saved.GetRecordTree();
        await Task.Run(() => AtomicWrite(path, bytes));
        if (markSaved)
        {
            document = saved; dirty = false; sourcePath = Path.GetFullPath(path);
            RebuildRecords(); FocusOrdinal(ordinal); UpdateTitle();
        }
        Status.Text = $"Saved {Path.GetFileName(path)} · {bytes.Length:N0} bytes";
    }

    private void CheckDestination(string path, bool cfg)
    {
        string extension = Path.GetExtension(path);
        if (cfg ? !extension.Equals(".cfg", StringComparison.OrdinalIgnoreCase) : !DocumentFiles.IsBinaryFile(path))
            throw new IOException(cfg ? "Choose a filename ending in .cfg for CFG export." : "Choose a filename ending in .bin for binary output.");
        string fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (structureFiles.Any(file => string.Equals(file.Path, fullPath, comparison))
            || cfg && string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(dataDirectory), comparison))
            throw new IOException("That destination is reserved for Structure catalogs. Choose a document destination outside data.");
        if (sessions.Any(session => !ReferenceEquals(session, active) && string.Equals(session.SourcePath, fullPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            throw new IOException("That destination is open in a tab. Choose a different name for the export.");
    }

    private async Task<bool> SaveCurrentAsync()
    {
        if (document is null) { Status.Text = "Open a file before saving."; return false; }
        if (sourcePath.Length == 0) { await SaveAsync(false); return !HasChanges; }
        await WriteDocumentAsync(sourcePath, Path.GetExtension(sourcePath).Equals(".cfg", StringComparison.OrdinalIgnoreCase), true);
        return true;
    }
    private async void SaveClicked(object? sender, RoutedEventArgs args) => await RunAsync("Save", async () => { await SaveCurrentAsync(); });
    private async void SaveAllClicked(object? sender, RoutedEventArgs args) => await RunAsync("Save all", async () =>
    {
        ApplyFields(); var selected = active;
        try
        {
            foreach (var session in sessions.Where(session => session.Dirty).ToArray())
            {
                ActivateSession(session);
                if (!await SaveCurrentAsync()) return;
            }
            Status.Text = "All modified files saved.";
        }
        finally { ActivateSession(selected); }
    });

    private async void NewClicked(object? sender, RoutedEventArgs args) => await NewDocumentAsync();

    private Task NewDocumentAsync() => RunAsync("New", () =>
    {
        ApplyFields();
        var session = new EditorSession(catalogRoot, catalog, catalogName)
        { Document = CfgDocument.CreateT2b(catalog), IsAuthoring = true, Dirty = true };
        sessions.Add(session); ActivateSession(session);
        Status.Text = "New file. Add an entry, then add values and choose their types.";
        return Task.CompletedTask;
    });

}
