using System;
using FFmpeg.AutoGen;

namespace DoorBird.App.Video.Gpu;

/// <summary>
/// A zero-copy sink for hardware-decoded frames. Implementations keep the frame on
/// the GPU all the way into Avalonia's compositor; nothing is read back to system
/// memory. One implementation exists per platform graphics path (Linux: VAAPI
/// surfaces through EGL into Avalonia's shared OpenGL context).
///
/// Presenters are created by the view (they need the compositor) and handed to the
/// decoder, which calls <see cref="TryPresent"/> from its decode thread.
/// </summary>
public unsafe interface IGpuVideoPresenter : IDisposable {
    /// <summary>The ffmpeg hardware device type this presenter consumes frames from.</summary>
    AVHWDeviceType DeviceType { get; }

    /// <summary>
    /// Device path to hand to av_hwdevice_ctx_create so decoding happens on the same
    /// adapter the compositor renders with (for example a DRM render node). Null means
    /// let ffmpeg choose.
    /// </summary>
    string? PreferredDevice { get; }

    /// <summary>Short label for status display, e.g. "VAAPI zero-copy".</summary>
    string Label { get; }

    /// <summary>
    /// Presents one hardware frame. Called on the decode thread. Must return false
    /// (never throw) when the frame cannot be presented; the decoder then falls back.
    /// The frame is only valid for the duration of the call.
    /// </summary>
    bool TryPresent(AVFrame* hwFrame);
}
