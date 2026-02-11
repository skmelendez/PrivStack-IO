//! DDL and schema helpers for the datasets database.

use crate::error::DatasetResult;
use duckdb::Connection;

/// Metadata table DDL — stores info about each imported dataset.
const DATASETS_META_DDL: &str = r#"
CREATE TABLE IF NOT EXISTS _datasets_meta (
    id VARCHAR PRIMARY KEY,
    name VARCHAR NOT NULL,
    source_file_name VARCHAR,
    row_count BIGINT NOT NULL DEFAULT 0,
    columns_json TEXT NOT NULL DEFAULT '[]',
    created_at BIGINT NOT NULL,
    modified_at BIGINT NOT NULL
);
"#;

/// Relations table DDL — foreign-key-like links between datasets.
const DATASET_RELATIONS_DDL: &str = r#"
CREATE TABLE IF NOT EXISTS _dataset_relations (
    id VARCHAR PRIMARY KEY,
    source_dataset_id VARCHAR NOT NULL,
    source_column VARCHAR NOT NULL,
    target_dataset_id VARCHAR NOT NULL,
    target_column VARCHAR NOT NULL,
    relation_type VARCHAR DEFAULT 'many_to_one',
    created_at BIGINT NOT NULL,
    FOREIGN KEY (source_dataset_id) REFERENCES _datasets_meta(id),
    FOREIGN KEY (target_dataset_id) REFERENCES _datasets_meta(id)
);
"#;

/// Row-page linking table DDL — maps dataset rows to Notes pages.
const DATASET_ROW_PAGES_DDL: &str = r#"
CREATE TABLE IF NOT EXISTS _dataset_row_pages (
    dataset_id VARCHAR NOT NULL,
    row_index BIGINT NOT NULL,
    row_key VARCHAR NOT NULL,
    page_id VARCHAR NOT NULL,
    created_at BIGINT NOT NULL,
    PRIMARY KEY (dataset_id, row_key)
);
"#;

/// Saved views table DDL — named view configs per dataset.
const DATASET_VIEWS_DDL: &str = r#"
CREATE TABLE IF NOT EXISTS _dataset_views (
    id VARCHAR PRIMARY KEY,
    dataset_id VARCHAR NOT NULL,
    name VARCHAR NOT NULL,
    config_json TEXT NOT NULL,
    is_default BOOLEAN DEFAULT FALSE,
    sort_order INTEGER DEFAULT 0,
    created_at BIGINT NOT NULL,
    modified_at BIGINT NOT NULL,
    FOREIGN KEY (dataset_id) REFERENCES _datasets_meta(id)
);
"#;

/// Saved queries table DDL — user-authored SQL queries.
const DATASET_SAVED_QUERIES_DDL: &str = r#"
CREATE TABLE IF NOT EXISTS _dataset_saved_queries (
    id VARCHAR PRIMARY KEY,
    name VARCHAR NOT NULL,
    sql TEXT NOT NULL,
    description VARCHAR,
    created_at BIGINT NOT NULL,
    modified_at BIGINT NOT NULL
);
"#;

/// Migration: add is_view flag to saved queries.
const SAVED_QUERIES_ADD_IS_VIEW: &str =
    "ALTER TABLE _dataset_saved_queries ADD COLUMN IF NOT EXISTS is_view BOOLEAN DEFAULT FALSE;";

/// Initialize all dataset schema tables.
pub fn initialize_datasets_schema(conn: &Connection) -> DatasetResult<()> {
    conn.execute_batch(DATASETS_META_DDL)?;
    conn.execute_batch(DATASET_RELATIONS_DDL)?;
    conn.execute_batch(DATASET_ROW_PAGES_DDL)?;
    conn.execute_batch(DATASET_VIEWS_DDL)?;
    conn.execute_batch(DATASET_SAVED_QUERIES_DDL)?;
    // Migrations
    conn.execute_batch(SAVED_QUERIES_ADD_IS_VIEW)?;
    Ok(())
}

/// Derive the per-dataset table name from a dataset ID.
/// Format: `ds_<uuid_no_hyphens>`.
pub fn dataset_table_name(id: &crate::types::DatasetId) -> String {
    id.table_name()
}
