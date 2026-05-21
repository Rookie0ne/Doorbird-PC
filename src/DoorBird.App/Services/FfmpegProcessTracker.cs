using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DoorBird.App.Services;

/// <summary>
/// Tracks PIDs of ffmpeg subprocesses across runs so orphans from a crashed or
/// force-killed previous run can be cleaned up at startup. Each app instance
/// writes to its own file named ffmpeg-{ownerPid}.pids — files whose owner PID
/// is no longer alive are treated as orphans, leaving concurrent live instances
/// untouched.
/// </summary>
public static class FfmpegProcessTracker {
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DoorBird");
    private static readonly int OwnPid = Environment.ProcessId;
    private static readonly string PidFile = Path.Combine(Dir, $"ffmpeg-{OwnPid}.pids");
    private static readonly object Lock = new();

    public static void Register(int pid) {
        if (pid <= 0) return;
        lock (Lock) {
            try {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(PidFile, pid + Environment.NewLine);
            } catch { }
        }
    }

    public static void Unregister(int pid) {
        if (pid <= 0) return;
        lock (Lock) {
            try {
                if (!File.Exists(PidFile)) return;
                var pidStr = pid.ToString();
                var remaining = File.ReadAllLines(PidFile)
                    .Where(l => l.Trim() != pidStr)
                    .ToArray();
                if (remaining.Length == 0) File.Delete(PidFile);
                else File.WriteAllLines(PidFile, remaining);
            } catch { }
        }
    }

    /// <summary>Kills all ffmpeg PIDs in this instance's tracker file and deletes it.</summary>
    public static void KillOwn() {
        lock (Lock) {
            KillFromFile(PidFile);
        }
    }

    /// <summary>
    /// Scans for tracker files from previous app runs whose owner PID is no longer
    /// alive, and kills the ffmpeg processes they reference. Files belonging to live
    /// PIDs (concurrent instances) are left alone.
    /// </summary>
    public static void KillOrphansFromPriorRuns() {
        try {
            if (!Directory.Exists(Dir)) return;
            foreach (var path in Directory.EnumerateFiles(Dir, "ffmpeg-*.pids")) {
                var ownerPid = ParseOwnerPid(path);
                if (ownerPid == OwnPid) continue;
                if (ownerPid > 0 && IsProcessRunning(ownerPid)) continue;
                KillFromFile(path);
            }
        } catch { }
    }

    private static int ParseOwnerPid(string path) {
        var name = Path.GetFileNameWithoutExtension(path); // "ffmpeg-1234"
        var dash = name.IndexOf('-');
        if (dash < 0 || dash >= name.Length - 1) return -1;
        return int.TryParse(name.AsSpan(dash + 1), out var n) ? n : -1;
    }

    private static bool IsProcessRunning(int pid) {
        try {
            using var _ = Process.GetProcessById(pid);
            return true;
        } catch {
            return false;
        }
    }

    private static void KillFromFile(string path) {
        List<int> pids;
        try {
            if (!File.Exists(path)) return;
            pids = File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Select(l => int.TryParse(l, out var n) ? n : -1)
                .Where(n => n > 0)
                .Distinct()
                .ToList();
        } catch {
            return;
        }

        foreach (var pid in pids) {
            try {
                using var proc = Process.GetProcessById(pid);
                // ProcessName is "ffmpeg" on Linux/macOS (comm) and on Windows (exe basename
                // without extension). Verifying the name guards against PID reuse killing an
                // unrelated process that happened to inherit the same PID.
                if (proc.ProcessName.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase)) {
                    proc.Kill(entireProcessTree: true);
                    try { proc.WaitForExit(2000); } catch { }
                }
            } catch {
                // PID no longer exists, access denied, or other transient error — skip.
            }
        }

        try { File.Delete(path); } catch { }
    }
}
