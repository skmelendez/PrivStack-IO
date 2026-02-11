//! Row-to-page linking operations (connect dataset rows to Notes pages).

use super::helpers::now_millis;
use super::DatasetStore;
use crate::error::DatasetResult;
use crate::types::DatasetId;
use duckdb::params;
use uuid::Uuid;

impl DatasetStore {
    /// Link a dataset row to a Notes page.
    pub fn link_row_to_page(
        &self,
        dataset_id: &DatasetId,
        row_key: &str,
        page_id: &str,
    ) -> DatasetResult<()> {
        let now = now_millis();
        let conn = self.lock_conn();
        conn.execute(
            "INSERT OR REPLACE INTO _dataset_row_pages (dataset_id, row_index, row_key, page_id, created_at) VALUES (?, 0, ?, ?, ?)",
            params![dataset_id.to_string(), row_key, page_id, now],
        )?;
        Ok(())
    }

    /// Get the page ID linked to a dataset row.
    pub fn get_page_for_row(
        &self,
        dataset_id: &DatasetId,
        row_key: &str,
    ) -> DatasetResult<Option<String>> {
        let conn = self.lock_conn();
        let result = conn.query_row(
            "SELECT page_id FROM _dataset_row_pages WHERE dataset_id = ? AND row_key = ?",
            params![dataset_id.to_string(), row_key],
            |row| row.get::<_, String>(0),
        );
        match result {
            Ok(page_id) => Ok(Some(page_id)),
            Err(duckdb::Error::QueryReturnedNoRows) => Ok(None),
            Err(e) => Err(e.into()),
        }
    }

    /// Get the dataset/row linked to a page.
    pub fn get_row_for_page(
        &self,
        page_id: &str,
    ) -> DatasetResult<Option<(DatasetId, String)>> {
        let conn = self.lock_conn();
        let result = conn.query_row(
            "SELECT dataset_id, row_key FROM _dataset_row_pages WHERE page_id = ?",
            params![page_id],
            |row| Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?)),
        );
        match result {
            Ok((ds_id, row_key)) => Ok(Some((
                DatasetId(Uuid::parse_str(&ds_id).unwrap_or_default()),
                row_key,
            ))),
            Err(duckdb::Error::QueryReturnedNoRows) => Ok(None),
            Err(e) => Err(e.into()),
        }
    }

    /// Unlink a row from its page.
    pub fn unlink_row_page(
        &self,
        dataset_id: &DatasetId,
        row_key: &str,
    ) -> DatasetResult<()> {
        let conn = self.lock_conn();
        conn.execute(
            "DELETE FROM _dataset_row_pages WHERE dataset_id = ? AND row_key = ?",
            params![dataset_id.to_string(), row_key],
        )?;
        Ok(())
    }
}
