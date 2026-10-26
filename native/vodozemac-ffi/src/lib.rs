//! C interface to vodozemac for Matrix.Client.Crypto.
//!
//! Conventions:
//! - Functions return 0 on success, -1 on error and -2 on a caught panic; `vz_last_error` returns the message.
//! - Objects are opaque pointers created by `*_new`/`*_from_pickle` and released with `*_free`.
//! - Strings are UTF-8 and null-terminated; strings returned through out-parameters are freed with `vz_string_free`,
//!   byte buffers with `vz_bytes_free`.
//! - Ciphertexts and keys use unpadded base64, as on the Matrix wire. Olm and Megolm use session config version 1,
//!   i.e. `m.olm.v1.curve25519-aes-sha2` and `m.megolm.v1.aes-sha2`.
//! - Pickle keys are 32 bytes.

use std::cell::RefCell;
use std::collections::BTreeMap;
use std::ffi::{c_char, CStr, CString};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;

use vodozemac::megolm::{ExportedSessionKey, GroupSession, GroupSessionPickle, InboundGroupSession, InboundGroupSessionPickle, MegolmMessage, SessionConfig as MegolmConfig, SessionKey};
use vodozemac::olm::{Account, AccountPickle, OlmMessage, PreKeyMessage, Session, SessionConfig as OlmConfig, SessionPickle};
use vodozemac::{base64_decode, base64_encode, Curve25519PublicKey, Ed25519PublicKey, Ed25519Signature};

thread_local! {
    static LAST_ERROR: RefCell<Option<String>> = const { RefCell::new(None) };
}

type Result<T> = std::result::Result<T, String>;

/// Runs `f`, mapping errors and panics to status codes and recording the message.
fn guard(f: impl FnOnce() -> Result<()>) -> i32 {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(Ok(())) => 0,
        Ok(Err(message)) => {
            LAST_ERROR.with(|e| *e.borrow_mut() = Some(message));
            -1
        }
        Err(_) => {
            LAST_ERROR.with(|e| *e.borrow_mut() = Some("panic in vodozemac-ffi".to_owned()));
            -2
        }
    }
}

/// Reads a UTF-8 string argument.
unsafe fn arg_str<'a>(value: *const c_char, name: &str) -> Result<&'a str> {
    if value.is_null() {
        return Err(format!("{name} is null"));
    }
    CStr::from_ptr(value).to_str().map_err(|_| format!("{name} is not UTF-8"))
}

/// Reads a byte buffer argument; a null pointer is allowed for an empty buffer.
unsafe fn arg_bytes<'a>(value: *const u8, length: usize) -> &'a [u8] {
    if value.is_null() || length == 0 {
        &[]
    } else {
        std::slice::from_raw_parts(value, length)
    }
}

/// Reads a 32-byte pickle key.
unsafe fn arg_key<'a>(key: *const u8) -> Result<&'a [u8; 32]> {
    if key.is_null() {
        return Err("pickle key is null".to_owned());
    }
    Ok(&*(key as *const [u8; 32]))
}

/// Borrows an object behind a handle.
unsafe fn arg_ref<'a, T>(handle: *mut T, name: &str) -> Result<&'a mut T> {
    handle.as_mut().ok_or_else(|| format!("{name} is null"))
}

/// Writes a string to an out-parameter.
unsafe fn out_string(out: *mut *mut c_char, value: String) -> Result<()> {
    if out.is_null() {
        return Err("output is null".to_owned());
    }
    *out = CString::new(value).map_err(|_| "string contains NUL".to_owned())?.into_raw();
    Ok(())
}

/// Writes a byte buffer to out-parameters.
unsafe fn out_bytes(out: *mut *mut u8, out_length: *mut usize, value: Vec<u8>) -> Result<()> {
    if out.is_null() || out_length.is_null() {
        return Err("output is null".to_owned());
    }
    let boxed = value.into_boxed_slice();
    *out_length = boxed.len();
    *out = Box::into_raw(boxed) as *mut u8;
    Ok(())
}

/// Writes a new object to an out-parameter.
unsafe fn out_handle<T>(out: *mut *mut T, value: T) -> Result<()> {
    if out.is_null() {
        return Err("output is null".to_owned());
    }
    *out = Box::into_raw(Box::new(value));
    Ok(())
}

