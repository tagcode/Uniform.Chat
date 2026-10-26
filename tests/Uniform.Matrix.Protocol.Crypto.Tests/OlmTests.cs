namespace Uniform.Matrix.Protocol.Crypto.Tests;

using System.Security.Cryptography;
using System.Text;
using Uniform.Matrix.Protocol.Core;

/// <summary>Round-trip tests of the managed Olm, Megolm, Ed25519, backup and SAS implementation.</summary>
public class OlmTests
{
    private static readonly byte[] PickleKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>Identity keys are base64 Curve25519 and Ed25519 keys, stable for an account.</summary>
    [Fact]
    public void CanCreateAccountWithIdentityKeys()
    {
        // Act
        using OlmAccount account = OlmAccount.Create();
        IdentityKeys keys = account.IdentityKeys;

        // Assert: 32-byte keys are 43 characters in unpadded base64
        Assert.Equal(43, keys.Curve25519.Length);
        Assert.Equal(43, keys.Ed25519.Length);
        Assert.Equal(keys, account.IdentityKeys);
        Assert.True(account.MaxOneTimeKeys >= 50);
    }

    /// <summary>One-time and fallback keys are listed until published.</summary>
    [Fact]
    public void CanGenerateAndPublishKeys()
    {
        // Arrange
        using OlmAccount account = OlmAccount.Create();

        // Act
        account.GenerateOneTimeKeys(5);
        account.GenerateFallbackKey();

        // Assert
        Assert.Equal(5, account.OneTimeKeys.Count);
        Assert.Single(account.FallbackKey);
        account.MarkKeysAsPublished();
        Assert.Empty(account.OneTimeKeys);
        Assert.Empty(account.FallbackKey);
    }

    /// <summary>Signatures verify with the signer's Ed25519 key and fail for changed messages.</summary>
    [Fact]
    public void CanSignAndVerify()
    {
        // Arrange
        using OlmAccount account = OlmAccount.Create();
        string message = """{"algorithms":["m.olm.v1.curve25519-aes-sha2"],"device_id":"ABC"}""";

        // Act
        string signature = account.Sign(message);

        // Assert
        Assert.True(Ed25519.Verify(account.IdentityKeys.Ed25519, message, signature));
        Assert.False(Ed25519.Verify(account.IdentityKeys.Ed25519, message + " ", signature));
        Assert.False(Ed25519.Verify("not a key", message, signature));
    }

    /// <summary>Alice starts a session with Bob's one-time key; Bob creates his side from the pre-key message and they keep talking.</summary>
    [Fact]
    public void CanExchangeOlmMessages()
    {
        // Arrange: Bob publishes a one-time key
        using OlmAccount alice = OlmAccount.Create();
        using OlmAccount bob = OlmAccount.Create();
        bob.GenerateOneTimeKeys(1);
        string oneTimeKey = bob.OneTimeKeys.Values.Single();
        bob.MarkKeysAsPublished();

        // Act: first message is a pre-key message
        using OlmSession aliceSession = alice.CreateOutboundSession(bob.IdentityKeys.Curve25519, oneTimeKey);
        OlmMessage first = aliceSession.Encrypt("hello Bob");
        Assert.Equal(0, first.Type);
        Assert.Equal(aliceSession.SessionId, OlmSession.PreKeySessionId(first.Body));
        (OlmSession bobSession, byte[] plaintext) = bob.CreateInboundSession(alice.IdentityKeys.Curve25519, first.Body);
        using (bobSession)
        {
            // Assert first message
            Assert.Equal("hello Bob", Encoding.UTF8.GetString(plaintext));
            Assert.Equal(aliceSession.SessionId, bobSession.SessionId);

            // Act and assert: reply is a normal message once Alice has heard back
            OlmMessage reply = bobSession.Encrypt("hi Alice");
            Assert.Equal(1, reply.Type);
            Assert.Equal("hi Alice", Encoding.UTF8.GetString(aliceSession.Decrypt(reply)));
            Assert.Equal("again", Encoding.UTF8.GetString(bobSession.Decrypt(aliceSession.Encrypt("again"))));
        }
    }

    /// <summary>A one-time key is used up by the inbound session, so the same pre-key message cannot create a second one.</summary>
    [Fact]
    public void CanUseOneTimeKeyOnlyOnce()
    {
        // Arrange
        using OlmAccount alice = OlmAccount.Create();
        using OlmAccount bob = OlmAccount.Create();
        bob.GenerateOneTimeKeys(1);
        using OlmSession session = alice.CreateOutboundSession(bob.IdentityKeys.Curve25519, bob.OneTimeKeys.Values.Single());
        OlmMessage message = session.Encrypt("once");
        bob.CreateInboundSession(alice.IdentityKeys.Curve25519, message.Body).Session.Dispose();

        // Act and assert
        OlmException ex = Assert.Throws<OlmException>(() => bob.CreateInboundSession(alice.IdentityKeys.Curve25519, message.Body));
        Assert.NotEmpty(ex.Message);
    }

