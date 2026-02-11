//! Error types for the datasets module.

use thiserror::Error;

/// All errors that can occur in dataset operations.
#[derive(Debug, Error)]
pub enum DatasetError {
    #[error("DuckDB error: {0}")]
    DuckDb(#[from] duckdb::Error),

    #[error("Dataset not found: {0}")]
    NotFound(String),

    #[error("Import failed: {0}")]
    ImportFailed(String),

    #[error("Invalid query: {0}")]
    InvalidQuery(String),

    #[error("IO error: {0}")]
    Io(#[from] std::io::Error),

    #[error("JSON error: {0}")]
    Json(#[from] serde_json::Error),
}

pub type DatasetResult<T> = Result<T, DatasetError>;
