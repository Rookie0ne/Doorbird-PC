using System;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DoorBird.App.Services;
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
    private CancellationTokenSource? _streamCts;
    private Process? _ffmpegProcess;
    private Process? _recordProcess;
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
            var old = _currentImage;
            this.RaiseAndSetIfChanged(ref _currentImage, value);
            if (old != null && !ReferenceEquals(old, value)) {
                Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
            }
        }
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
        ScreenshotCommand = ReactiveCommand.Create(TakeScreenshot);
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

    private void TakeScreenshot() {
        if (CurrentImage == null) {
            Status = "No image to save";
            return;
        }

        try {
            var dir = _deviceService.Settings.RecordingPath;
            Directory.CreateDirectory(dir);
            var filename = $"doorbird_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
            var path = Path.Combine(dir, filename);
            using var fs = File.Create(path);
            CurrentImage.Save(fs);
            Status = $"Screenshot saved: {filename}";
        } catch (Exception ex) {
            Status = $"Screenshot failed: {ex.Message}";
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

        var ffmpegPath = FindFfmpeg();
        if (ffmpegPath == null) {
            Status = "ffmpeg not found - required for recording";
            return;
        }

        var dir = _deviceService.Settings.RecordingPath;
        Directory.CreateDirectory(dir);
        var filename = $"doorbird_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
        var path = Path.Combine(dir, filename);

        var user = _deviceService.Settings.Username;
        var pass = _deviceService.Settings.Password;
        var host = _deviceService.Settings.DeviceHost;

        // Build ffmpeg arguments based on current mode
        string args;
        var rtspUri = _deviceService.Device.RtspUri.ToString();
        var audioUri = $"http://{user}:{pass}@{host}/bha-api/audio-receive.cgi";

        var outputPath = FfmpegPath(path);

        // Record from RTSP (best quality, available in all modes) + audio from HTTP endpoint
        args = $"-y -rtsp_transport tcp -i \"{rtspUri}\" " +
               $"-f mulaw -ar 8000 -ac 1 -i \"{audioUri}\" " +
               $"-c:v copy -c:a aac -b:a 64k -map 0:v:0 -map 1:a:0 " +
               $"-movflags +frag_keyframe+empty_moov \"{outputPath}\"";

        try {
            var psi = new ProcessStartInfo {
                FileName = ffmpegPath,
                Arguments = args,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _recordProcess = Process.Start(psi);
            if (_recordProcess == null) {
                Status = "Failed to start recording";
                return;
            }

            // Drain stderr in background
            _ = Task.Run(async () => {
                try {
                    using var stderr = _recordProcess.StandardError;
                    await stderr.ReadToEndAsync();
                } catch { }
            });

            IsRecording = true;
            Status = $"Recording: {filename}";
        } catch (Exception ex) {
            Status = $"Record failed: {ex.Message}";
        }
    }

    private void StopRecording() {
        if (_recordProcess != null) {
            try {
                if (!_recordProcess.HasExited) {
                    // Send 'q' to ffmpeg stdin to gracefully stop and finalize MP4
                    _recordProcess.StandardInput.Write('q');
                    _recordProcess.StandardInput.Flush();
                    _recordProcess.WaitForExit(5000);
                    if (!_recordProcess.HasExited)
                        _recordProcess.Kill(entireProcessTree: true);
                }
            } catch { }
            _recordProcess.Dispose();
            _recordProcess = null;
        }
        IsRecording = false;
        Status = "Recording saved";
    }

    private void StartStream() {
        if (_streamCts != null) return;
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        switch (Mode) {
            case LiveMode.Rtsp:
                Task.Run(() => RtspLoop(ct), ct);
                break;
            case LiveMode.Mjpeg:
                Task.Run(() => MjpegLoop(ct), ct);
                break;
            default:
                Task.Run(() => SnapshotLoop(ct), ct);
                break;
        }
    }

    private void StopStream() {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        KillFfmpeg();
    }

    private void KillFfmpeg() {
        if (_ffmpegProcess != null) {
            try {
                if (!_ffmpegProcess.HasExited)
                    _ffmpegProcess.Kill(entireProcessTree: true);
            } catch { }
            _ffmpegProcess.Dispose();
            _ffmpegProcess = null;
        }
    }

    /// <summary>
    /// Finds the ffmpeg binary. Returns null if not found.
    /// </summary>
    private static string? FindFfmpeg() {
        // Check PATH
        var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        foreach (var dir in pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries)) {
            var full = Path.Combine(dir, name);
            if (File.Exists(full)) return full;
        }
        // Common locations on Windows
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            string[] commonPaths = {
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    @"scoop\shims\ffmpeg.exe"),
            };
            foreach (var p in commonPaths)
                if (File.Exists(p)) return p;
        }
        // Common locations on macOS (Homebrew)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
            string[] commonPaths = {
                "/opt/homebrew/bin/ffmpeg",       // Apple Silicon
                "/usr/local/bin/ffmpeg",          // Intel
            };
            foreach (var p in commonPaths)
                if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// Normalizes a file path for use in ffmpeg arguments.
    /// FFmpeg on Windows doesn't handle backslashes well in some contexts.
    /// </summary>
    private static string FfmpegPath(string path) {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? path.Replace('\\', '/')
            : path;
    }

    /// <summary>
    /// Spawns ffmpeg to decode the RTSP stream into MJPEG on stdout, then parses JPEG frames.
    /// </summary>
    private async Task RtspLoop(CancellationToken ct) {
        if (_deviceService.Device == null) {
            Status = "Not connected";
            return;
        }

        var ffmpegPath = FindFfmpeg();
        if (ffmpegPath == null) {
            Status = "ffmpeg not found - install ffmpeg for RTSP support";
            return;
        }

        var rtspUri = _deviceService.Device.RtspUri.ToString();

        while (!ct.IsCancellationRequested) {
            try {
                var psi = new ProcessStartInfo {
                    FileName = ffmpegPath,
                    Arguments = $"-rtsp_transport tcp -i \"{rtspUri}\" -f mjpeg -q:v 3 -an pipe:1",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _ffmpegProcess = Process.Start(psi);
                if (_ffmpegProcess == null) {
                    Status = "Failed to start ffmpeg";
                    return;
                }

                // Discard stderr in background to prevent buffer deadlock
                _ = Task.Run(async () => {
                    try {
                        using var stderr = _ffmpegProcess.StandardError;
                        await stderr.ReadToEndAsync(ct);
                    } catch { }
                }, ct);

                Status = "RTSP Stream (ffmpeg)";
                using var stdout = _ffmpegProcess.StandardOutput.BaseStream;
                await ReadMjpegFrames(stdout, ct);

                // If we get here, stream ended — wait briefly then retry
                KillFfmpeg();
                if (!ct.IsCancellationRequested) {
                    Status = "RTSP stream ended - reconnecting...";
                    await Task.Delay(2000, ct);
                }
            } catch (OperationCanceledException) {
                break;
            } catch (Exception ex) {
                KillFfmpeg();
                Status = $"RTSP error: {ex.Message} - reconnecting...";
                try { await Task.Delay(2000, ct); } catch { break; }
            }
        }
    }

    private async Task MjpegLoop(CancellationToken ct) {
        if (_deviceService.Device == null) {
            Status = "Not connected";
            return;
        }

        while (!ct.IsCancellationRequested) {
            try {
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
        _currentImage = null;
        img?.Dispose();
    }
}
