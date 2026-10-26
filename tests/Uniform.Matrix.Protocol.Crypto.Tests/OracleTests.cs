namespace Uniform.Matrix.Protocol.Crypto.Tests;

using System.Security.Cryptography;
using System.Text;
using Uniform.Matrix.Protocol.Core;
using Uniform.Matrix.Protocol.Crypto.Tests.Native;

/// <summary>Differential tests of the managed implementation against native vodozemac: messages, keys and pickles must work across both, in both directions.</summary>
/// <remarks>Skipped when the native library is not built; run <c>native/build.sh</c> to enable them.</remarks>
public class OracleTests
{
    private static readonly byte[] PickleKey = RandomNumberGenerator.GetBytes(32);
    private static readonly Lazy<bool> NativeAvailable = new(() =>
    {
        // Probe the library once
        try
        {
            NativeOlmAccount.Create().Dispose();
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    });

    /// <summary>Skip the test without the native oracle.</summary>
    private static void RequireNative() => Assert.SkipUnless(NativeAvailable.Value, "Native vodozemac is not built; run native/build.sh.");

    /// <summary>Accounts pickled by either implementation load in the other with the same keys, and signatures verify across.</summary>
    [Fact]
    public void CanShareAccountPickles()
    {
        // Arrange
        RequireNative();
        using NativeOlmAccount native = NativeOlmAccount.Create();
        native.GenerateOneTimeKeys(3);
        native.GenerateFallbackKey();

        // Act: native to managed, then back
        using OlmAccount managed = OlmAccount.FromPickle(native.Pickle(PickleKey), PickleKey);
        managed.GenerateOneTimeKeys(2);
        using NativeOlmAccount again = NativeOlmAccount.FromPickle(managed.Pickle(PickleKey), PickleKey);

        // Assert
        Assert.Equal(native.IdentityKeys, managed.IdentityKeys);
        Assert.Equal(native.FallbackKey, managed.FallbackKey);
        Assert.Equal(native.MaxOneTimeKeys, managed.MaxOneTimeKeys);
        Assert.Equal(managed.OneTimeKeys, again.OneTimeKeys);
        Assert.Equal(5, again.OneTimeKeys.Count);
        Assert.Equal(native.Sign("{}"), managed.Sign("{}"));
        Assert.True(NativeEd25519.Verify(managed.IdentityKeys.Ed25519, "message", managed.Sign("message")));
    }

    /// <summary>A managed device starts a session with a native one; both sides send several messages, some out of order.</summary>
    [Fact]
    public void CanTalkOlmManagedToNative()
    {
        // Arrange: native bob publishes a one-time key
        RequireNative();
        using OlmAccount alice = OlmAccount.Create();
        using NativeOlmAccount bob = NativeOlmAccount.Create();
        bob.GenerateOneTimeKeys(1);

        // Act: pre-key message creates bob's session
        using OlmSession aliceSession = alice.CreateOutboundSession(bob.IdentityKeys.Curve25519, bob.OneTimeKeys.Values.Single());
        OlmMessage first = aliceSession.Encrypt("hello bob");
        (NativeOlmSession bobSession, byte[] plaintext) = bob.CreateInboundSession(alice.IdentityKeys.Curve25519, first.Body);
        using (bobSession)
        {
            // Assert first message and session ID
            Assert.Equal(0, first.Type);
            Assert.Equal("hello bob", Encoding.UTF8.GetString(plaintext));
            Assert.Equal(aliceSession.SessionId, bobSession.SessionId);
            Assert.Equal(aliceSession.SessionId, OlmSession.PreKeySessionId(first.Body));
            Assert.Empty(bob.OneTimeKeys);

            // Act and assert: conversation with ratchet steps both ways
            Converse(aliceSession, bobSession);
        }
    }

    /// <summary>A native device starts a session with a managed one; the managed side creates the inbound session, uses up the key, and the conversation continues.</summary>
    [Fact]
    public void CanTalkOlmNativeToManaged()
    {
        // Arrange: managed bob publishes a one-time key
        RequireNative();
        using NativeOlmAccount alice = NativeOlmAccount.Create();
        using OlmAccount bob = OlmAccount.Create();
        bob.GenerateOneTimeKeys(1);
        string oneTimeKey = bob.OneTimeKeys.Values.Single();

        // Act
        using NativeOlmSession aliceSession = alice.CreateOutboundSession(bob.IdentityKeys.Curve25519, oneTimeKey);
        OlmMessage first = aliceSession.Encrypt("hello managed bob");
        (OlmSession bobSession, byte[] plaintext) = bob.CreateInboundSession(alice.IdentityKeys.Curve25519, first.Body);
        using (bobSession)
        {
            // Assert first message; the key is used up only now
            Assert.Equal("hello managed bob", Encoding.UTF8.GetString(plaintext));
            Assert.Equal(aliceSession.SessionId, bobSession.SessionId);
            Assert.Throws<OlmException>(() => bob.CreateInboundSession(alice.IdentityKeys.Curve25519, first.Body));

            // Act and assert: conversation
            Converse(bobSession, aliceSession);
        }
    }

    /// <summary>A session started with a fallback key works across implementations, and the fallback key stays usable.</summary>
    [Fact]
    public void CanUseFallbackKeyAcross()
    {
        // Arrange: managed bob has only a fallback key
        RequireNative();
        using NativeOlmAccount alice = NativeOlmAccount.Create();
        using OlmAccount bob = OlmAccount.Create();
        bob.GenerateFallbackKey();
        string fallback = bob.FallbackKey.Values.Single();

        // Act: two sessions with the same fallback key
        using NativeOlmSession first = alice.CreateOutboundSession(bob.IdentityKeys.Curve25519, fallback);
        using NativeOlmSession second = alice.CreateOutboundSession(bob.IdentityKeys.Curve25519, fallback);
        (OlmSession one, byte[] p1) = bob.CreateInboundSession(alice.IdentityKeys.Curve25519, first.Encrypt("one").Body);
        (OlmSession two, byte[] p2) = bob.CreateInboundSession(alice.IdentityKeys.Curve25519, second.Encrypt("two").Body);

        // Assert
        using (one) using (two)
        {
            Assert.Equal("one", Encoding.UTF8.GetString(p1));
            Assert.Equal("two", Encoding.UTF8.GetString(p2));
            Assert.NotEqual(one.SessionId, two.SessionId);
        }
    }

    /// <summary>Olm sessions move between implementations through pickles mid-conversation, on both sides and in both directions.</summary>
    [Fact]
    public void CanContinueOlmSessionsAcrossPickles()
    {
        // Arrange: native pair talking
        RequireNative();
        using NativeOlmAccount aliceAccount = NativeOlmAccount.Create();
        using NativeOlmAccount bobAccount = NativeOlmAccount.Create();
        bobAccount.GenerateOneTimeKeys(1);
        NativeOlmSession alice = aliceAccount.CreateOutboundSession(bobAccount.IdentityKeys.Curve25519, bobAccount.OneTimeKeys.Values.Single());
        (NativeOlmSession bob, _) = bobAccount.CreateInboundSession(aliceAccount.IdentityKeys.Curve25519, alice.Encrypt("start").Body);
        Assert.Equal("reply", Encoding.UTF8.GetString(alice.Decrypt(bob.Encrypt("reply"))));

        // Act: alice moves to managed, keeps talking, then back to native; bob moves to managed
        using OlmSession managedAlice = OlmSession.FromPickle(alice.Pickle(PickleKey), PickleKey);
        alice.Dispose();
        Converse(managedAlice, bob);
        using NativeOlmSession nativeAlice = NativeOlmSession.FromPickle(managedAlice.Pickle(PickleKey), PickleKey);
        using OlmSession managedBob = OlmSession.FromPickle(bob.Pickle(PickleKey), PickleKey);
        bob.Dispose();

        // Assert
        Assert.Equal(nativeAlice.SessionId, managedBob.SessionId);
        Converse(managedBob, nativeAlice);
    }

    /// <summary>Megolm messages decrypt across implementations in both directions, including skipped and earlier indices.</summary>
    [Fact]
    public void CanShareMegolmAcross()
    {
        // Arrange
        RequireNative();
        using MegolmOutboundSession managedOut = MegolmOutboundSession.Create();
        using NativeMegolmOutboundSession nativeOut = NativeMegolmOutboundSession.Create();
        using NativeMegolmInboundSession nativeIn = NativeMegolmInboundSession.FromSessionKey(managedOut.SessionKey);
        using MegolmInboundSession managedIn = MegolmInboundSession.FromSessionKey(nativeOut.SessionKey);

        // Act: 300 messages each way, crossing a ratchet part boundary
        List<string> fromManaged = [.. Enumerable.Range(0, 300).Select(i => managedOut.Encrypt($"m{i}"))];
        List<string> fromNative = [.. Enumerable.Range(0, 300).Select(i => nativeOut.Encrypt($"n{i}"))];

        // Assert in mixed order
        foreach (int i in new[] { 299, 0, 255, 256, 17, 298 })
        {
            Assert.Equal($"m{i}", nativeIn.Decrypt(fromManaged[i]).Text);
            Assert.Equal((uint)i, nativeIn.Decrypt(fromManaged[i]).MessageIndex);
            Assert.Equal($"n{i}", managedIn.Decrypt(fromNative[i]).Text);
        }
        Assert.Equal(managedOut.SessionId, nativeIn.SessionId);
        Assert.Equal(nativeOut.SessionId, managedIn.SessionId);
        Assert.Equal(300u, managedOut.MessageIndex);
    }

    /// <summary>Export at large indices are byte-identical, so ratchet advancing matches across every part boundary.</summary>
    [Theory]
    [InlineData(1u)]
    [InlineData(255u)]
    [InlineData(256u)]
    [InlineData(65535u)]
    [InlineData(65536u)]
    [InlineData(16777215u)]
    [InlineData(16777216u)]
    [InlineData(4294967295u)]
    public void CanAdvanceMegolmLikeNative(uint index)
    {
        // Arrange: one session key in both
        RequireNative();
        using NativeMegolmOutboundSession outbound = NativeMegolmOutboundSession.Create();
        using NativeMegolmInboundSession native = NativeMegolmInboundSession.FromSessionKey(outbound.SessionKey);
        using MegolmInboundSession managed = MegolmInboundSession.FromSessionKey(outbound.SessionKey);

        // Act and assert: same key bytes at the index, and an import continues there
        string exported = managed.ExportAt(index);
        Assert.Equal(native.ExportAt(index), exported);
        using NativeMegolmInboundSession imported = NativeMegolmInboundSession.Import(exported);
        Assert.Equal(index, imported.FirstKnownIndex);
    }

    /// <summary>Megolm sessions move between implementations through pickles, and imported keys decrypt across.</summary>
    [Fact]
    public void CanShareMegolmPicklesAndExports()
    {
        // Arrange: native outbound with some history
        RequireNative();
        using NativeMegolmOutboundSession native = NativeMegolmOutboundSession.Create();
        string early = native.Encrypt("early");
        using NativeMegolmInboundSession nativeIn = NativeMegolmInboundSession.FromSessionKey(native.SessionKey);
        native.Encrypt("skipped");

        // Act: outbound to managed and back; inbound pickles both ways; export from managed at index 1
        using MegolmOutboundSession managed = MegolmOutboundSession.FromPickle(native.Pickle(PickleKey), PickleKey);
        string late = managed.Encrypt("late");
        using NativeMegolmOutboundSession back = NativeMegolmOutboundSession.FromPickle(managed.Pickle(PickleKey), PickleKey);
        using MegolmInboundSession managedIn = MegolmInboundSession.FromPickle(nativeIn.Pickle(PickleKey), PickleKey);
        using NativeMegolmInboundSession nativeImported = NativeMegolmInboundSession.Import(managedIn.ExportAt(2));
        using MegolmInboundSession managedImported = MegolmInboundSession.Import(nativeIn.ExportAt(1));

        // Assert
        Assert.Equal(3u, back.MessageIndex);
        Assert.Equal(back.SessionKey, managed.SessionKey);
        Assert.Equal("late", managedIn.Decrypt(late).Text);
        Assert.Equal("late", nativeImported.Decrypt(late).Text);
        Assert.Throws<OlmException>(() => managedImported.Decrypt(early));
        Assert.Equal(1u, managedImported.FirstKnownIndex);
        using NativeMegolmInboundSession pickledBack = NativeMegolmInboundSession.FromPickle(managedImported.Pickle(PickleKey), PickleKey);
        Assert.Equal("late", pickledBack.Decrypt(late).Text);
    }

    /// <summary>Backup encryption works across implementations in both directions, and public keys match.</summary>
    [Fact]
    public void CanShareBackupEncryption()
    {
        // Arrange
        RequireNative();
        string secret = Base64.EncodeUnpadded(RandomNumberGenerator.GetBytes(32));
        string publicKey = PkEncryption.GetPublicKey(secret);

        // Act
        PkMessage fromManaged = PkEncryption.Encrypt(publicKey, """{"session_key":"managed"}""");
        PkMessage fromNative = NativePkEncryption.Encrypt(publicKey, """{"session_key":"native"}""");

        // Assert
        Assert.Equal(NativePkEncryption.GetPublicKey(secret), publicKey);
        Assert.Equal("""{"session_key":"managed"}""", NativePkEncryption.Decrypt(secret, fromManaged));
        Assert.Equal("""{"session_key":"native"}""", PkEncryption.Decrypt(secret, fromNative));
        Assert.Throws<OlmException>(() => PkEncryption.Decrypt(Base64.EncodeUnpadded(RandomNumberGenerator.GetBytes(32)), fromNative));
    }

    /// <summary>A managed and a native SAS derive the same bytes and accepts each other's MACs.</summary>
    [Fact]
    public void CanAgreeOnSasAcross()
    {
        // Arrange
        RequireNative();
        using Sas managed = Sas.Create();
        using NativeSas native = NativeSas.Create();
        string managedKey = managed.PublicKey, nativeKey = native.PublicKey;

        // Act
        using EstablishedSas managedSas = managed.Establish(nativeKey);
        using NativeEstablishedSas nativeSas = native.Establish(managedKey);

        // Assert
        Assert.Equal(nativeSas.GetBytes("MATRIX_KEY_VERIFICATION_SAS|info"), managedSas.GetBytes("MATRIX_KEY_VERIFICATION_SAS|info"));
        string mac = managedSas.CalculateMac("ed25519:KEY", "MAC_INFO");
        Assert.Equal(nativeSas.CalculateMac("ed25519:KEY", "MAC_INFO"), mac);
        Assert.True(nativeSas.VerifyMac("ed25519:KEY", "MAC_INFO", mac));
        Assert.True(managedSas.VerifyMac("ed25519:KEY", "MAC_INFO", nativeSas.CalculateMac("ed25519:KEY", "MAC_INFO")));
        Assert.False(managedSas.VerifyMac("ed25519:OTHER", "MAC_INFO", mac));
    }

    /// <summary>Standalone Ed25519 keys give identical public keys and signatures, as Ed25519 is deterministic.</summary>
    [Fact]
    public void CanSignLikeNative()
    {
        // Arrange
        RequireNative();
        (string secret, string publicKey) = Ed25519.GenerateKey();

        // Act and assert
        Assert.Equal(NativeEd25519.GetPublicKey(secret), publicKey);
        Assert.Equal(NativeEd25519.Sign(secret, """{"a":1}"""), Ed25519.Sign(secret, """{"a":1}"""));
        Assert.True(Ed25519.Verify(publicKey, "x", NativeEd25519.Sign(secret, "x")));
        Assert.False(Ed25519.Verify(publicKey, "y", NativeEd25519.Sign(secret, "x")));
    }

    /// <summary>Exchange messages in both directions, with out-of-order delivery and ratchet steps, checking each plaintext.</summary>
    private static void Converse(OlmSession managed, NativeOlmSession native)
    {
        // Managed sends a burst, delivered out of order
        OlmMessage[] burst = [.. Enumerable.Range(0, 5).Select(i => managed.Encrypt($"to native {i}"))];
        foreach (int i in new[] { 2, 0, 4, 1, 3 }) Assert.Equal($"to native {i}", Encoding.UTF8.GetString(native.Decrypt(burst[i])));

        // Several turns, each a ratchet step
        for (int turn = 0; turn < 4; turn++)
        {
            Assert.Equal($"n{turn}", Encoding.UTF8.GetString(managed.Decrypt(native.Encrypt($"n{turn}"))));
            Assert.Equal($"m{turn}", Encoding.UTF8.GetString(native.Decrypt(managed.Encrypt($"m{turn}"))));
        }

        // Native burst with one message lost for good, and a replay refused
        OlmMessage[] reply = [.. Enumerable.Range(0, 4).Select(i => native.Encrypt($"to managed {i}"))];
        Assert.Equal("to managed 3", Encoding.UTF8.GetString(managed.Decrypt(reply[3])));
        Assert.Equal("to managed 0", Encoding.UTF8.GetString(managed.Decrypt(reply[0])));
        Assert.Throws<OlmException>(() => managed.Decrypt(reply[0]));
        Assert.Equal("to managed 2", Encoding.UTF8.GetString(managed.Decrypt(reply[2])));
    }
}
