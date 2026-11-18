namespace Uniform.LiveKit.Tests;

using System.Net;
using System.Text;

/// <summary>Answers HTTP requests by URL as homeservers and LiveKit would, and records them: OpenID tokens of <see cref="Users"/>, <c>.well-known</c> delegations of <see cref="Delegations"/>, and LiveKit's CreateRoom.</summary>
internal sealed class FakeNetwork : HttpMessageHandler
{
    /// <summary>Users by OpenID token, answered at any homeserver's <c>userinfo</c>.</summary>
    public Dictionary<string, string> Users { get; } = [];
    /// <summary>The <c>m.server</c> of server names that delegate.</summary>
    public Dictionary<string, string> Delegations { get; } = [];
    /// <summary>Request URLs in order.</summary>
    public List<string> Requests { get; } = [];
    /// <summary>Bodies of LiveKit calls, with their authorization.</summary>
    public List<(string Body, string? Authorization)> LiveKitCalls { get; } = [];

    /// <summary>Return a client over this network.</summary>
    public HttpClient CreateClient() => new(this);

    /// <summary>Answer by path.</summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Recorded, then by path
        Uri url = request.RequestUri!;
        Requests.Add(url.ToString());
        if (url.AbsolutePath == "/.well-known/matrix/server") return Delegations.TryGetValue(url.Host, out string? server) ? Json($$"""{"m.server":"{{server}}"}""") : new HttpResponseMessage(HttpStatusCode.NotFound);
        if (url.AbsolutePath == "/_matrix/federation/v1/openid/userinfo")
        {
            string token = Uri.UnescapeDataString(url.Query.Split("access_token=")[1]);
            return Users.TryGetValue(token, out string? user) ? Json($$"""{"sub":"{{user}}"}""") : Json("""{"errcode":"M_UNKNOWN_TOKEN"}""", HttpStatusCode.Unauthorized);
        }
        if (url.AbsolutePath.StartsWith("/twirp/livekit.RoomService/", StringComparison.Ordinal))
        {
            LiveKitCalls.Add((await request.Content!.ReadAsStringAsync(cancellationToken), request.Headers.Authorization?.Parameter));
            return Json("""{"name":"room"}""");
        }
        // Return none
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    /// <summary>Return a JSON answer.</summary>
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
