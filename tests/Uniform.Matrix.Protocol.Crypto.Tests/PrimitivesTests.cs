namespace Uniform.Matrix.Protocol.Crypto.Tests;

using System.Security.Cryptography;
using System.Text.Json;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;

/// <summary>Tests of the managed primitives: base64 forms, AES-CTR, attachments, secret storage and recovery keys.</summary>
public class PrimitivesTests
{
    /// <summary>Base64 decoding accepts padded, unpadded and URL-safe input.</summary>
    [Fact]
    public void CanDecodeAllBase64Forms()
    {
        // Arrange: bytes that produce '+' and '/' and need padding
        byte[] bytes = [0xFB, 0xFF, 0xBF, 0x01];

        // Act
        string unpadded = Base64.EncodeUnpadded(bytes);
        string urlSafe = Base64.EncodeUrlSafe(bytes);

        // Assert
        Assert.Equal("+/+/AQ", unpadded);
        Assert.Equal("-_-_AQ", urlSafe);
        Assert.Equal(bytes, Base64.Decode(unpadded));
        Assert.Equal(bytes, Base64.Decode(urlSafe));
        Assert.Equal(bytes, Base64.Decode("+/+/AQ=="));
    }

    /// <summary>AES-CTR matches the NIST SP 800-38A F.5.5 vector, including the counter carry.</summary>
    [Fact]
    public void CanEncryptNistCtrVector()
    {
        // Arrange
        byte[] key = Convert.FromHexString("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");
        byte[] iv = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
        byte[] plaintext = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51");

        // Act
        byte[] ciphertext = AesCtr.Transform(key, iv, plaintext);

        // Assert
        Assert.Equal("601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c5", Convert.ToHexString(ciphertext).ToLowerInvariant());
        Assert.Equal(plaintext, AesCtr.Transform(key, iv, ciphertext));
    }

    /// <summary>Attachments round-trip, use an IV with a zero low half, and fail when changed.</summary>
    [Fact]
    public void CanEncryptAttachment()
    {
        // Arrange: not a multiple of the block size
        byte[] file = RandomNumberGenerator.GetBytes(1000);

        // Act
        (byte[] ciphertext, AttachmentKeys keys) = AttachmentCrypto.Encrypt(file);

        // Assert
        Assert.NotEqual(file, ciphertext);
        Assert.Equal(file, AttachmentCrypto.Decrypt(ciphertext, keys));
        Assert.All(Base64.Decode(keys.Iv)[8..], b => Assert.Equal(0, b));
        Assert.Equal(32, Base64.Decode(keys.Key).Length);
        ciphertext[0] ^= 1;
        Assert.Throws<CryptographicException>(() => AttachmentCrypto.Decrypt(ciphertext, keys));
    }

    /// <summary>Secrets decrypt with the right key and name only.</summary>
    [Fact]
    public void CanEncryptSecret()
    {
        // Arrange
        byte[] key = RandomNumberGenerator.GetBytes(32);

        // Act
        EncryptedSecret secret = SecretStorageCrypto.Encrypt(key, "m.cross_signing.master", "c2VjcmV0");

        // Assert
        Assert.Equal("c2VjcmV0", SecretStorageCrypto.Decrypt(key, "m.cross_signing.master", secret));
        Assert.Equal(0, Convert.FromBase64String(secret.Iv)[8] & 0x80);
        Assert.Throws<CryptographicException>(() => SecretStorageCrypto.Decrypt(key, "m.cross_signing.self_signing", secret));
        Assert.Throws<CryptographicException>(() => SecretStorageCrypto.Decrypt(RandomNumberGenerator.GetBytes(32), "m.cross_signing.master", secret));
    }

    /// <summary>The key check accepts only the key it was made with.</summary>
    [Fact]
    public void CanCheckSecretStorageKey()
    {
        // Arrange
        byte[] key = RandomNumberGenerator.GetBytes(32);

        // Act
        (string iv, string mac) = SecretStorageCrypto.CreateKeyCheck(key);

        // Assert
        Assert.True(SecretStorageCrypto.CheckKey(key, iv, mac));
        Assert.False(SecretStorageCrypto.CheckKey(RandomNumberGenerator.GetBytes(32), iv, mac));
    }

    /// <summary>Recovery keys round-trip with spaces, start with "Es", and detect typos.</summary>
    [Fact]
    public void CanEncodeRecoveryKey()
    {
        // Arrange
        byte[] key = RandomNumberGenerator.GetBytes(32);

        // Act
        string recoveryKey = SecretStorageCrypto.EncodeRecoveryKey(key);

        // Assert
        Assert.StartsWith("Es", recoveryKey);
        Assert.Equal(key, SecretStorageCrypto.DecodeRecoveryKey(recoveryKey));
        Assert.Equal(key, SecretStorageCrypto.DecodeRecoveryKey(recoveryKey.Replace(" ", "")));
        char last = recoveryKey[^1];
        string typo = recoveryKey[..^1] + (last == 'a' ? 'b' : 'a');
        Assert.Throws<FormatException>(() => SecretStorageCrypto.DecodeRecoveryKey(typo));
        Assert.Throws<FormatException>(() => SecretStorageCrypto.DecodeRecoveryKey("hello 0"));
    }

    /// <summary>Passphrase keys are deterministic for the same salt and iterations.</summary>
    [Fact]
    public void CanDeriveKeyFromPassphrase()
    {
        // Act
        byte[] first = SecretStorageCrypto.KeyFromPassphrase("correct horse", "salt", 1000);
        byte[] second = SecretStorageCrypto.KeyFromPassphrase("correct horse", "salt", 1000);

        // Assert
        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
        Assert.NotEqual(first, SecretStorageCrypto.KeyFromPassphrase("correct horse", "pepper", 1000));
    }

    /// <summary>Encrypted file content serializes in the spec's shape and its keys read back.</summary>
    [Fact]
    public void CanSerializeEncryptedFile()
    {
        // Arrange
        (byte[] ciphertext, AttachmentKeys keys) = AttachmentCrypto.Encrypt([1, 2, 3]);
        MessageContent content = MatrixClient.CreateFileContent(EncryptedAttachments.ToEncryptedFile("mxc://example.org/abc", keys), "a.bin", "application/octet-stream", 3);

        // Act
        string json = JsonSerializer.Serialize(content, MatrixJson.Options);
        MessageContent parsed = JsonSerializer.Deserialize<MessageContent>(json, MatrixJson.Options)!;

        // Assert
        Assert.Contains("\"key_ops\":[\"encrypt\",\"decrypt\"]", json);
        Assert.Contains("\"hashes\":{\"sha256\":", json);
        Assert.Contains("\"v\":\"v2\"", json);
        Assert.DoesNotContain("media_url", json);
        Assert.Equal("mxc://example.org/abc", parsed.MediaUrl);
        Assert.Equal(new byte[] { 1, 2, 3 }, AttachmentCrypto.Decrypt(ciphertext, EncryptedAttachments.GetKeys(parsed.File!)));
        Assert.Throws<FormatException>(() => EncryptedAttachments.GetKeys(parsed.File! with { Key = parsed.File!.Key! with { Alg = "A128CTR" } }));
    }
}