/// Frees an object created by this library.
unsafe fn free_handle<T>(handle: *mut T) {
    if !handle.is_null() {
        drop(Box::from_raw(handle));
    }
}

fn curve_key(value: &str, name: &str) -> Result<Curve25519PublicKey> {
    Curve25519PublicKey::from_base64(value).map_err(|e| format!("invalid {name}: {e}"))
}

fn key_map(keys: impl IntoIterator<Item = (vodozemac::KeyId, Curve25519PublicKey)>) -> Result<String> {
    let map: BTreeMap<String, String> = keys.into_iter().map(|(id, key)| (id.to_base64(), key.to_base64())).collect();
    serde_json::to_string(&map).map_err(|e| e.to_string())
}

// ---------------------------------------------------------------- memory and errors

/// Returns the last error message of this thread, or null; free it with `vz_string_free`.
#[no_mangle]
pub extern "C" fn vz_last_error() -> *mut c_char {
    LAST_ERROR.with(|e| e.borrow_mut().take()).and_then(|m| CString::new(m).ok()).map_or(ptr::null_mut(), CString::into_raw)
}

/// Frees a string returned by this library.
#[no_mangle]
pub unsafe extern "C" fn vz_string_free(value: *mut c_char) {
    if !value.is_null() {
        drop(CString::from_raw(value));
    }
}

/// Frees a byte buffer returned by this library.
#[no_mangle]
pub unsafe extern "C" fn vz_bytes_free(value: *mut u8, length: usize) {
    if !value.is_null() {
        drop(Box::from_raw(ptr::slice_from_raw_parts_mut(value, length)));
    }
}

// ---------------------------------------------------------------- account

/// Creates a new account with fresh identity keys.
#[no_mangle]
pub unsafe extern "C" fn vz_account_new(out: *mut *mut Account) -> i32 {
    guard(|| out_handle(out, Account::new()))
}

/// Frees an account.
#[no_mangle]
pub unsafe extern "C" fn vz_account_free(account: *mut Account) {
    free_handle(account);
}

/// Restores an account from an encrypted pickle.
#[no_mangle]
pub unsafe extern "C" fn vz_account_from_pickle(pickle: *const c_char, key: *const u8, out: *mut *mut Account) -> i32 {
    guard(|| {
        let pickle = AccountPickle::from_encrypted(arg_str(pickle, "pickle")?, arg_key(key)?).map_err(|e| e.to_string())?;
        out_handle(out, Account::from_pickle(pickle))
    })
}

/// Encrypts the account's state into a pickle.
#[no_mangle]
pub unsafe extern "C" fn vz_account_pickle(account: *mut Account, key: *const u8, out: *mut *mut c_char) -> i32 {
    guard(|| {
        let account = arg_ref(account, "account")?;
        out_string(out, account.pickle().encrypt(arg_key(key)?))
    })
}

/// Returns `{"curve25519": "...", "ed25519": "..."}`.
#[no_mangle]
pub unsafe extern "C" fn vz_account_identity_keys(account: *mut Account, out: *mut *mut c_char) -> i32 {
    guard(|| {
        let keys = arg_ref(account, "account")?.identity_keys();
        let json = serde_json::json!({ "curve25519": keys.curve25519.to_base64(), "ed25519": keys.ed25519.to_base64() });
        out_string(out, json.to_string())
    })
}

/// Signs a message with the account's Ed25519 key; returns the signature in base64.
#[no_mangle]
pub unsafe extern "C" fn vz_account_sign(account: *mut Account, message: *const u8, length: usize, out: *mut *mut c_char) -> i32 {
    guard(|| {
        let signature = arg_ref(account, "account")?.sign(arg_bytes(message, length));
        out_string(out, signature.to_base64())
    })
}

/// Returns how many one-time keys the account can hold.
#[no_mangle]
pub unsafe extern "C" fn vz_account_max_one_time_keys(account: *mut Account) -> usize {
    account.as_ref().map_or(0, Account::max_number_of_one_time_keys)
}

