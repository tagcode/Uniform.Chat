namespace Uniform.Matrix.Protocol.Crypto.Tests.Native;

using System.Text;
using System.Text.Json;
using Uniform.Matrix.Protocol.Crypto;

// Native vodozemac through vodozemac-ffi, kept as the oracle that the managed implementation is tested against.

/// <summary>vodozemac reported an error, e.g. a message that does not decrypt or a wrong pickle key.</summary>
public sealed class NativeException(string message) : Exception(message);

/// <summary>Helpers shared by the native wrappers.</summary>
internal static class NativePickling
{
    /// <summary>Length of pickle keys in bytes.</summary>
    public const int KeyLength = 32;

    /// <summary>Throw when <paramref name="key"/> is not 32 bytes.</summary>
    public static void AssertKey(ReadOnlySpan<byte> key)
    {
        // Assert length
        if (key.Length != KeyLength) throw new ArgumentException($"Pickle keys are {KeyLength} bytes.", nameof(key));
    }

    /// <summary>Parse a <c>{"id": "key"}</c> JSON object returned by the native library.</summary>
    public static IReadOnlyDictionary<string, string> ParseKeyMap(string? json) => json is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
}

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#end-to-end-encryption">Olm account</see> of a device: identity keys, one-time keys, fallback key and session creation.</summary>
/// <example><code><![CDATA[
/// using NativeOlmAccount account = NativeOlmAccount.Create();
/// account.GenerateOneTimeKeys(50);
/// IReadOnlyDictionary<string, string> keys = account.OneTimeKeys; // upload, then
/// account.MarkKeysAsPublished();
/// ]]></code></example>
public sealed unsafe class NativeOlmAccount : IDisposable
{
    private readonly AccountHandle handle;

    /// <summary>Wrap a native account.</summary>
    private NativeOlmAccount(AccountHandle handle)
    {
        // Own the handle
        this.handle = handle;
    }

    /// <summary>Create an account with fresh identity keys.</summary>
    public static NativeOlmAccount Create()
    {
        // Create natively
        NativeMethods.Check(NativeMethods.vz_account_new(out AccountHandle handle));
        // Return wrapper
        return new NativeOlmAccount(handle);
    }

    /// <summary>Restore an account from <paramref name="pickle"/> made with the same 32-byte <paramref name="key"/>.</summary>
    public static NativeOlmAccount FromPickle(string pickle, ReadOnlySpan<byte> key)
    {
        // Decrypt the pickle
        NativePickling.AssertKey(key);
        AccountHandle handle;
        fixed (byte* keyPointer = key) NativeMethods.Check(NativeMethods.vz_account_from_pickle(pickle, keyPointer, out handle));
        // Return wrapper
        return new NativeOlmAccount(handle);
    }

    /// <summary>Public identity keys.</summary>
    public IdentityKeys IdentityKeys
    {
        get
        {
            // Read JSON
            NativeMethods.Check(NativeMethods.vz_account_identity_keys(handle, out IntPtr json));
            using JsonDocument document = JsonDocument.Parse(NativeMethods.TakeString(json)!);
            // Return keys
            return new IdentityKeys(document.RootElement.GetProperty("curve25519").GetString()!, document.RootElement.GetProperty("ed25519").GetString()!);
        }
    }

    /// <summary>Largest number of one-time keys the account keeps.</summary>
    public int MaxOneTimeKeys => (int)NativeMethods.vz_account_max_one_time_keys(handle);

    /// <summary>Unpublished one-time keys by key ID.</summary>
    public IReadOnlyDictionary<string, string> OneTimeKeys
    {
        get
        {
            // Read JSON
            NativeMethods.Check(NativeMethods.vz_account_one_time_keys(handle, out IntPtr json));
            // Return keys
            return NativePickling.ParseKeyMap(NativeMethods.TakeString(json));
        }
    }

    /// <summary>Unpublished fallback key by key ID; empty when published or none.</summary>
    public IReadOnlyDictionary<string, string> FallbackKey
    {
        get
        {
            // Read JSON
            NativeMethods.Check(NativeMethods.vz_account_fallback_key(handle, out IntPtr json));
            // Return key
            return NativePickling.ParseKeyMap(NativeMethods.TakeString(json));
        }
    }

