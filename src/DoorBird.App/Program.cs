using Avalonia;
using Avalonia.ReactiveUI;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DoorBird.App;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Restore terminal state on exit (PortAudio/ALSA can corrupt it)
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ResetTerminal();
            Console.CancelKeyPress += (_, _) => ResetTerminal();
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                ResetTerminal();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();

    private static void ResetTerminal()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "stty",
                Arguments = "sane",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(1000);
        }
        catch { }
    }
}
