using DoorBird.API.BhaStructs;
using DoorBird.API.Util;
using Newtonsoft.Json.Linq;
using System.Net;

namespace DoorBird.API;

public class DoorBirdUserDevice : DoorBirdDevice {
    public DoorBirdUserDevice(IPAddress host, string username, string password) : base(host, username, password) {
        if (username.EndsWith("0000")) {
            throw new DeviceAccessException(UserType.User);
        }
    }

    public override UserType UserType => UserType.User;

    public override async Task<HttpStatusCode> Ready() {
        try {
            var http = new BhaHttpJson(UriTools);
            var result = await http.Get("info.cgi");
            return result.StatusCode;
        } catch {
            return 0;
        }
    }

    public Uri LiveImageUri => UriTools.Create("image.cgi");
    public Uri LiveVideoUri => UriTools.Create("video.cgi");

    public async Task<HttpStatusCode> OpenDoor() {
        var http = new BhaHttp(UriTools);
        var result = await http.Get("open-door.cgi");
        return result.StatusCode;
    }

    public async Task<HttpStatusCode> LightOn() {
        var http = new BhaHttp(UriTools);
        var result = await http.Get("light-on.cgi");
        return result.StatusCode;
    }

    public Uri HistoryImageUri(int index) => UriTools.Create("history.cgi", ("index", index.ToString()));

    public async Task<DeviceInfo> DeviceInfo() {
        var http = new BhaHttpJson(UriTools);
        var result = await http.Get("info.cgi");
        var bha = result.Data;

        // Device info is nested: BHA -> VERSION -> [0] -> {fields}
        var version = bha["VERSION"] as JArray;
        var device = version?.First as JObject ?? bha;

        return new DeviceInfo {
            FirmwareVersion = device["FIRMWARE"]?.ToString() ?? "",
            BuildNumber = device["BUILD_NUMBER"]?.ToString() ?? "",
            WifiMacAddress = device["WIFI_MAC_ADDR"]?.ToString() ?? "",
            DeviceType = device["DEVICE-TYPE"]?.ToString() ?? "",
            Relays = device["RELAYS"]?.ToObject<string[]>() ?? Array.Empty<string>()
        };
    }

    public async Task<List<NotificationRule>> ListNotificationRules() {
        var http = new BhaHttpMap(UriTools);
        var result = await http.Get("notification.cgi");
        var rules = new List<NotificationRule>();
        // Parse notification rules from key-value response
        foreach (var kvp in result.Data) {
            if (kvp.Key.StartsWith("http")) {
                rules.Add(new NotificationRule {
                    Url = kvp.Key,
                    Event = kvp.Value
                });
            }
        }
        return rules;
    }

    public async Task<HttpStatusCode> SetNotificationRule(string url, string subscriber, string events) {
        var http = new BhaHttp(UriTools);
        var result = await http.Get("notification.cgi",
            ("url", url),
            ("user", subscriber),
            ("event", events));
        return result.StatusCode;
    }

    public async Task<HttpStatusCode> ResetNotificationRules() {
        var http = new BhaHttp(UriTools);
        var result = await http.Get("notification.cgi", ("reset", "1"));
        return result.StatusCode;
    }

    public async Task<bool> MonitorDoorbell() {
        var http = new BhaHttpMap(UriTools);
        var result = await http.Get("monitor.cgi", ("check", "doorbell"));
        return result.Data.TryGetValue("doorbell", out var v) && v == "1";
    }

    public async Task<bool> MonitorMotion() {
        var http = new BhaHttpMap(UriTools);
        var result = await http.Get("monitor.cgi", ("check", "motionsensor"));
        return result.Data.TryGetValue("motionsensor", out var v) && v == "1";
    }

    public Uri RtspUri => new Uri($"rtsp://{UriTools.Username}:{UriTools.Password}@{UriTools.Host}/mpeg/media.amp");

    public Uri AudioTxUri => UriTools.Create("audio-transmit.cgi");
    public Uri AudioRxUri => UriTools.Create("audio-receive.cgi");
    public string AudioContentType => "audio/basic;rate=8000";
}
