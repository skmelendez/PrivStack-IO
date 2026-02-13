//! FFI bindings for the datasets module.
//!
//! Exposes dataset CRUD, import, query, mutation, and view operations to the C ABI.

use serde::{Deserialize, Serialize};

/// Build a JSON `{"error":"..."}` string with proper escaping of control characters.
pub(crate) fn error_json(msg: &str) -> String {
    // Use serde_json to escape the message (handles \n, \t, quotes, etc.)
    let escaped = serde_json::to_string(msg).unwrap_or_else(|_| "\"unknown error\"".to_string());
    format!(r#"{{"error":{escaped}}}"#)
}

/// Helper: acquire the HANDLE lock + dataset store, or return an error C string.
macro_rules! with_store_json {
    ($fallback:expr, |$store:ident| $body:expr) => {{
        let handle = crate::lock_handle();
        let handle = match handle.as_ref() {
            Some(h) => h,
            None => return to_c_string($fallback),
        };
        let $store = match handle.dataset_store.as_ref() {
            Some(s) => s,
            None => return to_c_string($fallback),
        };
        $body
    }};
}

/// Helper: parse a C string into &str, or return error JSON.
macro_rules! parse_cstr {
    ($ptr:expr, $fallback:expr) => {{
        if $ptr.is_null() {
            return to_c_string($fallback);
        }
        match CStr::from_ptr($ptr).to_str() {
            Ok(s) => s,
            Err(_) => return to_c_string($fallback),
        }
    }};
}

/// Helper: parse JSON from a C string, or return error JSON.
macro_rules! parse_json_request {
    ($ptr:expr, $type:ty) => {{
        let json_str = parse_cstr!($ptr, r#"{"error":"null pointer"}"#);
        match serde_json::from_str::<$type>(json_str) {
            Ok(r) => r,
            Err(e) => return to_c_string(&$crate::datasets::error_json(&format!("invalid json: {e}"))),
        }
    }};
}

/// Helper: check license for write operations in functions returning `*mut c_char`.
/// Returns an error JSON string if the license is not writable.
macro_rules! check_license_json {
    () => {{
        let handle = crate::lock_handle();
        if let Some(h) = handle.as_ref() {
            if let Err(e) = crate::check_license_writable(h) {
                let msg = match e {
                    crate::PrivStackError::LicenseExpired => "license expired — read-only mode",
                    crate::PrivStackError::LicenseNotActivated => "no active license — read-only mode",
                    _ => "license check failed — read-only mode",
                };
                return to_c_string(&$crate::datasets::error_json(msg));
            }
        }
    }};
}

mod crud;
mod mutations;
mod queries;
mod relations;
mod row_pages;
mod views;

// Re-export request types for sub-modules
#[derive(Deserialize)]
pub(crate) struct DatasetQueryRequest {
    pub dataset_id: String,
    pub page: Option<i64>,
    pub page_size: Option<i64>,
    pub filter_text: Option<String>,
    pub sort_column: Option<String>,
    pub sort_desc: Option<bool>,
}

#[derive(Deserialize)]
pub(crate) struct CreateRelationRequest {
    pub source_dataset_id: String,
    pub source_column: String,
    pub target_dataset_id: String,
    pub target_column: String,
}

#[derive(Deserialize)]
pub(crate) struct CreateViewRequest {
    pub dataset_id: String,
    pub name: String,
    pub config: privstack_datasets::ViewConfig,
}

#[derive(Deserialize)]
pub(crate) struct UpdateViewRequest {
    pub view_id: String,
    pub config: privstack_datasets::ViewConfig,
}

#[derive(Deserialize)]
pub(crate) struct AggregateQueryRequest {
    pub dataset_id: String,
    pub x_column: String,
    pub y_column: String,
    pub aggregation: Option<String>,
    pub group_by: Option<String>,
    pub filter_text: Option<String>,
}

#[derive(Serialize)]
pub(crate) struct AggregateQueryResponse {
    pub labels: Vec<String>,
    pub values: Vec<f64>,
}

#[derive(Deserialize)]
pub(crate) struct RawSqlRequest {
    pub sql: String,
    pub page: Option<i64>,
    pub page_size: Option<i64>,
}

#[derive(Deserialize)]
pub(crate) struct SqlV2Request {
    pub sql: String,
    pub page: Option<i64>,
    pub page_size: Option<i64>,
    pub dry_run: Option<bool>,
}

#[derive(Deserialize)]
pub(crate) struct SavedQueryRequest {
    pub id: Option<String>,
    pub name: String,
    pub sql: String,
    pub description: Option<String>,
    pub is_view: Option<bool>,
}

#[derive(Deserialize)]
pub(crate) struct CreateEmptyRequest {
    pub name: String,
    pub columns: Vec<privstack_datasets::ColumnDef>,
}

#[derive(Deserialize)]
pub(crate) struct DuplicateRequest {
    pub source_dataset_id: String,
    pub new_name: String,
}

#[derive(Deserialize)]
pub(crate) struct ImportContentRequest {
    pub content: String,
    pub name: String,
}

#[derive(Deserialize)]
pub(crate) struct InsertRowRequest {
    pub dataset_id: String,
    pub values: std::collections::HashMap<String, serde_json::Value>,
}

#[derive(Deserialize)]
pub(crate) struct UpdateCellRequest {
    pub dataset_id: String,
    pub row_index: i64,
    pub column: String,
    pub value: serde_json::Value,
}

#[derive(Deserialize)]
pub(crate) struct DeleteRowsRequest {
    pub dataset_id: String,
    pub row_indices: Vec<i64>,
}

#[derive(Deserialize)]
pub(crate) struct ColumnModifyRequest {
    pub dataset_id: String,
    pub column_name: String,
    pub column_type: Option<String>,
    pub default_value: Option<String>,
    pub new_name: Option<String>,
}
