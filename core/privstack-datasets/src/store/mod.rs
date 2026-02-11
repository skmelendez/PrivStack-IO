//! Core dataset store — thread-safe DuckDB wrapper with modular operations.

mod crud;
pub(crate) mod helpers;
mod mutations;
pub(crate) mod preprocessor;
mod query;
mod relations;
mod row_pages;
mod saved_queries;
mod views;

use crate::error::{DatasetError, DatasetResult};
use crate::schema::initialize_datasets_schema;
use duckdb::Connection;
use std::path::Path;
use std::sync::{Arc, Mutex, MutexGuard};

/// Thread-safe store for tabular datasets backed by DuckDB.
#[derive(Clone)]
pub struct DatasetStore {
    conn: Arc<Mutex<Connection>>,
}

impl DatasetStore {
    /// Open (or create) the datasets database at the given path.
    pub fn open(path: &Path) -> DatasetResult<Self> {
        let conn = crate::open_datasets_db(path)?;
        initialize_datasets_schema(&conn)?;
        Ok(Self {
            conn: Arc::new(Mutex::new(conn)),
        })
    }

    /// Open an in-memory datasets database (for testing).
    pub fn open_in_memory() -> DatasetResult<Self> {
        let conn = Connection::open_in_memory().map_err(DatasetError::DuckDb)?;
        initialize_datasets_schema(&conn)?;
        Ok(Self {
            conn: Arc::new(Mutex::new(conn)),
        })
    }

    /// Acquire the connection lock, recovering from poison if a prior
    /// `catch_unwind` caught a DuckDB panic while the lock was held.
    pub(crate) fn lock_conn(&self) -> MutexGuard<'_, Connection> {
        self.conn.lock().unwrap_or_else(|poisoned| {
            eprintln!("[DatasetStore] recovering from poisoned mutex");
            poisoned.into_inner()
        })
    }

    /// Run VACUUM / CHECKPOINT for maintenance.
    pub fn maintenance(&self) -> DatasetResult<()> {
        let conn = self.lock_conn();
        conn.execute_batch("CHECKPOINT")?;
        Ok(())
    }
}
