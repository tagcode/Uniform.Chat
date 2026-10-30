namespace Uniform.Matrix.Server.Accounts;

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;

/// <summary>A request needs more <see href="https://spec.matrix.org/latest/client-server-api/#user-interactive-authentication-api">user-interactive auth</see>; answered with 401 and <see cref="Response"/>.</summary>
public sealed class AuthenticationRequiredException(UserInteractiveAuthResponse response) : Exception(response.Error ?? "Authentication required.")
{
    /// <summary>Flows, session and completed stages to send.</summary>
    public UserInteractiveAuthResponse Response { get; } = response;
}

/// <summary>Runs <see href="https://spec.matrix.org/latest/client-server-api/#user-interactive-authentication-api">user-interactive auth</see>: sessions of completed stages, kept in memory for a short while.</summary>
public sealed class UserInteractiveAuth
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, Session> sessions = new();
    private readonly TimeProvider time;

    /// <summary>Use <paramref name="time"/> to expire sessions.</summary>
    public UserInteractiveAuth(TimeProvider time)
    {
        // Store dependency
        this.time = time;
    }

    /// <summary>Complete the stage in <paramref name="auth"/>, and return once a flow is complete; otherwise throws <see cref="AuthenticationRequiredException"/>.</summary>
    /// <param name="auth">The request's <c>auth</c> object, or null on the first attempt.</param>
    /// <param name="flows">Accepted stage combinations.</param>
    /// <param name="checkStage">Returns whether the stage of the given type is passed by the <c>auth</c> object.</param>
    /// <param name="parameters">Parameters of stages by type, e.g. the policies of <see cref="AuthTypes.Terms"/>; null for none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AuthenticateAsync(JsonObject? auth, IReadOnlyList<IReadOnlyList<string>> flows, Func<string, JsonObject, CancellationToken, Task<bool>> checkStage, IReadOnlyDictionary<string, JsonObject>? parameters, CancellationToken cancellationToken)
    {
        // Existing session, else a new one
        RemoveExpired();
        string? sessionId = auth?["session"]?.GetValue<string>();
        Session session = sessionId is not null && sessions.TryGetValue(sessionId, out Session? known) ? known : NewSession();

        // Check the stage of this attempt
        string? error = null;
        if (auth?["type"]?.GetValue<string>() is string type)
        {
            bool offered = flows.Any(f => f.Contains(type));
            if (offered && await checkStage(type, auth, cancellationToken)) session.Completed.Add(type);
            else error = offered ? "Authentication failed." : $"Stage {type} is not offered.";
        }

        // Done when a flow is complete
        if (flows.Any(f => f.All(session.Completed.Contains)))
        {
            sessions.TryRemove(session.Id, out _);
            return;
        }

        // Ask for the next stage
        UserInteractiveAuthResponse response = new()
        {
            Flows = [.. flows.Select(f => new AuthFlow { Stages = f })],
            Params = parameters ?? new Dictionary<string, JsonObject>(),
            Session = session.Id,
            Completed = [.. session.Completed],
            Errcode = error is null ? null : ErrorCodes.Forbidden,
            Error = error,
        };
        // Throws on incomplete auth
        throw new AuthenticationRequiredException(response);
    }

    /// <summary>Start a session.</summary>
    private Session NewSession()
    {
        // Random ID
        Session session = new(Base64.EncodeUrlSafe(RandomNumberGenerator.GetBytes(18)), time.GetUtcNow());
        sessions[session.Id] = session;
        // Return session
        return session;
    }

    /// <summary>Forget old sessions.</summary>
    private void RemoveExpired()
    {
        // Older than the lifetime
        DateTimeOffset limit = time.GetUtcNow() - SessionLifetime;
        foreach (Session session in sessions.Values)
        {
            if (session.Created < limit) sessions.TryRemove(session.Id, out _);
        }
    }

    /// <summary>A session of completed stages.</summary>
    /// <param name="Id">Session ID.</param>
    /// <param name="Created">Creation time.</param>
    private sealed record Session(string Id, DateTimeOffset Created)
    {
        /// <summary>Stage types done so far.</summary>
        public HashSet<string> Completed { get; } = [];
    }

    /// <summary>Return the error of an invalid <c>auth</c> object.</summary>
    internal static MatrixException BadAuth(string message) => new(HttpStatusCode.BadRequest, ErrorCodes.BadJson, message);
}
