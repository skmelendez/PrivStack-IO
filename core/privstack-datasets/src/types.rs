//! Core data types for the datasets module.

use serde::{Deserialize, Serialize};
use uuid::Uuid;

/// Strongly-typed dataset identifier (NewType pattern).
#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct DatasetId(pub Uuid);

impl DatasetId {
    pub fn new() -> Self {
        Self(Uuid::new_v4())
    }

    /// Table name: `ds_<uuid_no_hyphens>`.
    pub fn table_name(&self) -> String {
        format!("ds_{}", self.0.simple())
    }
}

impl std::fmt::Display for DatasetId {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.0)
    }
}

/// Column type as detected by DuckDB's `read_csv_auto`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum DatasetColumnType {
    Text,
    Integer,
    Float,
    Boolean,
    Date,
    Timestamp,
    Blob,
    Unknown,
}

impl DatasetColumnType {
    /// Map DuckDB type name to our enum.
    pub fn from_duckdb(type_name: &str) -> Self {
        let upper = type_name.to_uppercase();
        match upper.as_str() {
            s if s.contains("VARCHAR") || s.contains("TEXT") || s.contains("CHAR") => Self::Text,
            s if s.contains("BIGINT") || s.contains("INTEGER") || s.contains("SMALLINT")
                || s.contains("TINYINT") || s.contains("HUGEINT") || s.contains("UBIGINT")
                || s.contains("UINTEGER") || s.contains("USMALLINT") || s.contains("UTINYINT") =>
            {
                Self::Integer
            }
            s if s.contains("DOUBLE") || s.contains("FLOAT") || s.contains("REAL")
                || s.contains("DECIMAL") || s.contains("NUMERIC") =>
            {
                Self::Float
            }
            s if s.contains("BOOLEAN") => Self::Boolean,
            s if s.contains("DATE") && !s.contains("TIMESTAMP") => Self::Date,
            s if s.contains("TIMESTAMP") || s.contains("DATETIME") => Self::Timestamp,
            s if s.contains("BLOB") => Self::Blob,
            _ => Self::Unknown,
        }
    }
}

/// A single column in a dataset.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DatasetColumn {
    pub name: String,
    pub column_type: DatasetColumnType,
    pub ordinal: i32,
}

/// Metadata for a stored dataset.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DatasetMeta {
    pub id: DatasetId,
    pub name: String,
    pub source_file_name: Option<String>,
    pub row_count: i64,
    pub columns: Vec<DatasetColumn>,
    pub created_at: i64,
    pub modified_at: i64,
}

/// Result of a paginated query against a dataset.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DatasetQueryResult {
    pub columns: Vec<String>,
    pub column_types: Vec<DatasetColumnType>,
    pub rows: Vec<Vec<serde_json::Value>>,
    pub total_count: i64,
    pub page: i64,
    pub page_size: i64,
}

// ── Phase 5: Relations ──────────────────────────────────────────────────

/// Relation type between datasets.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum RelationType {
    ManyToOne,
    ManyToMany,
}

impl RelationType {
    pub fn as_str(&self) -> &str {
        match self {
            Self::ManyToOne => "many_to_one",
            Self::ManyToMany => "many_to_many",
        }
    }

    pub fn from_str(s: &str) -> Self {
        match s {
            "many_to_many" => Self::ManyToMany,
            _ => Self::ManyToOne,
        }
    }
}

/// Cross-dataset foreign-key-like relation.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DatasetRelation {
    pub id: String,
    pub source_dataset_id: DatasetId,
    pub source_column: String,
    pub target_dataset_id: DatasetId,
    pub target_column: String,
    pub relation_type: RelationType,
    pub created_at: i64,
}

// ── Phase 6: Row-Page Linking ───────────────────────────────────────────

/// Link between a dataset row and a Notes page.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RowPageLink {
    pub dataset_id: DatasetId,
    pub row_index: i64,
    pub row_key: String,
    pub page_id: String,
    pub created_at: i64,
}

// ── Phase 8: Dataset Views ──────────────────────────────────────────────

/// A saved view configuration for a dataset.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DatasetView {
    pub id: String,
    pub dataset_id: DatasetId,
    pub name: String,
    pub config: ViewConfig,
    pub is_default: bool,
    pub sort_order: i32,
    pub created_at: i64,
    pub modified_at: i64,
}

/// View configuration: column visibility, filters, sorts, grouping.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ViewConfig {
    pub visible_columns: Option<Vec<String>>,
    pub filters: Vec<ViewFilter>,
    pub sorts: Vec<ViewSort>,
    pub group_by: Option<String>,
}

/// Filter operator for view filters.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum FilterOperator {
    Equals,
    Contains,
    GreaterThan,
    LessThan,
    IsEmpty,
    IsNotEmpty,
}

/// A single filter condition in a view.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ViewFilter {
    pub column: String,
    pub operator: FilterOperator,
    pub value: String,
}

/// Sort direction.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SortDirection {
    Asc,
    Desc,
}

/// A single sort directive in a view.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ViewSort {
    pub column: String,
    pub direction: SortDirection,
}

// ── Phase 10: Saved Queries ─────────────────────────────────────────────

/// A user-authored SQL query saved for reuse.
/// When `is_view` is true, it appears as an immutable dataset in the sidebar.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SavedQuery {
    pub id: String,
    pub name: String,
    pub sql: String,
    pub description: Option<String>,
    #[serde(default)]
    pub is_view: bool,
    pub created_at: i64,
    pub modified_at: i64,
}

// ── Mutations & SQL v2 ──────────────────────────────────────────────────

/// Column definition for creating empty datasets.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ColumnDef {
    pub name: String,
    pub column_type: String,
}

/// Result of a SQL mutation (INSERT/UPDATE/DELETE/CREATE/ALTER).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MutationResult {
    pub affected_rows: i64,
    pub statement_type: String,
    pub committed: bool,
    pub preview: Option<DatasetQueryResult>,
}

/// Unified result of `execute_sql_v2`: either a query result or a mutation result.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum SqlExecutionResult {
    Query(DatasetQueryResult),
    Mutation(MutationResult),
}

/// Preprocessed SQL with resolved `source:` aliases.
#[derive(Debug, Clone)]
pub struct PreprocessedSql {
    pub sql: String,
    pub statement_type: StatementType,
    pub referenced_datasets: Vec<(String, DatasetId)>,
}

/// SQL statement classification.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum StatementType {
    Select,
    Insert,
    Update,
    Delete,
    CreateTable,
    AlterTable,
    Other,
}
