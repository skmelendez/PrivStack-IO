//! Mutation operations: dataset creation, row CRUD, column CRUD, SQL mutations with dry-run.

use super::helpers::{introspect_columns, now_millis, row_value_to_json, sanitize_identifier};
use super::DatasetStore;
use crate::error::{DatasetError, DatasetResult};
use crate::schema::dataset_table_name;
use crate::types::{ColumnDef, DatasetId, DatasetMeta, DatasetQueryResult, MutationResult};
use duckdb::params;
use tracing::info;

impl DatasetStore {
    /// Create an empty dataset with a defined schema.
    pub fn create_empty(
        &self,
        name: &str,
        columns: &[ColumnDef],
    ) -> DatasetResult<DatasetMeta> {
        if columns.is_empty() {
            return Err(DatasetError::InvalidQuery(
                "At least one column is required".to_string(),
            ));
        }

        let id = DatasetId::new();
        let table = dataset_table_name(&id);
        let now = now_millis();

        let col_defs: Vec<String> = columns
            .iter()
            .map(|c| {
                format!(
                    "\"{}\" {}",
                    sanitize_identifier(&c.name),
                    c.column_type.to_uppercase()
                )
            })
            .collect();
        let create_sql = format!("CREATE TABLE {table} ({})", col_defs.join(", "));

        let conn = self.lock_conn();
        conn.execute_batch(&create_sql)?;

        let ds_columns = introspect_columns(&conn, &table)?;
        let columns_json = serde_json::to_string(&ds_columns)?;

        conn.execute(
            r#"INSERT INTO _datasets_meta (id, name, source_file_name, row_count, columns_json, created_at, modified_at)
               VALUES (?, ?, NULL, 0, ?, ?, ?)"#,
            params![id.to_string(), name, columns_json, now, now],
        )?;

        info!(dataset_id = %id, name, "Empty dataset created");

        Ok(DatasetMeta {
            id,
            name: name.to_string(),
            source_file_name: None,
            row_count: 0,
            columns: ds_columns,
            created_at: now,
            modified_at: now,
        })
    }

    /// Duplicate an existing dataset (schema + data).
    pub fn duplicate(
        &self,
        source_id: &DatasetId,
        new_name: &str,
    ) -> DatasetResult<DatasetMeta> {
        let source_table = dataset_table_name(source_id);
        let new_id = DatasetId::new();
        let new_table = dataset_table_name(&new_id);
        let now = now_millis();

        let conn = self.lock_conn();

        let create_sql = format!("CREATE TABLE {new_table} AS SELECT * FROM {source_table}");
        conn.execute_batch(&create_sql).map_err(|e| {
            DatasetError::ImportFailed(format!("Failed to duplicate dataset: {e}"))
        })?;

        let columns = introspect_columns(&conn, &new_table)?;
        let row_count: i64 = conn
            .query_row(&format!("SELECT COUNT(*) FROM {new_table}"), [], |row| {
                row.get(0)
            })?;
        let columns_json = serde_json::to_string(&columns)?;

        conn.execute(
            r#"INSERT INTO _datasets_meta (id, name, source_file_name, row_count, columns_json, created_at, modified_at)
               VALUES (?, ?, NULL, ?, ?, ?, ?)"#,
            params![new_id.to_string(), new_name, row_count, columns_json, now, now],
        )?;

        info!(dataset_id = %new_id, new_name, "Dataset duplicated from {}", source_id);

        Ok(DatasetMeta {
            id: new_id,
            name: new_name.to_string(),
            source_file_name: None,
            row_count,
            columns,
            created_at: now,
            modified_at: now,
        })
    }

