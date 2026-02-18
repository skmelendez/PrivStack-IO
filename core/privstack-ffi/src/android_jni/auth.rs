//! JNI wrappers for authentication functions.

use jni::JNIEnv;
use jni::objects::{JClass, JString};
use jni::sys::{jboolean, jint, jstring, JNI_FALSE, JNI_TRUE};

use super::{jstring_to_cstring, empty_jstring};

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackAuthIsInitialized(
    _env: JNIEnv,
    _class: JClass,
) -> jboolean {
    if crate::privstack_auth_is_initialized() { JNI_TRUE } else { JNI_FALSE }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackAuthIsUnlocked(
    _env: JNIEnv,
    _class: JClass,
) -> jboolean {
    if crate::privstack_auth_is_unlocked() { JNI_TRUE } else { JNI_FALSE }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackAuthInitialize(
    mut env: JNIEnv,
    _class: JClass,
    password: JString,
) -> jint {
    let c_pw = match jstring_to_cstring(&mut env, &password) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    unsafe { crate::privstack_auth_initialize(c_pw.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackAuthUnlock(
    mut env: JNIEnv,
    _class: JClass,
    password: JString,
) -> jint {
    let c_pw = match jstring_to_cstring(&mut env, &password) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    unsafe { crate::privstack_auth_unlock(c_pw.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackAuthLock(
    _env: JNIEnv,
    _class: JClass,
) -> jint {
    crate::privstack_auth_lock() as jint
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackAuthChangePassword(
    mut env: JNIEnv,
    _class: JClass,
    current_password: JString,
    new_password: JString,
) -> jint {
    let c_current = match jstring_to_cstring(&mut env, &current_password) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    let c_new = match jstring_to_cstring(&mut env, &new_password) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    unsafe { crate::privstack_auth_change_password(c_current.as_ptr(), c_new.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackAuthSetupRecovery(
    mut env: JNIEnv,
    _class: JClass,
    _password: JString,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::privstack_auth_setup_recovery(&mut out) };
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(&mut env);
    }
    unsafe { super::owned_cptr_to_jstring(&mut env, out) }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackAuthHasRecovery(
    _env: JNIEnv,
    _class: JClass,
) -> jboolean {
    if crate::privstack_auth_has_recovery() { JNI_TRUE } else { JNI_FALSE }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackAuthResetWithRecovery(
    mut env: JNIEnv,
    _class: JClass,
    mnemonic: JString,
    new_password: JString,
) -> jint {
    let c_mnemonic = match jstring_to_cstring(&mut env, &mnemonic) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    let c_new = match jstring_to_cstring(&mut env, &new_password) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    unsafe {
        crate::privstack_auth_reset_with_recovery(c_mnemonic.as_ptr(), c_new.as_ptr()) as jint
    }
}
