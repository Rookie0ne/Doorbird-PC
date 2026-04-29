using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using DoorBird.API;
using DoorBird.App.Models;

namespace DoorBird.App.Services;

/// <summary>
/// Listens for DoorBird HTTP push notifications and plays a chime on doorbell events.
/// Owns a <see cref="PushNotificationServer"/> + <see cref="RingPlayer"/> for the
/// duration of a connected session.
/// </summary>
public class DoorbellMonitor : IDisposable {
    private readonly DoorBirdUserDevice _device;
    private readonly IPAddress _deviceIp;
    private readonly AppSettings _settings;
    private PushNotificationServer? _server;
    private RingPlayer? _ringPlayer;

    public bool IsRunning => _server != null;
    public string? Status { get; private set; }

    public DoorbellMonitor(DoorBirdUserDevice device, IPAddress deviceIp, AppSettings settings) {
        _device = device;
        _deviceIp = deviceIp;
        _settings = settings;
    }

    public async Task StartAsync() {
        var localIp = GetLocalAddressForRemote(_deviceIp);
        if (localIp == null) {
            Status = "Could not determine local LAN address; doorbell sound disabled.";
            return;
        }

        _ringPlayer = new RingPlayer { OutputDeviceName = _settings.AudioOutputDevice };

        _server = new PushNotificationServer(_settings.NotificationPort) {
            PreferredLocalAddress = localIp.ToString()
        };
        _server.NotificationReceived += OnNotificationReceived;
        try {
            _server.Start();
        } catch (Exception ex) {
            Status = $"Could not bind notification port {_settings.NotificationPort}: {ex.Message}";
            _server.NotificationReceived -= OnNotificationReceived;
            _server = null;
            _ringPlayer = null;
            return;
        }

        var callbackUrl = $"http://{localIp}:{_settings.NotificationPort}/?event=doorbell";
        try {
            var status = await _device.SetNotificationRule(callbackUrl, _settings.Username, "doorbell");
            Status = status == HttpStatusCode.OK
                ? $"Listening for doorbell at {callbackUrl}"
                : $"Device rejected notification rule: HTTP {(int)status}";
        } catch (Exception ex) {
            Status = $"Failed to register notification rule: {ex.Message}";
        }
    }

    private void OnNotificationReceived(object? sender, PushNotificationEventArgs e) {
        if (string.Equals(e.Event, "doorbell", StringComparison.OrdinalIgnoreCase)) {
            _ringPlayer?.PlayRing();
        }
    }

    public void Stop() {
        if (_server != null) {
            try { _server.NotificationReceived -= OnNotificationReceived; } catch { }
            try { _server.Dispose(); } catch { }
            _server = null;
        }
        _ringPlayer = null;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Finds the local IP that the OS would use to reach <paramref name="remote"/> by
    /// "connecting" a UDP socket — no packet is actually sent.
    /// </summary>
    private static IPAddress? GetLocalAddressForRemote(IPAddress remote) {
        try {
            var family = remote.AddressFamily == AddressFamily.InterNetworkV6
                ? AddressFamily.InterNetworkV6
                : AddressFamily.InterNetwork;
            using var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(remote, 65530);
            if (socket.LocalEndPoint is IPEndPoint local) return local.Address;
        } catch { }
        return null;
    }
}
