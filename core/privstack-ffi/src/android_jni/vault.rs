//! JNI wrappers for vault and blob operations.

use jni::JNIEnv;
use jni::objects::{JByteArray, JClass, JString};
use jni::sys::{jboolean, jbyteArray, jint, jstring, JNI_FALSE, JNI_TRUE};
use std::ffi::CString;

use super::{jstring_to_cstring, empty_jstring};

// ── Vault Lifecycle ──────────────────────────────────────────────────────

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackVaultCreate(
    mut env: JNIEnv,
    _class: JClass,
    name: JString,
    _password: JString,
) -> jstring {
    let c_name = match jstring_to_cstring(&mut env, &name) {
        Some(s) => s,
        None => return empty_jstring(&mut env),
    };
    let err = unsafe { crate::privstack_vault_create(c_name.as_ptr()) };
    let json = format!(r#"{{"error_code":{}}}"#, err as i32);
    env.new_string(&json).map(|js| js.into_raw()).unwrap_or(std::ptr::null_mut())
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackVaultInitialize(
    mut env: JNIEnv,
    _class: JClass,
    vault_id: JString,
    password: JString,
) -> jint {
    let c_vid = match jstring_to_cstring(&mut env, &vault_id) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    let c_pw = match jstring_to_cstring(&mut env, &password) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    unsafe { crate::privstack_vault_initialize(c_vid.as_ptr(), c_pw.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackVaultUnlock(
    mut env: JNIEnv,
    _class: JClass,
    vault_id: JString,
    password: JString,
) -> jint {
    let c_vid = match jstring_to_cstring(&mut env, &vault_id) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    let c_pw = match jstring_to_cstring(&mut env, &password) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    unsafe { crate::privstack_vault_unlock(c_vid.as_ptr(), c_pw.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackVaultLock(
    mut env: JNIEnv,
    _class: JClass,
    vault_id: JString,
) -> jint {
    let c_vid = match jstring_to_cstring(&mut env, &vault_id) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    unsafe { crate::privstack_vault_lock(c_vid.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackVaultLockAll(
    _env: JNIEnv,
    _class: JClass,
) -> jint {
    crate::privstack_vault_lock_all() as jint
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackVaultIsInitialized(
    mut env: JNIEnv,
    _class: JClass,
    vault_id: JString,
) -> jboolean {
    let c_vid = match jstring_to_cstring(&mut env, &vault_id) {
        Some(s) => s,
        None => return JNI_FALSE,
    };
    if unsafe { crate::privstack_vault_is_initialized(c_vid.as_ptr()) } { JNI_TRUE } else { JNI_FALSE }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackVaultIsUnlocked(
    mut env: JNIEnv,
    _class: JClass,
    vault_id: JString,
) -> jboolean {
    let c_vid = match jstring_to_cstring(&mut env, &vault_id) {
        Some(s) => s,
        None => return JNI_FALSE,
    };
    if unsafe { crate::privstack_vault_is_unlocked(c_vid.as_ptr()) } { JNI_TRUE } else { JNI_FALSE }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackVaultChangePassword(
    mut env: JNIEnv,
    _class: JClass,
    vault_id: JString,
    current_password: JString,
    new_password: JString,
) -> jint {
    let c_vid = match jstring_to_cstring(&mut env, &vault_id) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    let c_cur = match jstring_to_cstring(&mut env, &current_password) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    let c_new = match jstring_to_cstring(&mut env, &new_password) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    unsafe {
        crate::privstack_vault_change_password(c_vid.as_ptr(), c_cur.as_ptr(), c_new.as_ptr()) as jint
    }
}

// ── Blob Store (unencrypted, "default" namespace) ────────────────────────

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackBlobStore(
    mut env: JNIEnv,
    _class: JClass,
    blob_id: JString,
    data: JByteArray,
) -> jint {
    let c_bid = match jstring_to_cstring(&mut env, &blob_id) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    let bytes = match env.convert_byte_array(&data) {
        Ok(b) => b,
        Err(_) => return crate::PrivStackError::InvalidArgument as jint,
    };
    let c_ns = CString::new("default").unwrap();
    unsafe {
        crate::privstack_blob_store(
            c_ns.as_ptr(),
            c_bid.as_ptr(),
            bytes.as_ptr(),
            bytes.len(),
            std::ptr::null(),
        ) as jint
    }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackBlobRead(
    mut env: JNIEnv,
    _class: JClass,
    blob_id: JString,
) -> jbyteArray {
    let c_bid = match jstring_to_cstring(&mut env, &blob_id) {
        Some(s) => s,
        None => return std::ptr::null_mut(),
    };
    let c_ns = CString::new("default").unwrap();
    let mut out_data: *mut u8 = std::ptr::null_mut();
    let mut out_len: usize = 0;
    let err = unsafe {
        crate::privstack_blob_read(c_ns.as_ptr(), c_bid.as_ptr(), &mut out_data, &mut out_len)
    };
    if err != crate::PrivStackError::Ok || out_data.is_null() {
        return std::ptr::null_mut();
    }
    // SAFETY: out_data was allocated by the FFI layer.
    let slice = unsafe { std::slice::from_raw_parts(out_data, out_len) };
    let result = env.byte_array_from_slice(slice)
        .map(|ba| ba.into_raw())
        .unwrap_or(std::ptr::null_mut());
    unsafe { crate::privstack_free_bytes(out_data, out_len) };
    result
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackBlobDelete(
    mut env: JNIEnv,
    _class: JClass,
    blob_id: JString,
) -> jint {
    let c_bid = match jstring_to_cstring(&mut env, &blob_id) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    let c_ns = CString::new("default").unwrap();
    unsafe { crate::privstack_blob_delete(c_ns.as_ptr(), c_bid.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackBlobList(
    mut env: JNIEnv,
    _class: JClass,
) -> jstring {
    let c_ns = CString::new("default").unwrap();
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::privstack_blob_list(c_ns.as_ptr(), &mut out) };
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(&mut env);
    }
    unsafe { super::owned_cptr_to_jstring(&mut env, out) }
}
