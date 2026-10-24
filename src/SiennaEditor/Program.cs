using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace SiennaEditor;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (CommandLine.IsCommand(args)) return CommandLine.Run(args);
        return AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class App : Application
{
    public override void Initialize()
    {
        Name = "Sienna Editor";
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new EditorWindow();
            desktop.MainWindow = window;
            if (desktop.Args is [var path, ..]) window.Opened += async (_, _) => await window.OpenPathAsync(path);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
