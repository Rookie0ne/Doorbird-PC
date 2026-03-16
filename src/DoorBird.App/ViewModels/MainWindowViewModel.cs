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
        set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowHomeCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowLiveViewCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowIntercomCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowSettingsCommand { get; }

    public DeviceService DeviceService => _deviceService;

    public MainWindowViewModel() {
        _deviceService = new DeviceService();
        _currentPage = new HomeViewModel();

        ConnectCommand = ReactiveCommand.CreateFromTask(ConnectAsync);
        ShowHomeCommand = ReactiveCommand.Create(() => { CurrentPage = new HomeViewModel(); });
        ShowLiveViewCommand = ReactiveCommand.Create(() => { CurrentPage = new LiveViewModel(_deviceService); });
        ShowHistoryCommand = ReactiveCommand.Create(() => { CurrentPage = new HistoryViewModel(_deviceService); });
        ShowIntercomCommand = ReactiveCommand.Create(() => { CurrentPage = new IntercomViewModel(_deviceService); });
        ShowSettingsCommand = ReactiveCommand.Create(() => { CurrentPage = new SettingsViewModel(_deviceService); });
    }

    private async Task ConnectAsync() {
        ConnectionStatus = "Connecting...";
        var success = await _deviceService.Connect();
        IsConnected = success;
        ConnectionStatus = success ? "Connected" : "Connection failed";
    }
}
