//! SQL preprocessor: resolves `source:Name` aliases to dataset table names.

use crate::error::{DatasetError, DatasetResult};
use crate::types::{DatasetId, PreprocessedSql, StatementType};

/// Preprocess SQL by resolving `source:Name` or `source:"Quoted Name"` aliases
/// into their actual DuckDB table names (`ds_<uuid>`).
pub fn preprocess_sql(
    raw_sql: &str,
    resolver: impl Fn(&str) -> Option<DatasetId>,
) -> DatasetResult<PreprocessedSql> {
    let mut sql = raw_sql.to_string();
    let mut referenced_datasets = Vec::new();

    // Match source:Name or source:"Quoted Name"
    let re = regex_lite::Regex::new(r#"source:("([^"]+)"|([A-Za-z0-9_]+))"#)
        .map_err(|e| DatasetError::InvalidQuery(format!("Regex error: {e}")))?;

    // Collect all matches first to avoid borrow issues
    let matches: Vec<(String, String)> = re
        .captures_iter(raw_sql)
        .filter_map(|cap| {
            let full_match = cap.get(0)?.as_str().to_string();
            // Quoted name or bare name
            let name = cap
                .get(2)
                .or_else(|| cap.get(3))
                .map(|m| m.as_str().to_string())?;
            Some((full_match, name))
        })
        .collect();

    for (full_match, name) in matches {
        if let Some(id) = resolver(&name) {
            let table_name = id.table_name();
            sql = sql.replace(&full_match, &table_name);
            referenced_datasets.push((name, id));
        } else {
            return Err(DatasetError::NotFound(format!(
                "Dataset not found: {name}"
            )));
        }
    }

    let statement_type = classify_sql(&sql);

    Ok(PreprocessedSql {
        sql,
        statement_type,
        referenced_datasets,
    })
}

/// Classify the SQL statement type from its first keyword.
fn classify_sql(sql: &str) -> StatementType {
    let upper = sql.trim().to_uppercase();
    if upper.starts_with("SELECT") || upper.starts_with("WITH") {
        StatementType::Select
    } else if upper.starts_with("INSERT") {
        StatementType::Insert
    } else if upper.starts_with("UPDATE") {
        StatementType::Update
    } else if upper.starts_with("DELETE") {
        StatementType::Delete
    } else if upper.starts_with("CREATE TABLE") {
        StatementType::CreateTable
    } else if upper.starts_with("ALTER TABLE") {
        StatementType::AlterTable
    } else {
        StatementType::Other
    }
}
