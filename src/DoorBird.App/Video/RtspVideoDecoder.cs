using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DoorBird.App.Video.Gpu;
using FFmpeg.AutoGen;

namespace DoorBird.App.Video;

/// <summary>
/// In-process RTSP H.264 decoder built on the bundled FFmpeg libraries.
///
/// Three frame paths, chosen per session from the user's hardware acceleration
/// setting and what the view can present:
///   - HardwareZeroCopy: GPU decode, frame handed to an <see cref="IGpuVideoPresenter"/>
///     which puts it into Avalonia's compositor without touching system memory.
///   - HardwareCopy: GPU decode, frame copied back and converted to a bitmap.
///     Only used when the user forces acceleration and no zero-copy presenter exists.
///   - Software: CPU decode straight to a bitmap.
///
/// Auto mode verifies the zero-copy chain by producing real frames through it; if
/// that fails for any reason the session restarts in software and stays there.
/// Capability lists lie (see HARDWARE-ACCELERATION.md), frames do not.
/// </summary>
public sealed unsafe class RtspVideoDecoder : IDisposable {
    private const int ZeroCopyProbeFrames = 3;
    private const int BitmapPoolSize = 3;
    private const int SwsBilinear = 2; // SWS_BILINEAR; an enum rather than a constant in FFmpeg 8 headers

    private readonly string _url;
    private readonly HardwareAccelerationMode _mode;
    private readonly Func<IGpuVideoPresenter?> _presenterProvider;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    // Native callbacks must stay rooted for the lifetime of the contexts using them.
    private static readonly AVIOInterruptCB_callback InterruptDelegate = InterruptCallback;
    private static readonly AVCodecContext_get_format GetFormatDelegate = GetFormatCallback;
    private GCHandle _selfHandle;

    private long _opDeadlineTicks;
    private AVPixelFormat _hwPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
    private bool _zeroCopyFailed;
    private bool _hwDecodeFailed;

    private SwsContext* _sws;
    private readonly WriteableBitmap?[] _pool = new WriteableBitmap?[BitmapPoolSize];
    private int _poolIndex;
    private TaskCompletionSource<WriteableBitmap?>? _snapshotRequest;

    public VideoPath ActivePath { get; private set; }
    public string? DeviceLabel { get; private set; }
    public DateTime LastFrameAt { get; private set; } = DateTime.UtcNow;
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>A decoded frame is ready in a pooled bitmap. Do not dispose it.</summary>
    public event Action<WriteableBitmap>? FrameReady;
    /// <summary>A frame was presented through the zero-copy path (no bitmap).</summary>
    public event Action? FramePresented;
    public event Action<string>? StatusChanged;
    /// <summary>The active path changed (e.g. probe fell back to software).</summary>
    public event Action<VideoPath>? PathChanged;

    public RtspVideoDecoder(string url, HardwareAccelerationMode mode, Func<IGpuVideoPresenter?> presenterProvider) {
        _url = url;
        _mode = mode;
        _presenterProvider = presenterProvider;
    }

