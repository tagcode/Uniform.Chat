namespace Uniform.Matrix.Server.Http;

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Uniform.Matrix.Server.Configuration;

/// <summary>The server's HTTPS certificate from the PEM files of <see cref="TlsOptions"/>, with its intermediates, read again when the files change.</summary>
/// <remarks>Other servers check the whole chain, so the intermediates of <see cref="TlsOptions.CertificatePath"/> are sent too. The files are looked at once per <see cref="CheckInterval"/> at most; when a new file cannot be read, the previous certificate stays in use.</remarks>
public sealed class TlsCertificate
{
    /// <summary>How often the files are looked at for changes.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    private readonly string certificatePath;
    private readonly string keyPath;
    private readonly TimeProvider time;
    private readonly ILogger<TlsCertificate> logger;
    private readonly Lock gate = new();
    private SslStreamCertificateContext? context;
    private (DateTime Certificate, DateTime Key) loadedWriteTimes;
    private DateTimeOffset nextCheck;

    /// <summary>Create from the configured files; throw when they are not set or cannot be read, so a bad path stops the start.</summary>
    public TlsCertificate(IOptions<TlsOptions> options, TimeProvider time, ILogger<TlsCertificate> logger)
    {
        // Assert both files are set
        certificatePath = options.Value.CertificatePath ?? throw new InvalidOperationException("Tls:CertificatePath is not set.");
        keyPath = options.Value.KeyPath ?? throw new InvalidOperationException("Tls:KeyPath is not set.");
        this.time = time;
        this.logger = logger;
        // Load now
        context = Load();
    }

    /// <summary>Get the certificate and its chain for a TLS handshake, read again first when the files have changed.</summary>
    public SslStreamCertificateContext Context
    {
        get
        {
            // Check the files now and then, not on every connection
            lock (gate)
            {
                if (time.GetUtcNow() < nextCheck) return context!;
                nextCheck = time.GetUtcNow() + CheckInterval;
                if (WriteTimes() == loadedWriteTimes) return context!;
                try
                {
                    context = Load();
                }
                catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Keeping the previous TLS certificate: {Error}", ex.Message);
                }
                // Return current
                return context!;
            }
        }
    }

    /// <summary>Get the certificate itself.</summary>
    public X509Certificate2 Certificate => Context.TargetCertificate;

    /// <summary>Read the certificate, its intermediates and its key.</summary>
    private SslStreamCertificateContext Load()
    {
        // Leaf with key; some platforms need the key out of the ephemeral PEM form
        (DateTime, DateTime) writeTimes = WriteTimes();
        using X509Certificate2 pem = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
        X509Certificate2 leaf = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);

        // Intermediates are the rest of the chain file
        X509Certificate2Collection chain = [];
        chain.ImportFromPemFile(certificatePath);
        X509Certificate2Collection intermediates = [.. chain.Where(c => c.Thumbprint != leaf.Thumbprint)];
        SslStreamCertificateContext loaded = SslStreamCertificateContext.Create(leaf, intermediates, offline: true);
        loadedWriteTimes = writeTimes;
        logger.LogInformation("TLS certificate for {Names}, valid until {NotAfter:yyyy-MM-dd}", string.Join(", ", Names(leaf)), leaf.NotAfter.ToUniversalTime());
        // Return context
        return loaded;
    }

    /// <summary>Return the last write times of both files.</summary>
    private (DateTime Certificate, DateTime Key) WriteTimes() => (File.GetLastWriteTimeUtc(certificatePath), File.GetLastWriteTimeUtc(keyPath));

    /// <summary>Return the DNS names of a certificate, or its subject when it has none.</summary>
    private static IEnumerable<string> Names(X509Certificate2 certificate)
    {
        // Subject alternative names
        X509SubjectAlternativeNameExtension? names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        // Return names
        return names is null ? [certificate.GetNameInfo(X509NameType.SimpleName, false)] : names.EnumerateDnsNames();
    }
}
