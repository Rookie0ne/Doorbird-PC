using System;
using System.IO;
using System.Text.Json;

namespace DoorBird.App.Models;

public class AppSettings {
    public string DeviceHost { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int NotificationPort { get; set; } = 8080;
    public string RecordingPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "DoorBird");
    public bool AutoRecord { get; set; } = false;
    public bool AutoConnect { get; set; } = false;

    public bool LiveViewMaximized { get; set; } = false;

    /// <summary>Live view mode: "Rtsp", "Mjpeg", or "Snapshot". Default is MJPEG.</summary>
    public string LiveViewMode { get; set; } = "Mjpeg";

    /// <summary>Saved audio output device name. Null/empty = system default.</summary>
    public string? AudioOutputDevice { get; set; }

    /// <summary>Saved audio input device name. Null/empty = system default.</summary>
    public string? AudioInputDevice { get; set; }

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DoorBird");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load() {
        try {
            if (File.Exists(SettingsPath)) {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        } catch { }
        return new AppSettings();
    }

    public void Save() {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
