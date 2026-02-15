//! Cloud workspace CRUD FFI exports.

use super::{cloud_err, parse_cstr, write_json_out};
use crate::{lock_handle, PrivStackError};
use std::ffi::c_char;

/// Registers a workspace for cloud sync.
///
/// # Safety
/// - `workspace_id`, `name` must be valid null-terminated UTF-8 strings.
/// - `out_json` must be a valid pointer. Result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_register_workspace(
    workspace_id: *const c_char,
    name: *const c_char,
    out_json: *mut *mut c_char,
) -> PrivStackError {
    let ws_id = match unsafe { parse_cstr(workspace_id) } {
        Ok(s) => s,
        Err(e) => return e,
    };
    let ws_name = match unsafe { parse_cstr(name) } {
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

    match handle.runtime.block_on(api.register_workspace(ws_id, ws_name)) {
        Ok(ws) => write_json_out(out_json, &ws),
        Err(e) => cloud_err(&e),
    }
}

/// Lists cloud-registered workspaces for the authenticated user.
///
/// # Safety
/// - `out_json` must be a valid pointer. Result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_list_workspaces(
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

    match handle.runtime.block_on(api.list_workspaces()) {
        Ok(workspaces) => write_json_out(out_json, &workspaces),
        Err(e) => cloud_err(&e),
    }
}

/// Deletes a cloud workspace registration.
///
/// # Safety
/// - `workspace_id` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_delete_workspace(
    workspace_id: *const c_char,
) -> PrivStackError {
    let ws_id = match unsafe { parse_cstr(workspace_id) } {
        Ok(s) => s,
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

    match handle.runtime.block_on(api.delete_workspace(ws_id)) {
        Ok(()) => PrivStackError::Ok,
        Err(e) => cloud_err(&e),
    }
}

/// Gets storage quota info for a workspace.
///
/// # Safety
/// - `workspace_id` must be a valid null-terminated UTF-8 string.
/// - `out_json` must be a valid pointer. Result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_get_quota(
    workspace_id: *const c_char,
    out_json: *mut *mut c_char,
) -> PrivStackError {
    let ws_id = match unsafe { parse_cstr(workspace_id) } {
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

    match handle.runtime.block_on(api.get_quota(ws_id)) {
        Ok(quota) => write_json_out(out_json, &quota),
        Err(e) => cloud_err(&e),
    }
}