    /// <summary>Sign <paramref name="message"/>, e.g. canonical JSON, with the NativeEd25519 key; return the signature in base64.</summary>
    public string Sign(string message)
    {
        // Sign UTF-8 bytes
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        IntPtr signature;
        fixed (byte* pointer = bytes) NativeMethods.Check(NativeMethods.vz_account_sign(handle, pointer, (nuint)bytes.Length, out signature));
        // Return signature
        return NativeMethods.TakeString(signature)!;
    }

    /// <summary>Generate <paramref name="count"/> one-time keys, shown in <see cref="OneTimeKeys"/> until published.</summary>
    public void GenerateOneTimeKeys(int count) => NativeMethods.Check(NativeMethods.vz_account_generate_one_time_keys(handle, (nuint)count));

    /// <summary>Generate a fallback key, used when one-time keys run out.</summary>
    public void GenerateFallbackKey() => NativeMethods.Check(NativeMethods.vz_account_generate_fallback_key(handle));

    /// <summary>Mark <see cref="OneTimeKeys"/> and <see cref="FallbackKey"/> as uploaded.</summary>
    public void MarkKeysAsPublished() => NativeMethods.Check(NativeMethods.vz_account_mark_keys_as_published(handle));

    /// <summary>Start an Olm session to a device from its Curve25519 identity key and a claimed one-time key.</summary>
    public NativeOlmSession CreateOutboundSession(string identityKey, string oneTimeKey)
    {
        // Create natively
        NativeMethods.Check(NativeMethods.vz_account_create_outbound_session(handle, identityKey, oneTimeKey, out SessionHandle session));
        // Return session
        return new NativeOlmSession(session);
    }

    /// <summary>Create a session from a received pre-key message and return it with the decrypted plaintext; use up the one-time key.</summary>
    public (NativeOlmSession Session, byte[] Plaintext) CreateInboundSession(string identityKey, string preKeyCiphertext)
    {
        // Create natively
        NativeMethods.Check(NativeMethods.vz_account_create_inbound_session(handle, identityKey, preKeyCiphertext, out SessionHandle session, out IntPtr plaintext, out nuint length));
        // Return session and plaintext
        return (new NativeOlmSession(session), NativeMethods.TakeBytes(plaintext, length));
    }

    /// <summary>Encrypt the account's state with the 32-byte <paramref name="key"/> for storage.</summary>
    public string Pickle(ReadOnlySpan<byte> key)
    {
        // Pickle natively
        NativePickling.AssertKey(key);
        IntPtr pickle;
        fixed (byte* keyPointer = key) NativeMethods.Check(NativeMethods.vz_account_pickle(handle, keyPointer, out pickle));
        // Return pickle
        return NativeMethods.TakeString(pickle)!;
    }

    /// <summary>Free the native account.</summary>
    public void Dispose() => handle.Dispose();
}

/// <summary><see href="https://spec.matrix.org/latest/client-server-api/#molmv1curve25519-aes-sha2">Olm session</see> with one other device, carrying to-device messages such as room keys.</summary>
public sealed unsafe class NativeOlmSession : IDisposable
{
    private readonly SessionHandle handle;

    /// <summary>Wrap a native session.</summary>
    internal NativeOlmSession(SessionHandle handle)
    {
        // Own the handle
        this.handle = handle;
    }

    /// <summary>Restore a session from <paramref name="pickle"/> made with the same 32-byte <paramref name="key"/>.</summary>
    public static NativeOlmSession FromPickle(string pickle, ReadOnlySpan<byte> key)
    {
        // Decrypt the pickle
        NativePickling.AssertKey(key);
        SessionHandle handle;
        fixed (byte* keyPointer = key) NativeMethods.Check(NativeMethods.vz_session_from_pickle(pickle, keyPointer, out handle));
        // Return wrapper
        return new NativeOlmSession(handle);
    }

    /// <summary>Return the ID of the session a pre-key message belongs to, to find an existing session before creating one.</summary>
    public static string PreKeySessionId(string preKeyCiphertext)
    {
        // Parse natively
        NativeMethods.Check(NativeMethods.vz_prekey_session_id(preKeyCiphertext, out IntPtr id));
        // Return ID
        return NativeMethods.TakeString(id)!;
    }

    /// <summary>Session ID.</summary>
    public string SessionId
    {
        get
        {
            // Read natively
            NativeMethods.Check(NativeMethods.vz_session_id(handle, out IntPtr id));
            // Return ID
            return NativeMethods.TakeString(id)!;
        }
    }

