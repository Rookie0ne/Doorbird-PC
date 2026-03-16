namespace DoorBird.API.BhaStructs;

public struct DeviceInfo {
    public string FirmwareVersion { get; set; }
    public string BuildNumber { get; set; }
    public string WifiMacAddress { get; set; }
    public string DeviceType { get; set; }
    public string[] Relays { get; set; }
}
