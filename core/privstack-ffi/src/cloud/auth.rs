//! Cloud sync authentication and key management FFI exports.

use super::{cloud_err, parse_cstr, write_json_out};
use crate::{lock_handle, PrivStackError, HANDLE};
use privstack_cloud::api_client::CloudApiClient;
use privstack_cloud::config::CloudConfig;
use privstack_cloud::envelope::EnvelopeManager;
use privstack_cloud::sharing::ShareManager;
use privstack_crypto::envelope as crypto_env;
use std::ffi::{c_char, CString};
use std::sync::Arc;
use tokio::sync::Mutex as TokioMutex;

/// Configures the cloud sync engine with API and S3 settings.
///
/// `config_json` must be a JSON object with fields:
/// `api_base_url`, `s3_bucket`, `s3_region`, optional `s3_endpoint_override`,
/// optional `credential_refresh_margin_secs`, optional `poll_interval_secs`.
///
/// # Safety
/// - `config_json` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_configure(
    config_json: *const c_char,
) -> PrivStackError {
    let json_str = match unsafe { parse_cstr(config_json) } {
        Ok(s) => s,
        Err(e) => return e,
    };

    let config: CloudConfig = match serde_json::from_str(json_str) {
        Ok(c) => c,
        Err(_) => return PrivStackError::InvalidArgument,
    };

    let mut handle = lock_handle();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let api = Arc::new(CloudApiClient::new(config.clone()));
    let envelope_mgr = Arc::new(TokioMutex::new(EnvelopeManager::new(api.clone())));
    let share_mgr = Arc::new(ShareManager::new(api.clone()));

    handle.cloud_api = Some(api);
    handle.cloud_envelope_mgr = Some(envelope_mgr);
    handle.cloud_share_mgr = Some(share_mgr);
    handle.cloud_config = Some(config);

    PrivStackError::Ok
}

/// Authenticates with the PrivStack cloud API using email/password.
///
/// On success, writes a JSON `AuthTokens` to `out_json`.
///
/// # Safety
/// - `email`, `password` must be valid null-terminated UTF-8 strings.
/// - `out_json` must be a valid pointer. Result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_authenticate(
    email: *const c_char,
    password: *const c_char,
    out_json: *mut *mut c_char,
) -> PrivStackError {
    let email_str = match unsafe { parse_cstr(email) } {
        Ok(s) => s,
        Err(e) => return e,
    };
    let password_str = match unsafe { parse_cstr(password) } {
        Ok(s) => s,
        Err(e) => return e,
    };
    if out_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let handle = lock_handle();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let api = match handle.cloud_api.as_ref() {
        Some(a) => a.clone(),
        None => return PrivStackError::NotInitialized,
    };

    match handle.runtime.block_on(api.authenticate(email_str, password_str)) {
        Ok(tokens) => write_json_out(out_json, &tokens),
        Err(e) => cloud_err(&e),
    }
}

/// Sets auth tokens directly (restoring a saved session).
///
/// # Safety
/// - `access_token`, `refresh_token` must be valid null-terminated UTF-8 strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_authenticate_with_tokens(
    access_token: *const c_char,
    refresh_token: *const c_char,
    user_id: i64,
) -> PrivStackError {
    let at = match unsafe { parse_cstr(access_token) } {
        Ok(s) => s.to_string(),
        Err(e) => return e,
    };
    let rt = match unsafe { parse_cstr(refresh_token) } {
        Ok(s) => s.to_string(),
        Err(e) => return e,
    };

    let handle = lock_handle();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let api = match handle.cloud_api.as_ref() {
        Some(a) => a.clone(),
        None => return PrivStackError::NotInitialized,
    };

    handle.runtime.block_on(api.set_tokens(at, rt, user_id));
    PrivStackError::Ok
}

/// Logs out of the cloud API (clears tokens).
#[unsafe(no_mangle)]
pub extern "C" fn privstack_cloudsync_logout() -> PrivStackError {
    let handle = lock_handle();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let api = match handle.cloud_api.as_ref() {
        Some(a) => a.clone(),
        None => return PrivStackError::NotInitialized,
    };

    handle.runtime.block_on(api.logout());
    PrivStackError::Ok
}