/// Generates `count` new one-time keys.
#[no_mangle]
pub unsafe extern "C" fn vz_account_generate_one_time_keys(account: *mut Account, count: usize) -> i32 {
    guard(|| {
        arg_ref(account, "account")?.generate_one_time_keys(count);
        Ok(())
    })
}

/// Returns unpublished one-time keys as `{"key id": "public key"}`.
#[no_mangle]
pub unsafe extern "C" fn vz_account_one_time_keys(account: *mut Account, out: *mut *mut c_char) -> i32 {
    guard(|| out_string(out, key_map(arg_ref(account, "account")?.one_time_keys())?))
}

/// Generates a new fallback key, replacing the unused one.
#[no_mangle]
pub unsafe extern "C" fn vz_account_generate_fallback_key(account: *mut Account) -> i32 {
    guard(|| {
        arg_ref(account, "account")?.generate_fallback_key();
        Ok(())
    })
}

/// Returns the unpublished fallback key as `{"key id": "public key"}`, empty when none.
#[no_mangle]
pub unsafe extern "C" fn vz_account_fallback_key(account: *mut Account, out: *mut *mut c_char) -> i32 {
    guard(|| out_string(out, key_map(arg_ref(account, "account")?.fallback_key())?))
}

/// Marks current one-time and fallback keys as published, so they are not returned again.
#[no_mangle]
pub unsafe extern "C" fn vz_account_mark_keys_as_published(account: *mut Account) -> i32 {
    guard(|| {
        arg_ref(account, "account")?.mark_keys_as_published();
        Ok(())
    })
}

/// Starts an Olm session to a device, given its Curve25519 identity key and a claimed one-time key.
#[no_mangle]
pub unsafe extern "C" fn vz_account_create_outbound_session(account: *mut Account, identity_key: *const c_char, one_time_key: *const c_char, out: *mut *mut Session) -> i32 {
    guard(|| {
        let account = arg_ref(account, "account")?;
        let identity = curve_key(arg_str(identity_key, "identity key")?, "identity key")?;
        let one_time = curve_key(arg_str(one_time_key, "one-time key")?, "one-time key")?;
        let session = account.create_outbound_session(OlmConfig::version_1(), identity, one_time).map_err(|e| e.to_string())?;
        out_handle(out, session)
    })
}

/// Creates an Olm session from a received pre-key message (type 0) and returns its plaintext; uses up the one-time key.
#[no_mangle]
pub unsafe extern "C" fn vz_account_create_inbound_session(account: *mut Account, identity_key: *const c_char, ciphertext: *const c_char, out: *mut *mut Session, out_plaintext: *mut *mut u8, out_length: *mut usize) -> i32 {
    guard(|| {
        let account = arg_ref(account, "account")?;
        let identity = curve_key(arg_str(identity_key, "identity key")?, "identity key")?;
        let message = PreKeyMessage::from_base64(arg_str(ciphertext, "ciphertext")?).map_err(|e| e.to_string())?;
        let result = account.create_inbound_session(OlmConfig::version_1(), identity, &message).map_err(|e| e.to_string())?;
        out_bytes(out_plaintext, out_length, result.plaintext)?;
        out_handle(out, result.session)
    })
}

// ---------------------------------------------------------------- olm session

/// Frees an Olm session.
#[no_mangle]
pub unsafe extern "C" fn vz_session_free(session: *mut Session) {
    free_handle(session);
}

/// Returns the session ID.
#[no_mangle]
pub unsafe extern "C" fn vz_session_id(session: *mut Session, out: *mut *mut c_char) -> i32 {
    guard(|| out_string(out, arg_ref(session, "session")?.session_id()))
}

/// Returns the ID of the session a pre-key message would create, to find an existing one.
#[no_mangle]
pub unsafe extern "C" fn vz_prekey_session_id(ciphertext: *const c_char, out: *mut *mut c_char) -> i32 {
    guard(|| {
        let message = PreKeyMessage::from_base64(arg_str(ciphertext, "ciphertext")?).map_err(|e| e.to_string())?;
        out_string(out, message.session_id())
    })
}

