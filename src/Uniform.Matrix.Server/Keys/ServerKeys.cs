namespace Uniform.Matrix.Server.Keys;

using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;
using Uniform.Matrix.Server.Configuration;

/// <summary>The server's Ed25519 signing key, which signs every event it creates; stored in <c>signing.key</c> in the data directory, readable only by the user.</summary>
/// <remarks>The file has one line, <c>ed25519 &lt;version&gt; &lt;seed&gt;</c>, the same format Synapse uses.</remarks>
public sealed class ServerKeys
{
    private readonly string secret;

    /// <summary>Load the key, creating one on first start.</summary>
    public ServerKeys(IOptions<ServerStorageOptions> storage, IHostEnvironment environment)
    {
        // Existing key file
        string path = Path.Combine(storage.Value.ResolveDataDirectory(environment.ApplicationName), "signing.key");
        if (File.Exists(path))
        {
            string[] parts = File.ReadAllText(path).Trim().Split(' ');
            if (parts.Length != 3 || parts[0] != "ed25519") throw new InvalidDataException($"{path} is not an ed25519 signing key.");
            KeyId = $"ed25519:{parts[1]}";
            secret = parts[2];
        }
        else
        {
            // New key, private to the user
            KeyId = $"ed25519:a_{Base64.EncodeUrlSafe(RandomNumberGenerator.GetBytes(3))}";
            (secret, _) = Ed25519.GenerateKey();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"ed25519 {KeyId["ed25519:".Length..]} {secret}\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        PublicKey = Ed25519.GetPublicKey(secret);
    }

    /// <summary>Key ID, e.g. <c>ed25519:a_AbCd</c>.</summary>
    public string KeyId { get; }
    /// <summary>Public key, unpadded base64.</summary>
    public string PublicKey { get; }
    /// <summary>The seed, for signing; never sent anywhere.</summary>
    internal string Secret => secret;
}
