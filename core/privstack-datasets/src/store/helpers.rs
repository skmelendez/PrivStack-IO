//! Shared helper functions for dataset store operations.

use crate::error::DatasetResult;
use crate::types::{DatasetColumn, DatasetColumnType};
use duckdb::{params, Connection};

/// Introspect column names and types from an existing table.
pub(crate) fn introspect_columns(
    conn: &Connection,
    table: &str,
) -> DatasetResult<Vec<DatasetColumn>> {
    let mut stmt = conn.prepare(
        "SELECT column_name, data_type, ordinal_position FROM information_schema.columns WHERE table_name = ? ORDER BY ordinal_position",
    )?;

    let columns = stmt
        .query_map(params![table], |row| {
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, String>(1)?,
                row.get::<_, i32>(2)?,
            ))
        })?
        .filter_map(|r| r.ok())
        .map(|(name, dtype, ordinal)| DatasetColumn {
            name,
            column_type: DatasetColumnType::from_duckdb(&dtype),
            ordinal,
        })
        .collect();

    Ok(columns)
}

/// Build a WHERE clause that ILIKEs the filter text across all text columns.
pub(crate) fn build_filter_clause(
    columns: &[DatasetColumn],
    filter_text: Option<&str>,
) -> String {
    match filter_text {
        Some(text) if !text.is_empty() => {
            let escaped = text.replace('\'', "''");
            let text_cols: Vec<&DatasetColumn> = columns
                .iter()
                .filter(|c| c.column_type == DatasetColumnType::Text)
                .collect();
            if text_cols.is_empty() {
                return String::new();
            }
            let conditions: Vec<String> = text_cols
                .iter()
                .map(|c| format!("\"{}\" ILIKE '%{escaped}%'", sanitize_identifier(&c.name)))
                .collect();
            format!(" WHERE {}", conditions.join(" OR "))
        }
        _ => String::new(),
    }
}

/// Sanitize a column name for use in SQL (remove anything that isn't alphanumeric/underscore/space).
pub(crate) fn sanitize_identifier(name: &str) -> String {
    name.chars()
        .filter(|c| c.is_alphanumeric() || *c == '_' || *c == ' ')
        .collect()
}

/// Extract a single row value as serde_json::Value.
pub(crate) fn row_value_to_json(row: &duckdb::Row<'_>, idx: usize) -> serde_json::Value {
    if let Ok(v) = row.get::<_, String>(idx) {
        return serde_json::Value::String(v);
    }
    if let Ok(v) = row.get::<_, i64>(idx) {
        return serde_json::Value::Number(v.into());
    }
    if let Ok(v) = row.get::<_, f64>(idx) {
        return serde_json::Number::from_f64(v)
            .map(serde_json::Value::Number)
            .unwrap_or(serde_json::Value::Null);
    }
    if let Ok(v) = row.get::<_, bool>(idx) {
        return serde_json::Value::Bool(v);
    }
    serde_json::Value::Null
}

/// Build a SELECT clause that CASTs DATE and TIMESTAMP columns to VARCHAR
/// so DuckDB returns formatted strings instead of raw epoch-day integers.
pub(crate) fn build_typed_select(columns: &[DatasetColumn]) -> String {
    columns
        .iter()
        .map(|c| {
            let name = sanitize_identifier(&c.name);
            match c.column_type {
                DatasetColumnType::Date | DatasetColumnType::Timestamp => {
                    format!("CAST(\"{name}\" AS VARCHAR) AS \"{name}\"")
                }
                _ => format!("\"{name}\""),
            }
        })
        .collect::<Vec<_>>()
        .join(", ")
}

/// Current time in milliseconds since Unix epoch.
pub(crate) fn now_millis() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as i64
}