    /// <summary>Room messages decrypt with the shared session key, carry increasing indices, and fail with another session.</summary>
    [Fact]
    public void CanExchangeMegolmMessages()
    {
        // Arrange: sender shares the key before the first message
        using MegolmOutboundSession outbound = MegolmOutboundSession.Create();
        using MegolmInboundSession inbound = MegolmInboundSession.FromSessionKey(outbound.SessionKey);
        using MegolmInboundSession stranger = MegolmInboundSession.FromSessionKey(MegolmOutboundSession.Create().SessionKey);

        // Act
        string first = outbound.Encrypt("""{"type":"m.room.message","content":{"body":"hi"}}""");
        string second = outbound.Encrypt("second");

        // Assert
        Assert.Equal(outbound.SessionId, inbound.SessionId);
        Assert.Equal(2u, outbound.MessageIndex);
        MegolmPlaintext decrypted = inbound.Decrypt(first);
        Assert.Equal(0u, decrypted.MessageIndex);
        Assert.Contains("\"body\":\"hi\"", decrypted.Text);
        Assert.Equal((1u, "second"), (inbound.Decrypt(second).MessageIndex, inbound.Decrypt(second).Text));
        Assert.Throws<OlmException>(() => stranger.Decrypt(first));
    }

    /// <summary>A key shared later decrypts only later messages; an export from an index can be imported elsewhere.</summary>
    [Fact]
    public void CanShareFromLaterIndexAndExport()
    {
        // Arrange: one message before a late joiner gets the key
        using MegolmOutboundSession outbound = MegolmOutboundSession.Create();
        string early = outbound.Encrypt("before you joined");
        using MegolmInboundSession late = MegolmInboundSession.FromSessionKey(outbound.SessionKey);
        string later = outbound.Encrypt("after");

        // Act
        using MegolmInboundSession imported = MegolmInboundSession.Import(late.ExportAt(1));

        // Assert
        Assert.Equal(1u, late.FirstKnownIndex);
        Assert.Throws<OlmException>(() => late.Decrypt(early));
        Assert.Equal("after", late.Decrypt(later).Text);
        Assert.Equal("after", imported.Decrypt(later).Text);
        Assert.Throws<OlmException>(() => late.ExportAt(0));
    }

    /// <summary>All objects survive pickling and keeps working; a wrong key does not open the pickle.</summary>
    [Fact]
    public void CanPickleAndRestore()
    {
        // Arrange
        using OlmAccount alice = OlmAccount.Create();
        using OlmAccount bob = OlmAccount.Create();
        bob.GenerateOneTimeKeys(1);
        using OlmSession aliceSession = alice.CreateOutboundSession(bob.IdentityKeys.Curve25519, bob.OneTimeKeys.Values.Single());
        OlmMessage prekey = aliceSession.Encrypt("one");
        using MegolmOutboundSession outbound = MegolmOutboundSession.Create();
        using MegolmInboundSession inbound = MegolmInboundSession.FromSessionKey(outbound.SessionKey);

        // Act
        using OlmAccount restoredBob = OlmAccount.FromPickle(bob.Pickle(PickleKey), PickleKey);
        using OlmSession restoredAlice = OlmSession.FromPickle(aliceSession.Pickle(PickleKey), PickleKey);
        using MegolmOutboundSession restoredOutbound = MegolmOutboundSession.FromPickle(outbound.Pickle(PickleKey), PickleKey);
        using MegolmInboundSession restoredInbound = MegolmInboundSession.FromPickle(inbound.Pickle(PickleKey), PickleKey);

        // Assert restored objects keep state and interoperate
        Assert.Equal(bob.IdentityKeys, restoredBob.IdentityKeys);
        (OlmSession bobSession, byte[] plaintext) = restoredBob.CreateInboundSession(alice.IdentityKeys.Curve25519, prekey.Body);
        bobSession.Dispose();
        Assert.Equal("one", Encoding.UTF8.GetString(plaintext));
        Assert.Equal(aliceSession.SessionId, restoredAlice.SessionId);
        Assert.Equal("restored", restoredInbound.Decrypt(restoredOutbound.Encrypt("restored")).Text);

        // Assert wrong key fails
        Assert.Throws<OlmException>(() => OlmAccount.FromPickle(bob.Pickle(PickleKey), RandomNumberGenerator.GetBytes(32)));
        Assert.Throws<ArgumentException>(() => bob.Pickle(new byte[16]));
    }

