using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DoorBird.App.Services;

public record MissingDependency(string Name, string InstallInstructions);

public static class DependencyChecker {
    public static List<MissingDependency> Check() {
        var missing = new List<MissingDependency>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
            CheckLinuxDependencies(missing);
        } else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
            CheckMacOsDependencies(missing);
        }
        // Windows: all native deps are bundled via NuGet, nothing to check

        return missing;
    }

    private static void CheckLinuxDependencies(List<MissingDependency> missing) {
        // PortAudio — required for intercom audio
        if (!CanLoadLibrary("libportaudio.so.2")) {
            missing.Add(new MissingDependency(
                "PortAudio (libportaudio2)",
                "Install with one of:\n" +
                "  Ubuntu/Debian:  sudo apt install libportaudio2\n" +
                "  Fedora:         sudo dnf install portaudio\n" +
                "  Arch:           sudo pacman -S portaudio"));
        }
    }

    private static void CheckMacOsDependencies(List<MissingDependency> missing) {
        // PortAudio — required for intercom audio
        if (!CanLoadLibrary("libportaudio.dylib") && !CanLoadLibrary("libportaudio.2.dylib")) {
            missing.Add(new MissingDependency(
                "PortAudio",
                "Install with Homebrew:\n" +
                "  brew install portaudio"));
        }
    }

    private static bool CanLoadLibrary(string name) {
        try {
            var handle = NativeLibrary.Load(name);
            NativeLibrary.Free(handle);
            return true;
        } catch {
            return false;
        }
    }

    public static string FormatMessage(List<MissingDependency> missing) {
        var lines = new List<string> {
            "The following dependencies are missing:\n"
        };
        foreach (var dep in missing) {
            lines.Add($"--- {dep.Name} ---");
            lines.Add(dep.InstallInstructions);
            lines.Add("");
        }
        lines.Add("Please install the missing dependencies and restart the application.");
        return string.Join('\n', lines);
    }
}
