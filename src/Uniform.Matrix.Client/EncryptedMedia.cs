namespace Uniform.Matrix.Client;

using System.Security.Cryptography;
using Uniform.Matrix.Protocol.Client;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto;

/// <summary>Upload and download of <see href="https://spec.matrix.org/latest/client-server-api/#sending-encrypted-attachments">encrypted attachments</see> through a <see cref="MatrixClient"/>; the format is in <see cref="EncryptedAttachments"/>.</summary>
public static class EncryptedMedia
{
    /// <summary>Encrypt and upload a file as <c>application/octet-stream</c>; put the result in <see cref="MessageContent.File"/>.</summary>
    /// <example><code><![CDATA[
    /// EncryptedFile file = await client.UploadEncryptedAsync(await File.ReadAllBytesAsync(path));
    /// MessageContent content = MatrixClient.CreateFileContent(file, "cat.png", "image/png", size);
    /// ]]></code></example>
    public static async Task<EncryptedFile> UploadEncryptedAsync(this MatrixClient client, byte[] plaintext, CancellationToken cancellationToken = default)
    {
        // Encrypt, then upload without a name or type that would leak through the server
        (byte[] ciphertext, AttachmentKeys keys) = AttachmentCrypto.Encrypt(plaintext);
        using MemoryStream stream = new(ciphertext);
        MxcUri uri = await client.UploadMediaAsync(stream, "application/octet-stream", cancellationToken: cancellationToken);
        // Return file reference
        return EncryptedAttachments.ToEncryptedFile(uri.ToString(), keys);
    }

    /// <summary>Download and decrypt <paramref name="file"/>; throw <see cref="CryptographicException"/> when the ciphertext was changed.</summary>
    public static async Task<byte[]> DownloadEncryptedAsync(this MatrixClient client, EncryptedFile file, CancellationToken cancellationToken = default)
    {
        // Assert keys before downloading
        AttachmentKeys keys = EncryptedAttachments.GetKeys(file);
        if (!MxcUri.TryParse(file.Url, out MxcUri uri)) throw new FormatException("Encrypted file has no content URI.");

        // Download whole ciphertext; the hash covers all of it
        using MediaDownload media = await client.DownloadMediaAsync(uri, cancellationToken);
        using MemoryStream buffer = new();
        await media.Content.CopyToAsync(buffer, cancellationToken);
        // Return plaintext
        return AttachmentCrypto.Decrypt(buffer.ToArray(), keys);
    }
}