/// Encrypts a message; returns its type (0 pre-key, 1 normal) and ciphertext.
#[no_mangle]
pub unsafe extern "C" fn vz_session_encrypt(session: *mut Session, plaintext: *const u8, length: usize, out_type: *mut u32, out: *mut *mut c_char) -> i32 {
    guard(|| {
        let message = arg_ref(session, "session")?.encrypt(arg_bytes(plaintext, length)).map_err(|e| e.to_string())?;
        let (message_type, bytes) = message.to_parts();
        if out_type.is_null() {
            return Err("output is null".to_owned());
        }
        *out_type = message_type as u32;
        out_string(out, base64_encode(bytes))
    })
}

/// Decrypts a message of the given type.
#[no_mangle]
pub unsafe extern "C" fn vz_session_decrypt(session: *mut Session, message_type: u32, ciphertext: *const c_char, out: *mut *mut u8, out_length: *mut usize) -> i32 {
    guard(|| {
        let bytes = base64_decode(arg_str(ciphertext, "ciphertext")?).map_err(|e| e.to_string())?;
        let message = OlmMessage::from_parts(message_type as usize, &bytes).map_err(|e| e.to_string())?;
        let plaintext = arg_ref(session, "session")?.decrypt(&message).map_err(|e| e.to_string())?;
        out_bytes(out, out_length, plaintext)
    })
}

/// Encrypts the session's state into a pickle.
#[no_mangle]
pub unsafe extern "C" fn vz_session_pickle(session: *mut Session, key: *const u8, out: *mut *mut c_char) -> i32 {
    guard(|| out_string(out, arg_ref(session, "session")?.pickle().encrypt(arg_key(key)?)))
}

/// Restores an Olm session from an encrypted pickle.
#[no_mangle]
pub unsafe extern "C" fn vz_session_from_pickle(pickle: *const c_char, key: *const u8, out: *mut *mut Session) -> i32 {
    guard(|| {
        let pickle = SessionPickle::from_encrypted(arg_str(pickle, "pickle")?, arg_key(key)?).map_err(|e| e.to_string())?;
        out_handle(out, Session::from_pickle(pickle))
    })
}

// ---------------------------------------------------------------- megolm outbound

/// Creates a new outbound Megolm session for sending to a room.
#[no_mangle]
pub unsafe extern "C" fn vz_group_session_new(out: *mut *mut GroupSession) -> i32 {
    guard(|| out_handle(out, GroupSession::new(MegolmConfig::version_1())))
}

/// Frees an outbound Megolm session.
#[no_mangle]
pub unsafe extern "C" fn vz_group_session_free(session: *mut GroupSession) {
    free_handle(session);
}

/// Returns the session ID.
#[no_mangle]
pub unsafe extern "C" fn vz_group_session_id(session: *mut GroupSession, out: *mut *mut c_char) -> i32 {
    guard(|| out_string(out, arg_ref(session, "session")?.session_id()))
}

/// Returns the index of the next message.
#[no_mangle]
pub unsafe extern "C" fn vz_group_session_message_index(session: *mut GroupSession) -> u32 {
    session.as_ref().map_or(0, GroupSession::message_index)
}

/// Returns the session key to share with room members, valid from the current index.
#[no_mangle]
pub unsafe extern "C" fn vz_group_session_key(session: *mut GroupSession, out: *mut *mut c_char) -> i32 {
    guard(|| out_string(out, arg_ref(session, "session")?.session_key().to_base64()))
}

/// Encrypts a message for the room.
#[no_mangle]
pub unsafe extern "C" fn vz_group_session_encrypt(session: *mut GroupSession, plaintext: *const u8, length: usize, out: *mut *mut c_char) -> i32 {
    guard(|| out_string(out, arg_ref(session, "session")?.encrypt(arg_bytes(plaintext, length)).to_base64()))
}

/// Encrypts the session's state into a pickle.
#[no_mangle]
pub unsafe extern "C" fn vz_group_session_pickle(session: *mut GroupSession, key: *const u8, out: *mut *mut c_char) -> i32 {
    guard(|| out_string(out, arg_ref(session, "session")?.pickle().encrypt(arg_key(key)?)))
}

