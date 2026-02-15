//! Cloud sync authentication and key management FFI exports.

use super::{cloud_err, parse_cstr, write_json_out};
use crate::{lock_handle, PrivStackError, HANDLE};
use privstack_cloud::api_client::CloudApiClient;
use privstack_cloud::compaction::{private_key_s3_key, recovery_key_s3_key};
use privstack_cloud::config::CloudConfig;
use privstack_cloud::credential_manager::CredentialManager;
use privstack_cloud::dek_registry::DekRegistry;
use privstack_cloud::envelope::EnvelopeManager;
use privstack_cloud::s3_transport::S3Transport;
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
    handle.cloud_dek_registry = Some(DekRegistry::new());

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
/// uploads to S3, uploads public key to API, and returns a 12-word
/// BIP39 recovery mnemonic.
///
/// Requires at least one cloud workspace to be registered (for S3 storage).
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

    let config = match handle.cloud_config.as_ref() {
        Some(c) => c.clone(),
        None => return PrivStackError::NotInitialized,
    };

    // Generate keypair
    let keypair = crypto_env::generate_cloud_keypair();
    let public_bytes = keypair.public_bytes();

    // Upload public key to API
    if let Err(e) = handle.runtime.block_on(api.upload_public_key(&public_bytes)) {
        return cloud_err(&e);
    }

    // Encrypt private key with passphrase
    let protected = match crypto_env::encrypt_private_key(&keypair.secret, passphrase_str) {
        Ok(p) => p,
        Err(_) => return PrivStackError::EnvelopeError,
    };

    // Generate mnemonic and encrypt recovery copy
    let mnemonic = match crypto_env::generate_recovery_mnemonic() {
        Ok(m) => m,
        Err(_) => return PrivStackError::EnvelopeError,
    };

    let recovery_encrypted =
        match crypto_env::encrypt_private_key_with_mnemonic(&keypair.secret, &mnemonic) {
            Ok(e) => e,
            Err(_) => return PrivStackError::EnvelopeError,
        };

    // Upload encrypted keys to S3
    let result = handle.runtime.block_on(async {
        // Find a workspace to store keys in
        let workspaces = api.list_workspaces().await?;
        let ws = workspaces
            .first()
            .ok_or(privstack_cloud::CloudError::Config(
                "no cloud workspace registered — register a workspace first".to_string(),
            ))?;

        let user_id = api
            .user_id()
            .await
            .ok_or(privstack_cloud::CloudError::AuthRequired)?;

        let transport = S3Transport::new(
            config.s3_bucket.clone(),
            config.s3_region.clone(),
            config.s3_endpoint_override.clone(),
        );

        let cred_mgr = CredentialManager::new(
            api.clone(),
            ws.workspace_id.clone(),
            config.credential_refresh_margin_secs,
        );
        let creds = cred_mgr.get_credentials().await?;

        // Upload passphrase-protected key
        let protected_bytes = serde_json::to_vec(&protected)
            .map_err(|e| privstack_cloud::CloudError::S3(e.to_string()))?;
        let key_path = private_key_s3_key(user_id, &ws.workspace_id);
        transport.upload(&creds, &key_path, protected_bytes).await?;

        // Upload mnemonic-encrypted recovery key
        let recovery_bytes = serde_json::to_vec(&recovery_encrypted)
            .map_err(|e| privstack_cloud::CloudError::S3(e.to_string()))?;
        let recovery_path = recovery_key_s3_key(user_id, &ws.workspace_id);
        transport
            .upload(&creds, &recovery_path, recovery_bytes)
            .await?;

        Ok::<(), privstack_cloud::CloudError>(())
    });

    if let Err(e) = result {
        return cloud_err(&e);
    }

    // Set keypair in envelope manager
    if let Some(ref env_mgr) = handle.cloud_envelope_mgr {
        handle.runtime.block_on(async {
            env_mgr.lock().await.set_keypair(keypair);
        });
    }

    let c_str = CString::new(mnemonic).unwrap();
    unsafe { *out_mnemonic = c_str.into_raw() };
    PrivStackError::Ok
}

