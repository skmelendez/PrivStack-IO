//! FFI exports for PrivStack Cloud Sync (S3-backed multi-device sync + sharing).
//!
//! All functions use the `privstack_cloudsync_` prefix to distinguish from
//! the existing `privstack_cloud_` functions (Google Drive / iCloud file sync).
//!
//! Pattern: acquire HANDLE → validate inputs → block_on async call → write JSON to out pointer.

mod auth;
mod blobs;
mod devices;
mod sharing;
mod sync;
mod workspaces;

use crate::PrivStackError;
use privstack_cloud::CloudError;
use std::ffi::{c_char, CStr, CString};

/// Converts a `CloudError` to the appropriate FFI error code.
pub(crate) fn cloud_err(e: &CloudError) -> PrivStackError {
    eprintln!("[FFI cloud_err] {e}");
    match e {
        CloudError::AuthRequired | CloudError::AuthFailed(_) => PrivStackError::CloudAuthError,
        CloudError::QuotaExceeded { .. } => PrivStackError::QuotaExceeded,
        CloudError::ShareDenied(_) => PrivStackError::ShareDenied,
        CloudError::Envelope(_) => PrivStackError::EnvelopeError,
        CloudError::CredentialExpired => PrivStackError::CloudSyncError,
        CloudError::LockContention(_) => PrivStackError::CloudSyncError,
        _ => PrivStackError::CloudSyncError,
    }
}

/// Helper: parse a C string pointer to &str.
pub(crate) unsafe fn parse_cstr<'a>(ptr: *const c_char) -> Result<&'a str, PrivStackError> {
    if ptr.is_null() {
        return Err(PrivStackError::NullPointer);
    }
    unsafe { CStr::from_ptr(ptr).to_str().map_err(|_| PrivStackError::InvalidUtf8) }
}

/// Helper: write a JSON-serializable value to an out pointer.
pub(crate) fn write_json_out(
    out: *mut *mut c_char,
    value: &impl serde::Serialize,
) -> PrivStackError {
    match serde_json::to_string(value) {
        Ok(json) => {
            let c_json = CString::new(json).unwrap();
            unsafe { *out = c_json.into_raw() };
            PrivStackError::Ok
        }
        Err(_) => PrivStackError::JsonError,
    }
}
