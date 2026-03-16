using DoorBird.API.Util;
using System.Net;

namespace DoorBird.API;

public enum UserType {
    User,
    Administrator
}

public abstract class DoorBirdDevice {
    protected BhaUriTools UriTools { get; private set; }
    protected IPAddress Host => UriTools.Host;
    protected string Username => UriTools.Username;

    protected DoorBirdDevice(IPAddress host, string username, string password) {
        UriTools = new BhaUriTools(host, username, password);
    }

    public abstract UserType UserType { get; }
    public abstract Task<HttpStatusCode> Ready();
}
