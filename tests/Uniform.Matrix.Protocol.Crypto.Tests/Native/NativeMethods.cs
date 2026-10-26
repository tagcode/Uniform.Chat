namespace Uniform.Matrix.Protocol.Crypto.Tests.Native;

using System.Reflection;
using System.Runtime.InteropServices;

/// <summary>P/Invoke declarations of the vodozemac-ffi C interface; see <c>native/vodozemac-ffi/src/lib.rs</c> for conventions.</summary>
internal static unsafe partial class NativeMethods
{
    /// <summary>Library name; resolved to <c>libvodozemac_ffi.so</c> or <c>vodozemac_ffi.dll</c>.</summary>
    private const string Library = "vodozemac_ffi";

    /// <summary>Register <see cref="Resolve"/>; run before the first native call, as all of them are members of this class.</summary>
    static NativeMethods()
    {
        // Register the resolver for this assembly
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
    }

    /// <summary>Load the library from <c>runtimes/&lt;os&gt;-&lt;arch&gt;/native/</c> next to the assembly, then falls back to the default search.</summary>
    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // Only our library
        if (name != Library) return IntPtr.Zero;

        // Portable runtime folder; RuntimeIdentifier is distro-specific on Linux
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string file = OperatingSystem.IsWindows() ? "vodozemac_ffi.dll" : OperatingSystem.IsMacOS() ? "libvodozemac_ffi.dylib" : "libvodozemac_ffi.so";
        foreach (string directory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(assembly.Location) ?? "" })
        {
            string path = Path.Combine(directory, "runtimes", $"{os}-{arch}", "native", file);
            if (File.Exists(path)) return NativeLibrary.Load(path);
        }
        // Return default search result
        return NativeLibrary.TryLoad(name, assembly, searchPath, out IntPtr handle) ? handle : IntPtr.Zero;
    }

    /// <summary>Returns and clears the last error of this thread, or null.</summary>
    [LibraryImport(Library)] internal static partial IntPtr vz_last_error();
    /// <summary>Frees a returned string.</summary>
    [LibraryImport(Library)] internal static partial void vz_string_free(IntPtr value);
    /// <summary>Frees a returned byte buffer.</summary>
    [LibraryImport(Library)] internal static partial void vz_bytes_free(IntPtr value, nuint length);

    /// <summary>Creates an account.</summary>
    [LibraryImport(Library)] internal static partial int vz_account_new(out AccountHandle account);
    /// <summary>Frees an account.</summary>
    [LibraryImport(Library)] internal static partial void vz_account_free(IntPtr account);
    /// <summary>Restores an account from a pickle.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_account_from_pickle(string pickle, byte* key, out AccountHandle account);
    /// <summary>Pickles an account.</summary>
    [LibraryImport(Library)] internal static partial int vz_account_pickle(AccountHandle account, byte* key, out IntPtr pickle);
    /// <summary>Returns identity keys as JSON.</summary>
    [LibraryImport(Library)] internal static partial int vz_account_identity_keys(AccountHandle account, out IntPtr json);
    /// <summary>Signs a message.</summary>
    [LibraryImport(Library)] internal static partial int vz_account_sign(AccountHandle account, byte* message, nuint length, out IntPtr signature);
    /// <summary>Returns the one-time key capacity.</summary>
    [LibraryImport(Library)] internal static partial nuint vz_account_max_one_time_keys(AccountHandle account);
    /// <summary>Generates one-time keys.</summary>
    [LibraryImport(Library)] internal static partial int vz_account_generate_one_time_keys(AccountHandle account, nuint count);
    /// <summary>Returns unpublished one-time keys as JSON.</summary>
    [LibraryImport(Library)] internal static partial int vz_account_one_time_keys(AccountHandle account, out IntPtr json);
    /// <summary>Generates a fallback key.</summary>
    [LibraryImport(Library)] internal static partial int vz_account_generate_fallback_key(AccountHandle account);
    /// <summary>Returns the unpublished fallback key as JSON.</summary>
    [LibraryImport(Library)] internal static partial int vz_account_fallback_key(AccountHandle account, out IntPtr json);
    /// <summary>Marks keys as published.</summary>
    [LibraryImport(Library)] internal static partial int vz_account_mark_keys_as_published(AccountHandle account);
    /// <summary>Starts an outbound Olm session.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_account_create_outbound_session(AccountHandle account, string identityKey, string oneTimeKey, out SessionHandle session);
    /// <summary>Creates an inbound Olm session from a pre-key message.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_account_create_inbound_session(AccountHandle account, string identityKey, string ciphertext, out SessionHandle session, out IntPtr plaintext, out nuint length);

    /// <summary>Frees an Olm session.</summary>
    [LibraryImport(Library)] internal static partial void vz_session_free(IntPtr session);
    /// <summary>Returns the session ID.</summary>
    [LibraryImport(Library)] internal static partial int vz_session_id(SessionHandle session, out IntPtr id);
    /// <summary>Returns the session ID a pre-key message belongs to.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_prekey_session_id(string ciphertext, out IntPtr id);
    /// <summary>Encrypts with an Olm session.</summary>
    [LibraryImport(Library)] internal static partial int vz_session_encrypt(SessionHandle session, byte* plaintext, nuint length, out uint type, out IntPtr ciphertext);
    /// <summary>Decrypts with an Olm session.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_session_decrypt(SessionHandle session, uint type, string ciphertext, out IntPtr plaintext, out nuint length);
    /// <summary>Pickles an Olm session.</summary>
    [LibraryImport(Library)] internal static partial int vz_session_pickle(SessionHandle session, byte* key, out IntPtr pickle);
    /// <summary>Restores an Olm session.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_session_from_pickle(string pickle, byte* key, out SessionHandle session);

    /// <summary>Creates an outbound Megolm session.</summary>
    [LibraryImport(Library)] internal static partial int vz_group_session_new(out GroupSessionHandle session);
    /// <summary>Frees an outbound Megolm session.</summary>
    [LibraryImport(Library)] internal static partial void vz_group_session_free(IntPtr session);
    /// <summary>Returns the session ID.</summary>
    [LibraryImport(Library)] internal static partial int vz_group_session_id(GroupSessionHandle session, out IntPtr id);
    /// <summary>Returns the next message index.</summary>
    [LibraryImport(Library)] internal static partial uint vz_group_session_message_index(GroupSessionHandle session);
    /// <summary>Returns the session key to share.</summary>
    [LibraryImport(Library)] internal static partial int vz_group_session_key(GroupSessionHandle session, out IntPtr key);
    /// <summary>Encrypts a room message.</summary>
    [LibraryImport(Library)] internal static partial int vz_group_session_encrypt(GroupSessionHandle session, byte* plaintext, nuint length, out IntPtr ciphertext);
    /// <summary>Pickles an outbound Megolm session.</summary>
    [LibraryImport(Library)] internal static partial int vz_group_session_pickle(GroupSessionHandle session, byte* key, out IntPtr pickle);
    /// <summary>Restores an outbound Megolm session.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_group_session_from_pickle(string pickle, byte* key, out GroupSessionHandle session);

    /// <summary>Creates an inbound Megolm session from a session key.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_inbound_group_session_new(string sessionKey, out InboundGroupSessionHandle session);
    /// <summary>Creates an inbound Megolm session from an exported key.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_inbound_group_session_import(string exportedKey, out InboundGroupSessionHandle session);
    /// <summary>Frees an inbound Megolm session.</summary>
    [LibraryImport(Library)] internal static partial void vz_inbound_group_session_free(IntPtr session);
    /// <summary>Returns the session ID.</summary>
    [LibraryImport(Library)] internal static partial int vz_inbound_group_session_id(InboundGroupSessionHandle session, out IntPtr id);
    /// <summary>Returns the first decryptable index.</summary>
    [LibraryImport(Library)] internal static partial uint vz_inbound_group_session_first_known_index(InboundGroupSessionHandle session);
    /// <summary>Decrypts a room message.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_inbound_group_session_decrypt(InboundGroupSessionHandle session, string ciphertext, out IntPtr plaintext, out nuint length, out uint index);
    /// <summary>Exports the session from an index.</summary>
    [LibraryImport(Library)] internal static partial int vz_inbound_group_session_export_at(InboundGroupSessionHandle session, uint index, out IntPtr key);
    /// <summary>Pickles an inbound Megolm session.</summary>
    [LibraryImport(Library)] internal static partial int vz_inbound_group_session_pickle(InboundGroupSessionHandle session, byte* key, out IntPtr pickle);
    /// <summary>Restores an inbound Megolm session.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_inbound_group_session_from_pickle(string pickle, byte* key, out InboundGroupSessionHandle session);

    /// <summary>Verifies an Ed25519 signature.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_ed25519_verify(string publicKey, byte* message, nuint length, string signature);

    /// <summary>Creates an Ed25519 key.</summary>
    [LibraryImport(Library)] internal static partial int vz_ed25519_key_new(out IntPtr secret, out IntPtr publicKey);
    /// <summary>Returns the public key of an Ed25519 seed.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_ed25519_public_key(string secret, out IntPtr publicKey);
    /// <summary>Signs with an Ed25519 seed.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_ed25519_sign(string secret, byte* message, nuint length, out IntPtr signature);

    /// <summary>Returns the public key of a backup secret key.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_pk_public_key(string secret, out IntPtr publicKey);
    /// <summary>Encrypts for a backup public key.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_pk_encrypt(string publicKey, byte* plaintext, nuint length, out IntPtr ciphertext, out IntPtr mac, out IntPtr ephemeral);
    /// <summary>Decrypts a backup message.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_pk_decrypt(string secret, string ciphertext, string mac, string ephemeral, out IntPtr plaintext, out nuint length);

    /// <summary>Starts a SAS.</summary>
    [LibraryImport(Library)] internal static partial int vz_sas_new(out SasHandle sas);
    /// <summary>Frees a SAS.</summary>
    [LibraryImport(Library)] internal static partial void vz_sas_free(IntPtr sas);
    /// <summary>Returns our SAS public key.</summary>
    [LibraryImport(Library)] internal static partial int vz_sas_public_key(SasHandle sas, out IntPtr publicKey);
    /// <summary>Establishes a SAS, consuming it.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_sas_establish(SasHandle sas, string theirKey, out EstablishedSasHandle established);
    /// <summary>Frees an established SAS.</summary>
    [LibraryImport(Library)] internal static partial void vz_established_sas_free(IntPtr sas);
    /// <summary>Returns SAS bytes.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_established_sas_bytes(EstablishedSasHandle sas, string info, out IntPtr bytes, out nuint length);
    /// <summary>Calculates a SAS MAC.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_established_sas_calculate_mac(EstablishedSasHandle sas, string input, string info, out IntPtr mac);
    /// <summary>Verifies a SAS MAC.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int vz_established_sas_verify_mac(EstablishedSasHandle sas, string input, string info, string mac);

    /// <summary>Throw <see cref="NativeException"/> with the native message when <paramref name="status"/> is not 0.</summary>
    internal static void Check(int status)
    {
        // Success
        if (status == 0) return;
        // Throw with the native message
        throw new NativeException(TakeString(vz_last_error()) ?? $"vodozemac error {status}");
    }

    /// <summary>Copy and free a returned string; null for a null pointer.</summary>
    internal static string? TakeString(IntPtr value)
    {
        // Null stays null
        if (value == IntPtr.Zero) return null;
        // Copy, then free
        try
        {
            return Marshal.PtrToStringUTF8(value);
        }
        finally
        {
            vz_string_free(value);
        }
    }

    /// <summary>Copy and free a returned byte buffer.</summary>
    internal static byte[] TakeBytes(IntPtr value, nuint length)
    {
        // Copy, then free
        try
        {
            return value == IntPtr.Zero ? [] : new ReadOnlySpan<byte>((void*)value, checked((int)length)).ToArray();
        }
        finally
        {
            vz_bytes_free(value, length);
        }
    }
}