    /// Import dataset from CSV content string (for clipboard paste).
    pub fn import_csv_content(
        &self,
        csv_content: &str,
        name: &str,
    ) -> DatasetResult<DatasetMeta> {
        let id = DatasetId::new();
        let table = dataset_table_name(&id);
        let now = now_millis();

        // Write to a temp file for DuckDB's read_csv_auto
        let tmp_dir = std::env::temp_dir();
        let tmp_path = tmp_dir.join(format!("privstack_import_{}.csv", id.0.simple()));
        std::fs::write(&tmp_path, csv_content)?;

        let conn = self.lock_conn();

        let escaped_path = tmp_path.to_string_lossy().replace('\'', "''");
        let create_sql = format!(
            "CREATE TABLE {table} AS SELECT * FROM read_csv_auto('{escaped_path}', header=true, auto_detect=true)"
        );

        let result = conn.execute_batch(&create_sql);
        // Clean up temp file regardless of outcome
        let _ = std::fs::remove_file(&tmp_path);
        result.map_err(|e| {
            DatasetError::ImportFailed(format!("Failed to import content: {e}"))
        })?;

        let columns = introspect_columns(&conn, &table)?;
        let row_count: i64 = conn
            .query_row(&format!("SELECT COUNT(*) FROM {table}"), [], |row| {
                row.get(0)
            })?;
        let columns_json = serde_json::to_string(&columns)?;

        conn.execute(
            r#"INSERT INTO _datasets_meta (id, name, source_file_name, row_count, columns_json, created_at, modified_at)
               VALUES (?, ?, NULL, ?, ?, ?, ?)"#,
            params![id.to_string(), name, row_count, columns_json, now, now],
        )?;

        info!(dataset_id = %id, name, row_count, "Dataset imported from content");

        Ok(DatasetMeta {
            id,
            name: name.to_string(),
            source_file_name: None,
            row_count,
            columns,
            created_at: now,
            modified_at: now,
        })
    }

    /// Insert a new row into a dataset.
    pub fn insert_row(
        &self,
        id: &DatasetId,
        values: &[(&str, serde_json::Value)],
    ) -> DatasetResult<()> {
        let table = dataset_table_name(id);
        let now = now_millis();

        let col_names: Vec<String> = values
            .iter()
            .map(|(name, _)| format!("\"{}\"", sanitize_identifier(name)))
            .collect();
        let placeholders: Vec<&str> = values.iter().map(|_| "?").collect();

        let sql = format!(
            "INSERT INTO {table} ({}) VALUES ({})",
            col_names.join(", "),
            placeholders.join(", ")
        );

        let conn = self.lock_conn();

        // Build parameter list from JSON values
        let str_vals: Vec<String> = values
            .iter()
            .map(|(_, v)| json_value_to_sql_string(v))
            .collect();
        let param_refs: Vec<&dyn duckdb::ToSql> = str_vals
            .iter()
            .map(|s| s as &dyn duckdb::ToSql)
            .collect();

        conn.execute(&sql, param_refs.as_slice())?;
        self.update_row_count_and_meta(&conn, id, &table, now)?;

        Ok(())
    }

    /// Update a single cell value.
    pub fn update_cell(
        &self,
        id: &DatasetId,
        row_index: i64,
        column: &str,
        value: serde_json::Value,
    ) -> DatasetResult<()> {
        let table = dataset_table_name(id);
        let now = now_millis();
        let col = sanitize_identifier(column);
        let val_str = json_value_to_sql_string(&value);

        // Use DuckDB's rowid pseudo-column for stable row addressing
        let sql = format!(
            "UPDATE {table} SET \"{col}\" = ? WHERE rowid = (SELECT rowid FROM {table} LIMIT 1 OFFSET ?)"
        );

        let conn = self.lock_conn();
        conn.execute(&sql, params![val_str, row_index])?;

        conn.execute(
            "UPDATE _datasets_meta SET modified_at = ? WHERE id = ?",
            params![now, id.to_string()],
        )?;

        Ok(())
    }

    /// Delete rows by their indices.
    pub fn delete_rows(
        &self,
        id: &DatasetId,
        row_indices: &[i64],
    ) -> DatasetResult<()> {
        if row_indices.is_empty() {
            return Ok(());
        }

        let table = dataset_table_name(id);
        let now = now_millis();
        let conn = self.lock_conn();

        // Delete using rowid-based subqueries for each index
        for &idx in row_indices {
            let sql = format!(
                "DELETE FROM {table} WHERE rowid = (SELECT rowid FROM {table} LIMIT 1 OFFSET ?)"
            );
            conn.execute(&sql, params![idx])?;
        }

        self.update_row_count_and_meta(&conn, id, &table, now)?;
        Ok(())
    }

