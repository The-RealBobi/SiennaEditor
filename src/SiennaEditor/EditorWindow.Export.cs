using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace SiennaEditor;

public sealed partial class EditorWindow
{
    private async void ExportFormatClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is not MenuItem { Header: string label } || !Enum.TryParse<DocumentExportFormat>(label, true, out var format)) return;
        await RunAsync("Export " + label, async () =>
        {
            if (document is null) throw new InvalidOperationException("Open a file before exporting.");
            ApplyFields();
            var snapshot = document;
            string extension = DocumentFiles.ExportExtension(format);
            var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export " + label,
                ShowOverwritePrompt = true,
                SuggestedFileName = DocumentFiles.ExportFileName(sourcePath.Length == 0 ? "Untitled" : Path.GetFileName(sourcePath), format),
                DefaultExtension = extension.TrimStart('.'),
                FileTypeChoices = [new FilePickerFileType(label) { Patterns = ["*" + extension] }]
            });
            if (target is null) return;
            string path = target.TryGetLocalPath() ?? throw new IOException("Choose a local destination.");
            if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new IOException($"Choose a filename ending in {extension}.");
            var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            string fullPath = Path.GetFullPath(path);
            if (sessions.Any(session => comparer.Equals(session.SourcePath, fullPath)))
                throw new IOException("That destination is open in a tab. Choose a different filename.");
            if (comparer.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(dataDirectory))
                || structureFiles.Any(file => comparer.Equals(file.Path, fullPath)))
                throw new IOException("Choose a destination outside the Structure folder.");
            await Task.Run(() =>
            {
                byte[] bytes = DocumentFiles.EncodeExport(snapshot, format);
                if (format == DocumentExportFormat.Cfg)
                    bytes = Encoding.UTF8.GetBytes(ExportProvenance.MarkCfg(Encoding.UTF8.GetString(bytes)));
                AtomicWrite(fullPath, bytes);
            });
            Status.Text = "Exported " + Path.GetFileName(fullPath) + ".";
        });
    }
}
