//! Saved query CRUD operations.

use super::helpers::now_millis;
use super::DatasetStore;
use crate::error::DatasetResult;
use crate::types::SavedQuery;
use duckdb::params;
use uuid::Uuid;

impl DatasetStore {
    /// Create a saved query (or view when `is_view` is true).
    pub fn create_saved_query(
        &self,
        name: &str,
        sql: &str,
        description: Option<&str>,
        is_view: bool,
    ) -> DatasetResult<SavedQuery> {
        let id = Uuid::new_v4().to_string();
        let now = now_millis();
        let conn = self.lock_conn();
        conn.execute(
            "INSERT INTO _dataset_saved_queries (id, name, sql, description, is_view, created_at, modified_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
            params![id, name, sql, description, is_view, now, now],
        )?;
        Ok(SavedQuery {
            id,
            name: name.to_string(),
            sql: sql.to_string(),
            description: description.map(String::from),
            is_view,
            created_at: now,
            modified_at: now,
        })
    }

    /// Update a saved query.
    pub fn update_saved_query(
        &self,
        query_id: &str,
        name: &str,
        sql: &str,
        description: Option<&str>,
        is_view: bool,
    ) -> DatasetResult<()> {
        let now = now_millis();
        let conn = self.lock_conn();
        conn.execute(
            "UPDATE _dataset_saved_queries SET name = ?, sql = ?, description = ?, is_view = ?, modified_at = ? WHERE id = ?",
            params![name, sql, description, is_view, now, query_id],
        )?;
        Ok(())
    }

    /// Delete a saved query.
    pub fn delete_saved_query(&self, query_id: &str) -> DatasetResult<()> {
        let conn = self.lock_conn();
        conn.execute(
            "DELETE FROM _dataset_saved_queries WHERE id = ?",
            params![query_id],
        )?;
        Ok(())
    }

    /// List all saved queries.
    pub fn list_saved_queries(&self) -> DatasetResult<Vec<SavedQuery>> {
        let conn = self.lock_conn();
        let mut stmt = conn.prepare(
            "SELECT id, name, sql, description, COALESCE(is_view, FALSE), created_at, modified_at FROM _dataset_saved_queries ORDER BY name",
        )?;
        let rows = stmt
            .query_map([], |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, String>(2)?,
                    row.get::<_, Option<String>>(3)?,
                    row.get::<_, bool>(4)?,
                    row.get::<_, i64>(5)?,
                    row.get::<_, i64>(6)?,
                ))
            })?
            .filter_map(|r| r.ok())
            .map(|(id, name, sql, desc, is_view, created, modified)| SavedQuery {
                id,
                name,
                sql,
                description: desc,
                is_view,
                created_at: created,
                modified_at: modified,
            })
            .collect();
        Ok(rows)
    }
}
