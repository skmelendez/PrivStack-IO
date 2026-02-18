//! JNI wrappers for P2P sync functions.

use jni::JNIEnv;
use jni::objects::{JClass, JString};
use jni::sys::{jboolean, jint, jstring, JNI_FALSE, JNI_TRUE};

use super::{jstring_to_cstring, empty_jstring};

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackSyncStart(
    _env: JNIEnv,
    _class: JClass,
) -> jint {
    crate::privstack_sync_start() as jint
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackSyncStop(
    _env: JNIEnv,
    _class: JClass,
) -> jint {
    crate::privstack_sync_stop() as jint
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackSyncIsRunning(
    _env: JNIEnv,
    _class: JClass,
) -> jboolean {
    if crate::privstack_sync_is_running() { JNI_TRUE } else { JNI_FALSE }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackSyncTrigger(
    _env: JNIEnv,
    _class: JClass,
) -> jint {
    crate::privstack_sync_trigger() as jint
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackSyncStatus(
    mut env: JNIEnv,
    _class: JClass,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::privstack_sync_status(&mut out) };
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(&mut env);
    }
    unsafe { super::owned_cptr_to_jstring(&mut env, out) }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackSyncGetPeerId(
    mut env: JNIEnv,
    _class: JClass,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::privstack_sync_get_peer_id(&mut out) };
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(&mut env);
    }
    unsafe { super::owned_cptr_to_jstring(&mut env, out) }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackSyncGetPeers(
    mut env: JNIEnv,
    _class: JClass,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::privstack_sync_get_peers(&mut out) };
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(&mut env);
    }
    unsafe { super::owned_cptr_to_jstring(&mut env, out) }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackSyncPeerCount(
    _env: JNIEnv,
    _class: JClass,
) -> jint {
    crate::privstack_sync_peer_count()
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackSyncPollEvents(
    mut env: JNIEnv,
    _class: JClass,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::privstack_sync_poll_events(&mut out) };
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(&mut env);
    }
    unsafe { super::owned_cptr_to_jstring(&mut env, out) }
}

// ── P2P Sharing ──────────────────────────────────────────────────────────

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackShareEntityWithPeer(
    mut env: JNIEnv,
    _class: JClass,
    entity_id: JString,
    peer_id: JString,
) -> jint {
    let c_eid = match jstring_to_cstring(&mut env, &entity_id) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    let c_pid = match jstring_to_cstring(&mut env, &peer_id) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    unsafe { crate::privstack_share_entity_with_peer(c_eid.as_ptr(), c_pid.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackUnshareEntityWithPeer(
    mut env: JNIEnv,
    _class: JClass,
    entity_id: JString,
    peer_id: JString,
) -> jint {
    let c_eid = match jstring_to_cstring(&mut env, &entity_id) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    let c_pid = match jstring_to_cstring(&mut env, &peer_id) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    unsafe { crate::privstack_unshare_entity_with_peer(c_eid.as_ptr(), c_pid.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackListSharedPeers(
    mut env: JNIEnv,
    _class: JClass,
    entity_id: JString,
) -> jstring {
    let c_eid = match jstring_to_cstring(&mut env, &entity_id) {
        Some(s) => s,
        None => return empty_jstring(&mut env),
    };
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::privstack_list_shared_peers(c_eid.as_ptr(), &mut out) };
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(&mut env);
    }
    unsafe { super::owned_cptr_to_jstring(&mut env, out) }
}
