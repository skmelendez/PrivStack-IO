//! FFI: Dataset creation, row CRUD, and column CRUD mutations.

use super::{
    ColumnModifyRequest, CreateEmptyRequest,
    DeleteRowsRequest, DuplicateRequest, ImportContentRequest, InsertRowRequest,
    UpdateCellRequest,
};
use crate::{to_c_string, PrivStackError};
use std::ffi::{c_char, CStr};

/// Create an empty dataset with a defined schema.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_create_empty(
    request_json: *const c_char,
) -> *mut c_char {
    unsafe {
        let req = parse_json_request!(request_json, CreateEmptyRequest);

        with_store_json!(r#"{"error":"not initialized"}"#, |store| {
            match store.create_empty(&req.name, &req.columns) {
                Ok(meta) => {
                    let json =
                        serde_json::to_string(&meta).unwrap_or_else(|_| "{}".to_string());
                    to_c_string(&json)
                }
                Err(e) => {
                    eprintln!("[FFI DATASET] create_empty failed: {e:?}");
                    to_c_string(&super::error_json(&e.to_string()))
                }
            }
        })
    }
}

/// Duplicate an existing dataset.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_duplicate(
    request_json: *const c_char,
) -> *mut c_char {
    unsafe {
        let req = parse_json_request!(request_json, DuplicateRequest);

        with_store_json!(r#"{"error":"not initialized"}"#, |store| {
            let source_id = match uuid::Uuid::parse_str(&req.source_dataset_id) {
                Ok(u) => privstack_datasets::DatasetId(u),
                Err(_) => return to_c_string(r#"{"error":"invalid source dataset id"}"#),
            };

            match store.duplicate(&source_id, &req.new_name) {
                Ok(meta) => {
                    let json =
                        serde_json::to_string(&meta).unwrap_or_else(|_| "{}".to_string());
                    to_c_string(&json)
                }
                Err(e) => {
                    eprintln!("[FFI DATASET] duplicate failed: {e:?}");
                    to_c_string(&super::error_json(&e.to_string()))
                }
            }
        })
    }
}

/// Import dataset from CSV content string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_import_content(
    request_json: *const c_char,
) -> *mut c_char {
    unsafe {
        let req = parse_json_request!(request_json, ImportContentRequest);

        with_store_json!(r#"{"error":"not initialized"}"#, |store| {
            match store.import_csv_content(&req.content, &req.name) {
                Ok(meta) => {
                    let json =
                        serde_json::to_string(&meta).unwrap_or_else(|_| "{}".to_string());
                    to_c_string(&json)
                }
                Err(e) => {
                    eprintln!("[FFI DATASET] import_content failed: {e:?}");
                    to_c_string(&super::error_json(&e.to_string()))
                }
            }
        })
    }
}

