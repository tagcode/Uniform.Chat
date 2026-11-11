namespace Uniform.LiveKit;

using System.Net.Http.Json;
using System.Text.Json;
using Uniform.LiveKit.Protocol;

/// <summary>Asks a MatrixRTC authorization service, e.g. lk-jwt-service from a homeserver's <c>rtc_foci</c>, for LiveKit's URL and a token to join a call with.</summary>
/// <param name="http">The client of the requests.</param>
/// <param name="serviceUrl">The service's base URL, <c>livekit_service_url</c> of the focus, e.g. <c>https://example.org/livekit/jwt</c>.</param>
public sealed class MatrixRtcAuthorizationClient(HttpClient http, string serviceUrl)
{
    /// <summary>Return the URL and token for <paramref name="request"/> from <c>/get_token</c>.</summary>
    public Task<SfuResponse> GetTokenAsync(TokenRequest request, CancellationToken cancellationToken = default) => PostAsync("get_token", request, cancellationToken);

    /// <summary>Return the URL and token for <paramref name="request"/> from the legacy <c>/sfu/get</c>, which older services only have.</summary>
    public Task<SfuResponse> GetLegacyAsync(LegacySfuRequest request, CancellationToken cancellationToken = default) => PostAsync("sfu/get", request, cancellationToken);

    /// <summary>Post <paramref name="body"/> to <paramref name="path"/>; return the answer, or throw <see cref="HttpRequestException"/> with the service's reason.</summary>
    private async Task<SfuResponse> PostAsync<T>(string path, T body, CancellationToken cancellationToken)
    {
        // JSON both ways
        using HttpResponseMessage response = await http.PostAsJsonAsync($"{serviceUrl.TrimEnd('/')}/{path}", body, cancellationToken).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"The MatrixRTC service refused /{path}: {(int)response.StatusCode} {text}", null, response.StatusCode);
        SfuResponse? answer = JsonSerializer.Deserialize<SfuResponse>(text);
        if (answer is null || answer.Url.Length == 0 || answer.Jwt.Length == 0) throw new HttpRequestException($"The MatrixRTC service gave no URL or token from /{path}.");
        // Return answer
        return answer;
    }
}
