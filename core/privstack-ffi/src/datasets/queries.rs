//! FFI: Aggregation, raw SQL, SQL v2, and saved queries.

use super::{
    AggregateQueryRequest,
    AggregateQueryResponse, RawSqlRequest, SavedQueryRequest, SqlV2Request,
};
use crate::{to_c_string, PrivStackError};
use std::ffi::{c_char, CStr};

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_aggregate(
    request_json: *const c_char,
) -> *mut c_char {
    unsafe {
        let req = parse_json_request!(request_json, AggregateQueryRequest);

        with_store_json!(r#"{"error":"not initialized"}"#, |store| {
            let dataset_id = match uuid::Uuid::parse_str(&req.dataset_id) {
                Ok(u) => privstack_datasets::DatasetId(u),
                Err(_) => return to_c_string(r#"{"error":"invalid dataset id"}"#),
            };

            match store.aggregate_query(
                &dataset_id,
                &req.x_column,
                &req.y_column,
                req.aggregation.as_deref(),
                req.group_by.as_deref(),
                req.filter_text.as_deref(),
            ) {
                Ok(rows) => {
                    let labels: Vec<String> = rows
                        .iter()
                        .map(|(x, _)| match x {
                            serde_json::Value::String(s) => s.clone(),
                            other => other.to_string(),
                        })
                        .collect();
                    let values: Vec<f64> = rows
                        .iter()
                        .map(|(_, y)| match y {
                            serde_json::Value::Number(n) => n.as_f64().unwrap_or(0.0),
                            _ => 0.0,
                        })
                        .collect();
                    let resp = AggregateQueryResponse { labels, values };
                    let json =
                        serde_json::to_string(&resp).unwrap_or_else(|_| "{}".to_string());
                    to_c_string(&json)
                }
                Err(e) => {
                    eprintln!("[FFI DATASET] aggregate failed: {e:?}");
                    to_c_string(&super::error_json(&e.to_string()))
                }
            }
        })
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_execute_sql(
    request_json: *const c_char,
) -> *mut c_char {
    unsafe {
        let req = parse_json_request!(request_json, RawSqlRequest);

        with_store_json!(r#"{"error":"not initialized"}"#, |store| {
            let sql = req.sql.clone();
            let page = req.page.unwrap_or(0);
            let page_size = req.page_size.unwrap_or(100);

            let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                store.execute_raw_query(&sql, page, page_size)
            }));

            match result {
                Ok(Ok(query_result)) => {
                    let json = serde_json::to_string(&query_result)
                        .unwrap_or_else(|_| "{}".to_string());
                    to_c_string(&json)
                }
                Ok(Err(e)) => {
                    eprintln!("[FFI DATASET] execute_sql failed: {e:?}");
                    to_c_string(&super::error_json(&e.to_string()))
                }
                Err(_) => {
                    eprintln!("[FFI DATASET] execute_sql panicked (caught)");
                    to_c_string(r#"{"error":"internal error: query execution panicked"}"#)
                }
            }
        })
    }
}

/// Execute SQL v2: supports `source:` aliases, mutations with dry-run, and SELECT queries.
///
/// Wraps execution in `catch_unwind` to prevent DuckDB panics from aborting the process.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_execute_sql_v2(
    request_json: *const c_char,
) -> *mut c_char {
    unsafe {
        let req = parse_json_request!(request_json, SqlV2Request);

        with_store_json!(r#"{"error":"not initialized"}"#, |store| {
            let sql = req.sql.clone();
            let page = req.page.unwrap_or(0);
            let page_size = req.page_size.unwrap_or(100);
            let dry_run = req.dry_run.unwrap_or(false);

            // SAFETY: catch_unwind protects the FFI boundary from DuckDB panics
            // (e.g. DuckDB 1.4.4 panics on stmt.column_count() before execution).
            let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                store.execute_sql_v2(&sql, page, page_size, dry_run)
            }));

            match result {
                Ok(Ok(exec_result)) => {
                    let json = serde_json::to_string(&exec_result)
                        .unwrap_or_else(|_| "{}".to_string());
                    to_c_string(&json)
                }
                Ok(Err(e)) => {
                    eprintln!("[FFI DATASET] execute_sql_v2 failed: {e:?}");
                    to_c_string(&super::error_json(&e.to_string()))
                }
                Err(_) => {
                    eprintln!("[FFI DATASET] execute_sql_v2 panicked (caught)");
                    to_c_string(r#"{"error":"internal error: query execution panicked"}"#)
                }
            }
        })
    }
}

// ── Saved Queries ───────────────────────────────────────────────────────

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_create_saved_query(
    request_json: *const c_char,
) -> *mut c_char {
    unsafe {
        let req = parse_json_request!(request_json, SavedQueryRequest);

        with_store_json!(r#"{"error":"not initialized"}"#, |store| {
            match store.create_saved_query(&req.name, &req.sql, req.description.as_deref(), req.is_view.unwrap_or(false)) {
                Ok(sq) => {
                    let json =
                        serde_json::to_string(&sq).unwrap_or_else(|_| "{}".to_string());
                    to_c_string(&json)
                }
                Err(e) => {
                    eprintln!("[FFI DATASET] create_saved_query failed: {e:?}");
                    to_c_string(&super::error_json(&e.to_string()))
                }
            }
        })
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_update_saved_query(
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
        let req: SavedQueryRequest = match serde_json::from_str(json_str) {
            Ok(r) => r,
            Err(_) => return PrivStackError::JsonError,
        };
        let query_id = match req.id.as_deref() {
            Some(id) => id,
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

        match store.update_saved_query(query_id, &req.name, &req.sql, req.description.as_deref(), req.is_view.unwrap_or(false))
        {
            Ok(()) => PrivStackError::Ok,
            Err(e) => {
                eprintln!("[FFI DATASET] update_saved_query failed: {e:?}");
                PrivStackError::StorageError
            }
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn privstack_dataset_delete_saved_query(
    query_id: *const c_char,
) -> PrivStackError {
    unsafe {
        if query_id.is_null() {
            return PrivStackError::NullPointer;
        }
        let id_str = match CStr::from_ptr(query_id).to_str() {
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

        match store.delete_saved_query(id_str) {
            Ok(()) => PrivStackError::Ok,
            Err(e) => {
                eprintln!("[FFI DATASET] delete_saved_query failed: {e:?}");
                PrivStackError::StorageError
            }
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn privstack_dataset_list_saved_queries() -> *mut c_char {
    with_store_json!("[]", |store| {
        match store.list_saved_queries() {
            Ok(queries) => {
                let json =
                    serde_json::to_string(&queries).unwrap_or_else(|_| "[]".to_string());
                to_c_string(&json)
            }
            Err(e) => {
                eprintln!("[FFI DATASET] list_saved_queries failed: {e:?}");
                to_c_string("[]")
            }
        }
    })
}
