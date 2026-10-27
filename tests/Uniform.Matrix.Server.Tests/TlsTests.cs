namespace Uniform.Matrix.Server.Tests;

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Uniform.Matrix.Server.Http;

/// <summary>Tests of HTTPS from PEM files on real Kestrel: the chain is sent, and renewed files are picked up.</summary>
public class TlsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A test certificate authority with a root and an intermediate, which issues server certificates.</summary>
    private sealed class TestAuthority : IDisposable
    {
        /// <summary>Self-signed root, the only certificate the client trusts.</summary>
        public X509Certificate2 Root { get; }
        /// <summary>Intermediate signed by <see cref="Root"/>, which signs server certificates.</summary>
        public X509Certificate2 Intermediate { get; }

        /// <summary>Create the root and the intermediate.</summary>
        public TestAuthority()
        {
            // Root, then intermediate under it
            Root = CreateAuthority("Test Root", null);
            Intermediate = CreateAuthority("Test Intermediate", Root);
        }

        /// <summary>Write a server certificate for localhost with the intermediate after it, and its key, as certbot lays them out; return the certificate.</summary>
        public X509Certificate2 WriteServerCertificate(string certificatePath, string keyPath)
        {
            // Leaf signed by the intermediate
            using RSA key = RSA.Create(2048);
            CertificateRequest request = new("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            SubjectAlternativeNameBuilder names = new();
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
            using X509Certificate2 leaf = request.Create(Intermediate, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30), RandomNumberGenerator.GetBytes(8));

            // fullchain.pem and privkey.pem
            File.WriteAllText(certificatePath, leaf.ExportCertificatePem() + "\n" + Intermediate.ExportCertificatePem() + "\n");
            File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
            // Return certificate without key
            return X509CertificateLoader.LoadCertificate(leaf.RawData);
        }

        /// <summary>Return a certificate authority, self-signed when <paramref name="issuer"/> is null.</summary>
        private static X509Certificate2 CreateAuthority(string name, X509Certificate2? issuer)
        {
            // Authority that may sign certificates
            using RSA key = RSA.Create(2048);
            CertificateRequest request = new($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            DateTimeOffset from = DateTimeOffset.UtcNow.AddDays(-1);
            if (issuer is null) return request.CreateSelfSigned(from, from.AddYears(1));
            using X509Certificate2 signed = request.Create(issuer, from, from.AddYears(1), RandomNumberGenerator.GetBytes(8));
            // Return with its key
            return signed.CopyWithPrivateKey(key);
        }

        /// <summary>Dispose the certificates.</summary>
        public void Dispose()
        {
            // Both
            Root.Dispose();
            Intermediate.Dispose();
        }
    }

    /// <summary>The server sends its certificate with the intermediate, so a client trusting only the root accepts it; replaced files are served after the check interval.</summary>
    [Fact]
    public async Task ServeChainAndRenew()
    {
        // Arrange: certbot-like files, and the server on a free port
        string directory = Directory.CreateTempSubdirectory("uniform-matrix-tls-").FullName;
        string certificatePath = Path.Combine(directory, "fullchain.pem");
        string keyPath = Path.Combine(directory, "privkey.pem");
        using TestAuthority authority = new();
        using X509Certificate2 first = authority.WriteServerCertificate(certificatePath, keyPath);
        FakeTimeProvider time = new(DateTimeOffset.UtcNow);
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "Uniform.Matrix.Server.Tests", EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Urls"] = "https://127.0.0.1:0",
            ["Server:ServerName"] = "localhost",
            ["Storage:DataDirectory"] = directory,
            ["Tls:CertificatePath"] = certificatePath,
            ["Tls:KeyPath"] = keyPath,
        });
        builder.AddMatrixServer();
        builder.Services.AddSingleton<TimeProvider>(time);
        await using WebApplication app = builder.Build();
        app.MapMatrixServer();
        await app.StartAsync(Ct);
        Uri address = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());

        try
        {
            // Act: connect, renew the files, connect before and after the check interval
            (X509Certificate2 served, bool trusted) = await ConnectAsync(address, authority.Root);
            using X509Certificate2 second = authority.WriteServerCertificate(certificatePath, keyPath);
            (X509Certificate2 beforeCheck, _) = await ConnectAsync(address, authority.Root);
            time.Advance(TlsCertificate.CheckInterval);
            (X509Certificate2 afterCheck, bool renewedTrusted) = await ConnectAsync(address, authority.Root);

            // Assert
            Assert.True(trusted);
            Assert.Equal(first.Thumbprint, served.Thumbprint);
            Assert.Equal(first.Thumbprint, beforeCheck.Thumbprint);
            Assert.Equal(second.Thumbprint, afterCheck.Thumbprint);
            Assert.True(renewedTrusted);
        }
        finally
        {
            await app.StopAsync(Ct);
            Directory.Delete(directory, true);
        }
    }

    /// <summary>Make a TLS connection; return the server's certificate and whether it chains to <paramref name="root"/> with only the certificates the server sent.</summary>
    private static async Task<(X509Certificate2 Certificate, bool Trusted)> ConnectAsync(Uri address, X509Certificate2 root)
    {
        // Connect without fetching anything; the chain must come from the server
        using TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, address.Port, Ct);
        bool trusted = false;
        await using SslStream ssl = new(tcp.GetStream(), false, (_, certificate, chain, _) =>
        {
            using X509Chain check = new();
            check.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            check.ChainPolicy.CustomTrustStore.Add(root);
            check.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            check.ChainPolicy.DisableCertificateDownloads = true;
            check.ChainPolicy.ExtraStore.AddRange(chain!.ChainPolicy.ExtraStore);
            trusted = check.Build(X509CertificateLoader.LoadCertificate(certificate!.GetRawCertData()));
            return true;
        });
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, Ct);
        // Return what was served
        return (X509CertificateLoader.LoadCertificate(ssl.RemoteCertificate!.GetRawCertData()), trusted);
    }
}