    /// <summary>Encrypt UTF-8 <paramref name="plaintext"/>, usually event JSON.</summary>
    public OlmMessage Encrypt(string plaintext) => Encrypt(Encoding.UTF8.GetBytes(plaintext));

    /// <summary>Encrypt <paramref name="plaintext"/>.</summary>
    public OlmMessage Encrypt(byte[] plaintext)
    {
        // Encrypt natively
        uint type;
        IntPtr ciphertext;
        fixed (byte* pointer = plaintext) NativeMethods.Check(NativeMethods.vz_session_encrypt(handle, pointer, (nuint)plaintext.Length, out type, out ciphertext));
        // Return message
        return new OlmMessage((int)type, NativeMethods.TakeString(ciphertext)!);
    }

    /// <summary>Decrypt <paramref name="message"/>; throw <see cref="NativeException"/> when it is not for this session.</summary>
    public byte[] Decrypt(OlmMessage message)
    {
        // Decrypt natively
        NativeMethods.Check(NativeMethods.vz_session_decrypt(handle, (uint)message.Type, message.Body, out IntPtr plaintext, out nuint length));
        // Return plaintext
        return NativeMethods.TakeBytes(plaintext, length);
    }

    /// <summary>Encrypt the session's state with the 32-byte <paramref name="key"/> for storage.</summary>
    public string Pickle(ReadOnlySpan<byte> key)
    {
        // Pickle natively
        NativePickling.AssertKey(key);
        IntPtr pickle;
        fixed (byte* keyPointer = key) NativeMethods.Check(NativeMethods.vz_session_pickle(handle, keyPointer, out pickle));
        // Return pickle
        return NativeMethods.TakeString(pickle)!;
    }

    /// <summary>Free the native session.</summary>
    public void Dispose() => handle.Dispose();
}

/// <summary>Outbound <see href="https://spec.matrix.org/latest/client-server-api/#mmegolmv1aes-sha2">Megolm session</see> for sending to a room; its key is shared with each recipient device over Olm.</summary>
public sealed unsafe class NativeMegolmOutboundSession : IDisposable
{
    private readonly GroupSessionHandle handle;

    /// <summary>Wrap a native session.</summary>
    private NativeMegolmOutboundSession(GroupSessionHandle handle)
    {
        // Own the handle
        this.handle = handle;
    }

    /// <summary>Create a new session.</summary>
    public static NativeMegolmOutboundSession Create()
    {
        // Create natively
        NativeMethods.Check(NativeMethods.vz_group_session_new(out GroupSessionHandle handle));
        // Return wrapper
        return new NativeMegolmOutboundSession(handle);
    }

    /// <summary>Restore a session from <paramref name="pickle"/> made with the same 32-byte <paramref name="key"/>.</summary>
    public static NativeMegolmOutboundSession FromPickle(string pickle, ReadOnlySpan<byte> key)
    {
        // Decrypt the pickle
        NativePickling.AssertKey(key);
        GroupSessionHandle handle;
        fixed (byte* keyPointer = key) NativeMethods.Check(NativeMethods.vz_group_session_from_pickle(pickle, keyPointer, out handle));
        // Return wrapper
        return new NativeMegolmOutboundSession(handle);
    }

    /// <summary>Session ID.</summary>
    public string SessionId
    {
        get
        {
            // Read natively
            NativeMethods.Check(NativeMethods.vz_group_session_id(handle, out IntPtr id));
            // Return ID
            return NativeMethods.TakeString(id)!;
        }
    }

    /// <summary>Index of the next message; counts messages for rotation.</summary>
    public uint MessageIndex => NativeMethods.vz_group_session_message_index(handle);

    /// <summary>Key to share in <c>m.room_key</c>, decrypting from <see cref="MessageIndex"/> on.</summary>
    public string SessionKey
    {
        get
        {
            // Read natively
            NativeMethods.Check(NativeMethods.vz_group_session_key(handle, out IntPtr key));
            // Return key
            return NativeMethods.TakeString(key)!;
        }
    }

    /// <summary>Encrypt UTF-8 <paramref name="plaintext"/>, the event JSON with room ID; return the ciphertext.</summary>
    public string Encrypt(string plaintext) => Encrypt(Encoding.UTF8.GetBytes(plaintext));

