using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PortAudioSharp;

namespace DoorBird.App.Services;

public record AudioDeviceInfo(int Index, string Name, bool IsInput, bool IsOutput);

/// <summary>
/// Cross-platform audio service for DoorBird intercom.
/// Handles bidirectional G.711 mu-law audio over HTTP.
/// </summary>
public class AudioService : IDisposable {
    private const int SampleRate = 8000;
    private const int FramesPerBuffer = 800; // 100ms buffer

    private static readonly HttpClient HttpClient;

    private PortAudioSharp.Stream? _playbackStream;
    private PortAudioSharp.Stream? _captureStream;
    private CancellationTokenSource? _rxCts;
    private CancellationTokenSource? _txCts;
    private Task? _rxTask;
    private Task? _txTask;

    // Circular buffer for received audio
    private readonly byte[] _playbackBuffer = new byte[SampleRate * 2]; // 1 second of PCM16
    private int _playbackWritePos;
    private int _playbackReadPos;
    private int _playbackAvailable;
    private readonly object _playbackLock = new();

    // Buffer for captured audio to send
    private readonly byte[] _captureBuffer = new byte[SampleRate * 2];
    private int _captureWritePos;
    private int _captureReadPos;
    private int _captureAvailable;
    private readonly object _captureLock = new();

    private static bool _paInitialized;

    public float RxLevel { get; private set; }
    public float TxLevel { get; private set; }
    public bool IsReceiving => _rxTask is { IsCompleted: false };
    public bool IsTransmitting => _txTask is { IsCompleted: false };

    /// <summary>Configured output device name. Null = system default.</summary>
    public string? OutputDeviceName { get; set; }

    /// <summary>Configured input device name. Null = system default.</summary>
    public string? InputDeviceName { get; set; }

