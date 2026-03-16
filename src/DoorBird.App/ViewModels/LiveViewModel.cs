using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using DoorBird.App.Services;
using LibVLCSharp.Shared;
using ReactiveUI;

namespace DoorBird.App.ViewModels;

public class LiveViewModel : ViewModelBase, IDisposable {
    private readonly DeviceService _deviceService;
    private Bitmap? _currentImage;
    private string _status = "";
    private bool _useRtsp = true;
    private bool _isRtspActive;
    private CancellationTokenSource? _refreshCts;
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private static readonly HttpClient HttpClient;

    static LiveViewModel() {
        var handler = new HttpClientHandler {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        HttpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    public Bitmap? CurrentImage {
        get => _currentImage;
        set => this.RaiseAndSetIfChanged(ref _currentImage, value);
    }

    public string Status {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public bool UseRtsp {
        get => _useRtsp;
        set {
            this.RaiseAndSetIfChanged(ref _useRtsp, value);
            if (value) StartRtsp(); else StopRtsp();
        }
    }

    public bool IsRtspActive {
        get => _isRtspActive;
        set => this.RaiseAndSetIfChanged(ref _isRtspActive, value);
    }

    public MediaPlayer? MediaPlayer {
        get => _mediaPlayer;
        private set => this.RaiseAndSetIfChanged(ref _mediaPlayer, value);
    }

    public ReactiveCommand<Unit, Unit> OpenDoorCommand { get; }
    public ReactiveCommand<Unit, Unit> LightOnCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleModeCommand { get; }

    public LiveViewModel(DeviceService deviceService) {
        _deviceService = deviceService;

        OpenDoorCommand = ReactiveCommand.CreateFromTask(OpenDoorAsync);
        LightOnCommand = ReactiveCommand.CreateFromTask(LightOnAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshImageAsync);
        ToggleModeCommand = ReactiveCommand.Create(() => { UseRtsp = !UseRtsp; });

        if (_useRtsp)
            StartRtsp();
        else
            StartImagePolling();
    }

    private void StartRtsp() {
        if (_deviceService.Device == null) {
            Status = "Not connected";
            StartImagePolling(); // Fallback
            return;
        }

        StopImagePolling();

        try {
            _libVlc = new LibVLC("--no-audio"); // Video only in live view, audio is in Intercom
            var player = new MediaPlayer(_libVlc);
            var rtspUri = _deviceService.Device.RtspUri.ToString();
            using var media = new Media(_libVlc, rtspUri, FromType.FromLocation);
            media.AddOption(":network-caching=300");
            media.AddOption(":rtsp-tcp");
            player.Media = media;
            player.Play();
            MediaPlayer = player;
            IsRtspActive = true;
            Status = "RTSP Live Stream";
        } catch (Exception ex) {
            Status = $"RTSP failed: {ex.Message} - falling back to image polling";
            IsRtspActive = false;
            _useRtsp = false;
            this.RaisePropertyChanged(nameof(UseRtsp));
            StartImagePolling();
        }
    }

    private void StopRtsp() {
        IsRtspActive = false;
        if (_mediaPlayer != null) {
            _mediaPlayer.Stop();
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
            MediaPlayer = null;
        }
        _libVlc?.Dispose();
        _libVlc = null;
        StartImagePolling();
    }

    private void StartImagePolling() {
        if (_refreshCts != null) return;
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;
        Task.Run(async () => {
            while (!ct.IsCancellationRequested) {
                await RefreshImageAsync();
                await Task.Delay(1000, ct);
            }
        }, ct);
    }

    private void StopImagePolling() {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
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
                System.Text.Encoding.UTF8.GetBytes(
                    $"{_deviceService.Settings.Username}:{_deviceService.Settings.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);

            var response = await HttpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadAsByteArrayAsync();
            using var ms = new MemoryStream(data);
            CurrentImage = new Bitmap(ms);
            Status = $"Live - {DateTime.Now:HH:mm:ss}";
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
        StopImagePolling();
        StopRtsp();
    }
}
