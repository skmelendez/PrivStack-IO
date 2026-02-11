//! Saved view CRUD operations.

use super::helpers::now_millis;
use super::DatasetStore;
use crate::error::DatasetResult;
use crate::types::{DatasetId, DatasetView, ViewConfig};
use duckdb::params;
use uuid::Uuid;

impl DatasetStore {
    /// Create a saved view for a dataset.
    pub fn create_view(
        &self,
        dataset_id: &DatasetId,
        name: &str,
        config: &ViewConfig,
    ) -> DatasetResult<DatasetView> {
        let id = Uuid::new_v4().to_string();
        let now = now_millis();
        let config_json = serde_json::to_string(config)?;
        let conn = self.lock_conn();
        conn.execute(
            "INSERT INTO _dataset_views (id, dataset_id, name, config_json, is_default, sort_order, created_at, modified_at) VALUES (?, ?, ?, ?, FALSE, 0, ?, ?)",
            params![id, dataset_id.to_string(), name, config_json, now, now],
        )?;
        Ok(DatasetView {
            id,
            dataset_id: dataset_id.clone(),
            name: name.to_string(),
            config: config.clone(),
            is_default: false,
            sort_order: 0,
            created_at: now,
            modified_at: now,
        })
    }

    /// Update a view's configuration.
    pub fn update_view(&self, view_id: &str, config: &ViewConfig) -> DatasetResult<()> {
        let now = now_millis();
        let config_json = serde_json::to_string(config)?;
        let conn = self.lock_conn();
        conn.execute(
            "UPDATE _dataset_views SET config_json = ?, modified_at = ? WHERE id = ?",
            params![config_json, now, view_id],
        )?;
        Ok(())
    }

    /// Delete a saved view.
    pub fn delete_view(&self, view_id: &str) -> DatasetResult<()> {
        let conn = self.lock_conn();
        conn.execute(
            "DELETE FROM _dataset_views WHERE id = ?",
            params![view_id],
        )?;
        Ok(())
    }

    /// List saved views for a dataset.
    pub fn list_views(
        &self,
        dataset_id: &DatasetId,
    ) -> DatasetResult<Vec<DatasetView>> {
        let conn = self.lock_conn();
        let mut stmt = conn.prepare(
            "SELECT id, name, config_json, is_default, sort_order, created_at, modified_at FROM _dataset_views WHERE dataset_id = ? ORDER BY sort_order, name",
        )?;
        let rows = stmt
            .query_map(params![dataset_id.to_string()], |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, String>(2)?,
                    row.get::<_, bool>(3)?,
                    row.get::<_, i32>(4)?,
                    row.get::<_, i64>(5)?,
                    row.get::<_, i64>(6)?,
                ))
            })?
            .filter_map(|r| r.ok())
            .map(
                |(id, name, cfg_json, is_default, sort_order, created, modified)| {
                    let config: ViewConfig =
                        serde_json::from_str(&cfg_json).unwrap_or(ViewConfig {
                            visible_columns: None,
                            filters: vec![],
                            sorts: vec![],
                            group_by: None,
                        });
                    DatasetView {
                        id,
                        dataset_id: dataset_id.clone(),
                        name,
                        config,
                        is_default,
                        sort_order,
                        created_at: created,
                        modified_at: modified,
                    }
                },
            )
            .collect();
        Ok(rows)
    }
}
