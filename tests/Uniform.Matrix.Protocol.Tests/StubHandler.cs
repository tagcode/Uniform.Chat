namespace Uniform.Matrix.Protocol.Tests;

using System.Net;
using System.Text;

/// <summary>HttpMessageHandler that records requests and replies via <paramref name="respond"/>.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    /// <summary>Received requests with their bodies.</summary>
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    /// <summary>Create a JSON response.</summary>
    public static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Record <paramref name="request"/> and its body, then return the stubbed response.</summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Record request
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        // Return stubbed response
        return respond(request);
    }
}
