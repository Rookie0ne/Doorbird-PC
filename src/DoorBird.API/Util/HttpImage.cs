using System.Globalization;
using System.Net.Http.Headers;

namespace DoorBird.API.Util;

public class HttpImage {
    private readonly BhaUriTools _uriTools;
    private static readonly HttpClient Client;

    static HttpImage() {
        var handler = new HttpClientHandler {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        Client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public HttpImage(BhaUriTools uriTools) {
        _uriTools = uriTools;
    }

    public async Task<(byte[] imageData, DateTimeOffset timestamp)> DownloadImage(Uri uri) {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(_uriTools.GetBasicAuthHeader());

        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var imageData = await response.Content.ReadAsByteArrayAsync();
        var timestamp = DateTimeOffset.UtcNow;

        if (response.Headers.TryGetValues("X-Timestamp", out var values)) {
            var tsStr = values.FirstOrDefault();
            if (tsStr != null && long.TryParse(tsStr, out var epoch)) {
                timestamp = DateTimeOffset.FromUnixTimeSeconds(epoch);
            }
        }

        return (imageData, timestamp);
    }

    public async Task SaveImage(Uri uri, string basePath) {
        var (imageData, timestamp) = await DownloadImage(uri);
        var dt = timestamp.LocalDateTime;
        var dir = Path.Combine(basePath, dt.Year.ToString(), dt.Month.ToString("D2"), dt.Day.ToString("D2"));
        Directory.CreateDirectory(dir);
        var filename = $"doorbird_{dt:yyyy-MM-dd_HH-mm-ss}.jpg";
        await File.WriteAllBytesAsync(Path.Combine(dir, filename), imageData);
    }
}
