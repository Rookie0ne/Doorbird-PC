using System.Collections.Generic;
using System.Linq;
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
    private bool _autoConnect;
    private string _selectedLiveViewMode;
    private string? _selectedOutputDevice;
    private string? _selectedInputDevice;
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

    public bool AutoConnect {
        get => _autoConnect;
        set => this.RaiseAndSetIfChanged(ref _autoConnect, value);
    }

    public List<string> LiveViewModes { get; } = new() { "MJPEG Stream", "Snapshot Polling" };

    public string SelectedLiveViewMode {
        get => _selectedLiveViewMode;
        set => this.RaiseAndSetIfChanged(ref _selectedLiveViewMode, value);
    }

    public List<string> OutputDevices { get; }
    public List<string> InputDevices { get; }

    public string? SelectedOutputDevice {
        get => _selectedOutputDevice;
        set => this.RaiseAndSetIfChanged(ref _selectedOutputDevice, value);
    }

    public string? SelectedInputDevice {
        get => _selectedInputDevice;
        set => this.RaiseAndSetIfChanged(ref _selectedInputDevice, value);
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
        _autoConnect = s.AutoConnect;
        _selectedLiveViewMode = s.LiveViewMode == "Snapshot" ? "Snapshot Polling" : "MJPEG Stream";

        // Enumerate audio devices
        const string defaultLabel = "(System Default)";
        var devices = AudioService.ListDevices();

        OutputDevices = new List<string> { defaultLabel };
        OutputDevices.AddRange(devices.Where(d => d.IsOutput).Select(d => d.Name));

        InputDevices = new List<string> { defaultLabel };
        InputDevices.AddRange(devices.Where(d => d.IsInput).Select(d => d.Name));

        // Select saved device, or fall back to default if not found
        _selectedOutputDevice = !string.IsNullOrEmpty(s.AudioOutputDevice) && OutputDevices.Contains(s.AudioOutputDevice)
            ? s.AudioOutputDevice
            : defaultLabel;

        _selectedInputDevice = !string.IsNullOrEmpty(s.AudioInputDevice) && InputDevices.Contains(s.AudioInputDevice)
            ? s.AudioInputDevice
            : defaultLabel;

        SaveCommand = ReactiveCommand.Create(SaveSettings);
    }

    private void SaveSettings() {
        const string defaultLabel = "(System Default)";
        var s = _deviceService.Settings;
        s.DeviceHost = DeviceHost;
        s.Username = Username;
        s.Password = Password;
        s.NotificationPort = (int)NotificationPort;
        s.RecordingPath = RecordingPath;
        s.AutoConnect = AutoConnect;
        s.LiveViewMode = SelectedLiveViewMode == "Snapshot Polling" ? "Snapshot" : "Mjpeg";
        s.AudioOutputDevice = SelectedOutputDevice == defaultLabel ? null : SelectedOutputDevice;
        s.AudioInputDevice = SelectedInputDevice == defaultLabel ? null : SelectedInputDevice;
        s.Save();
        Status = "Settings saved";
    }
}
