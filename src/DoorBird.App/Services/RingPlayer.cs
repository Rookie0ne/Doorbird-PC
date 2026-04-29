using System;
using System.Threading;
using PortAudioSharp;

namespace DoorBird.App.Services;

/// <summary>
/// Plays a synthesized "ding-dong" chime through PortAudio when the doorbell rings.
/// One stream is opened per ring and closed when playback completes.
/// </summary>
public class RingPlayer {
    private const int SampleRate = 44100;
    private const uint FramesPerBuffer = 1024;

    public string? OutputDeviceName { get; set; }

    private readonly object _lock = new();
    private byte[]? _audio;
    private int _readPos;
    private PortAudioSharp.Stream? _stream;

    public void PlayRing() {
        lock (_lock) {
            if (_stream != null) return; // a ring is still playing — drop overlapping rings

            try {
                AudioService.EnsureInitialized();

                _audio = GenerateDingDong();
                _readPos = 0;

                int deviceIndex = ResolveOutputDevice(OutputDeviceName);
                if (deviceIndex < 0) return;
                var deviceInfo = PortAudio.GetDeviceInfo(deviceIndex);

                var outputParams = new StreamParameters {
                    device = deviceIndex,
                    channelCount = 1,
                    sampleFormat = SampleFormat.Int16,
                    suggestedLatency = deviceInfo.defaultLowOutputLatency
                };

                _stream = new PortAudioSharp.Stream(
                    inParams: null,
                    outParams: outputParams,
                    sampleRate: SampleRate,
                    framesPerBuffer: FramesPerBuffer,
                    streamFlags: StreamFlags.ClipOff,
                    callback: PlaybackCallback,
                    userData: IntPtr.Zero);

                _stream.Start();
            } catch {
                CloseStreamLocked();
            }
        }
    }

    private StreamCallbackResult PlaybackCallback(
        IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData) {

        var data = _audio;
        if (data == null) return StreamCallbackResult.Complete;

        int bytesNeeded = (int)frameCount * 2;
        bool finished = false;
        unsafe {
            var outPtr = (byte*)output;
            for (int i = 0; i < bytesNeeded; i++) {
                if (_readPos < data.Length) {
                    outPtr[i] = data[_readPos++];
                } else {
                    outPtr[i] = 0;
                    finished = true;
                }
            }
        }

        if (finished) {
            ThreadPool.QueueUserWorkItem(_ => {
                lock (_lock) { CloseStreamLocked(); }
            });
            return StreamCallbackResult.Complete;
        }
        return StreamCallbackResult.Continue;
    }

    private void CloseStreamLocked() {
        try { _stream?.Stop(); } catch { }
        try { _stream?.Dispose(); } catch { }
        _stream = null;
        _audio = null;
        _readPos = 0;
    }

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

    private static byte[] GenerateDingDong() {
        // G5 ding, then D5 dong — classic Westminster-style two-tone bell
        var ding = GenerateBellTone(783.99, 0.7);
        var gap = new short[(int)(SampleRate * 0.05)];
        var dong = GenerateBellTone(587.33, 1.0);

        var combined = new short[ding.Length + gap.Length + dong.Length];
        Array.Copy(ding, 0, combined, 0, ding.Length);
        Array.Copy(gap, 0, combined, ding.Length, gap.Length);
        Array.Copy(dong, 0, combined, ding.Length + gap.Length, dong.Length);

        var bytes = new byte[combined.Length * 2];
        Buffer.BlockCopy(combined, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static short[] GenerateBellTone(double freq, double durationSec) {
        int n = (int)(SampleRate * durationSec);
        var samples = new short[n];
        double tau = durationSec * 0.4;          // exponential decay time constant
        const double attackSec = 0.005;          // 5ms attack to avoid click
        const double amp = 0.45 * 32767;
        double w = 2 * Math.PI * freq;
        for (int i = 0; i < n; i++) {
            double t = (double)i / SampleRate;
            double envelope = Math.Exp(-t / tau);
            double attack = Math.Min(1.0, t / attackSec);
            // Fundamental + soft second harmonic for a bell-ish timbre
            double s = Math.Sin(w * t) + 0.3 * Math.Sin(2 * w * t);
            double v = s * envelope * attack * amp / 1.3;
            if (v > short.MaxValue) v = short.MaxValue;
            else if (v < short.MinValue) v = short.MinValue;
            samples[i] = (short)v;
        }
        return samples;
    }
}
