//! JNI bridge for Android. Maps Kotlin `NativeBridge` external functions
//! to the existing `extern "C"` FFI exports.
//!
//! Each submodule corresponds to a domain in the NativeBridge class.
//! All functions follow the naming convention:
//!   `Java_com_privstack_bridge_NativeBridge_<methodName>`

mod core;
mod auth;
mod vault;
mod sync;
mod pairing;
mod cloud;
mod misc;

use jni::JNIEnv;
use jni::objects::JString;
use jni::sys::jstring;
use std::ffi::{CStr, CString, c_char};

/// Convert a JNI JString to a CString for passing to C FFI functions.
/// Returns None if the string is null or invalid UTF-8.
pub(crate) fn jstring_to_cstring(env: &mut JNIEnv, input: &JString) -> Option<CString> {
    if input.is_null() {
        return None;
    }
    let java_str = env.get_string(input).ok()?;
    let rust_str: String = java_str.into();
    CString::new(rust_str).ok()
}

/// Convert an allocated `*mut c_char` (from Rust FFI) to a JNI jstring,
/// then free the Rust allocation via `privstack_free_string`.
///
/// # Safety
/// `ptr` must have been allocated by the FFI layer (CString::into_raw).
pub(crate) unsafe fn owned_cptr_to_jstring(env: &mut JNIEnv, ptr: *mut c_char) -> jstring {
    if ptr.is_null() {
        return empty_jstring(env);
    }
    // SAFETY: ptr was allocated by CString::into_raw in the FFI layer.
    let c_str = unsafe { CStr::from_ptr(ptr) };
    let result = match c_str.to_str() {
        Ok(s) => env.new_string(s).map(|js| js.into_raw()).unwrap_or(std::ptr::null_mut()),
        Err(_) => empty_jstring(env),
    };
    // Free the Rust-allocated string.
    unsafe { super::privstack_free_string(ptr) };
    result
}

/// Convert a static `*const c_char` to a JNI jstring. Does NOT free the pointer.
pub(crate) fn static_cptr_to_jstring(env: &mut JNIEnv, ptr: *const c_char) -> jstring {
    if ptr.is_null() {
        return empty_jstring(env);
    }
    // SAFETY: ptr points to a static string with a null terminator.
    let c_str = unsafe { CStr::from_ptr(ptr) };
    match c_str.to_str() {
        Ok(s) => env.new_string(s).map(|js| js.into_raw()).unwrap_or(std::ptr::null_mut()),
        Err(_) => empty_jstring(env),
    }
}

/// Return an empty Java string.
pub(crate) fn empty_jstring(env: &mut JNIEnv) -> jstring {
    env.new_string("").map(|js| js.into_raw()).unwrap_or(std::ptr::null_mut())
}
