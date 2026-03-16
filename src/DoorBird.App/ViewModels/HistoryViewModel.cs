using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using DoorBird.App.Services;
using ReactiveUI;

namespace DoorBird.App.ViewModels;

public class HistoryViewModel : ViewModelBase {
    private readonly DeviceService _deviceService;
    private Bitmap? _currentImage;
    private int _currentIndex = 1;
    private string _status = "";
    private static readonly HttpClient HttpClient;

    static HistoryViewModel() {
        var handler = new HttpClientHandler {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        HttpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    public Bitmap? CurrentImage {
        get => _currentImage;
        set => this.RaiseAndSetIfChanged(ref _currentImage, value);
    }

    public int CurrentIndex {
        get => _currentIndex;
        set => this.RaiseAndSetIfChanged(ref _currentIndex, value);
    }

    public string Status {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public ReactiveCommand<Unit, Unit> PreviousCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadCommand { get; }

    public HistoryViewModel(DeviceService deviceService) {
        _deviceService = deviceService;

        PreviousCommand = ReactiveCommand.CreateFromTask(async () => {
            if (CurrentIndex > 1) {
                CurrentIndex--;
                await LoadImageAsync();
            }
        });
        NextCommand = ReactiveCommand.CreateFromTask(async () => {
            CurrentIndex++;
            await LoadImageAsync();
        });
        LoadCommand = ReactiveCommand.CreateFromTask(LoadImageAsync);

        // Auto-load first image on open
        Task.Run(async () => await LoadImageAsync());
    }

    private async Task LoadImageAsync() {
        if (_deviceService.Device == null) {
            Status = "Not connected";
            return;
        }

        try {
            Status = "Loading...";
            var uri = _deviceService.Device.HistoryImageUri(CurrentIndex);
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
            Status = $"Image #{CurrentIndex}";
        } catch (Exception ex) {
            Status = $"Error: {ex.Message}";
        }
    }
}
