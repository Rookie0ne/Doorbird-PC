namespace DoorBird.App.Video;

/// <summary>Which decode and presentation path the RTSP decoder is currently using.</summary>
public enum VideoPath {
    None,
    Software,
    HardwareCopy,
    HardwareZeroCopy
}

public static class VideoPathExtensions {
    public static string Describe(this VideoPath p, string? deviceLabel) => p switch {
        VideoPath.Software => "software",
        VideoPath.HardwareCopy => $"{deviceLabel ?? "GPU"} decode, CPU copy",
        VideoPath.HardwareZeroCopy => $"{deviceLabel ?? "GPU"} zero-copy",
        _ => "idle"
    };
}