/// Insert a row into a dataset.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_insert_row(
    request_json: *const c_char,
) -> *mut c_char {
    unsafe {
        let req = parse_json_request!(request_json, InsertRowRequest);

        with_store_json!(r#"{"error":"not initialized"}"#, |store| {
            let dataset_id = match uuid::Uuid::parse_str(&req.dataset_id) {
                Ok(u) => privstack_datasets::DatasetId(u),
                Err(_) => return to_c_string(r#"{"error":"invalid dataset id"}"#),
            };

            let values: Vec<(&str, serde_json::Value)> = req
                .values
                .iter()
                .map(|(k, v)| (k.as_str(), v.clone()))
                .collect();

            match store.insert_row(&dataset_id, &values) {
                Ok(()) => to_c_string(r#"{"ok":true}"#),
                Err(e) => {
                    eprintln!("[FFI DATASET] insert_row failed: {e:?}");
                    to_c_string(&super::error_json(&e.to_string()))
                }
            }
        })
    }
}

/// Update a single cell value.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_update_cell(
    request_json: *const c_char,
) -> *mut c_char {
    unsafe {
        let req = parse_json_request!(request_json, UpdateCellRequest);

        with_store_json!(r#"{"error":"not initialized"}"#, |store| {
            let dataset_id = match uuid::Uuid::parse_str(&req.dataset_id) {
                Ok(u) => privstack_datasets::DatasetId(u),
                Err(_) => return to_c_string(r#"{"error":"invalid dataset id"}"#),
            };

            match store.update_cell(&dataset_id, req.row_index, &req.column, req.value.clone()) {
                Ok(()) => to_c_string(r#"{"ok":true}"#),
                Err(e) => {
                    eprintln!("[FFI DATASET] update_cell failed: {e:?}");
                    to_c_string(&super::error_json(&e.to_string()))
                }
            }
        })
    }
}

/// Delete rows by indices.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_delete_rows(
    request_json: *const c_char,
) -> *mut c_char {
    unsafe {
        let req = parse_json_request!(request_json, DeleteRowsRequest);

        with_store_json!(r#"{"error":"not initialized"}"#, |store| {
            let dataset_id = match uuid::Uuid::parse_str(&req.dataset_id) {
                Ok(u) => privstack_datasets::DatasetId(u),
                Err(_) => return to_c_string(r#"{"error":"invalid dataset id"}"#),
            };

            match store.delete_rows(&dataset_id, &req.row_indices) {
                Ok(()) => to_c_string(r#"{"ok":true}"#),
                Err(e) => {
                    eprintln!("[FFI DATASET] delete_rows failed: {e:?}");
                    to_c_string(&super::error_json(&e.to_string()))
                }
            }
        })
    }
}

/// Add a column to a dataset.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_add_column(
    request_json: *const c_char,
) -> *mut c_char {
    unsafe {
        let req = parse_json_request!(request_json, ColumnModifyRequest);

        with_store_json!(r#"{"error":"not initialized"}"#, |store| {
            let dataset_id = match uuid::Uuid::parse_str(&req.dataset_id) {
                Ok(u) => privstack_datasets::DatasetId(u),
                Err(_) => return to_c_string(r#"{"error":"invalid dataset id"}"#),
            };

            let col_type = req.column_type.as_deref().unwrap_or("VARCHAR");

            match store.add_column(
                &dataset_id,
                &req.column_name,
                col_type,
                req.default_value.as_deref(),
            ) {
                Ok(()) => to_c_string(r#"{"ok":true}"#),
                Err(e) => {
                    eprintln!("[FFI DATASET] add_column failed: {e:?}");
                    to_c_string(&super::error_json(&e.to_string()))
                }
            }
        })
    }
}

/// Drop a column from a dataset.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_drop_column(
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
        let req: ColumnModifyRequest = match serde_json::from_str(json_str) {
            Ok(r) => r,
            Err(_) => return PrivStackError::JsonError,
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

        let dataset_id = match uuid::Uuid::parse_str(&req.dataset_id) {
            Ok(u) => privstack_datasets::DatasetId(u),
            Err(_) => return PrivStackError::InvalidArgument,
        };

        match store.drop_column(&dataset_id, &req.column_name) {
            Ok(()) => PrivStackError::Ok,
            Err(e) => {
                eprintln!("[FFI DATASET] drop_column failed: {e:?}");
                PrivStackError::StorageError
            }
        }
    }
}

/// Rename a column in a dataset.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_rename_column(
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
        let req: ColumnModifyRequest = match serde_json::from_str(json_str) {
            Ok(r) => r,
            Err(_) => return PrivStackError::JsonError,
        };

        let new_name = match req.new_name.as_deref() {
            Some(n) => n,
            None => return PrivStackError::InvalidArgument,
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

        let dataset_id = match uuid::Uuid::parse_str(&req.dataset_id) {
            Ok(u) => privstack_datasets::DatasetId(u),
            Err(_) => return PrivStackError::InvalidArgument,
        };

        match store.rename_column(&dataset_id, &req.column_name, new_name) {
            Ok(()) => PrivStackError::Ok,
            Err(e) => {
                eprintln!("[FFI DATASET] rename_column failed: {e:?}");
                PrivStackError::StorageError
            }
        }
    }
}