    /// <summary>Encrypt <paramref name="plaintext"/>; return the ciphertext.</summary>
    public string Encrypt(byte[] plaintext)
    {
        // Encrypt natively
        IntPtr ciphertext;
        fixed (byte* pointer = plaintext) NativeMethods.Check(NativeMethods.vz_group_session_encrypt(handle, pointer, (nuint)plaintext.Length, out ciphertext));
        // Return ciphertext
        return NativeMethods.TakeString(ciphertext)!;
    }

    /// <summary>Encrypt the session's state with the 32-byte <paramref name="key"/> for storage.</summary>
    public string Pickle(ReadOnlySpan<byte> key)
    {
        // Pickle natively
        NativePickling.AssertKey(key);
        IntPtr pickle;
        fixed (byte* keyPointer = key) NativeMethods.Check(NativeMethods.vz_group_session_pickle(handle, keyPointer, out pickle));
        // Return pickle
        return NativeMethods.TakeString(pickle)!;
    }

    /// <summary>Free the native session.</summary>
    public void Dispose() => handle.Dispose();
}

/// <summary>Inbound <see href="https://spec.matrix.org/latest/client-server-api/#mmegolmv1aes-sha2">Megolm session</see> for decrypting one sender device's room messages.</summary>
public sealed unsafe class NativeMegolmInboundSession : IDisposable
{
    private readonly InboundGroupSessionHandle handle;

    /// <summary>Wrap a native session.</summary>
    private NativeMegolmInboundSession(InboundGroupSessionHandle handle)
    {
        // Own the handle
        this.handle = handle;
    }

    /// <summary>Create a session from a session key received in <c>m.room_key</c>; verified, as it is signed by the sender.</summary>
    public static NativeMegolmInboundSession FromSessionKey(string sessionKey)
    {
        // Create natively
        NativeMethods.Check(NativeMethods.vz_inbound_group_session_new(sessionKey, out InboundGroupSessionHandle handle));
        // Return wrapper
        return new NativeMegolmInboundSession(handle);
    }

    /// <summary>Create a session from an exported key, e.g. from <c>m.forwarded_room_key</c> or a key backup.</summary>
    public static NativeMegolmInboundSession Import(string exportedKey)
    {
        // Import natively
        NativeMethods.Check(NativeMethods.vz_inbound_group_session_import(exportedKey, out InboundGroupSessionHandle handle));
        // Return wrapper
        return new NativeMegolmInboundSession(handle);
    }

    /// <summary>Restore a session from <paramref name="pickle"/> made with the same 32-byte <paramref name="key"/>.</summary>
    public static NativeMegolmInboundSession FromPickle(string pickle, ReadOnlySpan<byte> key)
    {
        // Decrypt the pickle
        NativePickling.AssertKey(key);
        InboundGroupSessionHandle handle;
        fixed (byte* keyPointer = key) NativeMethods.Check(NativeMethods.vz_inbound_group_session_from_pickle(pickle, keyPointer, out handle));
        // Return wrapper
        return new NativeMegolmInboundSession(handle);
    }

    /// <summary>Session ID, matching <c>session_id</c> in encrypted events.</summary>
    public string SessionId
    {
        get
        {
            // Read natively
            NativeMethods.Check(NativeMethods.vz_inbound_group_session_id(handle, out IntPtr id));
            // Return ID
            return NativeMethods.TakeString(id)!;
        }
    }

    /// <summary>First message index this session can decrypt.</summary>
    public uint FirstKnownIndex => NativeMethods.vz_inbound_group_session_first_known_index(handle);

    /// <summary>Decrypt a room message ciphertext; throw <see cref="NativeException"/> when it cannot.</summary>
    public MegolmPlaintext Decrypt(string ciphertext)
    {
        // Decrypt natively
        NativeMethods.Check(NativeMethods.vz_inbound_group_session_decrypt(handle, ciphertext, out IntPtr plaintext, out nuint length, out uint index));
        // Return plaintext with index
        return new MegolmPlaintext(NativeMethods.TakeBytes(plaintext, length), index);
    }

    /// <summary>Export the session from <paramref name="index"/> on, e.g. to share or back it up.</summary>
    public string ExportAt(uint index)
    {
        // Export natively
        NativeMethods.Check(NativeMethods.vz_inbound_group_session_export_at(handle, index, out IntPtr key));
        // Return exported key
        return NativeMethods.TakeString(key)!;
    }

