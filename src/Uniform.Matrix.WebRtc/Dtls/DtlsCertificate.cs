namespace Uniform.Matrix.WebRtc.Dtls;

using System.Security.Cryptography;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

/// <summary>A self-signed ECDSA P-256 certificate for DTLS-SRTP; WebRTC trusts it by the fingerprint that the signalled SDP carries, not by any authority.</summary>
public sealed class DtlsCertificate
{
    /// <summary>Generate a key and a certificate valid for thirty days.</summary>
    public DtlsCertificate()
    {
        // P-256 key
        SecureRandom random = new();
        ECKeyPairGenerator generator = new();
        generator.Init(new ECKeyGenerationParameters(SecObjectIdentifiers.SecP256r1, random));
        AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();

        // Self-signed with SHA-256
        X509V3CertificateGenerator certificate = new();
        X509Name name = new("CN=WebRTC");
        certificate.SetSerialNumber(new BigInteger(64, random));
        certificate.SetIssuerDN(name);
        certificate.SetSubjectDN(name);
        certificate.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        certificate.SetNotAfter(DateTime.UtcNow.AddDays(30));
        certificate.SetPublicKey(pair.Public);
        PrivateKey = pair.Private;
        Der = certificate.Generate(new Asn1SignatureFactory("SHA256WITHECDSA", pair.Private, random)).GetEncoded();
        Fingerprint = FingerprintOf(Der);
    }

    /// <summary>The certificate, DER encoded.</summary>
    public byte[] Der { get; }

    /// <summary>The private key.</summary>
    internal AsymmetricKeyParameter PrivateKey { get; }

    /// <summary>The fingerprint for the SDP's <c>a=fingerprint</c>, e.g. <c>sha-256 AB:CD:…</c>.</summary>
    public string Fingerprint { get; }

    /// <summary>Return the SHA-256 fingerprint of a DER certificate as SDP writes it.</summary>
    public static string FingerprintOf(byte[] der) => "sha-256 " + Convert.ToHexString(SHA256.HashData(der)).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + ":" + b);

    /// <summary>Return whether a DER certificate matches an SDP fingerprint; only SHA-256 is known, as browsers use.</summary>
    public static bool Matches(byte[] der, string fingerprint) => string.Equals(FingerprintOf(der), fingerprint.Trim(), StringComparison.OrdinalIgnoreCase);
}
