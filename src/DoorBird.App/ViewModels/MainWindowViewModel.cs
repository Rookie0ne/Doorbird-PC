using System;
using System.Globalization;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Data.Converters;
using DoorBird.App.Services;
using ReactiveUI;

namespace DoorBird.App.ViewModels;

public class BoolToMarginConverter : IValueConverter {
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
        return value is true ? new Thickness(10) : new Thickness(0);
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) {
        throw new NotSupportedException();
    }
}

public class MainWindowViewModel : ViewModelBase {
    public static readonly BoolToMarginConverter ToolbarMarginConverter = new();
    private readonly DeviceService _deviceService;
    private ViewModelBase _currentPage;
    private string _connectionStatus = "Disconnected";
    private bool _isConnected;
    private string _connectButtonText = "Connect";
    private bool _isToolbarVisible = true;

    public ViewModelBase CurrentPage {
        get => _currentPage;
        set {
            // Dispose the previous page if it owns resources (LiveViewModel runs an
            // ffmpeg subprocess + audio threads + bitmap stream; without disposal these
            // accumulate every time the user navigates away and back).
            var old = _currentPage;
            this.RaiseAndSetIfChanged(ref _currentPage, value);
            if (!ReferenceEquals(old, value) && old is IDisposable disposable) {
                disposable.Dispose();
            }
        }
    }

    public string ConnectionStatus {
        get => _connectionStatus;
        set => this.RaiseAndSetIfChanged(ref _connectionStatus, value);
    }

    public bool IsConnected {
        get => _isConnected;
        set {
            this.RaiseAndSetIfChanged(ref _isConnected, value);
            ConnectButtonText = value ? "Disconnect" : "Connect";
        }
    }

    public string ConnectButtonText {
        get => _connectButtonText;
        set => this.RaiseAndSetIfChanged(ref _connectButtonText, value);
    }

    public bool IsToolbarVisible {
        get => _isToolbarVisible;
        set => this.RaiseAndSetIfChanged(ref _isToolbarVisible, value);
    }

    public ReactiveCommand<Unit, Unit> ConnectToggleCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowHomeCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowLiveViewCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowIntercomCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowInfoCommand { get; }

    public DeviceService DeviceService => _deviceService;

    public MainWindowViewModel() {
        _deviceService = new DeviceService();
        _currentPage = new HomeViewModel();

        ConnectToggleCommand = ReactiveCommand.CreateFromTask(ConnectToggleAsync);
        ShowHomeCommand = ReactiveCommand.Create(() => { CurrentPage = new HomeViewModel(); });
        ShowLiveViewCommand = ReactiveCommand.Create(() => { CurrentPage = CreateLiveViewModel(); });
        ShowHistoryCommand = ReactiveCommand.Create(() => { CurrentPage = new HistoryViewModel(_deviceService); });
        ShowIntercomCommand = ReactiveCommand.Create(() => { CurrentPage = new IntercomViewModel(_deviceService); });
        ShowSettingsCommand = ReactiveCommand.Create(() => { CurrentPage = new SettingsViewModel(_deviceService); });
        ShowInfoCommand = ReactiveCommand.Create(() => { CurrentPage = new InfoViewModel(_deviceService); });

        if (_deviceService.Settings.AutoConnect)
            Task.Run(AutoConnectAsync);
    }

    private async Task AutoConnectAsync() {
        ConnectionStatus = "Connecting...";
        var success = await _deviceService.Connect();
        IsConnected = success;
        ConnectionStatus = success ? "Connected" : "Connection failed";
        if (success)
            CurrentPage = CreateLiveViewModel();
    }

    private LiveViewModel CreateLiveViewModel() {
        var vm = new LiveViewModel(_deviceService);
        vm.OnMaximizeChanged = maximized => IsToolbarVisible = !maximized;
        IsToolbarVisible = !vm.IsMaximized;
        return vm;
    }

    private async Task ConnectToggleAsync() {
        if (IsConnected) {
            _deviceService.Disconnect();
            IsConnected = false;
            ConnectionStatus = "Disconnected";
            CurrentPage = new HomeViewModel();
        } else {
            ConnectionStatus = "Connecting...";
            var success = await _deviceService.Connect();
            IsConnected = success;
            ConnectionStatus = success ? "Connected" : "Connection failed";
        }
    }
}
