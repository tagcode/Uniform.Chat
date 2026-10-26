namespace Uniform.Matrix.Server.Accounts;

using System.Net;
using Uniform.Matrix.Protocol.Core;

/// <summary>Rules for <see href="https://spec.matrix.org/latest/appendices/#user-identifiers">user IDs</see> of local accounts.</summary>
public static class LocalUserIds
{
    private const int MaxUserIdLength = 255;

    /// <summary>Return the full user ID of <paramref name="localpartOrId"/> on <paramref name="serverName"/>, lowercased; throw <c>M_INVALID_USERNAME</c> when it is not a valid local user.</summary>
    public static string Normalize(string localpartOrId, string serverName)
    {
        // Full IDs must be ours
        string localpart = localpartOrId.Trim();
        if (localpart.StartsWith('@'))
        {
            int colon = localpart.IndexOf(':');
            if (colon < 0 || localpart[(colon + 1)..] != serverName) throw Invalid("The user is not on this server.");
            localpart = localpart[1..colon];
        }

        // Allowed characters and length
        localpart = localpart.ToLowerInvariant();
        if (localpart.Length == 0 || !localpart.All(IsAllowed)) throw Invalid("User IDs may only contain a-z, 0-9 and . _ = - / +.");
        string userId = $"@{localpart}:{serverName}";
        if (userId.Length > MaxUserIdLength) throw Invalid($"User IDs are at most {MaxUserIdLength} characters.");
        // Return user ID
        return userId;
    }

    /// <summary>Return whether <paramref name="character"/> is allowed in a localpart.</summary>
    private static bool IsAllowed(char character) => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '=' or '-' or '/' or '+';

    /// <summary>Return the error of an invalid user ID.</summary>
    private static MatrixException Invalid(string message) => new(HttpStatusCode.BadRequest, ErrorCodes.InvalidUsername, message);
}
