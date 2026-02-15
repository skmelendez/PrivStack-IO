//! Entity sharing FFI exports.

use super::{cloud_err, parse_cstr, write_json_out};
use crate::{lock_handle, PrivStackError};
use privstack_cloud::types::*;
use std::ffi::c_char;

/// Shares an entity with another user by email.
///
/// # Safety
/// - All string parameters must be valid null-terminated UTF-8 strings.
/// - `entity_name` may be null.
/// - `out_json` must be a valid pointer. Result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_share_entity(
    entity_id: *const c_char,
    entity_type: *const c_char,
    entity_name: *const c_char,
    workspace_id: *const c_char,
    recipient_email: *const c_char,
    permission: *const c_char,
    out_json: *mut *mut c_char,
) -> PrivStackError {
    let eid = match unsafe { parse_cstr(entity_id) } {
        Ok(s) => s.to_string(),
        Err(e) => return e,
    };
    let etype = match unsafe { parse_cstr(entity_type) } {
        Ok(s) => s.to_string(),
        Err(e) => return e,
    };
    let ename = if entity_name.is_null() {
        None
    } else {
        match unsafe { parse_cstr(entity_name) } {
            Ok(s) => Some(s.to_string()),
            Err(e) => return e,
        }
    };
    let ws_id = match unsafe { parse_cstr(workspace_id) } {
        Ok(s) => s.to_string(),
        Err(e) => return e,
    };
    let email = match unsafe { parse_cstr(recipient_email) } {
        Ok(s) => s.to_string(),
        Err(e) => return e,
    };
    let perm_str = match unsafe { parse_cstr(permission) } {
        Ok(s) => s,
        Err(e) => return e,
    };
    if out_json.is_null() {
        return PrivStackError::NullPointer;
    }

    let perm = match perm_str {
        "read" => SharePermission::Read,
        "write" => SharePermission::Write,
        _ => return PrivStackError::InvalidArgument,
    };

    let handle = lock_handle();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let share_mgr = match handle.cloud_share_mgr.as_ref() {
        Some(m) => m.clone(),
        None => return PrivStackError::NotInitialized,
    };

    let req = CreateShareRequest {
        entity_id: eid,
        entity_type: etype,
        entity_name: ename,
        workspace_id: ws_id,
        recipient_email: email,
        permission: perm,
    };

    match handle.runtime.block_on(share_mgr.create_share(&req)) {
        Ok(info) => write_json_out(out_json, &info),
        Err(e) => cloud_err(&e),
    }
}

/// Revokes a share for an entity.
///
/// # Safety
/// - `entity_id`, `recipient_email` must be valid null-terminated UTF-8 strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_revoke_share(
    entity_id: *const c_char,
    recipient_email: *const c_char,
) -> PrivStackError {
    let eid = match unsafe { parse_cstr(entity_id) } {
        Ok(s) => s,
        Err(e) => return e,
    };
    let email = match unsafe { parse_cstr(recipient_email) } {
        Ok(s) => s,
        Err(e) => return e,
    };

    let handle = lock_handle();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let share_mgr = match handle.cloud_share_mgr.as_ref() {
        Some(m) => m.clone(),
        None => return PrivStackError::NotInitialized,
    };

    match handle.runtime.block_on(share_mgr.revoke_share(eid, email)) {
        Ok(()) => PrivStackError::Ok,
        Err(e) => cloud_err(&e),
    }
}

/// Accepts a share invitation by token.
///
/// # Safety
/// - `invitation_token` must be a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_accept_share(
    invitation_token: *const c_char,
) -> PrivStackError {
    let token = match unsafe { parse_cstr(invitation_token) } {
        Ok(s) => s,
        Err(e) => return e,
    };

    let handle = lock_handle();
    let handle = match handle.as_ref() {
        Some(h) => h,
        None => return PrivStackError::NotInitialized,
    };

    let share_mgr = match handle.cloud_share_mgr.as_ref() {
        Some(m) => m.clone(),
        None => return PrivStackError::NotInitialized,
    };

    match handle.runtime.block_on(share_mgr.accept_share(token)) {
        Ok(()) => PrivStackError::Ok,
        Err(e) => cloud_err(&e),
    }
}

/// Lists shares for an entity owned by the current user.
///
/// # Safety
/// - `entity_id` must be a valid null-terminated UTF-8 string.
/// - `out_json` must be a valid pointer. Result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_list_entity_shares(
    entity_id: *const c_char,
    out_json: *mut *mut c_char,
) -> PrivStackError {
    let eid = match unsafe { parse_cstr(entity_id) } {
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

    let share_mgr = match handle.cloud_share_mgr.as_ref() {
        Some(m) => m.clone(),
        None => return PrivStackError::NotInitialized,
    };

    match handle.runtime.block_on(share_mgr.get_entity_shares(eid)) {
        Ok(shares) => write_json_out(out_json, &shares),
        Err(e) => cloud_err(&e),
    }
}

/// Gets entities shared with the current user.
///
/// # Safety
/// - `out_json` must be a valid pointer. Result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_get_shared_with_me(
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

    let share_mgr = match handle.cloud_share_mgr.as_ref() {
        Some(m) => m.clone(),
        None => return PrivStackError::NotInitialized,
    };

    match handle.runtime.block_on(share_mgr.get_shared_with_me()) {
        Ok(shared) => write_json_out(out_json, &shared),
        Err(e) => cloud_err(&e),
    }
}
