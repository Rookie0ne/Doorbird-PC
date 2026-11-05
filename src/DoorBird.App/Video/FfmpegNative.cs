using System;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace DoorBird.App.Video;

/// <summary>
/// Locates and initializes the bundled FFmpeg shared libraries. The libraries ship
/// next to the executable (see native/ffmpeg and scripts/fetch-ffmpeg.sh), so the
/// user never installs ffmpeg. FFmpeg.AutoGen is pinned to one FFmpeg major
/// version; the bundled libraries and the NuGet package must be upgraded together.
/// </summary>
public static unsafe class FfmpegNative {
    private static readonly object InitLock = new();
    private static bool _initialized;
    private static string? _initError;

    /// <summary>Human-readable library version, e.g. "8.1.3 (avcodec 62.28.103)".</summary>
    public static string VersionString { get; private set; } = "not loaded";

    /// <summary>True when the libraries loaded and the API is usable.</summary>
    public static bool IsAvailable => TryInitialize(out _);

    public static string? InitializationError {
        get { TryInitialize(out var err); return err; }
    }

    public static bool TryInitialize(out string? error) {
        lock (InitLock) {
            if (_initialized) { error = _initError; return _initError == null; }
            _initialized = true;
            try {
                var root = AppContext.BaseDirectory;
                if (!HasLibraries(root)) {
                    // Development builds run from bin/<cfg>/net8.0/ where the csproj
                    // copies the libraries as well; keep the probe explicit so a missing
                    // fetch step produces a clear message instead of a DllNotFoundException.
                    _initError = $"FFmpeg libraries not found in {root}. Run scripts/fetch-ffmpeg.sh for this platform.";
                    error = _initError;
                    return false;
                }
                ffmpeg.RootPath = root;
                DynamicallyLoadedBindings.Initialize();
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
                ffmpeg.avformat_network_init();
                var v = ffmpeg.avcodec_version();
                VersionString = $"{ffmpeg.av_version_info()} (avcodec {v >> 16}.{(v >> 8) & 0xFF}.{v & 0xFF})";
                error = null;
                return true;
            } catch (Exception ex) {
                _initError = ex.Message;
                error = _initError;
                return false;
            }
        }
    }

    private static bool HasLibraries(string dir) {
        string probe;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) probe = "avcodec-62.dll";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) probe = "libavcodec.62.dylib";
        else probe = "libavcodec.so.62";
        return File.Exists(Path.Combine(dir, probe));
    }

    public static string ErrorString(int error) {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(error, buf, 256);
        return Marshal.PtrToStringAnsi((IntPtr)buf) ?? $"error {error}";
    }

    public static int ThrowIfError(int result, string what) {
        if (result < 0) throw new FfmpegException($"{what}: {ErrorString(result)} ({result})", result);
        return result;
    }

    public static bool IsEagainOrEof(int r) => r == ffmpeg.AVERROR(ffmpeg.EAGAIN) || r == ffmpeg.AVERROR_EOF;
}

public class FfmpegException : Exception {
    public int Code { get; }
    public FfmpegException(string message, int code) : base(message) { Code = code; }
}
