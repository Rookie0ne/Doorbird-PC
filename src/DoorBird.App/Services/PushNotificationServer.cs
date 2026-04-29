using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace DoorBird.App.Services;

public class PushNotificationEventArgs : EventArgs {
    public string Event { get; }
    public PushNotificationEventArgs(string eventName) => Event = eventName;
}

public class PushNotificationServer : IDisposable {
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    public int Port { get; }

    /// <summary>Optional LAN IP to bind to if the wildcard prefix isn't permitted (Windows without urlacl).</summary>
    public string? PreferredLocalAddress { get; set; }

    public event EventHandler<PushNotificationEventArgs>? NotificationReceived;

    public PushNotificationServer(int port) {
        Port = port;
    }

    public void Start() {
        _cts = new CancellationTokenSource();

        // Try in order: wildcard (LAN-reachable, works without admin on Linux/macOS),
        // then the specific LAN IP (works without admin on Windows),
        // then localhost (last resort — the device on the LAN won't reach us, but startup still succeeds).
        var prefixes = new System.Collections.Generic.List<string> { $"http://+:{Port}/" };
        if (!string.IsNullOrWhiteSpace(PreferredLocalAddress))
            prefixes.Add($"http://{PreferredLocalAddress}:{Port}/");
        prefixes.Add($"http://localhost:{Port}/");

        foreach (var prefix in prefixes) {
            try {
                _listener = new HttpListener();
                _listener.Prefixes.Add(prefix);
                _listener.Start();
                break;
            } catch {
                _listener = null;
            }
        }
        if (_listener == null) throw new InvalidOperationException($"Could not bind notification listener on port {Port}.");

        _listenTask = Task.Run(() => ListenLoop(_cts.Token));
    }

    private async Task ListenLoop(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            HttpListener? listener;
            try {
                listener = _listener;
                if (listener?.IsListening != true) break;
            } catch (ObjectDisposedException) {
                break;
            }

            try {
                var context = await listener.GetContextAsync();
                var query = context.Request.QueryString;
                var eventName = query["event"] ?? "unknown";
                NotificationReceived?.Invoke(this, new PushNotificationEventArgs(eventName));

                context.Response.StatusCode = 200;
                context.Response.Close();
            } catch {
                // Any failure (listener closed, request aborted, handler threw) — bail out.
                break;
            }
        }
    }

    public void Stop() {
        try { _cts?.Cancel(); } catch { }
        var listener = _listener;
        _listener = null;
        try { listener?.Stop(); } catch { }
        try { listener?.Close(); } catch { }
    }

    public void Dispose() {
        Stop();
        try { _cts?.Dispose(); } catch { }
        _cts = null;
    }
}