/// Sets up unified recovery: single mnemonic for both vault and cloud keypair.
///
/// 1. Generates a single BIP39 mnemonic
/// 2. Re-encrypts vault recovery blob with that mnemonic
/// 3. Generates X25519 keypair, encrypts with passphrase + mnemonic
/// 4. Uploads cloud keys to S3
/// 5. Returns mnemonic (caller must show unified Recovery Kit PDF)
///
/// # Safety
/// - `passphrase` must be a valid null-terminated UTF-8 string (vault password).
/// - `out_mnemonic` must be a valid pointer. Result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_setup_unified_recovery(
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

    let config = match handle.cloud_config.as_ref() {
        Some(c) => c.clone(),
        None => return PrivStackError::NotInitialized,
    };

    // 1. Generate single mnemonic
    let mnemonic = match crypto_env::generate_recovery_mnemonic() {
        Ok(m) => m,
        Err(_) => return PrivStackError::EnvelopeError,
    };

    // 2. Re-encrypt vault recovery blob with this mnemonic
    if let Err(e) = handle.vault_manager.setup_recovery_with_mnemonic("default", &mnemonic) {
        eprintln!("[FFI] Failed to setup vault recovery with unified mnemonic: {e}");
        return PrivStackError::AuthError;
    }

    // 3. Generate cloud keypair
    let keypair = crypto_env::generate_cloud_keypair();
    let public_bytes = keypair.public_bytes();

    // Upload public key to API
    if let Err(e) = handle.runtime.block_on(api.upload_public_key(&public_bytes)) {
        return cloud_err(&e);
    }

    // Encrypt private key with passphrase
    let protected = match crypto_env::encrypt_private_key(&keypair.secret, passphrase_str) {
        Ok(p) => p,
        Err(_) => return PrivStackError::EnvelopeError,
    };

    // Encrypt private key with mnemonic (recovery copy)
    let recovery_encrypted =
        match crypto_env::encrypt_private_key_with_mnemonic(&keypair.secret, &mnemonic) {
            Ok(e) => e,
            Err(_) => return PrivStackError::EnvelopeError,
        };

    // 4. Upload encrypted keys to S3
    let result = handle.runtime.block_on(async {
        let workspaces = api.list_workspaces().await?;
        let ws = workspaces
            .first()
            .ok_or(privstack_cloud::CloudError::Config(
                "no cloud workspace registered — register a workspace first".to_string(),
            ))?;

        let user_id = api
            .user_id()
            .await
            .ok_or(privstack_cloud::CloudError::AuthRequired)?;

        let transport = S3Transport::new(
            config.s3_bucket.clone(),
            config.s3_region.clone(),
            config.s3_endpoint_override.clone(),
        );

        let cred_mgr = CredentialManager::new(
            api.clone(),
            ws.workspace_id.clone(),
            config.credential_refresh_margin_secs,
        );
        let creds = cred_mgr.get_credentials().await?;

        // Upload passphrase-protected key
        let protected_bytes = serde_json::to_vec(&protected)
            .map_err(|e| privstack_cloud::CloudError::S3(e.to_string()))?;
        let key_path = private_key_s3_key(user_id, &ws.workspace_id);
        transport.upload(&creds, &key_path, protected_bytes).await?;

        // Upload mnemonic-encrypted recovery key
        let recovery_bytes = serde_json::to_vec(&recovery_encrypted)
            .map_err(|e| privstack_cloud::CloudError::S3(e.to_string()))?;
        let recovery_path = recovery_key_s3_key(user_id, &ws.workspace_id);
        transport
            .upload(&creds, &recovery_path, recovery_bytes)
            .await?;

        Ok::<(), privstack_cloud::CloudError>(())
    });

    if let Err(e) = result {
        return cloud_err(&e);
    }

    // Set keypair in envelope manager
    if let Some(ref env_mgr) = handle.cloud_envelope_mgr {
        handle.runtime.block_on(async {
            env_mgr.lock().await.set_keypair(keypair);
        });
    }

    // 5. Return mnemonic
    let c_str = CString::new(mnemonic).unwrap();
    unsafe { *out_mnemonic = c_str.into_raw() };
    PrivStackError::Ok
}