    /// <summary>Standalone Ed25519 keys, as used for cross-signing, sign and verifies.</summary>
    [Fact]
    public void CanSignWithStandaloneKey()
    {
        // Arrange
        (string secret, string publicKey) = Ed25519.GenerateKey();

        // Act
        string signature = Ed25519.Sign(secret, "{}");

        // Assert
        Assert.Equal(publicKey, Ed25519.GetPublicKey(secret));
        Assert.True(Ed25519.Verify(publicKey, "{}", signature));
        Assert.False(Ed25519.Verify(publicKey, "{ }", signature));
    }

    /// <summary>Backup encryption decrypts with the secret key and fails with another.</summary>
    [Fact]
    public void CanEncryptForBackup()
    {
        // Arrange
        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=');
        string publicKey = PkEncryption.GetPublicKey(secret);

        // Act
        PkMessage message = PkEncryption.Encrypt(publicKey, """{"session_key":"abc"}""");

        // Assert
        Assert.Equal("""{"session_key":"abc"}""", PkEncryption.Decrypt(secret, message));
        string other = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=');
        Assert.Throws<OlmException>(() => PkEncryption.Decrypt(other, message));
    }

    /// <summary>Both sides of a SAS derive the same emoji and accepts each other's MACs.</summary>
    [Fact]
    public void CanAgreeOnSas()
    {
        // Arrange
        using Sas alice = Sas.Create();
        using Sas bob = Sas.Create();
        string alicePublic = alice.PublicKey, bobPublic = bob.PublicKey;

        // Act
        using EstablishedSas aliceSas = alice.Establish(bobPublic);
        using EstablishedSas bobSas = bob.Establish(alicePublic);
        byte[] aliceBytes = aliceSas.GetBytes("info"), bobBytes = bobSas.GetBytes("info");
        string mac = aliceSas.CalculateMac("ed25519:KEY", "mac-info");

        // Assert
        Assert.Equal(6, aliceBytes.Length);
        Assert.Equal(EstablishedSas.EmojiIndices(aliceBytes), EstablishedSas.EmojiIndices(bobBytes));
        Assert.All(EstablishedSas.EmojiIndices(aliceBytes), i => Assert.InRange(i, 0, 63));
        Assert.All(EstablishedSas.Decimals(aliceBytes), d => Assert.InRange(d, 1000, 9191));
        Assert.True(bobSas.VerifyMac("ed25519:KEY", "mac-info", mac));
        Assert.False(bobSas.VerifyMac("ed25519:OTHER", "mac-info", mac));
    }

    /// <summary>Emoji and decimals follow the spec bit layout.</summary>
    [Fact]
    public void CanSplitSasBytes()
    {
        // Arrange: 0b000001_000010_000011_000100_000101_000110_000111_xxxxxx
        byte[] bytes = [0x04, 0x20, 0xC4, 0x14, 0x61, 0xC0];

        // Act and assert
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], EstablishedSas.EmojiIndices(bytes));
        Assert.Equal([1000, 1000, 1000], EstablishedSas.Decimals(new byte[6]));
        Assert.Equal([9191, 9191, 9191], EstablishedSas.Decimals([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]));
    }

    /// <summary>Changed Olm and Megolm messages, and session keys with a bad signature, are rejected without changing state.</summary>
    [Fact]
    public void CanRejectTamperedMessages()
    {
        // Arrange: an Olm pair and a Megolm pair
        using OlmAccount alice = OlmAccount.Create();
        using OlmAccount bob = OlmAccount.Create();
        bob.GenerateOneTimeKeys(1);
        using OlmSession aliceSession = alice.CreateOutboundSession(bob.IdentityKeys.Curve25519, bob.OneTimeKeys.Values.Single());
        (OlmSession bobSession, _) = bob.CreateInboundSession(alice.IdentityKeys.Curve25519, aliceSession.Encrypt("hi").Body);
        using MegolmOutboundSession outbound = MegolmOutboundSession.Create();
        using MegolmInboundSession inbound = MegolmInboundSession.FromSessionKey(outbound.SessionKey);

        // Act: flip one byte in the middle of each
        OlmMessage reply = bobSession.Encrypt("reply");
        string megolm = outbound.Encrypt("room");
        static string Flip(string base64)
        {
            byte[] bytes = Base64.Decode(base64);
            bytes[bytes.Length / 2] ^= 1;
            return Base64.EncodeUnpadded(bytes);
        }

        // Assert rejected, and the originals still decrypt
        using (bobSession)
        {
            Assert.Throws<OlmException>(() => aliceSession.Decrypt(reply with { Body = Flip(reply.Body) }));
            Assert.Equal("reply", Encoding.UTF8.GetString(aliceSession.Decrypt(reply)));
        }
        Assert.Throws<OlmException>(() => inbound.Decrypt(Flip(megolm)));
        Assert.Equal("room", inbound.Decrypt(megolm).Text);
        Assert.Throws<OlmException>(() => MegolmInboundSession.FromSessionKey(Flip(outbound.SessionKey)));
        Assert.Throws<OlmException>(() => inbound.Decrypt("not base64!"));
    }
}
