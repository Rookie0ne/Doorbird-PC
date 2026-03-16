using System.Reactive;
using DoorBird.App.Services;
using ReactiveUI;

namespace DoorBird.App.ViewModels;

public class SettingsViewModel : ViewModelBase {
    private readonly DeviceService _deviceService;

    private string _deviceHost;
    private string _username;
    private string _password;
    private decimal _notificationPort;
    private string _recordingPath;
    private string _status = "";

    public string DeviceHost {
        get => _deviceHost;
        set => this.RaiseAndSetIfChanged(ref _deviceHost, value);
    }

    public string Username {
        get => _username;
        set => this.RaiseAndSetIfChanged(ref _username, value);
    }

    public string Password {
        get => _password;
        set => this.RaiseAndSetIfChanged(ref _password, value);
    }

    public decimal NotificationPort {
        get => _notificationPort;
        set => this.RaiseAndSetIfChanged(ref _notificationPort, value);
    }

    public string RecordingPath {
        get => _recordingPath;
        set => this.RaiseAndSetIfChanged(ref _recordingPath, value);
    }

    public string Status {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    public SettingsViewModel(DeviceService deviceService) {
        _deviceService = deviceService;
        var s = deviceService.Settings;
        _deviceHost = s.DeviceHost;
        _username = s.Username;
        _password = s.Password;
        _notificationPort = (decimal)s.NotificationPort;
        _recordingPath = s.RecordingPath;

        SaveCommand = ReactiveCommand.Create(SaveSettings);
    }

    private void SaveSettings() {
        var s = _deviceService.Settings;
        s.DeviceHost = DeviceHost;
        s.Username = Username;
        s.Password = Password;
        s.NotificationPort = (int)NotificationPort;
        s.RecordingPath = RecordingPath;
        s.Save();
        Status = "Settings saved";
    }
}
