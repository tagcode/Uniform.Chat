namespace Uniform.Stun.Hosting;

using System.Security.Cryptography.X509Certificates;
using Uniform.Matrix.Hosting;

/// <summary>A TLS certificate from PEM files, read again when either file changes, so renewals need no restart.</summary>
/// <param name="certificatePath">PEM file of the certificate chain; <c>~</c> is the home directory.</param>
/// <param name="keyPath">PEM file of the private key.</param>
public sealed class PemCertificate(string certificatePath, string keyPath)
{
    private readonly string certificatePath = AppDirectories.ExpandHome(certificatePath);
    private readonly string keyPath = AppDirectories.ExpandHome(keyPath);
    private readonly Lock gate = new();
    private X509Certificate2? current;
    private (DateTime Certificate, DateTime Key) loaded;

    /// <summary>Return the certificate with its key, reading the files when they changed; throw when they cannot be read.</summary>
    public X509Certificate2 Get()
    {
        // The cached one while the files are unchanged
        lock (gate)
        {
            (DateTime, DateTime) stamps = (File.GetLastWriteTimeUtc(certificatePath), File.GetLastWriteTimeUtc(keyPath));
            if (current is not null && stamps == loaded) return current;

            // Through PKCS #12, as TLS on Linux needs the key to be persisted
            using X509Certificate2 pem = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
            current = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
            loaded = stamps;
            // Return certificate
            return current;
        }
    }
}
