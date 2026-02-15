//! Blob upload/download FFI exports for cloud sync.
//!
//! Binary data is passed via pointer + length. DEKs are 32-byte keys.
//! Downloaded blob data must be freed via `privstack_cloudsync_free_blob_data`.

use super::{cloud_err, parse_cstr, write_json_out};
use crate::{PrivStackError, HANDLE};
use privstack_crypto::DerivedKey;
use std::ffi::c_char;

/// Uploads a blob encrypted with the provided DEK.
///
/// # Safety
/// - `workspace_id`, `blob_id` must be valid null-terminated UTF-8 strings.
/// - `entity_id` may be null (optional association).
/// - `data_ptr` must point to `data_len` valid bytes.
/// - `dek_ptr` must point to exactly 32 bytes (DEK).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_upload_blob(
    workspace_id: *const c_char,
    blob_id: *const c_char,
    entity_id: *const c_char,
    data_ptr: *const u8,
    data_len: usize,
    dek_ptr: *const u8,
) -> PrivStackError {
    let ws_id = match unsafe { parse_cstr(workspace_id) } {
        Ok(s) => s,
        Err(e) => return e,
    };
    let b_id = match unsafe { parse_cstr(blob_id) } {
        Ok(s) => s,
        Err(e) => return e,
    };
    let ent_id = if entity_id.is_null() {
        None
    } else {
        match unsafe { parse_cstr(entity_id) } {
            Ok(s) => Some(s),
            Err(e) => return e,
        }
    };
    if data_ptr.is_null() || dek_ptr.is_null() {
        return PrivStackError::NullPointer;
    }

    let data = unsafe { std::slice::from_raw_parts(data_ptr, data_len) };
    let dek_bytes: [u8; 32] = unsafe { std::ptr::read(dek_ptr as *const [u8; 32]) };
    let dek = DerivedKey::from_bytes(dek_bytes);

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let blob_mgr = match handle.cloud_blob_mgr.as_ref() {
        Some(m) => m.clone(),
        None => return PrivStackError::SyncNotRunning,
    };

    let user_id = match handle.cloud_user_id {
        Some(id) => id,
        None => return PrivStackError::CloudAuthError,
    };

    match handle.runtime.block_on(blob_mgr.upload_blob(
        user_id,
        ws_id,
        b_id,
        ent_id,
        data,
        &dek,
    )) {
        Ok(()) => PrivStackError::Ok,
        Err(e) => cloud_err(&e),
    }
}

/// Downloads and decrypts a blob.
///
/// On success, `*out_ptr` and `*out_len` are set to the decrypted data.
/// The caller must free the data via `privstack_cloudsync_free_blob_data`.
///
/// # Safety
/// - `s3_key` must be a valid null-terminated UTF-8 string.
/// - `dek_ptr` must point to exactly 32 bytes (DEK).
/// - `out_ptr` and `out_len` must be valid pointers.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_download_blob(
    s3_key: *const c_char,
    dek_ptr: *const u8,
    out_ptr: *mut *mut u8,
    out_len: *mut usize,
) -> PrivStackError {
    let key = match unsafe { parse_cstr(s3_key) } {
        Ok(s) => s,
        Err(e) => return e,
    };
    if dek_ptr.is_null() || out_ptr.is_null() || out_len.is_null() {
        return PrivStackError::NullPointer;
    }

    let dek_bytes: [u8; 32] = unsafe { std::ptr::read(dek_ptr as *const [u8; 32]) };
    let dek = DerivedKey::from_bytes(dek_bytes);

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let blob_mgr = match handle.cloud_blob_mgr.as_ref() {
        Some(m) => m.clone(),
        None => return PrivStackError::SyncNotRunning,
    };

    match handle.runtime.block_on(blob_mgr.download_blob(key, &dek)) {
        Ok(data) => {
            let len = data.len();
            let boxed = data.into_boxed_slice();
            let ptr = Box::into_raw(boxed) as *mut u8;
            unsafe {
                *out_ptr = ptr;
                *out_len = len;
            }
            PrivStackError::Ok
        }
        Err(e) => cloud_err(&e),
    }
}

/// Frees blob data allocated by `privstack_cloudsync_download_blob`.
///
/// # Safety
/// - `ptr` must have been returned by `privstack_cloudsync_download_blob`.
/// - `len` must match the length returned alongside the pointer.
/// - Must not be called more than once for the same pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_free_blob_data(ptr: *mut u8, len: usize) {
    if !ptr.is_null() && len > 0 {
        let _ = unsafe { Box::from_raw(std::slice::from_raw_parts_mut(ptr, len)) };
    }
}

/// Gets blob metadata for an entity as JSON.
///
/// # Safety
/// - `entity_id` must be a valid null-terminated UTF-8 string.
/// - `out_json` must be a valid pointer. Result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_get_entity_blobs(
    entity_id: *const c_char,
    out_json: *mut *mut c_char,
) -> PrivStackError {
    let ent_id = match unsafe { parse_cstr(entity_id) } {
        Ok(s) => s,
        Err(e) => return e,
    };
    if out_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let handle = HANDLE.lock().unwrap();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let blob_mgr = match handle.cloud_blob_mgr.as_ref() {
        Some(m) => m.clone(),
        None => return PrivStackError::SyncNotRunning,
    };

    match handle.runtime.block_on(blob_mgr.get_entity_blobs(ent_id)) {
        Ok(blobs) => write_json_out(out_json, &blobs),
        Err(e) => cloud_err(&e),
    }
}
