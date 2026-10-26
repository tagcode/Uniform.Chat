namespace Uniform.Matrix.Protocol.Crypto;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Uniform.Matrix.Protocol.Core;

/// <summary>Description of a <see href="https://spec.matrix.org/latest/client-server-api/#key-storage">secret storage key</see> in account data <c>m.secret_storage.key.&lt;id&gt;</c>.</summary>
public sealed record SecretStorageKeyDescription
{
    /// <summary>Human-readable name.</summary>
    public string? Name { get; init; }
    /// <summary>Algorithm; <see cref="SecretStorageCrypto.Algorithm"/>.</summary>
    public string? Algorithm { get; init; }
    /// <summary>How to derive the key from a passphrase, when one was set.</summary>
    public PassphraseInfo? Passphrase { get; init; }
    /// <summary>Key check counter block.</summary>
    public string? Iv { get; init; }
    /// <summary>Key check MAC.</summary>
    public string? Mac { get; init; }
}

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#deriving-keys-from-passphrases">Passphrase</see> settings of a <see cref="SecretStorageKeyDescription"/>.</summary>
public sealed record PassphraseInfo
{
    /// <summary>Algorithm; <c>m.pbkdf2</c>.</summary>
    public string? Algorithm { get; init; }
    /// <summary>Salt.</summary>
    public string? Salt { get; init; }
    /// <summary>PBKDF2 iterations.</summary>
    public int Iterations { get; init; }
    /// <summary>Key length in bits; 256 when unset.</summary>
    public int? Bits { get; init; }
}

/// <summary>Names of the secrets this library keeps in secret storage.</summary>
public static class SecretNames
{
    /// <summary>Cross-signing master private key.</summary>
    public const string CrossSigningMaster = "m.cross_signing.master";
    /// <summary>Cross-signing self-signing private key.</summary>
    public const string CrossSigningSelfSigning = "m.cross_signing.self_signing";
    /// <summary>Cross-signing user-signing private key.</summary>
    public const string CrossSigningUserSigning = "m.cross_signing.user_signing";
    /// <summary>Key backup private key.</summary>
    public const string MegolmBackup = "m.megolm_backup.v1";
}

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#secret-storage">Secret storage</see> (4S): secrets encrypted in account data with a key the user keeps as a recovery key.</summary>
/// <example><code><![CDATA[
/// SecretStorage storage = new(new MatrixKeyTransport(client));
/// (string keyId, SecretStorageKeyDescription description) = (await storage.GetDefaultKeyAsync())!.Value;
/// byte[] key = SecretStorage.Unlock(description, "EsTc 5rr1 ...");
/// string? master = await storage.GetSecretAsync(SecretNames.CrossSigningMaster, keyId, key);
/// ]]></code></example>
public sealed class SecretStorage
{
    /// <summary>Account data type naming the default key.</summary>
    public const string DefaultKeyType = "m.secret_storage.default_key";
    /// <summary>Account data type prefix of key descriptions.</summary>
    public const string KeyTypePrefix = "m.secret_storage.key.";

    private readonly IKeyTransport transport;

    /// <summary>Use <paramref name="transport"/> for account data.</summary>
    public SecretStorage(IKeyTransport transport)
    {
        // Store dependency
        this.transport = transport;
    }

    /// <summary>Return the default key's ID and description, or null when secret storage is not set up.</summary>
    public async Task<(string KeyId, SecretStorageKeyDescription Description)?> GetDefaultKeyAsync(CancellationToken cancellationToken = default)
    {
        // Default key ID, then its description
        if ((await transport.GetAccountDataAsync(DefaultKeyType, cancellationToken))?["key"]?.GetValue<string>() is not string keyId) return null;
        SecretStorageKeyDescription? description = (await transport.GetAccountDataAsync(KeyTypePrefix + keyId, cancellationToken))?.Deserialize<SecretStorageKeyDescription>(MatrixJson.Options);
        // Return key when supported
        return description?.Algorithm == SecretStorageCrypto.Algorithm ? (keyId, description) : null;
    }

    /// <summary>Create a random key, publishes its description and make it the default; return its ID and the key, to show as a recovery key.</summary>
    public async Task<(string KeyId, byte[] Key)> CreateKeyAsync(string? name = null, CancellationToken cancellationToken = default)
    {
        // Random key with a key check
        byte[] key = RandomNumberGenerator.GetBytes(32);
        string keyId = Base64.EncodeUnpadded(RandomNumberGenerator.GetBytes(24)).Replace('+', 'A').Replace('/', 'B');
        (string iv, string mac) = SecretStorageCrypto.CreateKeyCheck(key);
        JsonObject description = new() { ["algorithm"] = SecretStorageCrypto.Algorithm, ["iv"] = iv, ["mac"] = mac };
        if (name is not null) description["name"] = name;

        // Publish, then make default
        await transport.SetAccountDataAsync(KeyTypePrefix + keyId, description, cancellationToken);
        await transport.SetAccountDataAsync(DefaultKeyType, new JsonObject { ["key"] = keyId }, cancellationToken);
        // Return key
        return (keyId, key);
    }

    /// <summary>Return the key from a recovery key, or from a passphrase when the key has one; throw <see cref="CryptographicException"/> when it does not match.</summary>
    public static byte[] Unlock(SecretStorageKeyDescription description, string recoveryKeyOrPassphrase)
    {
        // Recovery key, else passphrase
        byte[] key;
        try
        {
            key = SecretStorageCrypto.DecodeRecoveryKey(recoveryKeyOrPassphrase);
        }
        catch (FormatException) when (description.Passphrase is { Algorithm: "m.pbkdf2", Salt: string salt, Iterations: > 0 } passphrase)
        {
            key = SecretStorageCrypto.KeyFromPassphrase(recoveryKeyOrPassphrase, salt, passphrase.Iterations, passphrase.Bits ?? 256);
        }

        // Key check, when the description has one
        if (description.Iv is string iv && description.Mac is string mac && !SecretStorageCrypto.CheckKey(key, iv, mac)) throw new CryptographicException("The recovery key does not match.");
        // Return key
        return key;
    }

    /// <summary>Return secret <paramref name="name"/> decrypted with <paramref name="key"/>, or null when it is not stored for that key.</summary>
    public async Task<string?> GetSecretAsync(string name, string keyId, byte[] key, CancellationToken cancellationToken = default)
    {
        // Ciphertext for this key
        JsonObject? stored = await transport.GetAccountDataAsync(name, cancellationToken);
        EncryptedSecret? secret = stored?["encrypted"]?[keyId]?.Deserialize<EncryptedSecret>(MatrixJson.Options);
        // Return plaintext
        return secret is null ? null : SecretStorageCrypto.Decrypt(key, name, secret);
    }

    /// <summary>Store secret <paramref name="name"/> encrypted with <paramref name="key"/>, replacing copies for other keys.</summary>
    public Task StoreSecretAsync(string name, string secret, string keyId, byte[] key, CancellationToken cancellationToken = default)
    {
        // Encrypt and store
        EncryptedSecret encrypted = SecretStorageCrypto.Encrypt(key, name, secret);
        JsonObject content = new() { ["encrypted"] = new JsonObject { [keyId] = JsonSerializer.SerializeToNode(encrypted, MatrixJson.Options) } };
        // Done
        return transport.SetAccountDataAsync(name, content, cancellationToken);
    }
}
