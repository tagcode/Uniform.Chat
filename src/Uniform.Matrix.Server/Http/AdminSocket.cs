namespace Uniform.Matrix.Server.Http;

using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Uniform.Matrix.Server.Accounts;

/// <summary>A local Unix socket through which the server's own machine manages it, e.g. <c>uniform-chat-server manage</c> while the server runs as a service: requests through it act as a server admin without an access token, and may use calls that the network never sees: the status and the settings.</summary>
/// <remarks>Who may connect is decided by the socket file's permissions, e.g. root and the service's group; the server serves it next to its other addresses, as <c>http://unix:</c> followed by the path.</remarks>
public static class AdminSocket
{
    /// <summary>Prefix of the calls that are answered on the socket only, see <see cref="Endpoints.ManagementEndpoints"/>.</summary>
    public const string Prefix = "/_uniform/admin/v1";

    /// <summary>The server's state for the manager: addresses, counters and where its files are.</summary>
    public const string StatusPath = Prefix + "/status";

    /// <summary>The server's settings: values and sources; <c>/apply</c> and <c>/save</c> below it change them.</summary>
    public const string SettingsPath = Prefix + "/settings";

    /// <summary>The requester of socket requests, an admin without an account.</summary>
    public static readonly Requester Requester = new("@admin-socket", "admin-socket", Admin: true);

    /// <summary>Mark connections to the socket at <paramref name="path"/> among the server's endpoints, so <see cref="IsAdminSocket"/> knows their requests.</summary>
    public static KestrelServerOptions MarkAdminSocket(this KestrelServerOptions kestrel, string path)
    {
        // Every endpoint gets the defaults; the socket's connections get the marker
        string socket = Path.GetFullPath(path);
        kestrel.ConfigureEndpointDefaults(listen =>
        {
            if (listen.EndPoint is not UnixDomainSocketEndPoint endPoint || Path.GetFullPath(endPoint.ToString()) != socket) return;
            listen.Use(next => connection =>
            {
                connection.Features.Set(new AdminSocketFeature());
                return next(connection);
            });
        });
        // Return options
        return kestrel;
    }

    /// <summary>Return whether the request came through the admin socket.</summary>
    public static bool IsAdminSocket(this HttpContext http) => http.Features.Get<AdminSocketFeature>() is not null;

    /// <summary>Marks a connection to the admin socket.</summary>
    private sealed class AdminSocketFeature;
}