    public void Start() {
        if (_thread != null) return;
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Weak);
        _thread = new Thread(Run) { IsBackground = true, Name = "rtsp-decode" };
        _thread.Start();
    }

    /// <summary>
    /// Produces a standalone bitmap of the next decoded frame, regardless of path.
    /// Used for screenshots when the live image lives on the GPU. Caller disposes.
    /// </summary>
    public Task<WriteableBitmap?> CaptureSnapshotAsync(TimeSpan timeout) {
        var tcs = new TaskCompletionSource<WriteableBitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = Interlocked.Exchange(ref _snapshotRequest, tcs);
        old?.TrySetResult(null);
        _ = Task.Delay(timeout).ContinueWith(_ => tcs.TrySetResult(null));
        return tcs.Task;
    }

    private void Run() {
        var ct = _cts.Token;
        if (!FfmpegNative.TryInitialize(out var initError)) {
            StatusChanged?.Invoke($"RTSP unavailable: {initError}");
            return;
        }

        while (!ct.IsCancellationRequested) {
            var restartImmediately = false;
            try {
                RunSession(ct);
                if (!ct.IsCancellationRequested) StatusChanged?.Invoke("RTSP stream ended - reconnecting...");
            } catch (ZeroCopyProbeFailed ex) {
                VideoLog.Write("decoder", $"zero-copy probe failed: {ex.Message}");
                _zeroCopyFailed = true;
                StatusChanged?.Invoke($"GPU path failed ({ex.Message}) - using software decode");
                restartImmediately = true;
            } catch (HardwareDecodeFailed ex) {
                VideoLog.Write("decoder", $"hardware decode failed: {ex.Message}");
                _hwDecodeFailed = true;
                StatusChanged?.Invoke($"Hardware decode failed ({ex.Message}) - using software decode");
                restartImmediately = true;
            } catch (OperationCanceledException) {
                break;
            } catch (Exception ex) {
                if (ct.IsCancellationRequested) break;
                VideoLog.Write("decoder", $"session error: {ex}");
                StatusChanged?.Invoke($"RTSP error: {ex.Message} - reconnecting...");
            }
            SetPath(VideoPath.None, null);
            if (ct.IsCancellationRequested) break;
            if (!restartImmediately) {
                try { Task.Delay(2000, ct).Wait(ct); } catch { break; }
            }
        }
    }

    private void RunSession(CancellationToken ct) {
        AVFormatContext* fmt = null;
        AVCodecContext* codecCtx = null;
        AVBufferRef* hwDevice = null;
        AVPacket* pkt = null;
        AVFrame* frame = null;
        AVFrame* swFrame = null;
        AVDictionary* opts = null;

        try {
            fmt = ffmpeg.avformat_alloc_context();
            fmt->interrupt_callback.callback = (AVIOInterruptCB_callback_func)InterruptDelegate;
            fmt->interrupt_callback.opaque = (void*)GCHandle.ToIntPtr(_selfHandle);

            ffmpeg.av_dict_set(&opts, "rtsp_transport", "tcp", 0);
            ffmpeg.av_dict_set(&opts, "timeout", "10000000", 0);   // socket I/O timeout, microseconds
            ffmpeg.av_dict_set(&opts, "fflags", "nobuffer", 0);
            ffmpeg.av_dict_set(&opts, "flags", "low_delay", 0);
            ffmpeg.av_dict_set(&opts, "max_delay", "500000", 0);
            // The SDP already carries SPS/PPS, so stream probing only needs the first
            // keyframe. The default analyzes up to 5 s before the first frame shows.
            ffmpeg.av_dict_set(&opts, "analyzeduration", "500000", 0);

            ArmDeadline(TimeSpan.FromSeconds(15));
            StatusChanged?.Invoke("RTSP connecting...");
            {
                var r = ffmpeg.avformat_open_input(&fmt, _url, null, &opts);
                if (r < 0) { fmt = null; FfmpegNative.ThrowIfError(r, "open RTSP"); }
            }
            ArmDeadline(TimeSpan.FromSeconds(15));
            FfmpegNative.ThrowIfError(ffmpeg.avformat_find_stream_info(fmt, null), "stream info");

            AVCodec* codec = null;
            var streamIndex = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            FfmpegNative.ThrowIfError(streamIndex, "find video stream");
            if (codec == null) throw new FfmpegException("no decoder for video stream", -1);
            var stream = fmt->streams[streamIndex];

            codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            FfmpegNative.ThrowIfError(ffmpeg.avcodec_parameters_to_context(codecCtx, stream->codecpar), "codec parameters");
            codecCtx->opaque = (void*)GCHandle.ToIntPtr(_selfHandle);

            // Decide the path for this session.
            var presenter = _presenterProvider();
            var wantHw = _mode switch {
                HardwareAccelerationMode.Disabled => false,
                HardwareAccelerationMode.Enabled => !_hwDecodeFailed,
                _ => presenter != null && !_zeroCopyFailed && !_hwDecodeFailed
            };
            var hwType = presenter?.DeviceType ?? DefaultDeviceType();
            _hwPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
            VideoLog.Write("decoder", $"session: mode={_mode} presenter={(presenter == null ? "none" : presenter.Label)} wantHw={wantHw} hwType={hwType} device={presenter?.PreferredDevice ?? "default"}");

            if (wantHw && hwType != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE) {
                try {
                    if (hwType == AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI) EnsureVaapiRuntime();
                    _hwPixFmt = FindHwPixelFormat(codec, hwType);
                    FfmpegNative.ThrowIfError(
                        ffmpeg.av_hwdevice_ctx_create(&hwDevice, hwType, presenter?.PreferredDevice, null, 0),
                        "create hardware device");
                    codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(hwDevice);
                    codecCtx->get_format = (AVCodecContext_get_format_func)GetFormatDelegate;
                    // Frames in flight in the presenter plus the decoder's own reference
                    // frames; too small a pool stalls decoding, not presentation.
                    codecCtx->extra_hw_frames = 6;
                    DeviceLabel = ffmpeg.av_hwdevice_get_type_name(hwType)?.ToUpperInvariant();
                } catch (Exception ex) when (_mode == HardwareAccelerationMode.Enabled || presenter != null) {
                    throw new HardwareDecodeFailed(ex.Message);
                }
            }

            FfmpegNative.ThrowIfError(ffmpeg.avcodec_open2(codecCtx, codec, null), "open decoder");

            pkt = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            swFrame = ffmpeg.av_frame_alloc();
            var zeroCopyFrames = 0;
            var anyFrame = false;

            while (!ct.IsCancellationRequested) {
                ArmDeadline(TimeSpan.FromSeconds(10));
                var r = ffmpeg.av_read_frame(fmt, pkt);
                if (r < 0) {
                    if (r == ffmpeg.AVERROR_EOF) break;
                    FfmpegNative.ThrowIfError(r, "read");
                }
                try {
                    if (pkt->stream_index != streamIndex) continue;
                    r = ffmpeg.avcodec_send_packet(codecCtx, pkt);
                    if (r < 0 && r != ffmpeg.AVERROR(ffmpeg.EAGAIN)) {
                        if (_hwPixFmt != AVPixelFormat.AV_PIX_FMT_NONE && !anyFrame)
                            throw new HardwareDecodeFailed(FfmpegNative.ErrorString(r));
                        continue; // corrupt packet; the decoder resyncs at the next keyframe
                    }
                } finally {
                    ffmpeg.av_packet_unref(pkt);
                }

                while (true) {
                    r = ffmpeg.avcodec_receive_frame(codecCtx, frame);
                    if (r == ffmpeg.AVERROR(ffmpeg.EAGAIN) || r == ffmpeg.AVERROR_EOF) break;
                    if (r < 0) {
                        if (_hwPixFmt != AVPixelFormat.AV_PIX_FMT_NONE && !anyFrame)
                            throw new HardwareDecodeFailed(FfmpegNative.ErrorString(r));
                        FfmpegNative.ThrowIfError(r, "decode");
                    }
                    try {
                        anyFrame = true;
                        HandleFrame(frame, swFrame, presenter, ref zeroCopyFrames);
                    } finally {
                        ffmpeg.av_frame_unref(frame);
                    }
                }
            }
        } finally {
            if (swFrame != null) ffmpeg.av_frame_free(&swFrame);
            if (frame != null) ffmpeg.av_frame_free(&frame);
            if (pkt != null) ffmpeg.av_packet_free(&pkt);
            if (codecCtx != null) ffmpeg.avcodec_free_context(&codecCtx);
            if (hwDevice != null) ffmpeg.av_buffer_unref(&hwDevice);
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
            if (opts != null) ffmpeg.av_dict_free(&opts);
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        }
    }

    private void HandleFrame(AVFrame* frame, AVFrame* swFrame, IGpuVideoPresenter? presenter, ref int zeroCopyFrames) {
        var isHw = _hwPixFmt != AVPixelFormat.AV_PIX_FMT_NONE && frame->format == (int)_hwPixFmt;
        Width = frame->width;
        Height = frame->height;

        var snapshot = Interlocked.Exchange(ref _snapshotRequest, null);

        if (isHw) {
            // Re-read the presenter each frame: the view may attach after the
            // session opened, and it may go away while we run.
            presenter = _presenterProvider() ?? presenter;
            var presented = false;
            if (presenter != null && presenter.DeviceType == DeviceTypeOf(frame)) {
                presented = presenter.TryPresent(frame);
            }

            if (presented) {
                if (zeroCopyFrames == 0) VideoLog.Write("decoder", $"first zero-copy frame presented ({Width}x{Height})");
                zeroCopyFrames++;
                SetPath(VideoPath.HardwareZeroCopy, presenter!.Label);
                LastFrameAt = DateTime.UtcNow;
                FramePresented?.Invoke();
                if (snapshot != null) snapshot.TrySetResult(TransferToBitmap(frame, swFrame, pooled: false));
                return;
            }

            if (presenter != null) VideoLog.Write("decoder", $"present failed: {(presenter as Gpu.Linux.VaapiEglPresenter)?.LastError ?? "unknown"}");
            if (_mode == HardwareAccelerationMode.Auto && zeroCopyFrames < ZeroCopyProbeFrames) {
                // Auto only accepts the full GPU chain. Decode-plus-copy is slower than
                // software here, so restart in software rather than limp along.
                throw new ZeroCopyProbeFailed(presenter == null ? "no GPU presenter" : "presenter rejected frame");
            }

            var bmp = TransferToBitmap(frame, swFrame, pooled: true);
            SetPath(VideoPath.HardwareCopy, DeviceLabel);
            Deliver(bmp, snapshot, frame, swFrame);
            return;
        }

        var sw = ConvertToBitmap(frame, pooled: true);
        SetPath(VideoPath.Software, null);
        Deliver(sw, snapshot, frame, null);
    }

    private void Deliver(WriteableBitmap? bmp, TaskCompletionSource<WriteableBitmap?>? snapshot, AVFrame* frame, AVFrame* swFrame) {
        if (bmp == null) return;
        LastFrameAt = DateTime.UtcNow;
        FrameReady?.Invoke(bmp);
        if (snapshot != null) {
            snapshot.TrySetResult(swFrame != null
                ? TransferToBitmap(frame, swFrame, pooled: false)
                : ConvertToBitmap(frame, pooled: false));
        }
    }

    private WriteableBitmap? TransferToBitmap(AVFrame* hwFrame, AVFrame* swFrame, bool pooled) {
        ffmpeg.av_frame_unref(swFrame);
        var r = ffmpeg.av_hwframe_transfer_data(swFrame, hwFrame, 0);
        if (r < 0) return null;
        return ConvertToBitmap(swFrame, pooled);
    }

    private WriteableBitmap? ConvertToBitmap(AVFrame* frame, bool pooled) {
        var w = frame->width;
        var h = frame->height;
        if (w <= 0 || h <= 0) return null;

        WriteableBitmap bmp;
        if (pooled) {
            var slot = _poolIndex;
            _poolIndex = (_poolIndex + 1) % BitmapPoolSize;
            var existing = _pool[slot];
            if (existing == null || existing.PixelSize.Width != w || existing.PixelSize.Height != h) {
                existing?.Dispose();
                existing = NewBitmap(w, h);
                _pool[slot] = existing;
            }
            bmp = existing;
        } else {
            bmp = NewBitmap(w, h);
        }

        _sws = ffmpeg.sws_getCachedContext(_sws, w, h, (AVPixelFormat)frame->format,
            w, h, AVPixelFormat.AV_PIX_FMT_BGRA, SwsBilinear, null, null, null);
        if (_sws == null) { if (!pooled) bmp.Dispose(); return null; }

        using (var fb = bmp.Lock()) {
            var dst = new byte*[4] { (byte*)fb.Address, null, null, null };
            var dstStride = new int[4] { fb.RowBytes, 0, 0, 0 };
            ffmpeg.sws_scale(_sws, frame->data.ToArray(), frame->linesize.ToArray(), 0, h, dst, dstStride);
        }
        return bmp;
    }

    private static WriteableBitmap NewBitmap(int w, int h) =>
        new(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

    private void SetPath(VideoPath path, string? label) {
        if (path == ActivePath) return;
        VideoLog.Write("decoder", $"path: {ActivePath} -> {path} ({label ?? DeviceLabel ?? "-"})");
        ActivePath = path;
        if (label != null) DeviceLabel = label;
        PathChanged?.Invoke(path);
    }

    private static AVHWDeviceType DeviceTypeOf(AVFrame* frame) => (AVPixelFormat)frame->format switch {
        AVPixelFormat.AV_PIX_FMT_VAAPI => AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI,
        AVPixelFormat.AV_PIX_FMT_D3D11 => AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
        AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX => AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX,
        AVPixelFormat.AV_PIX_FMT_CUDA => AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
        AVPixelFormat.AV_PIX_FMT_VULKAN => AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN,
        _ => AVHWDeviceType.AV_HWDEVICE_TYPE_NONE
    };

    /// <summary>
    /// The bundled Linux libraries reach libva/libdrm through dlopen shims (see
    /// .github/workflows/ffmpeg.yml), so they load on machines without a VA-API stack.
    /// A shim whose library is missing aborts the process on first call, though, so
    /// check up front and fail the session like any other hardware error instead.
    /// </summary>
    private static void EnsureVaapiRuntime() {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var lib in new[] { "libva.so.2", "libva-drm.so.2", "libdrm.so.2" }) {
            if (!NativeLibrary.TryLoad(lib, out _))
                throw new InvalidOperationException($"{lib} is not installed; VA-API needs the system libva and libdrm");
        }
    }

    /// <summary>The platform's native decode API, used when acceleration is forced without a presenter.</summary>
    public static AVHWDeviceType DefaultDeviceType() {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI;
        return AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
    }

    private static AVPixelFormat FindHwPixelFormat(AVCodec* codec, AVHWDeviceType type) {
        const int MethodHwDeviceCtx = 0x01; // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX
        for (var i = 0; ; i++) {
            var cfg = ffmpeg.avcodec_get_hw_config(codec, i);
            if (cfg == null) break;
            if ((cfg->methods & MethodHwDeviceCtx) != 0 && cfg->device_type == type)
                return cfg->pix_fmt;
        }
        throw new FfmpegException($"decoder has no {ffmpeg.av_hwdevice_get_type_name(type)} support", -1);
    }

    private static AVPixelFormat GetFormatCallback(AVCodecContext* ctx, AVPixelFormat* fmts) {
        var self = FromHandle((IntPtr)ctx->opaque);
        var wanted = self?._hwPixFmt ?? AVPixelFormat.AV_PIX_FMT_NONE;
        for (var p = fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            if (*p == wanted) return wanted;
        // The decoder could not offer the hardware format; take ffmpeg's default so
        // the session continues in software rather than failing outright.
        return fmts[0];
    }

    private static int InterruptCallback(void* opaque) {
        var self = FromHandle((IntPtr)opaque);
        if (self == null) return 1;
        if (self._cts.IsCancellationRequested) return 1;
        return DateTime.UtcNow.Ticks > Interlocked.Read(ref self._opDeadlineTicks) ? 1 : 0;
    }

    private static RtspVideoDecoder? FromHandle(IntPtr ptr) {
        if (ptr == IntPtr.Zero) return null;
        try { return GCHandle.FromIntPtr(ptr).Target as RtspVideoDecoder; } catch { return null; }
    }

    private void ArmDeadline(TimeSpan t) => Interlocked.Exchange(ref _opDeadlineTicks, (DateTime.UtcNow + t).Ticks);

    public void Dispose() {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        ArmDeadline(TimeSpan.Zero);
        if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(TimeSpan.FromSeconds(5));
        foreach (var b in _pool) b?.Dispose();
        Array.Clear(_pool);
        _snapshotRequest?.TrySetResult(null);
        if (_selfHandle.IsAllocated) _selfHandle.Free();
        _cts.Dispose();
    }

    private sealed class ZeroCopyProbeFailed : Exception { public ZeroCopyProbeFailed(string m) : base(m) { } }
    private sealed class HardwareDecodeFailed : Exception { public HardwareDecodeFailed(string m) : base(m) { } }
}
