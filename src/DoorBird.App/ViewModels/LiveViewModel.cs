using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using DoorBird.App.Services;
using ReactiveUI;

namespace DoorBird.App.ViewModels;

public class LiveViewModel : ViewModelBase, IDisposable {
    private readonly DeviceService _deviceService;
    private Bitmap? _currentImage;
    private string _status = "";
    private CancellationTokenSource? _refreshCts;
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

    public ReactiveCommand<Unit, Unit> OpenDoorCommand { get; }
    public ReactiveCommand<Unit, Unit> LightOnCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public LiveViewModel(DeviceService deviceService) {
        _deviceService = deviceService;

        OpenDoorCommand = ReactiveCommand.CreateFromTask(OpenDoorAsync);
        LightOnCommand = ReactiveCommand.CreateFromTask(LightOnAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshImageAsync);

        StartAutoRefresh();
    }

    private void StartAutoRefresh() {
        _refreshCts = new CancellationTokenSource();
        Task.Run(async () => {
            while (!_refreshCts.Token.IsCancellationRequested) {
                await RefreshImageAsync();
                await Task.Delay(1000, _refreshCts.Token);
            }
        }, _refreshCts.Token);
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
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
    }
}
