//! Cross-dataset relation operations.

use super::helpers::now_millis;
use super::DatasetStore;
use crate::error::DatasetResult;
use crate::types::{DatasetId, DatasetRelation, RelationType};
use duckdb::params;
use uuid::Uuid;

impl DatasetStore {
    /// Create a cross-dataset relation.
    pub fn create_relation(
        &self,
        source_dataset_id: &DatasetId,
        source_column: &str,
        target_dataset_id: &DatasetId,
        target_column: &str,
    ) -> DatasetResult<DatasetRelation> {
        let id = Uuid::new_v4().to_string();
        let now = now_millis();
        let conn = self.lock_conn();
        conn.execute(
            "INSERT INTO _dataset_relations (id, source_dataset_id, source_column, target_dataset_id, target_column, relation_type, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
            params![id, source_dataset_id.to_string(), source_column, target_dataset_id.to_string(), target_column, "many_to_one", now],
        )?;
        Ok(DatasetRelation {
            id,
            source_dataset_id: source_dataset_id.clone(),
            source_column: source_column.to_string(),
            target_dataset_id: target_dataset_id.clone(),
            target_column: target_column.to_string(),
            relation_type: RelationType::ManyToOne,
            created_at: now,
        })
    }

    /// Delete a relation by ID.
    pub fn delete_relation(&self, relation_id: &str) -> DatasetResult<()> {
        let conn = self.lock_conn();
        conn.execute(
            "DELETE FROM _dataset_relations WHERE id = ?",
            params![relation_id],
        )?;
        Ok(())
    }

    /// List relations where a dataset is source OR target.
    pub fn list_relations(
        &self,
        dataset_id: &DatasetId,
    ) -> DatasetResult<Vec<DatasetRelation>> {
        let conn = self.lock_conn();
        let id_str = dataset_id.to_string();
        let mut stmt = conn.prepare(
            "SELECT id, source_dataset_id, source_column, target_dataset_id, target_column, relation_type, created_at FROM _dataset_relations WHERE source_dataset_id = ? OR target_dataset_id = ?",
        )?;
        let rows = stmt
            .query_map(params![id_str, id_str], |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, String>(2)?,
                    row.get::<_, String>(3)?,
                    row.get::<_, String>(4)?,
                    row.get::<_, String>(5)?,
                    row.get::<_, i64>(6)?,
                ))
            })?
            .filter_map(|r| r.ok())
            .map(
                |(id, src_ds, src_col, tgt_ds, tgt_col, rel_type, created)| DatasetRelation {
                    id,
                    source_dataset_id: DatasetId(Uuid::parse_str(&src_ds).unwrap_or_default()),
                    source_column: src_col,
                    target_dataset_id: DatasetId(Uuid::parse_str(&tgt_ds).unwrap_or_default()),
                    target_column: tgt_col,
                    relation_type: RelationType::from_str(&rel_type),
                    created_at: created,
                },
            )
            .collect();
        Ok(rows)
    }
}
