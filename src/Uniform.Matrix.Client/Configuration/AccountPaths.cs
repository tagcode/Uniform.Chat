namespace Uniform.Matrix.Client.Configuration;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;

/// <summary>Per-account storage paths; each account gets its own directory so databases never mix users.</summary>
public sealed class AccountPaths
{
    /// <summary>Resolve paths for <see cref="MatrixOptions.UserId"/>; throw when it is not configured.</summary>
    /// <param name="matrix">Account settings.</param>
    /// <param name="storage">Storage settings.</param>
    /// <param name="environment">Host, whose application name selects the default data directory; <c>Uniform.Matrix.Client</c> when null.</param>
    public AccountPaths(IOptions<MatrixOptions> matrix, IOptions<StorageOptions> storage, IHostEnvironment? environment = null)
    {
        // Assert user ID
        string userIdText = matrix.Value.UserId ?? throw new InvalidOperationException($"Set {MatrixOptions.Section}:UserId, e.g. --{MatrixOptions.Section}:UserId=@alice:matrix.org");
        UserId = UserId.Parse(userIdText);

        // Build paths
        Directory = Path.Combine(storage.Value.ResolveDataDirectory(environment?.ApplicationName ?? "Uniform.Matrix.Client"), ToDirectoryName(UserId));
        DatabasePath = Path.Combine(Directory, storage.Value.DatabaseFileName);
        MediaDirectory = Path.Combine(Directory, storage.Value.MediaDirectoryName);
        CryptoKeyPath = Path.Combine(Directory, "crypto.key");
    }

    /// <summary>Configured account.</summary>
    public UserId UserId { get; }
    /// <summary>Account directory.</summary>
    public string Directory { get; }
    /// <summary>SQLite database file.</summary>
    public string DatabasePath { get; }
    /// <summary>Downloaded media files.</summary>
    public string MediaDirectory { get; }
    /// <summary>32-byte key encrypting stored encryption state; readable only by the user.</summary>
    public string CryptoKeyPath { get; }

    /// <summary>Return a file-system-safe name such as <c>alice_matrix.org</c>.</summary>
    public static string ToDirectoryName(UserId userId) => ToSafeName($"{userId.Localpart}_{userId.ServerName}");

    /// <summary>Replace characters that are invalid or awkward in file names with <c>_</c>.</summary>
    public static string ToSafeName(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
}
