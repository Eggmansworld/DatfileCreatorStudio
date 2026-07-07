using Avalonia;

namespace DatfileCreatorStudio;

internal static class Program
{
    // Avalonia configuration, don't remove; also used by visual designer.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogCrash("Main", ex);
            throw;
        }
    }

    /// <summary>Append an unhandled exception to a crash log next to the exe.</summary>
    internal static void LogCrash(string source, Exception? ex)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "DatfileCreatorStudio.crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {source}:\n{ex}\n\n");
        }
        catch
        {
            // Crash logging must never itself throw
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
