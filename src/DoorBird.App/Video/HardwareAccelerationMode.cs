namespace DoorBird.App.Video;

/// <summary>
/// User-facing hardware acceleration policy for the RTSP decoder.
/// </summary>
public enum HardwareAccelerationMode {
    /// <summary>
    /// Use the GPU only when the whole chain is zero-copy (hardware decode straight
    /// into the compositor) and a probe has produced real frames through it.
    /// Anything less is measurably slower than software at the stream sizes this
    /// app sees (see HARDWARE-ACCELERATION.md), so Auto falls back to software.
    /// </summary>
    Auto,

    /// <summary>
    /// Force hardware decode. Zero-copy presentation is used where it exists;
    /// otherwise decoded frames are copied back to system memory.
    /// </summary>
    Enabled,

    /// <summary>Software decode only.</summary>
    Disabled
}

public static class HardwareAccelerationModeExtensions {
    public static HardwareAccelerationMode ParseMode(string? value) => value switch {
        "Enabled" => HardwareAccelerationMode.Enabled,
        "Disabled" => HardwareAccelerationMode.Disabled,
        _ => HardwareAccelerationMode.Auto
    };
}
