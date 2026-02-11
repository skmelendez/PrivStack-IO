//! FFI: Cross-dataset relations.

use super::CreateRelationRequest;
use crate::{to_c_string, PrivStackError};
use std::ffi::{c_char, CStr};

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_create_relation(
    request_json: *const c_char,
) -> *mut c_char {
    unsafe {
        let req = parse_json_request!(request_json, CreateRelationRequest);

        with_store_json!(r#"{"error":"not initialized"}"#, |store| {
            let src_id = match uuid::Uuid::parse_str(&req.source_dataset_id) {
                Ok(u) => privstack_datasets::DatasetId(u),
                Err(_) => return to_c_string(r#"{"error":"invalid source dataset id"}"#),
            };
            let tgt_id = match uuid::Uuid::parse_str(&req.target_dataset_id) {
                Ok(u) => privstack_datasets::DatasetId(u),
                Err(_) => return to_c_string(r#"{"error":"invalid target dataset id"}"#),
            };

            match store.create_relation(&src_id, &req.source_column, &tgt_id, &req.target_column)
            {
                Ok(rel) => {
                    let json =
                        serde_json::to_string(&rel).unwrap_or_else(|_| "{}".to_string());
                    to_c_string(&json)
                }
                Err(e) => {
                    eprintln!("[FFI DATASET] create_relation failed: {e:?}");
                    to_c_string(&super::error_json(&e.to_string()))
                }
            }
        })
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_delete_relation(
    relation_id: *const c_char,
) -> PrivStackError {
    unsafe {
        if relation_id.is_null() {
            return PrivStackError::NullPointer;
        }
        let id_str = match CStr::from_ptr(relation_id).to_str() {
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

        match store.delete_relation(id_str) {
            Ok(()) => PrivStackError::Ok,
            Err(e) => {
                eprintln!("[FFI DATASET] delete_relation failed: {e:?}");
                PrivStackError::StorageError
            }
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_list_relations(
    dataset_id: *const c_char,
) -> *mut c_char {
    unsafe {
        let id_str = parse_cstr!(dataset_id, "[]");

        with_store_json!("[]", |store| {
            let dataset_id = match uuid::Uuid::parse_str(id_str) {
                Ok(u) => privstack_datasets::DatasetId(u),
                Err(_) => return to_c_string("[]"),
            };

            match store.list_relations(&dataset_id) {
                Ok(relations) => {
                    let json =
                        serde_json::to_string(&relations).unwrap_or_else(|_| "[]".to_string());
                    to_c_string(&json)
                }
                Err(e) => {
                    eprintln!("[FFI DATASET] list_relations failed: {e:?}");
                    to_c_string("[]")
                }
            }
        })
    }
}