    /// <summary>Encrypt the session's state with the 32-byte <paramref name="key"/> for storage.</summary>
    public string Pickle(ReadOnlySpan<byte> key)
    {
        // Pickle natively
        NativePickling.AssertKey(key);
        IntPtr pickle;
        fixed (byte* keyPointer = key) NativeMethods.Check(NativeMethods.vz_inbound_group_session_pickle(handle, keyPointer, out pickle));
        // Return pickle
        return NativeMethods.TakeString(pickle)!;
    }

    /// <summary>Free the native session.</summary>
    public void Dispose() => handle.Dispose();
}

/// <summary>NativeEd25519 keys and signatures: verification of device keys and signed one-time keys, and standalone keys for cross-signing.</summary>
public static unsafe class NativeEd25519
{
    /// <summary>Create a random key; return the 32-byte secret seed and the public key, both base64.</summary>
    public static (string Secret, string PublicKey) GenerateKey()
    {
        // Generate natively
        NativeMethods.Check(NativeMethods.vz_ed25519_key_new(out IntPtr secret, out IntPtr publicKey));
        // Return key pair
        return (NativeMethods.TakeString(secret)!, NativeMethods.TakeString(publicKey)!);
    }

    /// <summary>Return the public key of a base64 secret seed.</summary>
    public static string GetPublicKey(string secret)
    {
        // Derive natively
        NativeMethods.Check(NativeMethods.vz_ed25519_public_key(secret, out IntPtr publicKey));
        // Return key
        return NativeMethods.TakeString(publicKey)!;
    }

    /// <summary>Sign UTF-8 <paramref name="message"/> with a base64 secret seed; return the signature in base64.</summary>
    public static string Sign(string secret, string message)
    {
        // Sign natively
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        IntPtr signature;
        fixed (byte* pointer = bytes) NativeMethods.Check(NativeMethods.vz_ed25519_sign(secret, pointer, (nuint)bytes.Length, out signature));
        // Return signature
        return NativeMethods.TakeString(signature)!;
    }

    /// <summary>Return whether <paramref name="signature"/> by <paramref name="publicKey"/> is valid for UTF-8 <paramref name="message"/>.</summary>
    public static bool Verify(string publicKey, string message, string signature)
    {
        // Verify natively; any failure is "not valid"
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        int status;
        fixed (byte* pointer = bytes) status = NativeMethods.vz_ed25519_verify(publicKey, pointer, (nuint)bytes.Length, signature);
        if (status != 0) NativeMethods.TakeString(NativeMethods.vz_last_error());
        // Return result
        return status == 0;
    }
}


/// <summary>Public-key encryption of <see href="https://spec.matrix.org/latest/client-server-api/#backup-algorithm-mmegolm_backupv1curve25519-aes-sha2">m.megolm_backup.v1.curve25519-aes-sha2</see> key backups.</summary>
public static unsafe class NativePkEncryption
{
    /// <summary>Return the public key of a base64 32-byte backup secret key.</summary>
    public static string GetPublicKey(string secret)
    {
        // Derive natively
        NativeMethods.Check(NativeMethods.vz_pk_public_key(secret, out IntPtr publicKey));
        // Return key
        return NativeMethods.TakeString(publicKey)!;
    }

    /// <summary>Encrypt UTF-8 <paramref name="plaintext"/> for <paramref name="publicKey"/>.</summary>
    public static PkMessage Encrypt(string publicKey, string plaintext)
    {
        // Encrypt natively
        byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
        IntPtr ciphertext, mac, ephemeral;
        fixed (byte* pointer = bytes) NativeMethods.Check(NativeMethods.vz_pk_encrypt(publicKey, pointer, (nuint)bytes.Length, out ciphertext, out mac, out ephemeral));
        // Return message
        return new PkMessage(NativeMethods.TakeString(ciphertext)!, NativeMethods.TakeString(mac)!, NativeMethods.TakeString(ephemeral)!);
    }

    /// <summary>Decrypt <paramref name="message"/> with a base64 backup secret key; return UTF-8 text.</summary>
    public static string Decrypt(string secret, PkMessage message)
    {
        // Decrypt natively
        NativeMethods.Check(NativeMethods.vz_pk_decrypt(secret, message.Ciphertext, message.Mac, message.Ephemeral, out IntPtr plaintext, out nuint length));
        // Return text
        return Encoding.UTF8.GetString(NativeMethods.TakeBytes(plaintext, length));
    }
}

/// <summary>Key agreement of <see href="https://spec.matrix.org/latest/client-server-api/#short-authentication-string-sas-verification">SAS verification</see>: send <see cref="PublicKey"/>, then <see cref="Establish"/> with theirs.</summary>
public sealed class NativeSas : IDisposable
{
    private readonly SasHandle handle;

