//! Cloud sync engine start/stop/status/flush FFI exports.

use super::{parse_cstr, write_json_out};
use crate::{lock_handle, PrivStackError, HANDLE};
use privstack_cloud::blob_sync::BlobSyncManager;
use privstack_cloud::credential_manager::CredentialManager;
use privstack_cloud::s3_transport::S3Transport;
use privstack_cloud::sync_engine;
use privstack_cloud::types::*;
use std::ffi::c_char;
use std::sync::Arc;
use std::time::Duration;

/// Starts the cloud sync engine for a workspace.
///
/// Creates the S3 transport, credential manager, and sync engine, then
/// spawns the background event loop.
///
/// # Safety
/// - `workspace_id` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_start_sync(
    workspace_id: *const c_char,
) -> PrivStackError {
    let ws_id = match unsafe { parse_cstr(workspace_id) } {
        Ok(s) => s.to_string(),
        Err(e) => return e,
    };

    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    if handle.cloud_sync_handle.is_some() {
        return PrivStackError::SyncAlreadyRunning;
    }

    let api = match handle.cloud_api.as_ref() {
        Some(a) => a.clone(),
        None => return PrivStackError::NotInitialized,
    };

    let config = match handle.cloud_config.as_ref() {
        Some(c) => c.clone(),
        None => return PrivStackError::NotInitialized,
    };

    let user_id = match handle.runtime.block_on(api.user_id()) {
        Some(id) => id,
        None => return PrivStackError::CloudAuthError,
    };

    let transport = Arc::new(S3Transport::new(
        config.s3_bucket.clone(),
        config.s3_region.clone(),
        config.s3_endpoint_override.clone(),
    ));

    let cred_manager = Arc::new(CredentialManager::new(
        api.clone(),
        ws_id.clone(),
        config.credential_refresh_margin_secs,
    ));

    // Initialize blob sync manager (shares transport + cred_manager with sync engine)
    let blob_mgr = Arc::new(BlobSyncManager::new(
        api.clone(),
        transport.clone(),
        cred_manager.clone(),
    ));

    let (event_tx, _event_rx) = tokio::sync::mpsc::channel(256);

    let (sync_handle, mut engine) = sync_engine::create_cloud_sync_engine(
        api,
        transport,
        cred_manager,
        event_tx,
        user_id,
        ws_id,
        handle.peer_id.to_string(),
        Duration::from_secs(config.poll_interval_secs),
    );

    handle.runtime.spawn(async move {
        engine.run().await;
    });

    handle.cloud_sync_handle = Some(sync_handle);
    handle.cloud_blob_mgr = Some(blob_mgr);
    handle.cloud_user_id = Some(user_id);
    PrivStackError::Ok
}

/// Stops the cloud sync engine.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_cloudsync_stop_sync() -> PrivStackError {
    let mut handle = HANDLE.lock().unwrap();
    let handle = match handle.as_mut() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let sync_handle = match handle.cloud_sync_handle.take() {
        Some(h) => h,
        None => return PrivStackError::SyncNotRunning,
    };

    handle.cloud_blob_mgr = None;
    handle.cloud_user_id = None;

    match handle.runtime.block_on(sync_handle.stop()) {
        Ok(()) => PrivStackError::Ok,
        Err(_) => PrivStackError::CloudSyncError,
    }
}

/// Returns whether cloud sync is currently running.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_cloudsync_is_syncing() -> bool {
    let handle = lock_handle();
    match handle.as_ref() {
        Some(h) => h.cloud_sync_handle.is_some(),
        None => false,
    }
}

/// Gets the current cloud sync status as JSON.
///
/// # Safety
/// - `out_json` must be a valid pointer. Result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_get_status(
    out_json: *mut *mut c_char,
) -> PrivStackError {
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

    let is_syncing = handle.cloud_sync_handle.is_some();
    let is_authenticated = handle.runtime.block_on(api.is_authenticated());

    let status = CloudSyncStatus {
        is_syncing,
        is_authenticated,
        active_workspace: None, // TODO: track active workspace
        pending_upload_count: 0,
        last_sync_at: None,
        connected_devices: 0,
    };

    write_json_out(out_json, &status)
}

/// Forces an immediate outbox flush.
#[unsafe(no_mangle)]
pub extern "C" fn privstack_cloudsync_force_flush() -> PrivStackError {
    let handle = lock_handle();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let sync_handle = match handle.cloud_sync_handle.as_ref() {
        Some(h) => h.clone(),
        None => return PrivStackError::SyncNotRunning,
    };

    match handle.runtime.block_on(sync_handle.force_flush()) {
        Ok(()) => PrivStackError::Ok,
        Err(_) => PrivStackError::CloudSyncError,
    }
}
