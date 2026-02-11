//! Core CRUD operations: import, list, get, delete, rename.

use super::helpers::{introspect_columns, now_millis};
use super::DatasetStore;
use crate::error::{DatasetError, DatasetResult};
use crate::schema::dataset_table_name;
use crate::types::{DatasetColumn, DatasetId, DatasetMeta};
use duckdb::params;
use std::path::Path;
use tracing::info;
use uuid::Uuid;

impl DatasetStore {
    /// Import a CSV file into a new dataset.
    pub fn import_csv(&self, file_path: &Path, name: &str) -> DatasetResult<DatasetMeta> {
        if !file_path.exists() {
            return Err(DatasetError::ImportFailed(format!(
                "File not found: {}",
                file_path.display()
            )));
        }

        let id = DatasetId::new();
        let table = dataset_table_name(&id);
        let escaped_path = file_path.to_string_lossy().replace('\'', "''");
        let now = now_millis();

        let conn = self.lock_conn();

        let create_sql = format!(
            "CREATE TABLE {table} AS SELECT * FROM read_csv_auto('{escaped_path}', header=true, auto_detect=true)"
        );
        conn.execute_batch(&create_sql).map_err(|e| {
            DatasetError::ImportFailed(format!("Failed to import CSV: {e}"))
        })?;

        let columns = introspect_columns(&conn, &table)?;
        let row_count: i64 = conn
            .query_row(&format!("SELECT COUNT(*) FROM {table}"), [], |row| {
                row.get(0)
            })?;

        let columns_json = serde_json::to_string(&columns)?;
        let source_file_name = file_path
            .file_name()
            .map(|f| f.to_string_lossy().to_string());

        conn.execute(
            r#"INSERT INTO _datasets_meta (id, name, source_file_name, row_count, columns_json, created_at, modified_at)
               VALUES (?, ?, ?, ?, ?, ?, ?)"#,
            params![
                id.to_string(),
                name,
                source_file_name,
                row_count,
                columns_json,
                now,
                now,
            ],
        )?;

        info!(dataset_id = %id, name, row_count, "Dataset imported");

        Ok(DatasetMeta {
            id,
            name: name.to_string(),
            source_file_name,
            row_count,
            columns,
            created_at: now,
            modified_at: now,
        })
    }

    /// List all datasets.
    pub fn list(&self) -> DatasetResult<Vec<DatasetMeta>> {
        let conn = self.lock_conn();
        let mut stmt = conn.prepare(
            "SELECT id, name, source_file_name, row_count, columns_json, created_at, modified_at FROM _datasets_meta ORDER BY modified_at DESC"
        )?;

        let rows = stmt
            .query_map([], |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, Option<String>>(2)?,
                    row.get::<_, i64>(3)?,
                    row.get::<_, String>(4)?,
                    row.get::<_, i64>(5)?,
                    row.get::<_, i64>(6)?,
                ))
            })?
            .filter_map(|r| r.ok())
            .collect::<Vec<_>>();

        drop(stmt);
        drop(conn);

        rows.into_iter()
            .map(|(id, name, source, row_count, cols_json, created, modified)| {
                let columns: Vec<DatasetColumn> =
                    serde_json::from_str(&cols_json).unwrap_or_default();
                Ok(DatasetMeta {
                    id: DatasetId(Uuid::parse_str(&id).map_err(|e| {
                        DatasetError::ImportFailed(format!("Invalid UUID: {e}"))
                    })?),
                    name,
                    source_file_name: source,
                    row_count,
                    columns,
                    created_at: created,
                    modified_at: modified,
                })
            })
            .collect()
    }

    /// Get a single dataset's metadata by ID.
    pub fn get(&self, id: &DatasetId) -> DatasetResult<DatasetMeta> {
        let conn = self.lock_conn();
        let result = conn.query_row(
            "SELECT name, source_file_name, row_count, columns_json, created_at, modified_at FROM _datasets_meta WHERE id = ?",
            params![id.to_string()],
            |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, Option<String>>(1)?,
                    row.get::<_, i64>(2)?,
                    row.get::<_, String>(3)?,
                    row.get::<_, i64>(4)?,
                    row.get::<_, i64>(5)?,
                ))
            },
        );

        match result {
            Ok((name, source, row_count, cols_json, created, modified)) => {
                let columns: Vec<DatasetColumn> =
                    serde_json::from_str(&cols_json).unwrap_or_default();
                Ok(DatasetMeta {
                    id: id.clone(),
                    name,
                    source_file_name: source,
                    row_count,
                    columns,
                    created_at: created,
                    modified_at: modified,
                })
            }
            Err(duckdb::Error::QueryReturnedNoRows) => {
                Err(DatasetError::NotFound(id.to_string()))
            }
            Err(e) => Err(DatasetError::DuckDb(e)),
        }
    }

    /// Delete a dataset and its backing table.
    pub fn delete(&self, id: &DatasetId) -> DatasetResult<()> {
        let table = dataset_table_name(id);
        let conn = self.lock_conn();

        conn.execute_batch(&format!("DROP TABLE IF EXISTS {table}"))?;

        let deleted = conn.execute(
            "DELETE FROM _datasets_meta WHERE id = ?",
            params![id.to_string()],
        )?;

        conn.execute(
            "DELETE FROM _dataset_relations WHERE source_dataset_id = ? OR target_dataset_id = ?",
            params![id.to_string(), id.to_string()],
        )?;
        conn.execute(
            "DELETE FROM _dataset_row_pages WHERE dataset_id = ?",
            params![id.to_string()],
        )?;
        conn.execute(
            "DELETE FROM _dataset_views WHERE dataset_id = ?",
            params![id.to_string()],
        )?;

        if deleted == 0 {
            return Err(DatasetError::NotFound(id.to_string()));
        }

        info!(dataset_id = %id, "Dataset deleted");
        Ok(())
    }

    /// Rename a dataset.
    pub fn rename(&self, id: &DatasetId, new_name: &str) -> DatasetResult<()> {
        let now = now_millis();
        let conn = self.lock_conn();
        let updated = conn.execute(
            "UPDATE _datasets_meta SET name = ?, modified_at = ? WHERE id = ?",
            params![new_name, now, id.to_string()],
        )?;

        if updated == 0 {
            return Err(DatasetError::NotFound(id.to_string()));
        }
        Ok(())
    }
}
