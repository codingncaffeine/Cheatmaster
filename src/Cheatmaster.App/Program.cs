using Avalonia;

namespace Cheatmaster.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // Menus and drop-downs drawn inside the window: a popup that is its own X11 window can
            // open behind a borderless main window on some window managers.
            .With(new X11PlatformOptions { OverlayPopups = true });
}
