using System;
using System.Buffers;
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
using DoorBird.App.Services;
using ReactiveUI;

namespace DoorBird.App.ViewModels;

public enum LiveMode { Mjpeg, Snapshot }

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
    private CancellationTokenSource? _streamCts;
    private static readonly HttpClient StreamClient;
    private static readonly HttpClient SnapshotClient;

    static LiveViewModel() {
        var handler = new HttpClientHandler {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        // Long timeout for MJPEG stream (stays open indefinitely)
        StreamClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        var handler2 = new HttpClientHandler {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        SnapshotClient = new HttpClient(handler2) { Timeout = TimeSpan.FromSeconds(10) };
    }

    public Bitmap? CurrentImage {
        get => _currentImage;
        set => this.RaiseAndSetIfChanged(ref _currentImage, value);
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

    public ReactiveCommand<Unit, Unit> OpenDoorCommand { get; }
    public ReactiveCommand<Unit, Unit> LightOnCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleListenCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleTalkCommand { get; }

    public LiveViewModel(DeviceService deviceService) {
        _deviceService = deviceService;
        _audioService = new AudioService {
            OutputDeviceName = deviceService.Settings.AudioOutputDevice,
            InputDeviceName = deviceService.Settings.AudioInputDevice
        };
        _mode = deviceService.Settings.LiveViewMode == "Snapshot" ? LiveMode.Snapshot : LiveMode.Mjpeg;

        OpenDoorCommand = ReactiveCommand.CreateFromTask(OpenDoorAsync);
        LightOnCommand = ReactiveCommand.CreateFromTask(LightOnAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshImageAsync);
        ToggleListenCommand = ReactiveCommand.Create(ToggleListen);
        ToggleTalkCommand = ReactiveCommand.Create(ToggleTalk);

        StartStream();
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

    private void StartStream() {
        if (_streamCts != null) return;
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        if (Mode == LiveMode.Mjpeg)
            Task.Run(() => MjpegLoop(ct), ct);
        else
            Task.Run(() => SnapshotLoop(ct), ct);
    }

    private void StopStream() {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
    }

    /// <summary>
    /// Reads the MJPEG multipart stream from video.cgi and extracts JPEG frames.
    /// </summary>
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
        // MJPEG streams are multipart: each frame starts with a boundary,
        // followed by headers, then JPEG data starting with FF D8 and ending with FF D9.
        var buffer = new byte[256 * 1024]; // 256KB read buffer
        using var frameBuffer = new MemoryStream();
        int bufferPos = 0;
        int bufferLen = 0;
        bool inFrame = false;

        while (!ct.IsCancellationRequested) {
            if (bufferPos >= bufferLen) {
                bufferLen = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (bufferLen == 0) break; // Stream ended
                bufferPos = 0;
            }

            for (int i = bufferPos; i < bufferLen; i++) {
                if (!inFrame) {
                    // Look for JPEG SOI marker: FF D8
                    if (i + 1 < bufferLen && buffer[i] == 0xFF && buffer[i + 1] == 0xD8) {
                        frameBuffer.SetLength(0);
                        frameBuffer.WriteByte(0xFF);
                        frameBuffer.WriteByte(0xD8);
                        inFrame = true;
                        i++; // Skip D8
                    }
                } else {
                    frameBuffer.WriteByte(buffer[i]);
                    // Look for JPEG EOI marker: FF D9
                    if (buffer[i] == 0xD9 && frameBuffer.Length >= 2) {
                        var pos = frameBuffer.Length;
                        frameBuffer.Position = pos - 2;
                        int b1 = frameBuffer.ReadByte();
                        int b2 = frameBuffer.ReadByte();
                        if (b1 == 0xFF && b2 == 0xD9) {
                            // Complete JPEG frame
                            frameBuffer.Position = 0;
                            try {
                                var bitmap = new Bitmap(frameBuffer);
                                CurrentImage = bitmap;
                                Status = $"MJPEG - {DateTime.Now:HH:mm:ss}";
                            } catch {
                                // Skip corrupt frames
                            }
                            inFrame = false;
                        }
                    }
                }
            }
            bufferPos = bufferLen; // Consumed all bytes
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
        StopStream();
        _audioService.Dispose();
    }
}
