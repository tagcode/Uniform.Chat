namespace Uniform.Matrix.Client.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Client.Configuration;
using Uniform.Matrix.Client.Storage;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Owns the <see cref="MatrixClient"/>: resumes the stored session or logs in, and stores new sessions.</summary>
public sealed class ClientSession
{
    /// <summary>Name of the <see cref="IHttpClientFactory"/> client used for Matrix requests.</summary>
    public const string HttpClientName = "matrix";

    private readonly IHttpClientFactory httpFactory;
    private readonly ClientStore store;
    private readonly ICredentialPrompt prompt;
    private readonly AccountPaths paths;
    private readonly MatrixOptions options;
    private readonly ILogger<ClientSession> logger;
    private readonly TaskCompletionSource<MatrixClient> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Create the session owner.</summary>
    public ClientSession(IHttpClientFactory httpFactory, ClientStore store, ICredentialPrompt prompt, AccountPaths paths, IOptions<MatrixOptions> options, ILogger<ClientSession> logger)
    {
        // Store dependencies
        this.httpFactory = httpFactory;
        this.store = store;
        this.prompt = prompt;
        this.paths = paths;
        this.options = options.Value;
        this.logger = logger;
    }

    /// <summary>Task that completes with the logged-in client once <see cref="ConnectAsync"/> succeeds.</summary>
    public Task<MatrixClient> WhenReady => ready.Task;

    /// <summary>Resume the stored session when the server still accepts it, otherwise log in with a password; return the logged-in client.</summary>
    public async Task<MatrixClient> ConnectAsync(CancellationToken cancellationToken)
    {
        // Prefer stored session
        HttpClient http = httpFactory.CreateClient(HttpClientName);
        MatrixClient? client = await TryResumeAsync(http, cancellationToken) ?? await LoginAsync(http, cancellationToken);
        // Publish and return client
        ready.TrySetResult(client);
        return client;
    }

    /// <summary>Return a client on the stored session, or null when none is stored or the server rejected it; when the server cannot be reached, the session is resumed unchecked.</summary>
    private async Task<MatrixClient?> TryResumeAsync(HttpClient http, CancellationToken cancellationToken)
    {
        // Assert stored token of this user
        StoredAccount? account = await store.LoadAccountAsync(cancellationToken);
        if (account?.AccessToken is null || account.DeviceId is null || account.UserId != paths.UserId.ToString()) return null;

        // Validate token
        MatrixSession session = new() { Homeserver = new Uri(account.Homeserver), UserId = account.UserId, DeviceId = account.DeviceId, AccessToken = account.AccessToken };
        MatrixClient client = new(http, session);
        try
        {
            await client.WhoAmIAsync(cancellationToken);
        }
        catch (MatrixException ex) when (ex.Errcode is ErrorCodes.UnknownToken or ErrorCodes.MissingToken)
        {
            logger.LogWarning("Stored session of {UserId} was rejected: {Error}", account.UserId, ex.Message);
            await store.ClearAccessTokenAsync(cancellationToken);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The server cannot be reached, e.g. offline: keep the session, as syncing retries until it can
            logger.LogInformation("Cannot reach the server to check the stored session of {UserId}, resuming it anyway: {Error}", account.UserId, ex.Message);
        }

        // Return resumed client
        logger.LogInformation("Resumed session of {UserId} on device {DeviceId}", session.UserId, session.DeviceId);
        return client;
    }

    /// <summary>Log in with the configured or prompted password and store the session.</summary>
    private async Task<MatrixClient> LoginAsync(HttpClient http, CancellationToken cancellationToken)
    {
        // Configured address, else discovered from the user ID
        string userId = paths.UserId.ToString();
        Uri homeserver = options.Homeserver is string configured ? new Uri(configured.EndsWith('/') ? configured : configured + "/") : await HomeserverDiscovery.DiscoverAsync(http, paths.UserId.ServerName, cancellationToken);
        MatrixClient client = new(http, homeserver);

        // Log in and store session
        string password = options.Password ?? await prompt.GetPasswordAsync(userId, cancellationToken);
        await client.LoginWithPasswordAsync(userId, password, options.DeviceName, cancellationToken);
        await store.SaveSessionAsync(client.Session!, cancellationToken);
        logger.LogInformation("Logged in as {UserId} on device {DeviceId}", client.UserId, client.DeviceId);
        // Return client
        return client;
    }
}
