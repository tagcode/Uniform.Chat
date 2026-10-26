namespace Uniform.Matrix.Protocol.Client;

using Uniform.Matrix.Protocol.Core;

// Devices and user-interactive auth.
public sealed partial class MatrixClient
{
    /// <summary>List the user's <see href="https://spec.matrix.org/latest/client-server-api/#get_matrixclientv3devices">devices</see>.</summary>
    public async Task<IReadOnlyList<Device>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        // Fetch
        DevicesResponse response = await SendAsync<DevicesResponse>(HttpMethod.Get, "_matrix/client/v3/devices", body: null, authenticated: true, cancellationToken);
        // Return devices
        return response.Devices;
    }

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#put_matrixclientv3devicesdeviceid">Renames</see> a device.</summary>
    public Task RenameDeviceAsync(string deviceId, string displayName, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"_matrix/client/v3/devices/{Escape(deviceId)}", new { display_name = displayName }, authenticated: true, cancellationToken);

    /// <summary><see href="https://spec.matrix.org/latest/client-server-api/#post_matrixclientv3delete_devices">Deletes devices</see>, logging them out; the server asks for the password first.</summary>
    /// <param name="deviceIds">Devices to delete.</param>
    /// <param name="password">Password for the auth challenge; when null, <see cref="UserInteractiveAuthException"/> is thrown so the caller can ask for it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task DeleteDevicesAsync(IReadOnlyList<string> deviceIds, string? password, CancellationToken cancellationToken = default) =>
        SendWithPasswordAuthAsync(HttpMethod.Post, "_matrix/client/v3/delete_devices", auth => new { devices = deviceIds, auth }, password, cancellationToken);

    /// <summary>Send a request that may need <see href="https://spec.matrix.org/latest/client-server-api/#user-interactive-authentication-api">user-interactive auth</see>, answering a password challenge with <paramref name="password"/>.</summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Endpoint path.</param>
    /// <param name="createBody">Builds the body with the <c>auth</c> object, null on the first attempt.</param>
    /// <param name="password">Password, or null to let <see cref="UserInteractiveAuthException"/> through.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task SendWithPasswordAuthAsync(HttpMethod method, string path, Func<object?, object> createBody, string? password, CancellationToken cancellationToken)
    {
        // Try without auth; some servers skip the challenge right after login
        UserInteractiveAuthResponse challenge;
        try
        {
            await SendAsync(method, path, createBody(null), authenticated: true, cancellationToken);
            return;
        }
        catch (UserInteractiveAuthException ex) when (password is not null && ex.AcceptsPassword)
        {
            challenge = ex.Response;
        }

        // Answer with the password; a wrong password comes back as another challenge with an error
        object auth = new { type = AuthTypes.Password, identifier = new UserIdentifier { User = RequireSession().UserId }, password, session = challenge.Session };
        await SendAsync(method, path, createBody(auth), authenticated: true, cancellationToken);
    }
}
