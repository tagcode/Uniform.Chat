namespace Uniform.Matrix.Client.Avalonia.Core.Services;

/// <summary>What the user gave to sign in: the user ID, optionally the homeserver, and the password unless a stored session is resumed.</summary>
public sealed record SignIn
{
    /// <summary>Full user ID, e.g. <c>@alice:matrix.org</c>.</summary>
    public required string UserId { get; init; }
    /// <summary>Homeserver address for servers without discovery; found from the user ID when null.</summary>
    public string? Homeserver { get; init; }
    /// <summary>Password; null to resume the stored session.</summary>
    public string? Password { get; init; }
}