    /// <summary>Wrap a native SAS.</summary>
    private NativeSas(SasHandle handle)
    {
        // Own the handle
        this.handle = handle;
    }

    /// <summary>Start a SAS with a fresh key pair.</summary>
    public static NativeSas Create()
    {
        // Create natively
        NativeMethods.Check(NativeMethods.vz_sas_new(out SasHandle handle));
        // Return wrapper
        return new NativeSas(handle);
    }

    /// <summary>Our ephemeral public key, base64.</summary>
    public string PublicKey
    {
        get
        {
            // Read natively
            NativeMethods.Check(NativeMethods.vz_sas_public_key(handle, out IntPtr key));
            // Return key
            return NativeMethods.TakeString(key)!;
        }
    }

    /// <summary>Complete the key exchange with <paramref name="theirKey"/>; this object cannot be used afterwards.</summary>
    public NativeEstablishedSas Establish(string theirKey)
    {
        // Establish natively; the native side consumes the SAS whatever the outcome
        int status = NativeMethods.vz_sas_establish(handle, theirKey, out EstablishedSasHandle established);
        handle.SetHandleAsInvalid();
        NativeMethods.Check(status);
        // Return established SAS
        return new NativeEstablishedSas(established);
    }

    /// <summary>Free the native SAS if not established.</summary>
    public void Dispose() => handle.Dispose();
}

/// <summary>Shared secret of a SAS verification: emoji or decimals to compare, and MACs over the keys being verified.</summary>
public sealed class NativeEstablishedSas : IDisposable
{
    private readonly EstablishedSasHandle handle;

    /// <summary>Wrap a native established SAS.</summary>
    internal NativeEstablishedSas(EstablishedSasHandle handle)
    {
        // Own the handle
        this.handle = handle;
    }

    /// <summary>Return the 6 bytes derived for <paramref name="info"/>, from which <see cref="EmojiIndices"/> and <see cref="Decimals"/> come.</summary>
    public byte[] GetBytes(string info)
    {
        // Derive natively
        NativeMethods.Check(NativeMethods.vz_established_sas_bytes(handle, info, out IntPtr bytes, out nuint length));
        // Return bytes
        return NativeMethods.TakeBytes(bytes, length);
    }

    /// <summary>Return the 7 emoji indices of <paramref name="bytes"/>: 6 bits each from the first 42 bits.</summary>
    public static int[] EmojiIndices(byte[] bytes)
    {
        // Read 42 bits as big-endian
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits = (bits << 8) | bytes[i];
        bits >>= 6;
        // Return indices from the top
        return [.. Enumerable.Range(0, 7).Select(i => (int)((bits >> (36 - i * 6)) & 0x3F))];
    }

    /// <summary>Return the 3 four-digit numbers of <paramref name="bytes"/>: 13 bits each from the first 39 bits, plus 1000.</summary>
    public static int[] Decimals(byte[] bytes)
    {
        // Spec formula on the first 5 bytes
        int first = ((bytes[0] << 5) | (bytes[1] >> 3)) + 1000;
        int second = (((bytes[1] & 0x7) << 10) | (bytes[2] << 2) | (bytes[3] >> 6)) + 1000;
        int third = (((bytes[3] & 0x3F) << 7) | (bytes[4] >> 1)) + 1000;
        // Return numbers
        return [first, second, third];
    }

    /// <summary>Calculate the MAC of <paramref name="input"/>, e.g. a key, with <paramref name="info"/>; base64.</summary>
    public string CalculateMac(string input, string info)
    {
        // Calculate natively
        NativeMethods.Check(NativeMethods.vz_established_sas_calculate_mac(handle, input, info, out IntPtr mac));
        // Return MAC
        return NativeMethods.TakeString(mac)!;
    }

    /// <summary>Return whether <paramref name="mac"/> is the MAC of <paramref name="input"/> with <paramref name="info"/>.</summary>
    public bool VerifyMac(string input, string info, string mac)
    {
        // Verify natively; any failure is "not valid"
        int status = NativeMethods.vz_established_sas_verify_mac(handle, input, info, mac);
        if (status != 0) NativeMethods.TakeString(NativeMethods.vz_last_error());
        // Return result
        return status == 0;
    }

    /// <summary>Free the native state.</summary>
    public void Dispose() => handle.Dispose();
}
