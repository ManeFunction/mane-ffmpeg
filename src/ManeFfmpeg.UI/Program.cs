using System.Diagnostics;
using Avalonia;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Mpv;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.UI;

internal static class Program
{
    /// <summary>Set for the copy started by <see cref="TryRelaunchForBundledMpv"/>, whose parent waits for it.</summary>
    internal const string RelaunchedVariable = "MANE_FFMPEG_RELAUNCHED";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write($"Unhandled: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write($"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };
        // A Quick Action conversion needs no libmpv, so it never relaunches for it.
        if (FileConversion.Parse(args) is null && TryRelaunchForBundledMpv(args) is { } exitCode)
            return exitCode;
        AppLog.Write($"Start {typeof(Program).Assembly.GetName().Version} args=[{string.Join(", ", args)}]");
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Some libmpv copies load only with an environment variable set at process start (IINA's on macOS needs
    /// DYLD_FALLBACK_LIBRARY_PATH), so restart once with it and wait.
    /// </summary>
    private static int? TryRelaunchForBundledMpv(string[] args)
    {
        if (Environment.GetEnvironmentVariable(RelaunchedVariable) == "1")
            return null;
        var settings = new SettingsStore().Current;
        if (MpvLibrary.RelaunchEnvironment(settings.LibMpvPath) is not { } environment)
            return null;

        var processPath = Environment.ProcessPath;
        if (processPath is null)
            return null;
        AppLog.Write($"Relaunching with {string.Join(", ", environment.Keys)} for {MpvLibrary.Find(settings.LibMpvPath)}");
        var start = new ProcessStartInfo(processPath) { UseShellExecute = false };
        // Started as "dotnet ManeFfmpeg.dll": pass the dll path through.
        if (Path.GetFileNameWithoutExtension(processPath) == "dotnet")
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        foreach (var (name, value) in environment)
            start.Environment[name] = value;
        start.Environment[RelaunchedVariable] = "1";
        try
        {
            using var child = Process.Start(start);
            if (child is null)
                return null;
            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
