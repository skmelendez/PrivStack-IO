//! JNI wrappers for pairing functions.

use jni::JNIEnv;
use jni::objects::{JClass, JString};
use jni::sys::{jboolean, jint, jstring, JNI_FALSE, JNI_TRUE};

use super::{jstring_to_cstring, empty_jstring};

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackPairingGenerateCode(
    mut env: JNIEnv,
    _class: JClass,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::privstack_pairing_generate_code(&mut out) };
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(&mut env);
    }
    unsafe { super::owned_cptr_to_jstring(&mut env, out) }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackPairingJoinCode(
    mut env: JNIEnv,
    _class: JClass,
    code: JString,
) -> jint {
    let c_code = match jstring_to_cstring(&mut env, &code) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    unsafe { crate::privstack_pairing_join_code(c_code.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackPairingGetCode(
    mut env: JNIEnv,
    _class: JClass,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::privstack_pairing_get_code(&mut out) };
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(&mut env);
    }
    unsafe { super::owned_cptr_to_jstring(&mut env, out) }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackPairingSetCode(
    mut env: JNIEnv,
    _class: JClass,
    code: JString,
) -> jint {
    let c_code = match jstring_to_cstring(&mut env, &code) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    unsafe { crate::privstack_pairing_set_code(c_code.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackPairingClearCode(
    _env: JNIEnv,
    _class: JClass,
) -> jint {
    crate::privstack_pairing_clear_code() as jint
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackPairingGetTrustedPeers(
    mut env: JNIEnv,
    _class: JClass,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::privstack_pairing_get_trusted_peers(&mut out) };
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(&mut env);
    }
    unsafe { super::owned_cptr_to_jstring(&mut env, out) }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackPairingGetDiscoveredPeers(
    mut env: JNIEnv,
    _class: JClass,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::privstack_pairing_get_discovered_peers(&mut out) };
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(&mut env);
    }
    unsafe { super::owned_cptr_to_jstring(&mut env, out) }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackPairingListPeers(
    mut env: JNIEnv,
    _class: JClass,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::privstack_pairing_list_peers(&mut out) };
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(&mut env);
    }
    unsafe { super::owned_cptr_to_jstring(&mut env, out) }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackPairingTrustPeer(
    mut env: JNIEnv,
    _class: JClass,
    peer_id: JString,
    device_name: JString,
) -> jint {
    let c_pid = match jstring_to_cstring(&mut env, &peer_id) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    let c_name = match jstring_to_cstring(&mut env, &device_name) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    unsafe { crate::privstack_pairing_trust_peer(c_pid.as_ptr(), c_name.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackPairingRemovePeer(
    mut env: JNIEnv,
    _class: JClass,
    peer_id: JString,
) -> jint {
    let c_pid = match jstring_to_cstring(&mut env, &peer_id) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    unsafe { crate::privstack_pairing_remove_peer(c_pid.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackPairingRemoveTrustedPeer(
    mut env: JNIEnv,
    _class: JClass,
    peer_id: JString,
) -> jint {
    let c_pid = match jstring_to_cstring(&mut env, &peer_id) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    unsafe { crate::privstack_pairing_remove_trusted_peer(c_pid.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackPairingIsTrusted(
    mut env: JNIEnv,
    _class: JClass,
    peer_id: JString,
) -> jboolean {
    let c_pid = match jstring_to_cstring(&mut env, &peer_id) {
        Some(s) => s,
        None => return JNI_FALSE,
    };
    if unsafe { crate::privstack_pairing_is_trusted(c_pid.as_ptr()) } { JNI_TRUE } else { JNI_FALSE }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackPairingSaveState(
    mut env: JNIEnv,
    _class: JClass,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::privstack_pairing_save_state(&mut out) };
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(&mut env);
    }
    unsafe { super::owned_cptr_to_jstring(&mut env, out) }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackPairingLoadState(
    mut env: JNIEnv,
    _class: JClass,
    json: JString,
) -> jint {
    let c_json = match jstring_to_cstring(&mut env, &json) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    unsafe { crate::privstack_pairing_load_state(c_json.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackPairingGetDeviceName(
    mut env: JNIEnv,
    _class: JClass,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::privstack_pairing_get_device_name(&mut out) };
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(&mut env);
    }
    unsafe { super::owned_cptr_to_jstring(&mut env, out) }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackPairingSetDeviceName(
    mut env: JNIEnv,
    _class: JClass,
    name: JString,
) -> jint {
    let c_name = match jstring_to_cstring(&mut env, &name) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    unsafe { crate::privstack_pairing_set_device_name(c_name.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackPairingApprovePeer(
    mut env: JNIEnv,
    _class: JClass,
    peer_id: JString,
) -> jint {
    let c_pid = match jstring_to_cstring(&mut env, &peer_id) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    unsafe { crate::privstack_pairing_approve_peer(c_pid.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackPairingRejectPeer(
    mut env: JNIEnv,
    _class: JClass,
    peer_id: JString,
) -> jint {
    let c_pid = match jstring_to_cstring(&mut env, &peer_id) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    unsafe { crate::privstack_pairing_reject_peer(c_pid.as_ptr()) as jint }
}
