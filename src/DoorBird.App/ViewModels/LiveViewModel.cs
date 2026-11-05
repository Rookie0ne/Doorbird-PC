using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DoorBird.App.Services;
using DoorBird.App.Video;
using DoorBird.App.Video.Gpu;
using ReactiveUI;

namespace DoorBird.App.ViewModels;

public enum LiveMode { Rtsp, Mjpeg, Snapshot }

public class BoolToActiveBrushConverter : IValueConverter {
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
        return value is true ? new SolidColorBrush(Color.Parse("#4CAF50")) : Brushes.Transparent;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) {
        throw new NotSupportedException();
    }
}

public class LiveViewModel : ViewModelBase, IDisposable {
    public static readonly BoolToActiveBrushConverter BoolToBrush = new();
    private readonly DeviceService _deviceService;
    private readonly AudioService _audioService;
    private Bitmap? _currentImage;
    private string _status = "";
    private LiveMode _mode = LiveMode.Mjpeg;
    private bool _isListening;
    private bool _isTalking;
    private bool _isMaximized;
    private bool _isRecording;
    private bool _isGpuPresenting;
    private string _videoPathLabel = "";
    private IGpuVideoPresenter? _gpuPresenter;
    private string? _gpuUnavailableReason;
    private CancellationTokenSource? _streamCts;
    private RtspVideoDecoder? _decoder;
    private Mp4Recorder? _recorder;
    private bool _currentIsPooled;
    private DateTime _lastFrameAt;
    private static readonly HttpClient StreamClient;
    private static readonly HttpClient SnapshotClient;