    /// Add a column to a dataset.
    pub fn add_column(
        &self,
        id: &DatasetId,
        name: &str,
        col_type: &str,
        default: Option<&str>,
    ) -> DatasetResult<()> {
        let table = dataset_table_name(id);
        let now = now_millis();
        let col = sanitize_identifier(name);
        let dtype = col_type.to_uppercase();

        let default_clause = match default {
            Some(d) => format!(" DEFAULT '{}'", d.replace('\'', "''")),
            None => String::new(),
        };

        let sql = format!("ALTER TABLE {table} ADD COLUMN \"{col}\" {dtype}{default_clause}");

        let conn = self.lock_conn();
        conn.execute_batch(&sql)?;

        // Refresh column metadata
        let columns = introspect_columns(&conn, &table)?;
        let columns_json = serde_json::to_string(&columns)?;
        conn.execute(
            "UPDATE _datasets_meta SET columns_json = ?, modified_at = ? WHERE id = ?",
            params![columns_json, now, id.to_string()],
        )?;

        Ok(())
    }

    /// Drop a column from a dataset.
    pub fn drop_column(&self, id: &DatasetId, name: &str) -> DatasetResult<()> {
        let table = dataset_table_name(id);
        let now = now_millis();
        let col = sanitize_identifier(name);

        let sql = format!("ALTER TABLE {table} DROP COLUMN \"{col}\"");

        let conn = self.lock_conn();
        conn.execute_batch(&sql)?;

        let columns = introspect_columns(&conn, &table)?;
        let columns_json = serde_json::to_string(&columns)?;
        conn.execute(
            "UPDATE _datasets_meta SET columns_json = ?, modified_at = ? WHERE id = ?",
            params![columns_json, now, id.to_string()],
        )?;

        Ok(())
    }

    /// Rename a column in a dataset.
    pub fn rename_column(
        &self,
        id: &DatasetId,
        old_name: &str,
        new_name: &str,
    ) -> DatasetResult<()> {
        let table = dataset_table_name(id);
        let now = now_millis();
        let old_col = sanitize_identifier(old_name);
        let new_col = sanitize_identifier(new_name);

        let sql = format!("ALTER TABLE {table} RENAME COLUMN \"{old_col}\" TO \"{new_col}\"");

        let conn = self.lock_conn();
        conn.execute_batch(&sql)?;

        let columns = introspect_columns(&conn, &table)?;
        let columns_json = serde_json::to_string(&columns)?;
        conn.execute(
            "UPDATE _datasets_meta SET columns_json = ?, modified_at = ? WHERE id = ?",
            params![columns_json, now, id.to_string()],
        )?;

        Ok(())
    }

    /// Execute a SQL mutation (INSERT/UPDATE/DELETE/CREATE/ALTER) with optional dry-run.
    ///
    /// When `dry_run` is true, the mutation is executed inside a transaction that is
    /// rolled back, returning a preview of affected rows without persisting changes.
    pub fn execute_mutation(
        &self,
        sql: &str,
        dry_run: bool,
    ) -> DatasetResult<MutationResult> {
        let stmt_type = classify_statement(sql);
        let conn = self.lock_conn();

        if dry_run {
            conn.execute_batch("BEGIN TRANSACTION")?;

            let execute_result = conn.execute(sql, []);
            match execute_result {
                Ok(affected) => {
                    let preview = self.query_mutation_preview(&conn, sql, &stmt_type);
                    conn.execute_batch("ROLLBACK")?;

                    Ok(MutationResult {
                        affected_rows: affected as i64,
                        statement_type: stmt_type,
                        committed: false,
                        preview: preview.ok(),
                    })
                }
                Err(e) => {
                    let _ = conn.execute_batch("ROLLBACK");
                    Err(DatasetError::DuckDb(e))
                }
            }
        } else {
            let affected = conn.execute(sql, [])?;
            Ok(MutationResult {
                affected_rows: affected as i64,
                statement_type: stmt_type,
                committed: true,
                preview: None,
            })
        }
    }