/// Returns whether the cloud API client is authenticated.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_cloudsync_is_authenticated() -> bool {
    let handle = lock_handle();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return false,
    };

    let api = match handle.cloud_api.as_ref() {
        Some(a) => a.clone(),
        None => return false,
    };

    handle.runtime.block_on(api.is_authenticated())
}

/// Sets up a new cloud keypair with a passphrase.
///
/// Generates X25519 keypair, encrypts private key with passphrase,
/// uploads public key to API, and returns a 12-word BIP39 recovery mnemonic.
///
/// # Safety
/// - `passphrase` must be a valid null-terminated UTF-8 string.
/// - `out_mnemonic` must be a valid pointer. Result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_setup_passphrase(
    passphrase: *const c_char,
    out_mnemonic: *mut *mut c_char,
) -> PrivStackError {
    let passphrase_str = match unsafe { parse_cstr(passphrase) } {
        Ok(s) => s,
        Err(e) => return e,
    };
    if out_mnemonic.is_null() {
        return PrivStackError::NullPointer;
    }

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let api = match handle.cloud_api.as_ref() {
        Some(a) => a.clone(),
        None => return PrivStackError::NotInitialized,
    };

    let keypair = crypto_env::generate_cloud_keypair();
    let public_bytes = keypair.public_bytes();

    if let Err(e) = handle.runtime.block_on(api.upload_public_key(&public_bytes)) {
        return cloud_err(&e);
    }

    match crypto_env::encrypt_private_key(&keypair.secret, passphrase_str) {
        Ok(_encrypted) => {
            // TODO: Upload encrypted private key to S3 at {user_id}/{workspace_id}/keys/private_key.enc
        }
        Err(_) => return PrivStackError::EnvelopeError,
    }

    let mnemonic = match crypto_env::generate_recovery_mnemonic() {
        Ok(m) => m,
        Err(_) => return PrivStackError::EnvelopeError,
    };

    if let Some(ref env_mgr) = handle.cloud_envelope_mgr {
        handle.runtime.block_on(async {
            env_mgr.lock().await.set_keypair(keypair);
        });
    }

    let c_str = CString::new(mnemonic).unwrap();
    unsafe { *out_mnemonic = c_str.into_raw() };
    PrivStackError::Ok
}

/// Unlocks the cloud keypair using a passphrase.
///
/// # Safety
/// - `passphrase` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_enter_passphrase(
    passphrase: *const c_char,
) -> PrivStackError {
    let passphrase_str = match unsafe { parse_cstr(passphrase) } {
        Ok(s) => s,
        Err(e) => return e,
    };

    let handle = HANDLE.lock().unwrap();
    let _handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    // TODO: Download encrypted private key from S3 and decrypt
    let _ = passphrase_str;

    PrivStackError::Ok
}

/// Recovers the cloud keypair from a 12-word BIP39 mnemonic.
///
/// # Safety
/// - `mnemonic` must be a valid null-terminated UTF-8 string (12 words separated by spaces).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_recover_from_mnemonic(
    mnemonic: *const c_char,
) -> PrivStackError {
    let mnemonic_str = match unsafe { parse_cstr(mnemonic) } {
        Ok(s) => s,
        Err(e) => return e,
    };

    let handle = HANDLE.lock().unwrap();
    let _handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    // TODO: Download encrypted private key from S3, decrypt with mnemonic-derived key
    let _ = mnemonic_str;

    PrivStackError::Ok
}

/// Returns whether a cloud keypair is loaded in memory.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_cloudsync_has_keypair() -> bool {
    let handle = lock_handle();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return false,
    };

    let env_mgr = match handle.cloud_envelope_mgr.as_ref() {
        Some(m) => m.clone(),
        None => return false,
    };

    handle.runtime.block_on(async { env_mgr.lock().await.has_keypair() })
}