/// Restores an outbound Megolm session from an encrypted pickle.
#[no_mangle]
pub unsafe extern "C" fn vz_group_session_from_pickle(pickle: *const c_char, key: *const u8, out: *mut *mut GroupSession) -> i32 {
    guard(|| {
        let pickle = GroupSessionPickle::from_encrypted(arg_str(pickle, "pickle")?, arg_key(key)?).map_err(|e| e.to_string())?;
        out_handle(out, GroupSession::from_pickle(pickle))
    })
}

// ---------------------------------------------------------------- megolm inbound

/// Creates an inbound Megolm session from a session key received in `m.room_key`.
#[no_mangle]
pub unsafe extern "C" fn vz_inbound_group_session_new(session_key: *const c_char, out: *mut *mut InboundGroupSession) -> i32 {
    guard(|| {
        let key = SessionKey::from_base64(arg_str(session_key, "session key")?).map_err(|e| e.to_string())?;
        out_handle(out, InboundGroupSession::new(&key, MegolmConfig::version_1()))
    })
}

/// Creates an inbound Megolm session from an exported key, e.g. from `m.forwarded_room_key` or a backup.
#[no_mangle]
pub unsafe extern "C" fn vz_inbound_group_session_import(exported_key: *const c_char, out: *mut *mut InboundGroupSession) -> i32 {
    guard(|| {
        let key = ExportedSessionKey::from_base64(arg_str(exported_key, "exported key")?).map_err(|e| e.to_string())?;
        out_handle(out, InboundGroupSession::import(&key, MegolmConfig::version_1()))
    })
}

/// Frees an inbound Megolm session.
#[no_mangle]
pub unsafe extern "C" fn vz_inbound_group_session_free(session: *mut InboundGroupSession) {
    free_handle(session);
}

/// Returns the session ID.
#[no_mangle]
pub unsafe extern "C" fn vz_inbound_group_session_id(session: *mut InboundGroupSession, out: *mut *mut c_char) -> i32 {
    guard(|| out_string(out, arg_ref(session, "session")?.session_id()))
}

/// Returns the first message index this session can decrypt.
#[no_mangle]
pub unsafe extern "C" fn vz_inbound_group_session_first_known_index(session: *mut InboundGroupSession) -> u32 {
    session.as_ref().map_or(0, InboundGroupSession::first_known_index)
}

/// Decrypts a room message; returns the plaintext and its message index, for replay detection.
#[no_mangle]
pub unsafe extern "C" fn vz_inbound_group_session_decrypt(session: *mut InboundGroupSession, ciphertext: *const c_char, out: *mut *mut u8, out_length: *mut usize, out_index: *mut u32) -> i32 {
    guard(|| {
        let message = MegolmMessage::from_base64(arg_str(ciphertext, "ciphertext")?).map_err(|e| e.to_string())?;
        let decrypted = arg_ref(session, "session")?.decrypt(&message).map_err(|e| e.to_string())?;
        if out_index.is_null() {
            return Err("output is null".to_owned());
        }
        *out_index = decrypted.message_index;
        out_bytes(out, out_length, decrypted.plaintext)
    })
}

/// Exports the session from `index` on, e.g. to forward or back it up.
#[no_mangle]
pub unsafe extern "C" fn vz_inbound_group_session_export_at(session: *mut InboundGroupSession, index: u32, out: *mut *mut c_char) -> i32 {
    guard(|| {
        let exported = arg_ref(session, "session")?.export_at(index).ok_or_else(|| format!("index {index} is before the first known index"))?;
        out_string(out, exported.to_base64())
    })
}

/// Encrypts the session's state into a pickle.
#[no_mangle]
pub unsafe extern "C" fn vz_inbound_group_session_pickle(session: *mut InboundGroupSession, key: *const u8, out: *mut *mut c_char) -> i32 {
    guard(|| out_string(out, arg_ref(session, "session")?.pickle().encrypt(arg_key(key)?)))
}

/// Restores an inbound Megolm session from an encrypted pickle.
#[no_mangle]
pub unsafe extern "C" fn vz_inbound_group_session_from_pickle(pickle: *const c_char, key: *const u8, out: *mut *mut InboundGroupSession) -> i32 {
    guard(|| {
        let pickle = InboundGroupSessionPickle::from_encrypted(arg_str(pickle, "pickle")?, arg_key(key)?).map_err(|e| e.to_string())?;
        out_handle(out, InboundGroupSession::from_pickle(pickle))
    })
}

