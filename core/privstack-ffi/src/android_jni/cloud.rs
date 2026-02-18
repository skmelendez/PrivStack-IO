//! JNI wrappers for cloud sync (S3-backed) functions.
//! Rust FFI uses `privstack_cloudsync_*` naming with out-parameter patterns.

use jni::JNIEnv;
use jni::objects::{JClass, JString};
use jni::sys::{jboolean, jint, jlong, jstring, JNI_FALSE, JNI_TRUE};

use super::{jstring_to_cstring, empty_jstring};

fn out_param_to_jstring(env: &mut JNIEnv, err: crate::PrivStackError, out: *mut std::ffi::c_char) -> jstring {
    if err != crate::PrivStackError::Ok || out.is_null() {
        return empty_jstring(env);
    }
    unsafe { super::owned_cptr_to_jstring(env, out) }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncConfigure(
    mut env: JNIEnv, _class: JClass, config_json: JString,
) -> jint {
    let c = match jstring_to_cstring(&mut env, &config_json) { Some(s) => s, None => return 1 };
    unsafe { crate::cloud::privstack_cloudsync_configure(c.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncAuthenticate(
    mut env: JNIEnv, _class: JClass, email: JString, password: JString,
) -> jstring {
    let c_e = match jstring_to_cstring(&mut env, &email) { Some(s) => s, None => return empty_jstring(&mut env) };
    let c_p = match jstring_to_cstring(&mut env, &password) { Some(s) => s, None => return empty_jstring(&mut env) };
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::cloud::privstack_cloudsync_authenticate(c_e.as_ptr(), c_p.as_ptr(), &mut out) };
    out_param_to_jstring(&mut env, err, out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncAuthenticateWithTokens(
    mut env: JNIEnv, _class: JClass, access_token: JString, refresh_token: JString, user_id: jlong,
) -> jint {
    let c_at = match jstring_to_cstring(&mut env, &access_token) { Some(s) => s, None => return 1 };
    let c_rt = match jstring_to_cstring(&mut env, &refresh_token) { Some(s) => s, None => return 1 };
    unsafe { crate::cloud::privstack_cloudsync_authenticate_with_tokens(c_at.as_ptr(), c_rt.as_ptr(), user_id) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncLogout(
    _env: JNIEnv, _class: JClass,
) -> jint { crate::cloud::privstack_cloudsync_logout() as jint }

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncIsAuthenticated(
    _env: JNIEnv, _class: JClass,
) -> jboolean { if crate::cloud::privstack_cloudsync_is_authenticated() { JNI_TRUE } else { JNI_FALSE } }

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncGetAuthTokens(
    mut env: JNIEnv, _class: JClass,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::cloud::privstack_cloudsync_get_auth_tokens(&mut out) };
    out_param_to_jstring(&mut env, err, out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncSetupPassphrase(
    mut env: JNIEnv, _class: JClass, passphrase: JString,
) -> jstring {
    let c = match jstring_to_cstring(&mut env, &passphrase) { Some(s) => s, None => return empty_jstring(&mut env) };
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::cloud::privstack_cloudsync_setup_passphrase(c.as_ptr(), &mut out) };
    out_param_to_jstring(&mut env, err, out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncSetupUnifiedRecovery(
    mut env: JNIEnv, _class: JClass, passphrase: JString,
) -> jstring {
    let c = match jstring_to_cstring(&mut env, &passphrase) { Some(s) => s, None => return empty_jstring(&mut env) };
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::cloud::privstack_cloudsync_setup_unified_recovery(c.as_ptr(), &mut out) };
    out_param_to_jstring(&mut env, err, out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncEnterPassphrase(
    mut env: JNIEnv, _class: JClass, passphrase: JString,
) -> jint {
    let c = match jstring_to_cstring(&mut env, &passphrase) { Some(s) => s, None => return 1 };
    unsafe { crate::cloud::privstack_cloudsync_enter_passphrase(c.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncRecoverFromMnemonic(
    mut env: JNIEnv, _class: JClass, mnemonic: JString,
) -> jint {
    let c = match jstring_to_cstring(&mut env, &mnemonic) { Some(s) => s, None => return 1 };
    unsafe { crate::cloud::privstack_cloudsync_recover_from_mnemonic(c.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncHasKeypair(
    _env: JNIEnv, _class: JClass,
) -> jboolean { if crate::cloud::privstack_cloudsync_has_keypair() { JNI_TRUE } else { JNI_FALSE } }

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncRegisterWorkspace(
    mut env: JNIEnv, _class: JClass, workspace_id: JString, name: JString,
) -> jstring {
    let c_w = match jstring_to_cstring(&mut env, &workspace_id) { Some(s) => s, None => return empty_jstring(&mut env) };
    let c_n = match jstring_to_cstring(&mut env, &name) { Some(s) => s, None => return empty_jstring(&mut env) };
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::cloud::privstack_cloudsync_register_workspace(c_w.as_ptr(), c_n.as_ptr(), &mut out) };
    out_param_to_jstring(&mut env, err, out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncListWorkspaces(
    mut env: JNIEnv, _class: JClass,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::cloud::privstack_cloudsync_list_workspaces(&mut out) };
    out_param_to_jstring(&mut env, err, out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncDeleteWorkspace(
    mut env: JNIEnv, _class: JClass, workspace_id: JString,
) -> jint {
    let c = match jstring_to_cstring(&mut env, &workspace_id) { Some(s) => s, None => return 1 };
    unsafe { crate::cloud::privstack_cloudsync_delete_workspace(c.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncStartSync(
    mut env: JNIEnv, _class: JClass, workspace_id: JString,
) -> jint {
    let c = match jstring_to_cstring(&mut env, &workspace_id) { Some(s) => s, None => return 1 };
    unsafe { crate::cloud::privstack_cloudsync_start_sync(c.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncStopSync(
    _env: JNIEnv, _class: JClass,
) -> jint { crate::cloud::privstack_cloudsync_stop_sync() as jint }

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncIsSyncing(
    _env: JNIEnv, _class: JClass,
) -> jboolean { if crate::cloud::privstack_cloudsync_is_syncing() { JNI_TRUE } else { JNI_FALSE } }

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncGetStatus(
    mut env: JNIEnv, _class: JClass,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::cloud::privstack_cloudsync_get_status(&mut out) };
    out_param_to_jstring(&mut env, err, out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncForceFlush(
    _env: JNIEnv, _class: JClass,
) -> jint { crate::cloud::privstack_cloudsync_force_flush() as jint }

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncPushEvent(
    mut env: JNIEnv, _class: JClass, entity_id: JString, entity_type: JString, json_data: JString,
) -> jint {
    let c_eid = match jstring_to_cstring(&mut env, &entity_id) { Some(s) => s, None => return 1 };
    let c_et = match jstring_to_cstring(&mut env, &entity_type) { Some(s) => s, None => return 1 };
    let c_d = match jstring_to_cstring(&mut env, &json_data) { Some(s) => s, None => return 1 };
    unsafe { crate::cloud::privstack_cloudsync_push_event(c_eid.as_ptr(), c_et.as_ptr(), c_d.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncPushAllEntities(
    _env: JNIEnv, _class: JClass,
) -> jint {
    let mut out_count: u32 = 0;
    unsafe { crate::cloud::privstack_cloudsync_push_all_entities(&mut out_count) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncGetQuota(
    mut env: JNIEnv, _class: JClass, workspace_id: JString,
) -> jstring {
    let c = match jstring_to_cstring(&mut env, &workspace_id) { Some(s) => s, None => return empty_jstring(&mut env) };
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::cloud::privstack_cloudsync_get_quota(c.as_ptr(), &mut out) };
    out_param_to_jstring(&mut env, err, out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncShareEntity(
    mut env: JNIEnv, _class: JClass,
    entity_id: JString, entity_type: JString, entity_name: JString,
    workspace_id: JString, recipient_email: JString, permission: JString,
) -> jstring {
    let c_eid = match jstring_to_cstring(&mut env, &entity_id) { Some(s) => s, None => return empty_jstring(&mut env) };
    let c_et = match jstring_to_cstring(&mut env, &entity_type) { Some(s) => s, None => return empty_jstring(&mut env) };
    let c_name = if entity_name.is_null() { None } else { jstring_to_cstring(&mut env, &entity_name) };
    let c_wid = match jstring_to_cstring(&mut env, &workspace_id) { Some(s) => s, None => return empty_jstring(&mut env) };
    let c_email = match jstring_to_cstring(&mut env, &recipient_email) { Some(s) => s, None => return empty_jstring(&mut env) };
    let c_perm = match jstring_to_cstring(&mut env, &permission) { Some(s) => s, None => return empty_jstring(&mut env) };
    let name_ptr = c_name.as_ref().map(|s| s.as_ptr()).unwrap_or(std::ptr::null());
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe {
        crate::cloud::privstack_cloudsync_share_entity(
            c_eid.as_ptr(), c_et.as_ptr(), name_ptr,
            c_wid.as_ptr(), c_email.as_ptr(), c_perm.as_ptr(), &mut out,
        )
    };
    out_param_to_jstring(&mut env, err, out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncRevokeShare(
    mut env: JNIEnv, _class: JClass, entity_id: JString, recipient_email: JString,
) -> jint {
    let c_eid = match jstring_to_cstring(&mut env, &entity_id) { Some(s) => s, None => return 1 };
    let c_email = match jstring_to_cstring(&mut env, &recipient_email) { Some(s) => s, None => return 1 };
    unsafe { crate::cloud::privstack_cloudsync_revoke_share(c_eid.as_ptr(), c_email.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncAcceptShare(
    mut env: JNIEnv, _class: JClass, invitation_token: JString,
) -> jint {
    let c = match jstring_to_cstring(&mut env, &invitation_token) { Some(s) => s, None => return 1 };
    unsafe { crate::cloud::privstack_cloudsync_accept_share(c.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncListEntityShares(
    mut env: JNIEnv, _class: JClass, entity_id: JString,
) -> jstring {
    let c = match jstring_to_cstring(&mut env, &entity_id) { Some(s) => s, None => return empty_jstring(&mut env) };
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::cloud::privstack_cloudsync_list_entity_shares(c.as_ptr(), &mut out) };
    out_param_to_jstring(&mut env, err, out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncGetSharedWithMe(
    mut env: JNIEnv, _class: JClass,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::cloud::privstack_cloudsync_get_shared_with_me(&mut out) };
    out_param_to_jstring(&mut env, err, out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncRegisterDevice(
    mut env: JNIEnv, _class: JClass, name: JString, platform: JString,
) -> jint {
    let c_n = match jstring_to_cstring(&mut env, &name) { Some(s) => s, None => return 1 };
    let c_p = match jstring_to_cstring(&mut env, &platform) { Some(s) => s, None => return 1 };
    unsafe { crate::cloud::privstack_cloudsync_register_device(c_n.as_ptr(), c_p.as_ptr()) as jint }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_com_privstack_bridge_NativeBridge_privstackCloudSyncListDevices(
    mut env: JNIEnv, _class: JClass,
) -> jstring {
    let mut out: *mut std::ffi::c_char = std::ptr::null_mut();
    let err = unsafe { crate::cloud::privstack_cloudsync_list_devices(&mut out) };
    out_param_to_jstring(&mut env, err, out)
}