    /// Query a preview of mutation results (called inside dry-run transaction).
    fn query_mutation_preview(
        &self,
        conn: &duckdb::Connection,
        sql: &str,
        _stmt_type: &str,
    ) -> DatasetResult<DatasetQueryResult> {
        // Try to extract table name and query it for preview
        let table_name = extract_table_name(sql);
        if let Some(table) = table_name {
            let preview_sql = format!("SELECT * FROM {table} LIMIT 50");
            let mut stmt = conn.prepare(&preview_sql)?;
            let col_count = stmt.column_count();
            let col_names: Vec<String> = (0..col_count)
                .map(|i| stmt.column_name(i).map_or("?", |v| v).to_string())
                .collect();

            let rows: Vec<Vec<serde_json::Value>> = stmt
                .query_map([], |row| {
                    let mut vals = Vec::with_capacity(col_count);
                    for i in 0..col_count {
                        vals.push(row_value_to_json(row, i));
                    }
                    Ok(vals)
                })?
                .filter_map(|r| r.ok())
                .collect();

            let total: i64 = conn
                .query_row(&format!("SELECT COUNT(*) FROM {table}"), [], |r| r.get(0))
                .unwrap_or(rows.len() as i64);

            return Ok(DatasetQueryResult {
                columns: col_names,
                column_types: vec![],
                rows,
                total_count: total,
                page: 0,
                page_size: 50,
            });
        }

        Ok(DatasetQueryResult {
            columns: vec![],
            column_types: vec![],
            rows: vec![],
            total_count: 0,
            page: 0,
            page_size: 0,
        })
    }

    /// Helper: update row count and column metadata after a mutation.
    fn update_row_count_and_meta(
        &self,
        conn: &duckdb::Connection,
        id: &DatasetId,
        table: &str,
        now: i64,
    ) -> DatasetResult<()> {
        let row_count: i64 = conn
            .query_row(&format!("SELECT COUNT(*) FROM {table}"), [], |row| {
                row.get(0)
            })?;

        conn.execute(
            "UPDATE _datasets_meta SET row_count = ?, modified_at = ? WHERE id = ?",
            params![row_count, now, id.to_string()],
        )?;

        Ok(())
    }
}

/// Classify a SQL statement by its first keyword.
fn classify_statement(sql: &str) -> String {
    let upper = sql.trim().to_uppercase();
    if upper.starts_with("SELECT") {
        "SELECT".to_string()
    } else if upper.starts_with("INSERT") {
        "INSERT".to_string()
    } else if upper.starts_with("UPDATE") {
        "UPDATE".to_string()
    } else if upper.starts_with("DELETE") {
        "DELETE".to_string()
    } else if upper.starts_with("CREATE") {
        "CREATE".to_string()
    } else if upper.starts_with("ALTER") {
        "ALTER".to_string()
    } else {
        "OTHER".to_string()
    }
}

/// Extract the target table name from a SQL statement (best-effort).
fn extract_table_name(sql: &str) -> Option<String> {
    let upper = sql.trim().to_uppercase();
    let tokens: Vec<&str> = sql.split_whitespace().collect();

    if upper.starts_with("INSERT INTO") {
        tokens.get(2).map(|t| t.trim_matches('(').to_string())
    } else if upper.starts_with("UPDATE") {
        tokens.get(1).map(|s| s.to_string())
    } else if upper.starts_with("DELETE FROM") {
        tokens.get(2).map(|s| s.to_string())
    } else if upper.starts_with("CREATE TABLE") {
        // Skip IF NOT EXISTS
        if upper.contains("IF NOT EXISTS") {
            tokens.get(5).map(|t| t.trim_matches('(').to_string())
        } else {
            tokens.get(2).map(|t| t.trim_matches('(').to_string())
        }
    } else if upper.starts_with("ALTER TABLE") {
        tokens.get(2).map(|s| s.to_string())
    } else {
        None
    }
}

/// Convert a serde_json::Value to a SQL-safe string for parameterized queries.
fn json_value_to_sql_string(value: &serde_json::Value) -> String {
    match value {
        serde_json::Value::Null => String::new(),
        serde_json::Value::Bool(b) => b.to_string(),
        serde_json::Value::Number(n) => n.to_string(),
        serde_json::Value::String(s) => s.clone(),
        other => other.to_string(),
    }
}
