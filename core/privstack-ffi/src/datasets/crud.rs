//! FFI: Core CRUD — import, list, get, delete, rename, query, get_columns.

use super::DatasetQueryRequest;
use crate::{to_c_string, PrivStackError};
use std::ffi::{c_char, CStr};

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_import_csv(
    file_path: *const c_char,
    name: *const c_char,
) -> *mut c_char {
    unsafe {
        let file_path = parse_cstr!(file_path, r#"{"error":"null pointer"}"#);
        let name = parse_cstr!(name, r#"{"error":"null pointer"}"#);

        with_store_json!(r#"{"error":"not initialized"}"#, |store| {
            match store.import_csv(std::path::Path::new(file_path), name) {
                Ok(meta) => {
                    let json =
                        serde_json::to_string(&meta).unwrap_or_else(|_| "{}".to_string());
                    to_c_string(&json)
                }
                Err(e) => {
                    eprintln!("[FFI DATASET] import_csv failed: {e:?}");
                    to_c_string(&super::error_json(&e.to_string()))
                }
            }
        })
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn privstack_dataset_list() -> *mut c_char {
    with_store_json!("[]", |store| {
        match store.list() {
            Ok(datasets) => {
                let json =
                    serde_json::to_string(&datasets).unwrap_or_else(|_| "[]".to_string());
                to_c_string(&json)
            }
            Err(e) => {
                eprintln!("[FFI DATASET] list failed: {e:?}");
                to_c_string("[]")
            }
        }
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_get(dataset_id: *const c_char) -> *mut c_char {
    unsafe {
        let id_str = parse_cstr!(dataset_id, r#"{"error":"null pointer"}"#);

        with_store_json!(r#"{"error":"not initialized"}"#, |store| {
            let dataset_id = match uuid::Uuid::parse_str(id_str) {
                Ok(u) => privstack_datasets::DatasetId(u),
                Err(_) => return to_c_string(r#"{"error":"invalid dataset id"}"#),
            };

            match store.get(&dataset_id) {
                Ok(meta) => {
                    let json =
                        serde_json::to_string(&meta).unwrap_or_else(|_| "{}".to_string());
                    to_c_string(&json)
                }
                Err(e) => {
                    eprintln!("[FFI DATASET] get failed: {e:?}");
                    to_c_string(&super::error_json(&e.to_string()))
                }
            }
        })
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_delete(dataset_id: *const c_char) -> PrivStackError {
    unsafe {
        if dataset_id.is_null() {
            return PrivStackError::NullPointer;
        }
        let id_str = match CStr::from_ptr(dataset_id).to_str() {
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

        let dataset_id = match uuid::Uuid::parse_str(id_str) {
            Ok(u) => privstack_datasets::DatasetId(u),
            Err(_) => return PrivStackError::InvalidArgument,
        };

        match store.delete(&dataset_id) {
            Ok(()) => PrivStackError::Ok,
            Err(privstack_datasets::DatasetError::NotFound(_)) => PrivStackError::NotFound,
            Err(e) => {
                eprintln!("[FFI DATASET] delete failed: {e:?}");
                PrivStackError::StorageError
            }
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_rename(
    dataset_id: *const c_char,
    new_name: *const c_char,
) -> PrivStackError {
    unsafe {
        if dataset_id.is_null() || new_name.is_null() {
            return PrivStackError::NullPointer;
        }
        let id_str = match CStr::from_ptr(dataset_id).to_str() {
            Ok(s) => s,
            Err(_) => return PrivStackError::InvalidUtf8,
        };
        let new_name = match CStr::from_ptr(new_name).to_str() {
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

        let dataset_id = match uuid::Uuid::parse_str(id_str) {
            Ok(u) => privstack_datasets::DatasetId(u),
            Err(_) => return PrivStackError::InvalidArgument,
        };

        match store.rename(&dataset_id, new_name) {
            Ok(()) => PrivStackError::Ok,
            Err(privstack_datasets::DatasetError::NotFound(_)) => PrivStackError::NotFound,
            Err(e) => {
                eprintln!("[FFI DATASET] rename failed: {e:?}");
                PrivStackError::StorageError
            }
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_query(query_json: *const c_char) -> *mut c_char {
    let result =
        std::panic::catch_unwind(|| unsafe { privstack_dataset_query_inner(query_json) });
    match result {
        Ok(ptr) => ptr,
        Err(_) => to_c_string(r#"{"error":"internal panic in dataset query"}"#),
    }
}

unsafe fn privstack_dataset_query_inner(query_json: *const c_char) -> *mut c_char {
    unsafe {
        let json_str = parse_cstr!(query_json, r#"{"error":"null pointer"}"#);
        let req: DatasetQueryRequest = match serde_json::from_str(json_str) {
            Ok(r) => r,
            Err(e) => return to_c_string(&super::error_json(&format!("invalid json: {e}"))),
        };

        with_store_json!(r#"{"error":"not initialized"}"#, |store| {
            let dataset_id = match uuid::Uuid::parse_str(&req.dataset_id) {
                Ok(u) => privstack_datasets::DatasetId(u),
                Err(_) => return to_c_string(r#"{"error":"invalid dataset id"}"#),
            };

            match store.query_dataset(
                &dataset_id,
                req.page.unwrap_or(0),
                req.page_size.unwrap_or(100),
                req.filter_text.as_deref(),
                req.sort_column.as_deref(),
                req.sort_desc.unwrap_or(false),
            ) {
                Ok(result) => {
                    let json =
                        serde_json::to_string(&result).unwrap_or_else(|_| "{}".to_string());
                    to_c_string(&json)
                }
                Err(e) => {
                    eprintln!("[FFI DATASET] query failed: {e:?}");
                    to_c_string(&super::error_json(&e.to_string()))
                }
            }
        })
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_get_columns(
    dataset_id: *const c_char,
) -> *mut c_char {
    unsafe {
        let id_str = parse_cstr!(dataset_id, r#"{"error":"null pointer"}"#);

        with_store_json!(r#"{"error":"not initialized"}"#, |store| {
            let dataset_id = match uuid::Uuid::parse_str(id_str) {
                Ok(u) => privstack_datasets::DatasetId(u),
                Err(_) => return to_c_string(r#"{"error":"invalid dataset id"}"#),
            };

            match store.get_columns(&dataset_id) {
                Ok(columns) => {
                    let json =
                        serde_json::to_string(&columns).unwrap_or_else(|_| "[]".to_string());
                    to_c_string(&json)
                }
                Err(e) => {
                    eprintln!("[FFI DATASET] get_columns failed: {e:?}");
                    to_c_string("[]")
                }
            }
        })
    }
}
