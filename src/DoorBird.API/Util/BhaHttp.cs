using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Http.Headers;

namespace DoorBird.API.Util;

public class BhaHttpResult {
    public HttpStatusCode StatusCode { get; set; }
    public string Content { get; set; } = "";
}

public class BhaHttpMapResult {
    public HttpStatusCode StatusCode { get; set; }
    public Dictionary<string, string> Data { get; set; } = new();
}

public class BhaHttpJsonResult {
    public HttpStatusCode StatusCode { get; set; }
    public JObject Data { get; set; } = new();
}

public class BhaHttp {
    protected readonly BhaUriTools UriTools;
    private static readonly HttpClient Client;

    static BhaHttp() {
        var handler = new HttpClientHandler {
            // Accept self-signed certs from DoorBird devices
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        Client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public BhaHttp(BhaUriTools uriTools) {
        UriTools = uriTools;
    }

    public async Task<BhaHttpResult> Get(string endpoint, params (string key, string value)[] queryParams) {
        var uri = UriTools.Create(endpoint, queryParams);
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(UriTools.GetBasicAuthHeader());

        var response = await Client.SendAsync(request);
        return new BhaHttpResult {
            StatusCode = response.StatusCode,
            Content = await response.Content.ReadAsStringAsync()
        };
    }

    protected async Task<HttpResponseMessage> GetRaw(string endpoint, params (string key, string value)[] queryParams) {
        var uri = UriTools.Create(endpoint, queryParams);
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(UriTools.GetBasicAuthHeader());
        return await Client.SendAsync(request);
    }
}

public class BhaHttpMap : BhaHttp {
    public BhaHttpMap(BhaUriTools uriTools) : base(uriTools) { }

    public new async Task<BhaHttpMapResult> Get(string endpoint, params (string key, string value)[] queryParams) {
        var result = await base.Get(endpoint, queryParams);
        var data = new Dictionary<string, string>();
        foreach (var line in result.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            var parts = line.Split('=', 2);
            if (parts.Length == 2) {
                data[parts[0].Trim()] = parts[1].Trim();
            }
        }
        return new BhaHttpMapResult {
            StatusCode = result.StatusCode,
            Data = data
        };
    }
}

public class BhaHttpJson : BhaHttp {
    public BhaHttpJson(BhaUriTools uriTools) : base(uriTools) { }

    public new async Task<BhaHttpJsonResult> Get(string endpoint, params (string key, string value)[] queryParams) {
        var result = await base.Get(endpoint, queryParams);
        var json = JObject.Parse(result.Content);
        var bha = json["BHA"] as JObject ?? new JObject();
        return new BhaHttpJsonResult {
            StatusCode = result.StatusCode,
            Data = bha
        };
    }
}