// ---------------------------------------------------------------- signatures

/// Verifies an Ed25519 signature; returns 0 when valid and -1 with an error message otherwise.
#[no_mangle]
pub unsafe extern "C" fn vz_ed25519_verify(public_key: *const c_char, message: *const u8, length: usize, signature: *const c_char) -> i32 {
    guard(|| {
        let key = Ed25519PublicKey::from_base64(arg_str(public_key, "public key")?).map_err(|e| e.to_string())?;
        let signature = Ed25519Signature::from_base64(arg_str(signature, "signature")?).map_err(|e| e.to_string())?;
        key.verify(arg_bytes(message, length), &signature).map_err(|e| e.to_string())
    })
}

// ---------------------------------------------------------------- standalone ed25519 keys (cross-signing)

/// Creates a random Ed25519 key; returns the secret seed and public key in base64.
#[no_mangle]
pub unsafe extern "C" fn vz_ed25519_key_new(out_secret: *mut *mut c_char, out_public: *mut *mut c_char) -> i32 {
    guard(|| {
        let secret = vodozemac::Ed25519SecretKey::new();
        out_string(out_public, secret.public_key().to_base64())?;
        out_string(out_secret, secret.to_base64())
    })
}

/// Returns the public key of an Ed25519 secret seed.
#[no_mangle]
pub unsafe extern "C" fn vz_ed25519_public_key(secret: *const c_char, out: *mut *mut c_char) -> i32 {
    guard(|| {
        let secret = vodozemac::Ed25519SecretKey::from_base64(arg_str(secret, "secret")?).map_err(|e| e.to_string())?;
        out_string(out, secret.public_key().to_base64())
    })
}

/// Signs a message with an Ed25519 secret seed; returns the signature in base64.
#[no_mangle]
pub unsafe extern "C" fn vz_ed25519_sign(secret: *const c_char, message: *const u8, length: usize, out: *mut *mut c_char) -> i32 {
    guard(|| {
        let secret = vodozemac::Ed25519SecretKey::from_base64(arg_str(secret, "secret")?).map_err(|e| e.to_string())?;
        out_string(out, secret.sign(arg_bytes(message, length)).to_base64())
    })
}

// ---------------------------------------------------------------- pk encryption (m.megolm_backup.v1)

/// Parses a 32-byte Curve25519 secret key from base64.
fn curve_secret(value: &str) -> Result<vodozemac::Curve25519SecretKey> {
    let bytes = base64_decode(value).map_err(|e| e.to_string())?;
    let bytes: [u8; 32] = bytes.try_into().map_err(|_| "secret key is not 32 bytes".to_owned())?;
    Ok(vodozemac::Curve25519SecretKey::from_slice(&bytes))
}

/// Returns the Curve25519 public key of a backup secret key.
#[no_mangle]
pub unsafe extern "C" fn vz_pk_public_key(secret: *const c_char, out: *mut *mut c_char) -> i32 {
    guard(|| {
        let decryption = vodozemac::pk_encryption::PkDecryption::from_key(curve_secret(arg_str(secret, "secret")?)?);
        out_string(out, decryption.public_key().to_base64())
    })
}

/// Encrypts for a backup public key; returns ciphertext, MAC and ephemeral key in base64.
#[no_mangle]
pub unsafe extern "C" fn vz_pk_encrypt(public_key: *const c_char, plaintext: *const u8, length: usize, out_ciphertext: *mut *mut c_char, out_mac: *mut *mut c_char, out_ephemeral: *mut *mut c_char) -> i32 {
    guard(|| {
        let key = curve_key(arg_str(public_key, "public key")?, "public key")?;
        let message = vodozemac::pk_encryption::PkEncryption::from_key(key).encrypt(arg_bytes(plaintext, length)).map_err(|e| e.to_string())?;
        out_string(out_ciphertext, base64_encode(&message.ciphertext))?;
        out_string(out_mac, base64_encode(&message.mac))?;
        out_string(out_ephemeral, message.ephemeral_key.to_base64())
    })
}

