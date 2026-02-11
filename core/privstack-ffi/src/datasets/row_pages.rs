//! FFI: Row-page linking operations.

use crate::{to_c_string, PrivStackError};
use std::ffi::{c_char, CStr};

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_link_row_page(
    dataset_id: *const c_char,
    row_key: *const c_char,
    page_id: *const c_char,
) -> PrivStackError {
    unsafe {
        if dataset_id.is_null() || row_key.is_null() || page_id.is_null() {
            return PrivStackError::NullPointer;
        }
        let id_str = match CStr::from_ptr(dataset_id).to_str() {
            Ok(s) => s,
            Err(_) => return PrivStackError::InvalidUtf8,
        };
        let row_key = match CStr::from_ptr(row_key).to_str() {
            Ok(s) => s,
            Err(_) => return PrivStackError::InvalidUtf8,
        };
        let page_id = match CStr::from_ptr(page_id).to_str() {
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

        match store.link_row_to_page(&dataset_id, row_key, page_id) {
            Ok(()) => PrivStackError::Ok,
            Err(e) => {
                eprintln!("[FFI DATASET] link_row_page failed: {e:?}");
                PrivStackError::StorageError
            }
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_get_page_for_row(
    dataset_id: *const c_char,
    row_key: *const c_char,
) -> *mut c_char {
    unsafe {
        let id_str = parse_cstr!(dataset_id, "null");
        let row_key = parse_cstr!(row_key, "null");

        with_store_json!("null", |store| {
            let dataset_id = match uuid::Uuid::parse_str(id_str) {
                Ok(u) => privstack_datasets::DatasetId(u),
                Err(_) => return to_c_string("null"),
            };

            match store.get_page_for_row(&dataset_id, row_key) {
                Ok(Some(page_id)) => to_c_string(&format!(r#""{}""#, page_id)),
                Ok(None) => to_c_string("null"),
                Err(e) => {
                    eprintln!("[FFI DATASET] get_page_for_row failed: {e:?}");
                    to_c_string("null")
                }
            }
        })
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_get_row_for_page(
    page_id: *const c_char,
) -> *mut c_char {
    unsafe {
        let page_id = parse_cstr!(page_id, "null");

        with_store_json!("null", |store| {
            match store.get_row_for_page(page_id) {
                Ok(Some((ds_id, row_key))) => {
                    let json =
                        format!(r#"{{"dataset_id":"{}","row_key":"{}"}}"#, ds_id, row_key);
                    to_c_string(&json)
                }
                Ok(None) => to_c_string("null"),
                Err(e) => {
                    eprintln!("[FFI DATASET] get_row_for_page failed: {e:?}");
                    to_c_string("null")
                }
            }
        })
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_unlink_row_page(
    dataset_id: *const c_char,
    row_key: *const c_char,
) -> PrivStackError {
    unsafe {
        if dataset_id.is_null() || row_key.is_null() {
            return PrivStackError::NullPointer;
        }
        let id_str = match CStr::from_ptr(dataset_id).to_str() {
            Ok(s) => s,
            Err(_) => return PrivStackError::InvalidUtf8,
        };
        let row_key = match CStr::from_ptr(row_key).to_str() {
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

        match store.unlink_row_page(&dataset_id, row_key) {
            Ok(()) => PrivStackError::Ok,
            Err(e) => {
                eprintln!("[FFI DATASET] unlink_row_page failed: {e:?}");
                PrivStackError::StorageError
            }
        }
    }
}
