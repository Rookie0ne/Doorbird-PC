using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DoorBird.App.Services;
using ReactiveUI;

namespace DoorBird.App.ViewModels;

public class InfoViewModel : ViewModelBase {
    private readonly DeviceService _deviceService;
    private string _info = "Loading...";

    public string Info {
        get => _info;
        set => this.RaiseAndSetIfChanged(ref _info, value);
    }

    public InfoViewModel(DeviceService deviceService) {
        _deviceService = deviceService;
        Task.Run(GatherInfo);
    }

    private async Task GatherInfo() {
        var sb = new StringBuilder();

        // App info
        var appVersion = Assembly.GetExecutingAssembly().GetName().Version;
        sb.AppendLine("APPLICATION");
        sb.AppendLine($"  Version:           {appVersion?.ToString(3) ?? "unknown"}");
        sb.AppendLine($"  Build:             {(appVersion?.Revision > 0 ? appVersion.ToString() : "Release")}");
        sb.AppendLine();

        // Platform info
        sb.AppendLine("PLATFORM");
        sb.AppendLine($"  OS:                {RuntimeInformation.OSDescription}");
        sb.AppendLine($"  Architecture:      {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"  .NET Runtime:      {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"  Process Arch:      {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine();

        // Library versions
        sb.AppendLine("LIBRARIES");
        sb.AppendLine($"  Avalonia:          {GetPackageVersion("Avalonia")}");
        sb.AppendLine($"  ReactiveUI:        {GetPackageVersion("ReactiveUI")}");
        sb.AppendLine($"  PortAudioSharp2:   {GetPackageVersion("PortAudioSharp")}");
        sb.AppendLine($"  Newtonsoft.Json:   {GetPackageVersion("Newtonsoft.Json")}");

        // PortAudio native
        try {
            var devices = AudioService.ListDevices();
            sb.AppendLine($"  PortAudio Native:  Available ({devices.Count} device(s))");
        } catch (Exception ex) {
            sb.AppendLine($"  PortAudio Native:  Not available ({ex.Message})");
        }

        // FFmpeg (for RTSP mode)
        var ffmpegVersion = GetFfmpegVersion();
        sb.AppendLine($"  FFmpeg:            {ffmpegVersion}");
        sb.AppendLine();

        // Connection info
        sb.AppendLine("CONNECTION");
        if (_deviceService.IsConnected && _deviceService.Device != null) {
            sb.AppendLine($"  Status:            Connected");
            sb.AppendLine($"  Device Host:       {_deviceService.Settings.DeviceHost}");
            sb.AppendLine($"  Username:          {_deviceService.Settings.Username}");

            try {
                var info = await _deviceService.Device.DeviceInfo();
                sb.AppendLine();
                sb.AppendLine("DOORBIRD DEVICE");
                sb.AppendLine($"  Device Type:       {info.DeviceType}");
                sb.AppendLine($"  Firmware:          {info.FirmwareVersion}");
                sb.AppendLine($"  Build Number:      {info.BuildNumber}");
                sb.AppendLine($"  WiFi MAC:          {info.WifiMacAddress}");
                if (info.Relays is { Length: > 0 })
                    sb.AppendLine($"  Relays:            {string.Join(", ", info.Relays)}");
            } catch (Exception ex) {
                sb.AppendLine($"  Device Info:       Error: {ex.Message}");
            }

            // Video stream info via ffprobe
            try {
                var streamInfo = await ProbeVideoStream(
                    _deviceService.Device.RtspUri.ToString());
                if (streamInfo != null) {
                    sb.AppendLine();
                    sb.AppendLine("VIDEO STREAM (RTSP)");
                    sb.Append(streamInfo);
                }
            } catch { }

            // Also probe MJPEG endpoint
            try {
                var mjpegUri = _deviceService.Device.LiveVideoUri.ToString();
                // Add auth to URI for ffprobe
                var authUri = mjpegUri.Replace("http://",
                    $"http://{_deviceService.Settings.Username}:{_deviceService.Settings.Password}@");
                var mjpegInfo = await ProbeVideoStream(authUri);
                if (mjpegInfo != null) {
                    sb.AppendLine();
                    sb.AppendLine("VIDEO STREAM (MJPEG)");
                    sb.Append(mjpegInfo);
                }
            } catch { }

            // Doorbell listener
            sb.AppendLine();
            sb.AppendLine("DOORBELL LISTENER");
            var monitor = _deviceService.DoorbellMonitor;
            if (monitor == null) {
                sb.AppendLine("  Status:            Not initialized");
            } else {
                // StartAsync runs in the background after Connect — wait briefly so we don't
                // render this section before it has a chance to set Status.
                for (int i = 0; i < 30 && monitor.Status == null; i++)
                    await Task.Delay(100);
                sb.AppendLine($"  Running:           {(monitor.IsRunning ? "Yes" : "No")}");
                sb.AppendLine($"  Notification Port: {_deviceService.Settings.NotificationPort}");
                sb.AppendLine($"  Status:            {monitor.Status ?? "Initializing..."}");
            }
        } else {
            sb.AppendLine($"  Status:            Not connected");
        }
        sb.AppendLine();

        // Audio devices
        try {
            var devices = AudioService.ListDevices();
            sb.AppendLine("AUDIO DEVICES");
            sb.AppendLine($"  Configured Output: {_deviceService.Settings.AudioOutputDevice ?? "(System Default)"}");
            sb.AppendLine($"  Configured Input:  {_deviceService.Settings.AudioInputDevice ?? "(System Default)"}");
            sb.AppendLine();
            sb.AppendLine("  Available Output Devices:");
            foreach (var d in devices) {
                if (d.IsOutput) sb.AppendLine($"    - {d.Name}");
            }
            sb.AppendLine("  Available Input Devices:");
            foreach (var d in devices) {
                if (d.IsInput) sb.AppendLine($"    - {d.Name}");
            }
        } catch { }

        // Settings path
        sb.AppendLine();
        sb.AppendLine("PATHS");
        var configDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        sb.AppendLine($"  Settings:          {System.IO.Path.Combine(configDir, "DoorBird", "settings.json")}");
        sb.AppendLine($"  Recording:         {_deviceService.Settings.RecordingPath}");

        Info = sb.ToString();
    }

    private static async Task<string?> ProbeVideoStream(string uri) {
        try {
            var psi = new System.Diagnostics.ProcessStartInfo {
                FileName = "ffprobe",
                Arguments = $"-rtsp_transport tcp -v quiet -print_format json -show_streams -show_format \"{uri}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;

            var output = await proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync(); // drain stderr
            proc.WaitForExit(10000);
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return null;

            var json = Newtonsoft.Json.Linq.JObject.Parse(output);
            var sb = new StringBuilder();

            var streams = json["streams"] as Newtonsoft.Json.Linq.JArray;
            if (streams != null) {
                foreach (var stream in streams) {
                    var codecType = stream["codec_type"]?.ToString() ?? "";
                    var codecName = stream["codec_name"]?.ToString() ?? "unknown";
                    var codecLong = stream["codec_long_name"]?.ToString() ?? "";
                    var profile = stream["profile"]?.ToString() ?? "";

                    if (codecType == "video") {
                        var width = stream["width"]?.ToString() ?? "?";
                        var height = stream["height"]?.ToString() ?? "?";
                        var fps = stream["r_frame_rate"]?.ToString() ?? "";
                        var bitrate = stream["bit_rate"]?.ToString();
                        var pixFmt = stream["pix_fmt"]?.ToString() ?? "";

                        sb.AppendLine($"  Codec:             {codecName} ({codecLong})");
                        if (!string.IsNullOrEmpty(profile))
                            sb.AppendLine($"  Profile:           {profile}");
                        sb.AppendLine($"  Resolution:        {width}x{height}");
                        sb.AppendLine($"  Pixel Format:      {pixFmt}");
                        if (!string.IsNullOrEmpty(fps))
                            sb.AppendLine($"  Frame Rate:        {FormatFrameRate(fps)}");
                        if (!string.IsNullOrEmpty(bitrate) && long.TryParse(bitrate, out var bps))
                            sb.AppendLine($"  Bitrate:           {bps / 1000} kbps");
                    } else if (codecType == "audio") {
                        var sampleRate = stream["sample_rate"]?.ToString() ?? "?";
                        var channels = stream["channels"]?.ToString() ?? "?";
                        sb.AppendLine($"  Audio Codec:       {codecName} ({codecLong})");
                        sb.AppendLine($"  Sample Rate:       {sampleRate} Hz");
                        sb.AppendLine($"  Channels:          {channels}");
                    }
                }
            }

            // Format-level bitrate if per-stream wasn't available
            var format = json["format"];
            if (format != null) {
                var fmtBitrate = format["bit_rate"]?.ToString();
                if (!string.IsNullOrEmpty(fmtBitrate) && long.TryParse(fmtBitrate, out var fmtBps)) {
                    if (!sb.ToString().Contains("Bitrate:"))
                        sb.AppendLine($"  Bitrate (total):   {fmtBps / 1000} kbps");
                }
                var fmtName = format["format_long_name"]?.ToString();
                if (!string.IsNullOrEmpty(fmtName))
                    sb.AppendLine($"  Container:         {fmtName}");
            }

            return sb.Length > 0 ? sb.ToString() : null;
        } catch {
            return null;
        }
    }

    private static string FormatFrameRate(string rationalFps) {
        var parts = rationalFps.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], out var num) && double.TryParse(parts[1], out var den) && den > 0)
            return $"{num / den:F1} fps ({rationalFps})";
        return rationalFps;
    }

    private static string GetFfmpegVersion() {
        try {
            var psi = new System.Diagnostics.ProcessStartInfo {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return "Not found";
            var firstLine = proc.StandardOutput.ReadLine() ?? "";
            proc.WaitForExit(2000);
            // First line is like "ffmpeg version 6.1.1 Copyright ..."
            return firstLine.Length > 0 ? firstLine : "Installed (unknown version)";
        } catch {
            return "Not found (required for RTSP mode)";
        }
    }

    private static string GetPackageVersion(string assemblyPrefix) {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
            var name = asm.GetName();
            if (name.Name != null && name.Name.StartsWith(assemblyPrefix, StringComparison.OrdinalIgnoreCase)) {
                var ver = name.Version;
                if (ver != null) return ver.ToString(3);
            }
        }
        return "unknown";
    }
}
