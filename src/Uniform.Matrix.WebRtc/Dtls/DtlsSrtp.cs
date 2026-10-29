namespace Uniform.Matrix.WebRtc.Dtls;

using System.Collections.Concurrent;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Uniform.Matrix.WebRtc.Srtp;

/// <summary><see href="https://www.rfc-editor.org/rfc/rfc5764">DTLS-SRTP</see> over a datagram path, e.g. an ICE pair: a DTLS 1.2 handshake with BouncyCastle that checks the peer's certificate against its signalled fingerprint, and yields the SRTP sessions of both directions.</summary>
/// <remarks>Feed the DTLS records received, those with first bytes 20 to 63 as <see href="https://www.rfc-editor.org/rfc/rfc7983">RFC 7983</see> says, to <see cref="Receive"/>. BouncyCastle's DTLS blocks, so the handshake runs on a thread of its own, and after it a thread keeps answering retransmissions and notices the peer closing.</remarks>
public sealed class DtlsSrtp : IDisposable
{
    /// <summary>Largest datagram sent: fits the 1280-byte IPv6 minimum with the TURN, UDP and IP headers.</summary>
    private const int SendLimit = 1200;
    /// <summary>Largest datagram received.</summary>
    private const int ReceiveLimit = 1500;
    /// <summary>How long the handshake may take.</summary>
    private const int HandshakeTimeoutMilliseconds = 20_000;
    private readonly DtlsCertificate certificate;
    private readonly bool isClient;
    private readonly string remoteFingerprint;
    private readonly Transport transport;
    private DtlsTransport? dtls;
    private Thread? closeWatcher;

    /// <summary>Prepare a handshake as client or server.</summary>
    /// <param name="certificate">This side's certificate, whose fingerprint the local SDP carries.</param>
    /// <param name="isClient">Whether this side is the DTLS client: <c>a=setup:active</c>; the other is <c>passive</c>.</param>
    /// <param name="remoteFingerprint">The peer's <c>a=fingerprint</c>, e.g. <c>sha-256 AB:CD:…</c>.</param>
    /// <param name="send">Sends a datagram to the peer.</param>
    public DtlsSrtp(DtlsCertificate certificate, bool isClient, string remoteFingerprint, Action<byte[]> send)
    {
        // Stored for the handshake
        this.certificate = certificate;
        this.isClient = isClient;
        this.remoteFingerprint = remoteFingerprint;
        transport = new Transport(send);
    }

    /// <summary>Raised once when the peer closes DTLS, or it fails after the handshake.</summary>
    public event Action? Closed;

    /// <summary>Give a received DTLS record to the handshake.</summary>
    public void Receive(byte[] datagram) => transport.Incoming.TryAdd(datagram);

    /// <summary>Run the handshake; return the SRTP session that protects what this side sends, and the one that unprotects what it receives.</summary>
    /// <exception cref="TlsFatalAlert">On a failed handshake, e.g. a certificate that does not match the fingerprint.</exception>
    public async Task<(SrtpSession Outbound, SrtpSession Inbound)> HandshakeAsync(CancellationToken cancellationToken = default)
    {
        // On a thread of its own, as it blocks; cancelling closes the transport, which ends it
        TaskCompletionSource<byte[]> keys = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => Handshake(keys)) { IsBackground = true, Name = "DTLS handshake" };
        thread.Start();
        byte[] material;
        using (cancellationToken.Register(transport.Close)) material = await keys.Task;
        cancellationToken.ThrowIfCancellationRequested();