    static LiveViewModel() {
        var handler = new HttpClientHandler {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        StreamClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        var handler2 = new HttpClientHandler {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        SnapshotClient = new HttpClient(handler2) { Timeout = TimeSpan.FromSeconds(10) };
    }

    public Bitmap? CurrentImage {
        get => _currentImage;
        set {
            // Avalonia Bitmap wraps Skia memory the GC can't see, so replacing the
            // current frame without disposing the previous one leaks unmanaged memory
            // at ~MB-per-frame rates on a live MJPEG stream. Defer disposal to the UI
            // thread at background priority so any in-flight render of the old bitmap
            // completes first.
            //
            // RTSP frames come from a small pool the decoder owns and reuses, so those
            // must not be disposed here; _currentIsPooled tracks which kind is current.
            var old = _currentImage;
            var oldWasPooled = _currentIsPooled;
            _currentIsPooled = false;
            this.RaiseAndSetIfChanged(ref _currentImage, value);
            if (old != null && !ReferenceEquals(old, value) && !oldWasPooled) {
                Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
            }
        }
    }

    private void SetPooledFrame(WriteableBitmap frame) {
        var old = _currentImage;
        var oldWasPooled = _currentIsPooled;
        _currentIsPooled = true;
        this.RaiseAndSetIfChanged(ref _currentImage, frame, nameof(CurrentImage));
        if (old != null && !ReferenceEquals(old, frame) && !oldWasPooled) {
            Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
        }
    }

    /// <summary>True while RTSP frames are presented straight from the GPU (no bitmap).</summary>
    public bool IsGpuPresenting {
        get => _isGpuPresenting;
        set => this.RaiseAndSetIfChanged(ref _isGpuPresenting, value);
    }

    /// <summary>Human-readable decode/presentation path for the status line.</summary>
    public string VideoPathLabel {
        get => _videoPathLabel;
        set => this.RaiseAndSetIfChanged(ref _videoPathLabel, value);
    }

    /// <summary>Set by the view's VideoSurface once the compositor-backed presenter exists.</summary>
    public IGpuVideoPresenter? GpuPresenter {
        get => _gpuPresenter;
        set => this.RaiseAndSetIfChanged(ref _gpuPresenter, value);
    }

    /// <summary>Why the view could not build a zero-copy presenter, for the status line and Info page.</summary>
    public string? GpuUnavailableReason {
        get => _gpuUnavailableReason;
        set => this.RaiseAndSetIfChanged(ref _gpuUnavailableReason, value);
    }

    public string Status {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public LiveMode Mode {
        get => _mode;
        set => this.RaiseAndSetIfChanged(ref _mode, value);
    }

    public bool IsListening {
        get => _isListening;
        set => this.RaiseAndSetIfChanged(ref _isListening, value);
    }

    public bool IsTalking {
        get => _isTalking;
        set => this.RaiseAndSetIfChanged(ref _isTalking, value);
    }

    public bool IsMaximized {
        get => _isMaximized;
        set => this.RaiseAndSetIfChanged(ref _isMaximized, value);
    }

    public bool IsRecording {
        get => _isRecording;
        set => this.RaiseAndSetIfChanged(ref _isRecording, value);
    }

    public Action<bool>? OnMaximizeChanged { get; set; }

    public ReactiveCommand<Unit, Unit> OpenDoorCommand { get; }
    public ReactiveCommand<Unit, Unit> LightOnCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleListenCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleTalkCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleMaximizeCommand { get; }
    public ReactiveCommand<Unit, Unit> ScreenshotCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleRecordCommand { get; }

    public LiveViewModel(DeviceService deviceService) {
        _deviceService = deviceService;
        _audioService = new AudioService {
            OutputDeviceName = deviceService.Settings.AudioOutputDevice,
            InputDeviceName = deviceService.Settings.AudioInputDevice
        };
        _mode = deviceService.Settings.LiveViewMode switch {
            "Rtsp" => LiveMode.Rtsp,
            "Snapshot" => LiveMode.Snapshot,
            _ => LiveMode.Mjpeg
        };
        _isMaximized = deviceService.Settings.LiveViewMaximized;

        OpenDoorCommand = ReactiveCommand.CreateFromTask(OpenDoorAsync);
        LightOnCommand = ReactiveCommand.CreateFromTask(LightOnAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshImageAsync);
        ToggleListenCommand = ReactiveCommand.Create(ToggleListen);
        ToggleTalkCommand = ReactiveCommand.Create(ToggleTalk);
        ToggleMaximizeCommand = ReactiveCommand.Create(ToggleMaximize);
        ScreenshotCommand = ReactiveCommand.CreateFromTask(TakeScreenshot);
        ToggleRecordCommand = ReactiveCommand.Create(ToggleRecord);

        StartStream();
    }

    private void ToggleMaximize() {
        IsMaximized = !IsMaximized;
        OnMaximizeChanged?.Invoke(IsMaximized);
    }

    private void ToggleListen() {
        if (_deviceService.Device == null) return;
        if (IsListening) {
            _audioService.StopReceiving();
            IsListening = false;
        } else {
            try {
                _audioService.StartReceiving(
                    _deviceService.Device.AudioRxUri,
                    _deviceService.Settings.Username,
                    _deviceService.Settings.Password);
                IsListening = true;
            } catch { }
        }
    }

    private void ToggleTalk() {
        if (_deviceService.Device == null) return;
        if (IsTalking) {
            _audioService.StopTransmitting();
            IsTalking = false;
        } else {
            try {
                _audioService.StartTransmitting(
                    _deviceService.Device.AudioTxUri,
                    _deviceService.Settings.Username,
                    _deviceService.Settings.Password);
                IsTalking = true;
            } catch { }
        }
    }

    private async Task TakeScreenshot() {
        Bitmap? image = CurrentImage;
        Bitmap? owned = null;
        if (_decoder != null && (IsGpuPresenting || image == null)) {
            // The live frame lives on the GPU; ask the decoder for a system-memory copy.
            owned = await _decoder.CaptureSnapshotAsync(TimeSpan.FromSeconds(3));
            image = owned;
        }
        if (image == null) {
            Status = "No image to save";
            return;
        }

        try {
            var dir = _deviceService.Settings.RecordingPath;
            Directory.CreateDirectory(dir);
            var filename = $"doorbird_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
            var path = Path.Combine(dir, filename);
            using var fs = File.Create(path);
            image.Save(fs);
            Status = $"Screenshot saved: {filename}";
        } catch (Exception ex) {
            Status = $"Screenshot failed: {ex.Message}";
        } finally {
            owned?.Dispose();
        }
    }

    private void ToggleRecord() {
        if (IsRecording) {
            StopRecording();
        } else {
            StartRecording();
        }
    }

    private void StartRecording() {
        if (_deviceService.Device == null) {
            Status = "Not connected";
            return;
        }
        if (!FfmpegNative.TryInitialize(out var ffErr)) {
            Status = $"Recording unavailable: {ffErr}";
            return;
        }

        var dir = _deviceService.Settings.RecordingPath;
        Directory.CreateDirectory(dir);
        var filename = $"doorbird_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
        var path = Path.Combine(dir, filename);

        var user = Uri.EscapeDataString(_deviceService.Settings.Username);
        var pass = Uri.EscapeDataString(_deviceService.Settings.Password);
        var host = _deviceService.Settings.DeviceHost;
        var rtspUri = _deviceService.Device.RtspUri.ToString();
        var audioUri = $"http://{user}:{pass}@{host}/bha-api/audio-receive.cgi";

        try {
            // Records from RTSP (best quality, works in every live mode) plus audio
            // from the HTTP endpoint. The recorder opens its own RTSP session so
            // recording keeps working if the live stream reconnects.
            var rec = new Mp4Recorder(rtspUri, audioUri, path);
            rec.Error += msg => Dispatcher.UIThread.Post(() => Status = $"Recording: {msg}");
            rec.Start();
            _recorder = rec;
            IsRecording = true;
            Status = $"Recording: {filename}";
        } catch (Exception ex) {
            Status = $"Record failed: {ex.Message}";
        }
    }

    private void StopRecording() {
        var rec = _recorder;
        _recorder = null;
        if (rec != null) {
            Status = "Finishing recording...";
            // Stop joins the pumps and writes the trailer; keep the UI responsive.
            Task.Run(() => {
                try { rec.Dispose(); } catch { }
                Dispatcher.UIThread.Post(() => Status = "Recording saved");
            });
        }
        IsRecording = false;
    }

    private void StartStream() {
        if (_streamCts != null) return;
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;
        _lastFrameAt = DateTime.UtcNow;

        Task.Run(() => WatchdogLoop(ct), ct);

        switch (Mode) {
            case LiveMode.Rtsp:
                _ = RtspLoop(ct);
                break;
            case LiveMode.Mjpeg:
                Task.Run(() => MjpegLoop(ct), ct);
                break;
            default:
                Task.Run(() => SnapshotLoop(ct), ct);
                break;
        }
    }

    // Detects half-open TCP sockets (classic laptop sleep/resume failure mode)
    // and ffmpeg/device stalls. The mode loops' inner ReadAsync calls can block
    // indefinitely when the kernel never reports the socket as dead; this
    // watchdog forces a reconnect when no frame has arrived within the threshold.
    private async Task WatchdogLoop(CancellationToken ct) {
        var threshold = Mode == LiveMode.Snapshot
            ? TimeSpan.FromSeconds(8)
            : TimeSpan.FromSeconds(12);

        while (!ct.IsCancellationRequested) {
            try { await Task.Delay(2000, ct); } catch { return; }

            if (DateTime.UtcNow - _lastFrameAt > threshold) {
                Dispatcher.UIThread.Post(() => {
                    if (_streamCts != null && _streamCts.Token == ct && !ct.IsCancellationRequested) {
                        Status = "Stream stalled - reconnecting...";
                        StopStream();
                        StartStream();
                    }
                });
                return;
            }
        }
    }

    private void StopStream() {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        var dec = _decoder;
        _decoder = null;
        if (dec != null) {
            // Dispose joins the decode thread; do it off the UI thread because the
            // presenter may be waiting for the UI thread to import a texture.
            Task.Run(() => { try { dec.Dispose(); } catch { } });
        }
        IsGpuPresenting = false;
        VideoPathLabel = "";
    }

    /// <summary>
    /// Decodes the RTSP stream in-process (bundled FFmpeg). Frames arrive either as
    /// pooled bitmaps (software / copy paths) or straight on the GPU through the
    /// view's presenter (zero-copy path).
    /// </summary>
    private Task RtspLoop(CancellationToken ct) {
        if (_deviceService.Device == null) {
            Status = "Not connected";
            return Task.CompletedTask;
        }
        if (!FfmpegNative.TryInitialize(out var ffErr)) {
            Status = $"RTSP unavailable: {ffErr}";
            return Task.CompletedTask;
        }

        var mode = HardwareAccelerationModeExtensions.ParseMode(_deviceService.Settings.HardwareAcceleration);
        var decoder = new RtspVideoDecoder(_deviceService.Device.RtspUri.ToString(), mode, () => _gpuPresenter);

        decoder.StatusChanged += msg => Dispatcher.UIThread.Post(() => {
            if (!ReferenceEquals(_decoder, decoder)) return;
            Status = msg;
        });
        decoder.PathChanged += path => Dispatcher.UIThread.Post(() => {
            if (!ReferenceEquals(_decoder, decoder)) return;
            var zeroCopy = path == VideoPath.HardwareZeroCopy;
            if (zeroCopy && !IsGpuPresenting) CurrentImage = null;
            IsGpuPresenting = zeroCopy;
            VideoPathLabel = path.Describe(decoder.DeviceLabel);
        });
        decoder.FrameReady += bmp => Dispatcher.UIThread.Post(() => {
            if (!ReferenceEquals(_decoder, decoder) || ct.IsCancellationRequested) return;
            SetPooledFrame(bmp);
            _lastFrameAt = DateTime.UtcNow;
            Status = $"RTSP ({VideoPathLabel}) - {DateTime.Now:HH:mm:ss}";
        }, DispatcherPriority.Render);
        decoder.FramePresented += () => {
            _lastFrameAt = DateTime.UtcNow;
            Dispatcher.UIThread.Post(() => {
                if (!ReferenceEquals(_decoder, decoder) || ct.IsCancellationRequested) return;
                Status = $"RTSP ({VideoPathLabel}) - {DateTime.Now:HH:mm:ss}";
            }, DispatcherPriority.Background);
        };

        _decoder = decoder;
        decoder.Start();
        return Task.CompletedTask;
    }

    private async Task MjpegLoop(CancellationToken ct) {
        if (_deviceService.Device == null) {
            Status = "Not connected";
            return;
        }

        while (!ct.IsCancellationRequested) {
            try {
                _lastFrameAt = DateTime.UtcNow;
                var uri = _deviceService.Device.LiveVideoUri;
                var request = new HttpRequestMessage(HttpMethod.Get, uri);
                var auth = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(
                        $"{_deviceService.Settings.Username}:{_deviceService.Settings.Password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);

                using var response = await StreamClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                Status = "MJPEG Stream";
                await ReadMjpegFrames(stream, ct);
            } catch (OperationCanceledException) {
                break;
            } catch (Exception ex) {
                Status = $"MJPEG error: {ex.Message} - reconnecting...";
                try { await Task.Delay(2000, ct); } catch { break; }
            }
        }
    }

    private async Task ReadMjpegFrames(Stream stream, CancellationToken ct) {
        var buffer = new byte[256 * 1024];
        using var frameBuffer = new MemoryStream();
        int bufferPos = 0;
        int bufferLen = 0;
        bool inFrame = false;

        while (!ct.IsCancellationRequested) {
            if (bufferPos >= bufferLen) {
                bufferLen = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (bufferLen == 0) break;
                bufferPos = 0;
            }

            for (int i = bufferPos; i < bufferLen; i++) {
                if (!inFrame) {
                    if (i + 1 < bufferLen && buffer[i] == 0xFF && buffer[i + 1] == 0xD8) {
                        frameBuffer.SetLength(0);
                        frameBuffer.WriteByte(0xFF);
                        frameBuffer.WriteByte(0xD8);
                        inFrame = true;
                        i++;
                    }
                } else {
                    frameBuffer.WriteByte(buffer[i]);
                    if (buffer[i] == 0xD9 && frameBuffer.Length >= 2) {
                        var pos = frameBuffer.Length;
                        frameBuffer.Position = pos - 2;
                        int b1 = frameBuffer.ReadByte();
                        int b2 = frameBuffer.ReadByte();
                        if (b1 == 0xFF && b2 == 0xD9) {
                            frameBuffer.Position = 0;
                            try {
                                var bitmap = new Bitmap(frameBuffer);
                                CurrentImage = bitmap;
                                _lastFrameAt = DateTime.UtcNow;
                                var modeLabel = Mode == LiveMode.Rtsp ? "RTSP" : "MJPEG";
                                Status = $"{modeLabel} - {DateTime.Now:HH:mm:ss}";
                            } catch { }
                            inFrame = false;
                        }
                    }
                }
            }
            bufferPos = bufferLen;
        }
    }

    private async Task SnapshotLoop(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            await RefreshImageAsync();
            try { await Task.Delay(1000, ct); } catch { break; }
        }
    }

    private async Task RefreshImageAsync() {
        if (_deviceService.Device == null) {
            Status = "Not connected";
            return;
        }

        try {
            var uri = _deviceService.Device.LiveImageUri;
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var auth = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(
                    $"{_deviceService.Settings.Username}:{_deviceService.Settings.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);

            var response = await SnapshotClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadAsByteArrayAsync();
            using var ms = new MemoryStream(data);
            CurrentImage = new Bitmap(ms);
            _lastFrameAt = DateTime.UtcNow;
            Status = $"Snapshot - {DateTime.Now:HH:mm:ss}";
        } catch (Exception ex) {
            Status = $"Error: {ex.Message}";
        }
    }

    private async Task OpenDoorAsync() {
        if (_deviceService.Device != null) {
            var result = await _deviceService.Device.OpenDoor();
            Status = $"Door: {result}";
        }
    }

    private async Task LightOnAsync() {
        if (_deviceService.Device != null) {
            var result = await _deviceService.Device.LightOn();
            Status = $"Light: {result}";
        }
    }

    public void Dispose() {
        if (IsMaximized)
            OnMaximizeChanged?.Invoke(false);
        if (IsRecording)
            StopRecording();
        StopStream();
        _audioService.Dispose();
        var img = _currentImage;
        var pooled = _currentIsPooled;
        _currentImage = null;
        _currentIsPooled = false;
        if (!pooled) img?.Dispose();
    }
}
