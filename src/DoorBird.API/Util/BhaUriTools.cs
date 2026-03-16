using System.Net;
using System.Text;

namespace DoorBird.API.Util;

public class BhaUriTools {
    public IPAddress Host { get; }
    public string Username { get; }
    public string Password { get; }

    private const string ApiPrefix = "/bha-api/";

    public BhaUriTools(IPAddress host, string username, string password) {
        Host = host;
        Username = username;
        Password = password;
    }

    public Uri Create(string endpoint, params (string key, string value)[] queryParams) {
        var builder = new UriBuilder("http", Host.ToString(), 80, ApiPrefix + endpoint);
        if (queryParams.Length > 0) {
            var query = new StringBuilder();
            for (int i = 0; i < queryParams.Length; i++) {
                if (i > 0) query.Append('&');
                query.Append(Uri.EscapeDataString(queryParams[i].key));
                query.Append('=');
                query.Append(Uri.EscapeDataString(queryParams[i].value));
            }
            builder.Query = query.ToString();
        }
        return builder.Uri;
    }

    public string GetBasicAuthHeader() {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
        return $"Basic {credentials}";
    }
}
