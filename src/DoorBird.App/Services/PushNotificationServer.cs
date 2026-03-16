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

    public event EventHandler<PushNotificationEventArgs>? NotificationReceived;

    public PushNotificationServer(int port) {
        Port = port;
    }

    public void Start() {
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{Port}/");
        try {
            _listener.Start();
        } catch {
            // Fallback to localhost only if wildcard fails (no elevated privileges)
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
        }
        _listenTask = Task.Run(() => ListenLoop(_cts.Token));
    }

    private async Task ListenLoop(CancellationToken ct) {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true) {
            try {
                var context = await _listener.GetContextAsync();
                var query = context.Request.QueryString;
                var eventName = query["event"] ?? "unknown";
                NotificationReceived?.Invoke(this, new PushNotificationEventArgs(eventName));

                context.Response.StatusCode = 200;
                context.Response.Close();
            } catch (ObjectDisposedException) {
                break;
            } catch (HttpListenerException) {
                break;
            }
        }
    }

    public void Stop() {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
    }

    public void Dispose() {
        Stop();
        _cts?.Dispose();
    }
}
