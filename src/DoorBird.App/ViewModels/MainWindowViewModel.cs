using System;
using System.Reactive;
using System.Threading.Tasks;
using DoorBird.App.Services;
using ReactiveUI;

namespace DoorBird.App.ViewModels;

public class MainWindowViewModel : ViewModelBase {
    private readonly DeviceService _deviceService;
    private ViewModelBase _currentPage;
    private string _connectionStatus = "Disconnected";
    private bool _isConnected;
    private string _connectButtonText = "Connect";

    public ViewModelBase CurrentPage {
        get => _currentPage;
        set => this.RaiseAndSetIfChanged(ref _currentPage, value);
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

    public ReactiveCommand<Unit, Unit> ConnectToggleCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowHomeCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowLiveViewCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowIntercomCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowSettingsCommand { get; }

    public DeviceService DeviceService => _deviceService;

    public MainWindowViewModel() {
        _deviceService = new DeviceService();
        _currentPage = new HomeViewModel();

        ConnectToggleCommand = ReactiveCommand.CreateFromTask(ConnectToggleAsync);
        ShowHomeCommand = ReactiveCommand.Create(() => { CurrentPage = new HomeViewModel(); });
        ShowLiveViewCommand = ReactiveCommand.Create(() => { CurrentPage = new LiveViewModel(_deviceService); });
        ShowHistoryCommand = ReactiveCommand.Create(() => { CurrentPage = new HistoryViewModel(_deviceService); });
        ShowIntercomCommand = ReactiveCommand.Create(() => { CurrentPage = new IntercomViewModel(_deviceService); });
        ShowSettingsCommand = ReactiveCommand.Create(() => { CurrentPage = new SettingsViewModel(_deviceService); });

        if (_deviceService.Settings.AutoConnect)
            Task.Run(AutoConnectAsync);
    }

    private async Task AutoConnectAsync() {
        ConnectionStatus = "Connecting...";
        var success = await _deviceService.Connect();
        IsConnected = success;
        ConnectionStatus = success ? "Connected" : "Connection failed";
        if (success)
            CurrentPage = new LiveViewModel(_deviceService);
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
