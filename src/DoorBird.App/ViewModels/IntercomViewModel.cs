using System;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using DoorBird.App.Services;
using ReactiveUI;

namespace DoorBird.App.ViewModels;

public class IntercomViewModel : ViewModelBase, IDisposable {
    private readonly DeviceService _deviceService;
    private readonly AudioService _audioService;
    private string _status = "Idle";
    private bool _isListening;
    private bool _isTalking;
    private float _rxLevel;
    private float _txLevel;
    private CancellationTokenSource? _levelCts;

    public string Status {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public bool IsListening {
        get => _isListening;
        set => this.RaiseAndSetIfChanged(ref _isListening, value);
    }

    public bool IsTalking {
        get => _isTalking;
        set => this.RaiseAndSetIfChanged(ref _isTalking, value);
    }

    public float RxLevel {
        get => _rxLevel;
        set => this.RaiseAndSetIfChanged(ref _rxLevel, value);
    }

    public float TxLevel {
        get => _txLevel;
        set => this.RaiseAndSetIfChanged(ref _txLevel, value);
    }

    public ReactiveCommand<Unit, Unit> ToggleListenCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleTalkCommand { get; }

    public IntercomViewModel(DeviceService deviceService) {
        _deviceService = deviceService;
        _audioService = new AudioService();

        ToggleListenCommand = ReactiveCommand.Create(ToggleListen);
        ToggleTalkCommand = ReactiveCommand.Create(ToggleTalk);
    }

    private void ToggleListen() {
        if (_deviceService.Device == null) {
            Status = "Not connected";
            return;
        }

        if (IsListening) {
            _audioService.StopReceiving();
            IsListening = false;
            Status = IsTalking ? "Talking" : "Idle";
            StopLevelMonitor();
        } else {
            try {
                _audioService.StartReceiving(
                    _deviceService.Device.AudioRxUri,
                    _deviceService.Settings.Username,
                    _deviceService.Settings.Password);
                IsListening = true;
                Status = IsTalking ? "Listening & Talking" : "Listening";
                StartLevelMonitor();
            } catch (Exception ex) {
                Status = $"Audio error: {ex.Message}";
            }
        }
    }

    private void ToggleTalk() {
        if (_deviceService.Device == null) {
            Status = "Not connected";
            return;
        }

        if (IsTalking) {
            _audioService.StopTransmitting();
            IsTalking = false;
            Status = IsListening ? "Listening" : "Idle";
            StopLevelMonitor();
        } else {
            try {
                _audioService.StartTransmitting(
                    _deviceService.Device.AudioTxUri,
                    _deviceService.Settings.Username,
                    _deviceService.Settings.Password);
                IsTalking = true;
                Status = IsListening ? "Listening & Talking" : "Talking";
                StartLevelMonitor();
            } catch (Exception ex) {
                Status = $"Mic error: {ex.Message}";
            }
        }
    }

    private void StartLevelMonitor() {
        if (_levelCts != null) return;
        _levelCts = new CancellationTokenSource();
        var ct = _levelCts.Token;
        Task.Run(async () => {
            while (!ct.IsCancellationRequested) {
                RxLevel = _audioService.RxLevel;
                TxLevel = _audioService.TxLevel;
                await Task.Delay(100, ct);
            }
        }, ct);
    }

    private void StopLevelMonitor() {
        if (IsListening || IsTalking) return;
        _levelCts?.Cancel();
        _levelCts?.Dispose();
        _levelCts = null;
        RxLevel = 0;
        TxLevel = 0;
    }

    public void Dispose() {
        _levelCts?.Cancel();
        _levelCts?.Dispose();
        _audioService.Dispose();
    }
}
