//! FFI: Saved view CRUD operations.

use super::{CreateViewRequest, UpdateViewRequest};
use crate::{to_c_string, PrivStackError};
use std::ffi::{c_char, CStr};

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_create_view(
    request_json: *const c_char,
) -> *mut c_char {
    unsafe {
        let req = parse_json_request!(request_json, CreateViewRequest);

        with_store_json!(r#"{"error":"not initialized"}"#, |store| {
            let dataset_id = match uuid::Uuid::parse_str(&req.dataset_id) {
                Ok(u) => privstack_datasets::DatasetId(u),
                Err(_) => return to_c_string(r#"{"error":"invalid dataset id"}"#),
            };

            match store.create_view(&dataset_id, &req.name, &req.config) {
                Ok(view) => {
                    let json =
                        serde_json::to_string(&view).unwrap_or_else(|_| "{}".to_string());
                    to_c_string(&json)
                }
                Err(e) => {
                    eprintln!("[FFI DATASET] create_view failed: {e:?}");
                    to_c_string(&super::error_json(&e.to_string()))
                }
            }
        })
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_update_view(
    request_json: *const c_char,
) -> PrivStackError {
    unsafe {
        if request_json.is_null() {
            return PrivStackError::NullPointer;
        }
        let json_str = match CStr::from_ptr(request_json).to_str() {
            Ok(s) => s,
            Err(_) => return PrivStackError::InvalidUtf8,
        };
        let req: UpdateViewRequest = match serde_json::from_str(json_str) {
            Ok(r) => r,
            Err(e) => {
                eprintln!("[FFI DATASET] update_view parse failed: {e:?}");
                return PrivStackError::JsonError;
            }
        };

        let handle = crate::lock_handle();
        let handle = match handle.as_ref() {
            Some(h) => h,
            None => return PrivStackError::NotInitialized,
        };
        let store = match handle.dataset_store.as_ref() {
            Some(s) => s,
            None => return PrivStackError::StorageError,
        };

        match store.update_view(&req.view_id, &req.config) {
            Ok(()) => PrivStackError::Ok,
            Err(e) => {
                eprintln!("[FFI DATASET] update_view failed: {e:?}");
                PrivStackError::StorageError
            }
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_delete_view(
    view_id: *const c_char,
) -> PrivStackError {
    unsafe {
        if view_id.is_null() {
            return PrivStackError::NullPointer;
        }
        let id_str = match CStr::from_ptr(view_id).to_str() {
            Ok(s) => s,
            Err(_) => return PrivStackError::InvalidUtf8,
        };

        let handle = crate::lock_handle();
        let handle = match handle.as_ref() {
            Some(h) => h,
            None => return PrivStackError::NotInitialized,
        };
        let store = match handle.dataset_store.as_ref() {
            Some(s) => s,
            None => return PrivStackError::StorageError,
        };

        match store.delete_view(id_str) {
            Ok(()) => PrivStackError::Ok,
            Err(e) => {
                eprintln!("[FFI DATASET] delete_view failed: {e:?}");
                PrivStackError::StorageError
            }
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_list_views(
    dataset_id: *const c_char,
) -> *mut c_char {
    unsafe {
        let id_str = parse_cstr!(dataset_id, "[]");

        with_store_json!("[]", |store| {
            let dataset_id = match uuid::Uuid::parse_str(id_str) {
                Ok(u) => privstack_datasets::DatasetId(u),
                Err(_) => return to_c_string("[]"),
            };

            match store.list_views(&dataset_id) {
                Ok(views) => {
                    let json =
                        serde_json::to_string(&views).unwrap_or_else(|_| "[]".to_string());
                    to_c_string(&json)
                }
                Err(e) => {
                    eprintln!("[FFI DATASET] list_views failed: {e:?}");
                    to_c_string("[]")
                }
            }
        })
    }
}
