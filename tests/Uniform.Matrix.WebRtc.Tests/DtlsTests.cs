namespace Uniform.Matrix.WebRtc.Tests;

using Org.BouncyCastle.Tls;
using Uniform.Matrix.WebRtc.Dtls;
using Uniform.Matrix.WebRtc.Srtp;

/// <summary>Tests of <see cref="DtlsSrtp"/> between two sides in memory.</summary>
public class DtlsTests
{
    /// <summary>Handshake, and protect with one side's outbound session what the other's inbound one unprotects.</summary>
    [Fact]
    public async Task HandshakeKeys()
    {
        // Two sides, each sending to the other
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DtlsCertificate clientCertificate = new(), serverCertificate = new();
        DtlsSrtp? client = null, server = null;
        client = new DtlsSrtp(clientCertificate, true, serverCertificate.Fingerprint, datagram => server!.Receive(datagram));
        server = new DtlsSrtp(serverCertificate, false, clientCertificate.Fingerprint, datagram => client.Receive(datagram));
        using (client)
        using (server)
        {
            (SrtpSession Outbound, SrtpSession Inbound)[] sessions = await Task.WhenAll(client.HandshakeAsync(cancellationToken), server.HandshakeAsync(cancellationToken)).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);

            // Both ways
            byte[] rtp = Convert.FromHexString("800f1234decafbadcafebabeabababababababababababababababab");
            Assert.Equal(rtp, sessions[1].Inbound.UnprotectRtp(sessions[0].Outbound.ProtectRtp(rtp)));
            // Done
            Assert.Equal(rtp, sessions[0].Inbound.UnprotectRtp(sessions[1].Outbound.ProtectRtp(rtp)));
        }
    }

    /// <summary>Fail the handshake when the server's certificate is not the one signalled.</summary>
    [Fact]
    public async Task RefuseWrongFingerprint()
    {
        // The client expects another certificate
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DtlsCertificate clientCertificate = new(), serverCertificate = new(), other = new();
        DtlsSrtp? client = null, server = null;
        client = new DtlsSrtp(clientCertificate, true, other.Fingerprint, datagram => server!.Receive(datagram));
        server = new DtlsSrtp(serverCertificate, false, clientCertificate.Fingerprint, datagram => client.Receive(datagram));
        using (client)
        using (server)
        {
            Task<(SrtpSession, SrtpSession)> serverHandshake = server.HandshakeAsync(cancellationToken);
            await Assert.ThrowsAsync<TlsFatalAlert>(() => client.HandshakeAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken));
            // Done
            await Assert.ThrowsAnyAsync<Exception>(() => serverHandshake.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken));
        }
    }

    /// <summary>Write fingerprints as SDP does.</summary>
    [Fact]
    public void WriteFingerprint()
    {
        // Algorithm, then 32 hex pairs
        DtlsCertificate certificate = new();
        Assert.Matches("^sha-256 ([0-9A-F]{2}:){31}[0-9A-F]{2}$", certificate.Fingerprint);
        // Done
        Assert.True(DtlsCertificate.Matches(certificate.Der, certificate.Fingerprint.ToLowerInvariant()));
    }
}