        // Client key, server key, client salt, server salt, as RFC 5764 4.2 lays them out
        const int KeyLength = SrtpSession.MasterKeyLength, SaltLength = SrtpSession.MasterSaltLength;
        ReadOnlySpan<byte> clientKey = material.AsSpan(0, KeyLength), serverKey = material.AsSpan(KeyLength, KeyLength);
        ReadOnlySpan<byte> clientSalt = material.AsSpan(2 * KeyLength, SaltLength), serverSalt = material.AsSpan(2 * KeyLength + SaltLength, SaltLength);
        SrtpSession client = new(clientKey, clientSalt), server = new(serverKey, serverSalt);
        closeWatcher = new Thread(WatchClose) { IsBackground = true, Name = "DTLS" };
        closeWatcher.Start();
        // Return outbound and inbound
        return isClient ? (client, server) : (server, client);
    }

    /// <summary>Send close_notify, and stop.</summary>
    public void Dispose()
    {
        // BouncyCastle sends the alert on close
        try { dtls?.Close(); }
        catch (Exception) { }
        // Done
        transport.Close();
    }

    /// <summary>Run BouncyCastle's handshake, completing <paramref name="keys"/> with the exported keying material.</summary>
    private void Handshake(TaskCompletionSource<byte[]> keys)
    {
        // Client or server
        try
        {
            BcTlsCrypto crypto = new();
            byte[]? material = null;
            void Export(TlsContext context) => material = context.ExportKeyingMaterial(ExporterLabel.dtls_srtp, null, 2 * (SrtpSession.MasterKeyLength + SrtpSession.MasterSaltLength));
            dtls = isClient
                ? new DtlsClientProtocol().Connect(new Client(crypto, certificate, remoteFingerprint, Export), transport)
                : new DtlsServerProtocol().Accept(new Server(crypto, certificate, remoteFingerprint, Export), transport);
            // Done
            if (material is null) throw new TlsFatalAlert(AlertDescription.internal_error);
            keys.TrySetResult(material);
        }
        catch (Exception e)
        {
            // On failure
            keys.TrySetException(e);
        }
    }

    /// <summary>Read DTLS after the handshake, which answers retransmitted flights, until the peer closes or the transport does.</summary>
    private void WatchClose()
    {
        // Application data is not expected; reading ends with an alert or a closed transport
        byte[] buffer = new byte[ReceiveLimit];
        try
        {
            while (!transport.IsClosed && dtls!.Receive(buffer, 0, buffer.Length, 1000) >= -1) { }
        }
        catch (Exception)
        {
            // Closed or failed
        }
        // Done
        Closed?.Invoke();
    }

    /// <summary>Return the credentials that sign this side's handshake with its certificate.</summary>
    private static TlsCredentialedSigner Signer(BcTlsCrypto crypto, TlsContext context, DtlsCertificate certificate)
    {
        // ECDSA with SHA-256
        Certificate chain = new([new BcTlsCertificate(crypto, certificate.Der)]);
        // Return signer
        return new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(context), crypto, certificate.PrivateKey, chain, new SignatureAndHashAlgorithm(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa));
    }

    /// <summary>Throw unless the peer's certificate matches its fingerprint.</summary>
    private static void Verify(Certificate peer, string fingerprint)
    {
        // The leaf certificate decides
        if (peer.IsEmpty || !DtlsCertificate.Matches(peer.GetCertificateAt(0).GetEncoded(), fingerprint)) throw new TlsFatalAlert(AlertDescription.bad_certificate);
    }

    /// <summary>The use_srtp extension offering AES_CM_128_HMAC_SHA1_80, the profile WebRTC requires.</summary>
    private static UseSrtpData SrtpProfile() => new([SrtpProtectionProfile.SRTP_AES128_CM_HMAC_SHA1_80], TlsUtilities.EmptyBytes);

    /// <summary>The DTLS client side.</summary>
    private sealed class Client(BcTlsCrypto crypto, DtlsCertificate certificate, string fingerprint, Action<TlsContext> export) : DefaultTlsClient(crypto)
    {
        /// <summary>DTLS 1.2 only.</summary>
        protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.DTLSv12.Only();

        /// <summary>Give up after the handshake timeout.</summary>
        public override int GetHandshakeTimeoutMillis() => HandshakeTimeoutMilliseconds;

        /// <summary>Offer use_srtp.</summary>
        public override IDictionary<int, byte[]> GetClientExtensions()
        {
            // The defaults, and use_srtp
            IDictionary<int, byte[]> extensions = TlsExtensionsUtilities.EnsureExtensionsInitialised(base.GetClientExtensions());
            TlsSrtpUtilities.AddUseSrtpExtension(extensions, SrtpProfile());
            // Return extensions
            return extensions;
        }

        /// <summary>Require the server to agree to use_srtp.</summary>
        public override void ProcessServerExtensions(IDictionary<int, byte[]> serverExtensions)
        {
            // The defaults' checks, then the profile
            base.ProcessServerExtensions(serverExtensions);
            UseSrtpData? srtp = TlsSrtpUtilities.GetUseSrtpExtension(serverExtensions);
            if (srtp is null || !srtp.ProtectionProfiles.Contains(SrtpProtectionProfile.SRTP_AES128_CM_HMAC_SHA1_80)) throw new TlsFatalAlert(AlertDescription.illegal_parameter);
        }

        /// <summary>Check the server's certificate, and sign with this side's.</summary>
        public override TlsAuthentication GetAuthentication() => new Authentication(this);

        /// <summary>Export the SRTP keys.</summary>
        public override void NotifyHandshakeComplete()
        {
            // While the context has them
            base.NotifyHandshakeComplete();
            export(m_context);
        }

        /// <summary>Checks the server's certificate, and signs with this side's.</summary>
        private sealed class Authentication(Client client) : TlsAuthentication
        {
            /// <summary>Check the server's certificate against its fingerprint.</summary>
            public void NotifyServerCertificate(TlsServerCertificate serverCertificate) => Verify(serverCertificate.Certificate, client.fingerprint);

            /// <summary>Sign with this side's certificate, which the server requires.</summary>
            public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest) => Signer(client.crypto, client.m_context, client.certificate);
        }

        private readonly BcTlsCrypto crypto = crypto;
        private readonly DtlsCertificate certificate = certificate;
        private readonly string fingerprint = fingerprint;
    }

    /// <summary>The DTLS server side.</summary>
    private sealed class Server(BcTlsCrypto crypto, DtlsCertificate certificate, string fingerprint, Action<TlsContext> export) : DefaultTlsServer(crypto)
    {
        private UseSrtpData? srtp;

        /// <summary>DTLS 1.2 only.</summary>
        protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.DTLSv12.Only();

        /// <summary>Give up after the handshake timeout.</summary>
        public override int GetHandshakeTimeoutMillis() => HandshakeTimeoutMilliseconds;

        /// <summary>The suites an ECDSA certificate signs, strongest first.</summary>
        protected override int[] GetSupportedCipherSuites() =>
        [
            CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
            CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
            CipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
            CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256,
            CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA,
        ];

        /// <summary>Require use_srtp with the profile.</summary>
        public override void ProcessClientExtensions(IDictionary<int, byte[]> clientExtensions)
        {
            // The defaults' checks, then the profile
            base.ProcessClientExtensions(clientExtensions);
            srtp = TlsSrtpUtilities.GetUseSrtpExtension(clientExtensions);
            if (srtp is null || !srtp.ProtectionProfiles.Contains(SrtpProtectionProfile.SRTP_AES128_CM_HMAC_SHA1_80)) throw new TlsFatalAlert(AlertDescription.handshake_failure);
        }

        /// <summary>Agree to use_srtp.</summary>
        public override IDictionary<int, byte[]> GetServerExtensions()
        {
            // The defaults, and the profile chosen
            IDictionary<int, byte[]> extensions = TlsExtensionsUtilities.EnsureExtensionsInitialised(base.GetServerExtensions());
            TlsSrtpUtilities.AddUseSrtpExtension(extensions, SrtpProfile());
            // Return extensions
            return extensions;
        }

        /// <summary>Sign with this side's certificate.</summary>
        protected override TlsCredentialedSigner GetECDsaSignerCredentials() => Signer(crypto, m_context, certificate);

        /// <summary>Require the client's certificate, to check its fingerprint.</summary>
        public override CertificateRequest GetCertificateRequest() =>
            new([ClientCertificateType.ecdsa_sign, ClientCertificateType.rsa_sign], TlsUtilities.GetDefaultSupportedSignatureAlgorithms(m_context), null);

        /// <summary>Check the client's certificate against its fingerprint.</summary>
        public override void NotifyClientCertificate(Certificate clientCertificate) => Verify(clientCertificate, fingerprint);

        /// <summary>Export the SRTP keys.</summary>
        public override void NotifyHandshakeComplete()
        {
            // While the context has them
            base.NotifyHandshakeComplete();
            export(m_context);
        }

        private readonly BcTlsCrypto crypto = crypto;
        private readonly DtlsCertificate certificate = certificate;
    }

    /// <summary>BouncyCastle's datagram transport over the records fed to <see cref="DtlsSrtp.Receive"/> and the send callback.</summary>
    private sealed class Transport(Action<byte[]> send) : DatagramTransport
    {
        /// <summary>Records received, not yet read.</summary>
        public BlockingCollection<byte[]> Incoming { get; } = new(64);

        /// <summary>Whether closed.</summary>
        public bool IsClosed => Incoming.IsAddingCompleted;

        /// <summary>Return the largest datagram received.</summary>
        public int GetReceiveLimit() => ReceiveLimit;

        /// <summary>Return the largest datagram sent.</summary>
        public int GetSendLimit() => SendLimit;

        /// <summary>Wait for a record; return its length, or -1 after the wait.</summary>
        public int Receive(byte[] buf, int off, int len, int waitMillis) => Receive(buf.AsSpan(off, len), waitMillis);

        /// <summary>Wait for a record; return its length, or -1 after the wait.</summary>
        public int Receive(Span<byte> buffer, int waitMillis)
        {
            // Closed ends the protocol
            if (IsClosed && Incoming.Count == 0) throw new TlsFatalAlert(AlertDescription.close_notify);
            if (!Incoming.TryTake(out byte[]? datagram, waitMillis)) return -1;
            int length = Math.Min(datagram.Length, buffer.Length);
            datagram.AsSpan(0, length).CopyTo(buffer);
            // Return length
            return length;
        }

        /// <summary>Send a record.</summary>
        public void Send(byte[] buf, int off, int len) => send(buf.AsSpan(off, len).ToArray());

        /// <summary>Send a record.</summary>
        public void Send(ReadOnlySpan<byte> buffer) => send(buffer.ToArray());

        /// <summary>Stop taking records.</summary>
        public void Close() => Incoming.CompleteAdding();
    }
}
