using DoorBird.API.Util;
using System.Net;

namespace DoorBird.API;

public class DoorBirdAdminDevice : DoorBirdDevice {
    public DoorBirdAdminDevice(IPAddress host, string username, string password) : base(host, username, password) {
        if (!username.EndsWith("0000")) {
            throw new DeviceAccessException(UserType.Administrator);
        }
    }

    public override UserType UserType => UserType.Administrator;

    public override Task<HttpStatusCode> Ready() {
        throw new NotImplementedException("Admin device support is not yet implemented.");
    }
}
