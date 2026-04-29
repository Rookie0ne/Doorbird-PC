using Avalonia;
using Avalonia.ReactiveUI;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DoorBird.App;

sealed class Program
{
    private static string? _savedSttyState;
    private static bool _isUnixTerminal;

    [STAThread]
    public static void Main(string[] args)
    {
        _isUnixTerminal = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                       || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        if (_isUnixTerminal)
        {
            // Snapshot the controlling terminal's mode now so we can restore it byte-for-byte on exit,
            // even after a crash or when PortAudio/ALSA has flipped the tty into raw / no-echo mode.
            CaptureTerminalState();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ResetTerminal();
            AppDomain.CurrentDomain.UnhandledException += (_, _) => ResetTerminal();
            Console.CancelKeyPress += (_, _) => ResetTerminal();
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            if (_isUnixTerminal) ResetTerminal();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();

    private static void CaptureTerminalState()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "stty",
                Arguments = "-g",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (proc == null) return;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(1000);
            if (proc.ExitCode == 0 && output.Length > 0) _savedSttyState = output;
        }
        catch { }
    }

    private static void ResetTerminal()
    {
        // Restore line discipline first — this is what fixes "keyboard doesn't work" after exit.
        RunStty(_savedSttyState ?? "sane");
        // Then normalize display attributes and leave any alternate screen buffer the TUI may have entered.
        try
        {
            Console.Out.Write("\x1b[0m\x1b[?1049l");
            Console.Out.Flush();
        }
        catch { }
    }

    private static void RunStty(string args)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "stty",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(1000);
        }
        catch { }
    }
}