    static AudioService() {
        var handler = new HttpClientHandler {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        HttpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    // Suppress noisy ALSA/JACK warnings on Linux during PortAudio init
    private delegate void AlsaErrorHandler(string file, int line, string function, int err, string fmt);
    [DllImport("libasound.so.2", EntryPoint = "snd_lib_error_set_handler", CallingConvention = CallingConvention.Cdecl)]
    private static extern int AlsaSetErrorHandler(AlsaErrorHandler? handler);
    private static readonly AlsaErrorHandler SilentHandler = (_, _, _, _, _) => { };

    // Redirect native stderr to suppress JACK warnings (no error handler API)
    [DllImport("libc", EntryPoint = "dup", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Dup(int fd);
    [DllImport("libc", EntryPoint = "dup2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Dup2(int oldfd, int newfd);
    [DllImport("libc", EntryPoint = "open", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);
    [DllImport("libc", EntryPoint = "close", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Close(int fd);

    private static void EnsureInitialized() {
        if (_paInitialized) return;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
            try { AlsaSetErrorHandler(SilentHandler); } catch { }

            // Temporarily redirect stderr to /dev/null to silence JACK warnings
            int savedStderr = -1;
            int devNull = -1;
            try {
                savedStderr = Dup(2);
                devNull = Open("/dev/null", 1 /* O_WRONLY */);
                if (devNull >= 0) Dup2(devNull, 2);
            } catch { }

            try {
                PortAudio.Initialize();
            } finally {
                // Restore stderr
                try {
                    if (savedStderr >= 0) { Dup2(savedStderr, 2); Close(savedStderr); }
                    if (devNull >= 0) Close(devNull);
                } catch { }
            }
        } else {
            PortAudio.Initialize();
        }
        _paInitialized = true;
    }

    /// <summary>
    /// Lists all available audio devices. Safe to call multiple times.
    /// </summary>
    public static List<AudioDeviceInfo> ListDevices() {
        EnsureInitialized();
        var devices = new List<AudioDeviceInfo>();
        int count = PortAudio.DeviceCount;
        for (int i = 0; i < count; i++) {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.maxInputChannels > 0 || info.maxOutputChannels > 0) {
                devices.Add(new AudioDeviceInfo(
                    i,
                    info.name,
                    info.maxInputChannels > 0,
                    info.maxOutputChannels > 0));
            }
        }
        return devices;
    }

    /// <summary>
    /// Resolves a saved device name to a PortAudio device index.
    /// Falls back to system default if the name is null/empty or not found.
    /// </summary>
    private static int ResolveOutputDevice(string? name) {
        if (string.IsNullOrEmpty(name)) return PortAudio.DefaultOutputDevice;
        int count = PortAudio.DeviceCount;
        for (int i = 0; i < count; i++) {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.maxOutputChannels > 0 && info.name == name)
                return i;
        }
        return PortAudio.DefaultOutputDevice;
    }

    private static int ResolveInputDevice(string? name) {
        if (string.IsNullOrEmpty(name)) return PortAudio.DefaultInputDevice;
        int count = PortAudio.DeviceCount;
        for (int i = 0; i < count; i++) {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.maxInputChannels > 0 && info.name == name)
                return i;
        }
        return PortAudio.DefaultInputDevice;
    }

    public void StartReceiving(Uri audioRxUri, string username, string password) {
        EnsureInitialized();
        _rxCts = new CancellationTokenSource();

        int deviceIndex = ResolveOutputDevice(OutputDeviceName);
        var deviceInfo = PortAudio.GetDeviceInfo(deviceIndex);

        var outputParams = new StreamParameters {
            device = deviceIndex,
            channelCount = 1,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = deviceInfo.defaultLowOutputLatency
        };

        _playbackStream = new PortAudioSharp.Stream(
            inParams: null,
            outParams: outputParams,
            sampleRate: SampleRate,
            framesPerBuffer: FramesPerBuffer,
            streamFlags: StreamFlags.ClipOff,
            callback: PlaybackCallback,
            userData: IntPtr.Zero);

        _playbackStream.Start();
        _rxTask = Task.Run(() => ReceiveLoop(audioRxUri, username, password, _rxCts.Token));
    }

    public void StartTransmitting(Uri audioTxUri, string username, string password) {
        EnsureInitialized();
        _txCts = new CancellationTokenSource();

        int deviceIndex = ResolveInputDevice(InputDeviceName);
        var deviceInfo = PortAudio.GetDeviceInfo(deviceIndex);

        var inputParams = new StreamParameters {
            device = deviceIndex,
            channelCount = 1,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = deviceInfo.defaultLowInputLatency
        };

        _captureStream = new PortAudioSharp.Stream(
            inParams: inputParams,
            outParams: null,
            sampleRate: SampleRate,
            framesPerBuffer: FramesPerBuffer,
            streamFlags: StreamFlags.ClipOff,
            callback: CaptureCallback,
            userData: IntPtr.Zero);

        _captureStream.Start();
        _txTask = Task.Run(() => TransmitLoop(audioTxUri, username, password, _txCts.Token));
    }

    private StreamCallbackResult PlaybackCallback(
        IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData) {

        int bytesNeeded = (int)frameCount * 2;
        unsafe {
            var outPtr = (byte*)output;
            lock (_playbackLock) {
                for (int i = 0; i < bytesNeeded; i++) {
                    if (_playbackAvailable > 0) {
                        outPtr[i] = _playbackBuffer[_playbackReadPos];
                        _playbackReadPos = (_playbackReadPos + 1) % _playbackBuffer.Length;
                        _playbackAvailable--;
                    } else {
                        outPtr[i] = 0;
                    }
                }
            }
        }
        return StreamCallbackResult.Continue;
    }

    private StreamCallbackResult CaptureCallback(
        IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData) {

        int bytesAvailable = (int)frameCount * 2;
        unsafe {
            var inPtr = (byte*)input;

            float sum = 0;
            for (int i = 0; i < (int)frameCount; i++) {
                short sample = (short)(inPtr[i * 2] | (inPtr[i * 2 + 1] << 8));
                sum += sample * sample;
            }
            TxLevel = (float)Math.Sqrt(sum / frameCount) / 32768f;

            lock (_captureLock) {
                for (int i = 0; i < bytesAvailable; i++) {
                    if (_captureAvailable < _captureBuffer.Length) {
                        _captureBuffer[_captureWritePos] = inPtr[i];
                        _captureWritePos = (_captureWritePos + 1) % _captureBuffer.Length;
                        _captureAvailable++;
                    }
                }
            }
        }
        return StreamCallbackResult.Continue;
    }

    private async Task ReceiveLoop(Uri uri, string username, string password, CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                var request = new HttpRequestMessage(HttpMethod.Get, uri);
                var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);

                using var response = await HttpClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[800];

                while (!ct.IsCancellationRequested) {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (read == 0) break;

                    var pcm = MuLawCodec.DecodeToPcm(buffer, 0, read);

                    float sum = 0;
                    for (int i = 0; i < read; i++) {
                        short sample = MuLawCodec.Decode(buffer[i]);
                        sum += sample * sample;
                    }
                    RxLevel = (float)Math.Sqrt(sum / read) / 32768f;

                    lock (_playbackLock) {
                        for (int i = 0; i < pcm.Length; i++) {
                            _playbackBuffer[_playbackWritePos] = pcm[i];
                            _playbackWritePos = (_playbackWritePos + 1) % _playbackBuffer.Length;
                            if (_playbackAvailable < _playbackBuffer.Length)
                                _playbackAvailable++;
                        }
                    }
                }
            } catch (OperationCanceledException) {
                break;
            } catch {
                if (!ct.IsCancellationRequested)
                    await Task.Delay(1000, ct);
            }
        }
    }

    private async Task TransmitLoop(Uri uri, string username, string password, CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                var request = new HttpRequestMessage(HttpMethod.Post, uri);
                var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);

                request.Content = new PushStreamContent(async (outputStream, _, _) => {
                    var pcmChunk = new byte[FramesPerBuffer * 2];
                    while (!ct.IsCancellationRequested) {
                        int available;
                        lock (_captureLock) {
                            available = _captureAvailable;
                        }

                        if (available >= pcmChunk.Length) {
                            lock (_captureLock) {
                                for (int i = 0; i < pcmChunk.Length; i++) {
                                    pcmChunk[i] = _captureBuffer[_captureReadPos];
                                    _captureReadPos = (_captureReadPos + 1) % _captureBuffer.Length;
                                    _captureAvailable--;
                                }
                            }

                            var encoded = MuLawCodec.EncodePcm(pcmChunk, 0, pcmChunk.Length);
                            await outputStream.WriteAsync(encoded, 0, encoded.Length, ct);
                            await outputStream.FlushAsync(ct);
                        } else {
                            await Task.Delay(10, ct);
                        }
                    }
                }, "audio/basic");

                using var response = await HttpClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, ct);
            } catch (OperationCanceledException) {
                break;
            } catch {
                if (!ct.IsCancellationRequested)
                    await Task.Delay(1000, ct);
            }
        }
    }

    public void StopReceiving() {
        _rxCts?.Cancel();
        _playbackStream?.Stop();
        _playbackStream?.Dispose();
        _playbackStream = null;
        _rxCts?.Dispose();
        _rxCts = null;
        RxLevel = 0;
    }

    public void StopTransmitting() {
        _txCts?.Cancel();
        _captureStream?.Stop();
        _captureStream?.Dispose();
        _captureStream = null;
        _txCts?.Dispose();
        _txCts = null;
        TxLevel = 0;
    }

    public void Dispose() {
        StopReceiving();
        StopTransmitting();
    }
}

/// <summary>
/// HttpContent that writes to the stream via a push callback.
/// Used for streaming audio data to the DoorBird device.
/// </summary>
internal class PushStreamContent : HttpContent {
    private readonly Func<System.IO.Stream, HttpContent, System.Net.TransportContext?, Task> _onStream;

    public PushStreamContent(Func<System.IO.Stream, HttpContent, System.Net.TransportContext?, Task> onStream,
        string mediaType) {
        _onStream = onStream;
        Headers.ContentType = new MediaTypeHeaderValue(mediaType);
    }

    protected override async Task SerializeToStreamAsync(System.IO.Stream stream, System.Net.TransportContext? context) {
        await _onStream(stream, this, context);
    }

    protected override bool TryComputeLength(out long length) {
        length = -1;
        return false;
    }
}
