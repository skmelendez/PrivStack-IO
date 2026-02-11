//! DuckDB-backed tabular dataset storage for PrivStack.
//!
//! This crate provides a 5th DuckDB database (`data.datasets.duckdb`) that
//! stores raw columnar data without encryption, enabling full SQL (WHERE,
//! GROUP BY, JOIN, aggregations) and DuckDB's native `read_csv_auto()`.
//!
//! # Architecture
//!
//! Unlike the entity system (which encrypts all data), datasets are stored
//! as plain DuckDB tables for maximum query performance. Each imported CSV
//! becomes a native DuckDB table named `ds_<uuid>`.

mod error;
mod schema;
mod store;
mod types;

pub use error::{DatasetError, DatasetResult};
pub use schema::{dataset_table_name, initialize_datasets_schema};
pub use store::DatasetStore;
pub use types::{
    ColumnDef, DatasetColumn, DatasetColumnType, DatasetId, DatasetMeta, DatasetQueryResult,
    DatasetRelation, DatasetView, FilterOperator, MutationResult, PreprocessedSql, RelationType,
    RowPageLink, SavedQuery, SortDirection, SqlExecutionResult, StatementType, ViewConfig,
    ViewFilter, ViewSort,
};

/// Open a DuckDB connection for the datasets database with WAL recovery.
///
/// Mirrors the pattern from `privstack-storage::open_duckdb_with_wal_recovery`.
pub fn open_datasets_db(path: &std::path::Path) -> DatasetResult<duckdb::Connection> {
    match duckdb::Connection::open(path) {
        Ok(conn) => Ok(conn),
        Err(first_err) => {
            let wal_path = path.with_extension(
                path.extension()
                    .map(|ext| format!("{}.wal", ext.to_string_lossy()))
                    .unwrap_or_else(|| "wal".to_string()),
            );
            if wal_path.exists() {
                eprintln!(
                    "[WARN] DuckDB datasets open failed, removing stale WAL and retrying: {}",
                    wal_path.display()
                );
                if std::fs::remove_file(&wal_path).is_ok() {
                    return duckdb::Connection::open(path).map_err(Into::into);
                }
            }
            Err(first_err.into())
        }
    }
}
