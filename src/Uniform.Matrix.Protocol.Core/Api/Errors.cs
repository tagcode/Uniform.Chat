namespace Uniform.Matrix.Protocol.Core;

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#standard-error-response">Standard error</see> body returned with non-2xx responses.</summary>
public sealed record ErrorResponse
{
    /// <summary>Error code, see <see cref="ErrorCodes"/>.</summary>
    public required string Errcode { get; init; }
    /// <summary>Human-readable error message.</summary>
    public string? Error { get; init; }
    /// <summary>Wait time in milliseconds for <see cref="ErrorCodes.LimitExceeded"/>.</summary>
    public long? RetryAfterMs { get; init; }
}

/// <summary>Common <see href="https://spec.matrix.org/latest/client-server-api/#common-error-codes">error codes</see>.</summary>
public static class ErrorCodes
{
    /// <summary>Access denied, e.g. wrong password or missing permission.</summary>
    public const string Forbidden = "M_FORBIDDEN";
    /// <summary>The request is not, or not correctly, signed by the server it says it is from.</summary>
    public const string Unauthorized = "M_UNAUTHORIZED";
    /// <summary>Access token is not recognized; the session has ended.</summary>
    public const string UnknownToken = "M_UNKNOWN_TOKEN";
    /// <summary>No access token was given.</summary>
    public const string MissingToken = "M_MISSING_TOKEN";
    /// <summary>Too many requests; see <see cref="MatrixException.RetryAfter"/>.</summary>
    public const string LimitExceeded = "M_LIMIT_EXCEEDED";
    /// <summary>Requested resource does not exist.</summary>
    public const string NotFound = "M_NOT_FOUND";
    /// <summary>Request was malformed.</summary>
    public const string BadJson = "M_BAD_JSON";
    /// <summary>Unrecognized endpoint, or a method the endpoint does not support.</summary>
    public const string Unrecognized = "M_UNRECOGNIZED";
    /// <summary>Unknown server error.</summary>
    public const string Unknown = "M_UNKNOWN";
    /// <summary>Request body was not JSON.</summary>
    public const string NotJson = "M_NOT_JSON";
    /// <summary>A required parameter was missing.</summary>
    public const string MissingParam = "M_MISSING_PARAM";
    /// <summary>A parameter had an invalid value.</summary>
    public const string InvalidParam = "M_INVALID_PARAM";
    /// <summary>The user ID is already taken.</summary>
    public const string UserInUse = "M_USER_IN_USE";
    /// <summary>The user ID is not valid.</summary>
    public const string InvalidUsername = "M_INVALID_USERNAME";
    /// <summary>The user is deactivated.</summary>
    public const string UserDeactivated = "M_USER_DEACTIVATED";
    /// <summary>The server does not support the requested room version.</summary>
    public const string UnsupportedRoomVersion = "M_UNSUPPORTED_ROOM_VERSION";
    /// <summary>The room alias is already taken.</summary>
    public const string RoomInUse = "M_ROOM_IN_USE";
    /// <summary>The initial state of a new room is invalid.</summary>
    public const string InvalidRoomState = "M_INVALID_ROOM_STATE";
    /// <summary>The upload is larger than the server allows.</summary>
    public const string TooLarge = "M_TOO_LARGE";
    /// <summary>Media was created with <c>/media/v1/create</c>, but its content has not been uploaded yet.</summary>
    public const string NotYetUploaded = "M_NOT_YET_UPLOADED";
    /// <summary>The media already has content, which cannot be replaced.</summary>
    public const string CannotOverwriteMedia = "M_CANNOT_OVERWRITE_MEDIA";
}
