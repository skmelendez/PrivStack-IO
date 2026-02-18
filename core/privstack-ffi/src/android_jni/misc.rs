//! JNI wrappers for license and device functions.

use jni::JNIEnv;
use jni::objects::{JClass, JString};
use jni::sys::{jboolean, jint, jstring, JNI_FALSE, JNI_TRUE};

use super::{jstring_to_cstring, empty_jstring};

// ── License ──────────────────────────────────────────────────────────────

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackLicenseParse(
    mut env: JNIEnv,
    _class: JClass,
    key: JString,
) -> jstring {
    let c_key = match jstring_to_cstring(&mut env, &key) {
        Some(s) => s,
        None => return empty_jstring(&mut env),
    };
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::privstack_license_parse(c_key.as_ptr(), &mut out) };
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(&mut env);
    }
    unsafe { super::owned_cptr_to_jstring(&mut env, out) }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackLicenseActivate(
    mut env: JNIEnv,
    _class: JClass,
    key: JString,
) -> jstring {
    let c_key = match jstring_to_cstring(&mut env, &key) {
        Some(s) => s,
        None => return empty_jstring(&mut env),
    };
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::privstack_license_activate(c_key.as_ptr(), &mut out) };
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(&mut env);
    }
    unsafe { super::owned_cptr_to_jstring(&mut env, out) }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackLicenseCheck(
    mut env: JNIEnv,
    _class: JClass,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::privstack_license_check(&mut out) };
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(&mut env);
    }
    unsafe { super::owned_cptr_to_jstring(&mut env, out) }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackLicenseIsValid(
    _env: JNIEnv,
    _class: JClass,
) -> jboolean {
    if crate::privstack_license_is_valid() { JNI_TRUE } else { JNI_FALSE }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackLicenseStatus(
    _env: JNIEnv,
    _class: JClass,
) -> jint {
    let mut out = crate::FfiLicenseStatus::NotActivated;
    unsafe { crate::privstack_license_status(&mut out) };
    out as jint
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackLicenseDeactivate(
    _env: JNIEnv,
    _class: JClass,
) -> jint {
    crate::privstack_license_deactivate() as jint
}

// ── Device ───────────────────────────────────────────────────────────────

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackDeviceInfo(
    mut env: JNIEnv,
    _class: JClass,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::privstack_device_info(&mut out) };
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(&mut env);
    }
    unsafe { super::owned_cptr_to_jstring(&mut env, out) }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackDeviceFingerprint(
    mut env: JNIEnv,
    _class: JClass,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::privstack_device_fingerprint(&mut out) };
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(&mut env);
    }
    unsafe { super::owned_cptr_to_jstring(&mut env, out) }
}
