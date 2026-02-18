//! JNI wrappers for core lifecycle and SDK execution functions.

use jni::JNIEnv;
use jni::objects::{JClass, JString};
use jni::sys::{jint, jstring};

use super::{jstring_to_cstring, owned_cptr_to_jstring, static_cptr_to_jstring, empty_jstring};

// ── privstackInit ────────────────────────────────────────────────────────

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackInit(
    mut env: JNIEnv,
    _class: JClass,
    db_path: JString,
) -> jint {
    let c_path = match jstring_to_cstring(&mut env, &db_path) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    // SAFETY: c_path is a valid null-terminated string.
    let result = unsafe { crate::privstack_init(c_path.as_ptr()) };
    result as jint
}

// ── privstackShutdown ────────────────────────────────────────────────────

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackShutdown(
    _env: JNIEnv,
    _class: JClass,
) {
    crate::privstack_shutdown();
}

// ── privstackVersion ─────────────────────────────────────────────────────

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackVersion(
    mut env: JNIEnv,
    _class: JClass,
) -> jstring {
    let ptr = crate::privstack_version();
    static_cptr_to_jstring(&mut env, ptr)
}

// ── privstackExecute ─────────────────────────────────────────────────────

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackExecute(
    mut env: JNIEnv,
    _class: JClass,
    request_json: JString,
) -> jstring {
    let c_json = match jstring_to_cstring(&mut env, &request_json) {
        Some(s) => s,
        None => return empty_jstring(&mut env),
    };
    // SAFETY: c_json is valid; returned pointer must be freed.
    let ptr = unsafe { crate::privstack_execute(c_json.as_ptr()) };
    unsafe { owned_cptr_to_jstring(&mut env, ptr) }
}

// ── privstackSearch ──────────────────────────────────────────────────────

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackSearch(
    mut env: JNIEnv,
    _class: JClass,
    query_json: JString,
) -> jstring {
    let c_json = match jstring_to_cstring(&mut env, &query_json) {
        Some(s) => s,
        None => return empty_jstring(&mut env),
    };
    // SAFETY: c_json is valid; returned pointer must be freed.
    let ptr = unsafe { crate::privstack_search(c_json.as_ptr()) };
    unsafe { owned_cptr_to_jstring(&mut env, ptr) }
}

// ── privstackRegisterEntityType ──────────────────────────────────────────

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackRegisterEntityType(
    mut env: JNIEnv,
    _class: JClass,
    schema_json: JString,
) -> jint {
    let c_json = match jstring_to_cstring(&mut env, &schema_json) {
        Some(s) => s,
        None => return crate::PrivStackError::NullPointer as jint,
    };
    // SAFETY: c_json is valid.
    unsafe { crate::privstack_register_entity_type(c_json.as_ptr()) as jint }
}

// ── privstackDbMaintenance ───────────────────────────────────────────────

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackDbMaintenance(
    _env: JNIEnv,
    _class: JClass,
) -> jint {
    crate::privstack_db_maintenance() as jint
}