/// Unlocks the cloud keypair using a passphrase.
///
/// Downloads the passphrase-encrypted private key from S3, decrypts it,
/// and loads the keypair into the envelope manager for sharing operations.
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

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let api = match handle.cloud_api.as_ref() {
        Some(a) => a.clone(),
        None => return PrivStackError::NotInitialized,
    };

    let config = match handle.cloud_config.as_ref() {
        Some(c) => c.clone(),
        None => return PrivStackError::NotInitialized,
    };

    let result = handle.runtime.block_on(async {
        let workspaces = api.list_workspaces().await?;
        let ws = workspaces
            .first()
            .ok_or(privstack_cloud::CloudError::Config(
                "no cloud workspace registered".to_string(),
            ))?;

        let user_id = api
            .user_id()
            .await
            .ok_or(privstack_cloud::CloudError::AuthRequired)?;

        let transport = S3Transport::new(
            config.s3_bucket.clone(),
            config.s3_region.clone(),
            config.s3_endpoint_override.clone(),
        );

        let cred_mgr = CredentialManager::new(
            api.clone(),
            ws.workspace_id.clone(),
            config.credential_refresh_margin_secs,
        );
        let creds = cred_mgr.get_credentials().await?;

        // Download passphrase-encrypted key
        let key_path = private_key_s3_key(user_id, &ws.workspace_id);
        let data = transport.download(&creds, &key_path).await?;

        let protected: privstack_crypto::PassphraseProtectedKey =
            serde_json::from_slice(&data).map_err(|e| {
                privstack_cloud::CloudError::Envelope(format!(
                    "failed to deserialize protected key: {e}"
                ))
            })?;

        // Decrypt with passphrase
        let secret_key =
            crypto_env::decrypt_private_key(&protected, passphrase_str).map_err(|e| {
                privstack_cloud::CloudError::Envelope(format!("passphrase decryption failed: {e}"))
            })?;

        let keypair = crypto_env::CloudKeyPair::from_secret_bytes(secret_key.to_bytes());
        Ok::<crypto_env::CloudKeyPair, privstack_cloud::CloudError>(keypair)
    });

    match result {
        Ok(keypair) => {
            if let Some(ref env_mgr) = handle.cloud_envelope_mgr {
                handle.runtime.block_on(async {
                    env_mgr.lock().await.set_keypair(keypair);
                });
            }
            PrivStackError::Ok
        }
        Err(e) => cloud_err(&e),
    }
}

/// Recovers the cloud keypair from a 12-word BIP39 mnemonic.
///
/// Downloads the mnemonic-encrypted recovery key from S3, decrypts it,
/// and loads the keypair into the envelope manager.
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

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let api = match handle.cloud_api.as_ref() {
        Some(a) => a.clone(),
        None => return PrivStackError::NotInitialized,
    };

    let config = match handle.cloud_config.as_ref() {
        Some(c) => c.clone(),
        None => return PrivStackError::NotInitialized,
    };

    let result = handle.runtime.block_on(async {
        let workspaces = api.list_workspaces().await?;
        let ws = workspaces
            .first()
            .ok_or(privstack_cloud::CloudError::Config(
                "no cloud workspace registered".to_string(),
            ))?;

        let user_id = api
            .user_id()
            .await
            .ok_or(privstack_cloud::CloudError::AuthRequired)?;

        let transport = S3Transport::new(
            config.s3_bucket.clone(),
            config.s3_region.clone(),
            config.s3_endpoint_override.clone(),
        );

        let cred_mgr = CredentialManager::new(
            api.clone(),
            ws.workspace_id.clone(),
            config.credential_refresh_margin_secs,
        );
        let creds = cred_mgr.get_credentials().await?;

        // Download mnemonic-encrypted recovery key
        let recovery_path = recovery_key_s3_key(user_id, &ws.workspace_id);
        let data = transport.download(&creds, &recovery_path).await?;

        let encrypted: privstack_crypto::EncryptedData =
            serde_json::from_slice(&data).map_err(|e| {
                privstack_cloud::CloudError::Envelope(format!(
                    "failed to deserialize recovery key: {e}"
                ))
            })?;

        // Decrypt with mnemonic-derived key
        let secret_key = crypto_env::decrypt_private_key_with_mnemonic(&encrypted, mnemonic_str)
            .map_err(|e| {
                privstack_cloud::CloudError::Envelope(format!("mnemonic recovery failed: {e}"))
            })?;

        let keypair = crypto_env::CloudKeyPair::from_secret_bytes(secret_key.to_bytes());
        Ok::<crypto_env::CloudKeyPair, privstack_cloud::CloudError>(keypair)
    });

    match result {
        Ok(keypair) => {
            if let Some(ref env_mgr) = handle.cloud_envelope_mgr {
                handle.runtime.block_on(async {
                    env_mgr.lock().await.set_keypair(keypair);
                });
            }
            PrivStackError::Ok
        }
        Err(e) => cloud_err(&e),
    }
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
