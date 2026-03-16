using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DoorBird.API;
using DoorBird.App.Models;

namespace DoorBird.App.Services;

public class DeviceService {
    public DoorBirdUserDevice? Device { get; private set; }
    public bool IsConnected => Device != null;
    public AppSettings Settings { get; }

    public DeviceService() {
        Settings = AppSettings.Load();
    }

    public async Task<bool> Connect() {
        if (string.IsNullOrWhiteSpace(Settings.DeviceHost) ||
            string.IsNullOrWhiteSpace(Settings.Username) ||
            string.IsNullOrWhiteSpace(Settings.Password))
            return false;

        try {
            if (!IPAddress.TryParse(Settings.DeviceHost, out var ip)) {
                var addresses = await Dns.GetHostAddressesAsync(Settings.DeviceHost);
                ip = addresses.FirstOrDefault() ?? throw new Exception("Could not resolve host");
            }

            var device = new DoorBirdUserDevice(ip, Settings.Username, Settings.Password);
            var status = await device.Ready();
            if (status == HttpStatusCode.OK) {
                Device = device;
                return true;
            }
        } catch { }
        return false;
    }

    public void Disconnect() {
        Device = null;
    }
}
