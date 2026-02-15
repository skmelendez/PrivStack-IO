//! Device registration FFI exports.

use super::{cloud_err, parse_cstr, write_json_out};
use crate::{lock_handle, PrivStackError};
use std::ffi::c_char;

/// Registers the current device for cloud sync.
///
/// # Safety
/// - `name`, `platform` must be valid null-terminated UTF-8 strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_register_device(
    name: *const c_char,
    platform: *const c_char,
) -> PrivStackError {
    let dev_name = match unsafe { parse_cstr(name) } {
        Ok(s) => s,
        Err(e) => return e,
    };
    let dev_platform = match unsafe { parse_cstr(platform) } {
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

    let device_id = handle.peer_id.to_string();

    match handle
        .runtime
        .block_on(api.register_device(dev_name, dev_platform, &device_id))
    {
        Ok(()) => PrivStackError::Ok,
        Err(e) => cloud_err(&e),
    }
}

/// Lists registered devices for the authenticated user.
///
/// # Safety
/// - `out_json` must be a valid pointer. Result must be freed with `privstack_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_cloudsync_list_devices(
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

    match handle.runtime.block_on(api.list_devices()) {
        Ok(devices) => write_json_out(out_json, &devices),
        Err(e) => cloud_err(&e),
    }
}