/// Decrypts a backup message with the backup secret key.
#[no_mangle]
pub unsafe extern "C" fn vz_pk_decrypt(secret: *const c_char, ciphertext: *const c_char, mac: *const c_char, ephemeral: *const c_char, out: *mut *mut u8, out_length: *mut usize) -> i32 {
    guard(|| {
        let decryption = vodozemac::pk_encryption::PkDecryption::from_key(curve_secret(arg_str(secret, "secret")?)?);
        let message = vodozemac::pk_encryption::Message::from_base64(arg_str(ciphertext, "ciphertext")?, arg_str(mac, "mac")?, arg_str(ephemeral, "ephemeral key")?).map_err(|e| e.to_string())?;
        out_bytes(out, out_length, decryption.decrypt(&message).map_err(|e| e.to_string())?)
    })
}

// ---------------------------------------------------------------- sas verification

/// Starts a SAS verification with a fresh key pair.
#[no_mangle]
pub unsafe extern "C" fn vz_sas_new(out: *mut *mut vodozemac::sas::Sas) -> i32 {
    guard(|| out_handle(out, vodozemac::sas::Sas::new()))
}

/// Frees a SAS object that was not established.
#[no_mangle]
pub unsafe extern "C" fn vz_sas_free(sas: *mut vodozemac::sas::Sas) {
    free_handle(sas);
}

/// Returns our SAS public key.
#[no_mangle]
pub unsafe extern "C" fn vz_sas_public_key(sas: *mut vodozemac::sas::Sas, out: *mut *mut c_char) -> i32 {
    guard(|| out_string(out, arg_ref(sas, "sas")?.public_key().to_base64()))
}

/// Completes the key exchange with their public key; consumes and frees `sas` in every case.
#[no_mangle]
pub unsafe extern "C" fn vz_sas_establish(sas: *mut vodozemac::sas::Sas, their_key: *const c_char, out: *mut *mut vodozemac::sas::EstablishedSas) -> i32 {
    guard(|| {
        if sas.is_null() {
            return Err("sas is null".to_owned());
        }
        let sas = *Box::from_raw(sas);
        let established = sas.diffie_hellman_with_raw(arg_str(their_key, "their key")?).map_err(|e| e.to_string())?;
        out_handle(out, established)
    })
}

/// Frees an established SAS.
#[no_mangle]
pub unsafe extern "C" fn vz_established_sas_free(sas: *mut vodozemac::sas::EstablishedSas) {
    free_handle(sas);
}

/// Returns the 6 SAS bytes for `info`, from which emoji and decimals are derived.
#[no_mangle]
pub unsafe extern "C" fn vz_established_sas_bytes(sas: *mut vodozemac::sas::EstablishedSas, info: *const c_char, out: *mut *mut u8, out_length: *mut usize) -> i32 {
    guard(|| {
        let bytes = arg_ref(sas, "sas")?.bytes(arg_str(info, "info")?);
        out_bytes(out, out_length, bytes.as_bytes().to_vec())
    })
}

/// Calculates the MAC of `input` with `info`, base64.
#[no_mangle]
pub unsafe extern "C" fn vz_established_sas_calculate_mac(sas: *mut vodozemac::sas::EstablishedSas, input: *const c_char, info: *const c_char, out: *mut *mut c_char) -> i32 {
    guard(|| out_string(out, arg_ref(sas, "sas")?.calculate_mac(arg_str(input, "input")?, arg_str(info, "info")?).to_base64()))
}

/// Verifies a MAC of `input` with `info`; returns 0 when valid.
#[no_mangle]
pub unsafe extern "C" fn vz_established_sas_verify_mac(sas: *mut vodozemac::sas::EstablishedSas, input: *const c_char, info: *const c_char, mac: *const c_char) -> i32 {
    guard(|| {
        let tag = vodozemac::sas::Mac::from_base64(arg_str(mac, "mac")?).map_err(|e| e.to_string())?;
        arg_ref(sas, "sas")?.verify_mac(arg_str(input, "input")?, arg_str(info, "info")?, &tag).map_err(|e| e.to_string())
    })
}
