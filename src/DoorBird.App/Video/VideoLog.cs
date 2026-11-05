using System;

namespace DoorBird.App.Video;

/// <summary>
/// Diagnostic trace for the video pipeline. Off by default; set DOORBIRD_VIDEO_LOG=1
/// to get path decisions and GPU/decoder errors on stderr. Cheap enough to leave
/// in: callers pay for a string only when enabled.
/// </summary>
public static class VideoLog {
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("DOORBIRD_VIDEO_LOG") is "1" or "true";

    public static void Write(string component, string message) {
        if (!Enabled) return;
        try { Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{component}] {message}"); } catch { }
    }
}
