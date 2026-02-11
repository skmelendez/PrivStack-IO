//! Query operations: paginated dataset queries, column introspection, raw SQL, aggregations.

use super::helpers::{
    build_filter_clause, build_typed_select, introspect_columns, row_value_to_json,
    sanitize_identifier,
};
use super::DatasetStore;
use crate::error::{DatasetError, DatasetResult};
use crate::schema::dataset_table_name;
use crate::types::{DatasetColumn, DatasetColumnType, DatasetId, DatasetQueryResult, SqlExecutionResult};

impl DatasetStore {
    /// Paginated query against a dataset with optional filter and sort.
    pub fn query_dataset(
        &self,
        id: &DatasetId,
        page: i64,
        page_size: i64,
        filter_text: Option<&str>,
        sort_column: Option<&str>,
        sort_desc: bool,
    ) -> DatasetResult<DatasetQueryResult> {
        let table = dataset_table_name(id);
        let conn = self.lock_conn();

        let columns = introspect_columns(&conn, &table)?;
        let where_clause = build_filter_clause(&columns, filter_text);

        let count_sql = format!("SELECT COUNT(*) FROM {table}{where_clause}");
        let total_count: i64 = conn.query_row(&count_sql, [], |row| row.get(0))?;

        let order_by = match sort_column {
            Some(col) => {
                let sanitized = sanitize_identifier(col);
                let dir = if sort_desc { "DESC" } else { "ASC" };
                format!(" ORDER BY \"{sanitized}\" {dir}")
            }
            None => String::new(),
        };

        let offset = page * page_size;
        let select_clause = build_typed_select(&columns);
        let data_sql =
            format!("SELECT {select_clause} FROM {table}{where_clause}{order_by} LIMIT {page_size} OFFSET {offset}");

        let col_count = columns.len();
        let col_names: Vec<String> = columns.iter().map(|c| c.name.clone()).collect();
        let col_types: Vec<DatasetColumnType> =
            columns.iter().map(|c| c.column_type.clone()).collect();

        let mut stmt = conn.prepare(&data_sql)?;
        let rows: Vec<Vec<serde_json::Value>> = stmt
            .query_map([], |row| {
                let mut vals = Vec::with_capacity(col_count);
                for i in 0..col_count {
                    let val = row_value_to_json(row, i);
                    vals.push(val);
                }
                Ok(vals)
            })?
            .filter_map(|r| r.ok())
            .collect();

        Ok(DatasetQueryResult {
            columns: col_names,
            column_types: col_types,
            rows,
            total_count,
            page,
            page_size,
        })
    }

    /// Get column metadata for a dataset.
    pub fn get_columns(&self, id: &DatasetId) -> DatasetResult<Vec<DatasetColumn>> {
        let table = dataset_table_name(id);
        let conn = self.lock_conn();
        introspect_columns(&conn, &table)
    }

    /// Execute an arbitrary read-only SQL query.
    /// Only SELECT statements are allowed.
    pub fn execute_raw_query(
        &self,
        sql: &str,
        page: i64,
        page_size: i64,
    ) -> DatasetResult<DatasetQueryResult> {
        let sql = sql.trim().trim_end_matches(';').trim();
        let trimmed = sql.to_uppercase();
        if !trimmed.starts_with("SELECT") {
            return Err(DatasetError::InvalidQuery(
                "Only SELECT statements are allowed".to_string(),
            ));
        }

        let conn = self.lock_conn();

        // Use DESCRIBE to get column names and types (avoids DuckDB 1.4.4 panic
        // where stmt.column_count() crashes on un-executed statements).
        let described = Self::describe_query_columns(&conn, sql)?;
        let col_count = described.len();
        let col_names: Vec<String> = described.iter().map(|(n, _)| n.clone()).collect();
        let col_types: Vec<DatasetColumnType> = described.iter().map(|(_, t)| t.clone()).collect();

        let count_sql = format!("SELECT COUNT(*) FROM ({sql}) AS q");
        let total_count: i64 = conn
            .query_row(&count_sql, [], |row| row.get(0))
            .unwrap_or(0);

        // Build a SELECT that casts DATE/TIMESTAMP columns to VARCHAR
        let select_cols: Vec<String> = described
            .iter()
            .map(|(name, col_type)| {
                let safe = sanitize_identifier(name);
                match col_type {
                    DatasetColumnType::Date | DatasetColumnType::Timestamp => {
                        format!("CAST(\"{safe}\" AS VARCHAR) AS \"{safe}\"")
                    }
                    _ => format!("\"{safe}\""),
                }
            })
            .collect();
        let select_clause = select_cols.join(", ");

        let offset = page * page_size;
        let data_sql = format!(
            "SELECT {select_clause} FROM ({sql}) AS q LIMIT {page_size} OFFSET {offset}"
        );

        let mut stmt = conn.prepare(&data_sql)?;
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

        Ok(DatasetQueryResult {
            columns: col_names,
            column_types: col_types,
            rows,
            total_count,
            page,
            page_size,
        })
    }

    /// Get column names and types for an arbitrary SELECT query via DESCRIBE.
    fn describe_query_columns(
        conn: &duckdb::Connection,
        sql: &str,
    ) -> DatasetResult<Vec<(String, DatasetColumnType)>> {
        let desc_sql = format!("DESCRIBE SELECT * FROM ({sql}) AS __subq");
        let mut desc_stmt = conn.prepare(&desc_sql)?;
        let cols: Vec<(String, DatasetColumnType)> = desc_stmt
            .query_map([], |row| {
                let name: String = row.get(0)?;
                let type_name: String = row.get(1)?;
                Ok((name, DatasetColumnType::from_duckdb(&type_name)))
            })?
            .filter_map(|r| r.ok())
            .collect();
        if cols.is_empty() {
            return Err(DatasetError::InvalidQuery(
                "Query returned no columns".to_string(),
            ));
        }
        Ok(cols)
    }

    /// Execute an aggregate query for charts/visualizations.
    pub fn aggregate_query(
        &self,
        dataset_id: &DatasetId,
        x_column: &str,
        y_column: &str,
        aggregation: Option<&str>,
        group_by: Option<&str>,
        filter_text: Option<&str>,
    ) -> DatasetResult<Vec<(serde_json::Value, serde_json::Value)>> {
        let table = dataset_table_name(dataset_id);
        let conn = self.lock_conn();
        let columns = introspect_columns(&conn, &table)?;
        let where_clause = build_filter_clause(&columns, filter_text);

        let x_col = sanitize_identifier(x_column);
        let y_col = sanitize_identifier(y_column);

        // If x or group-by columns are DATE/TIMESTAMP, cast them to VARCHAR for display
        let x_type = columns.iter().find(|c| c.name == x_column).map(|c| &c.column_type);
        let x_is_temporal = matches!(x_type, Some(DatasetColumnType::Date) | Some(DatasetColumnType::Timestamp));
        let x_expr = if x_is_temporal {
            format!("CAST(\"{x_col}\" AS VARCHAR)")
        } else {
            format!("\"{x_col}\"")
        };

        let sql = match (aggregation, group_by) {
            (Some(agg), Some(grp)) => {
                let grp_col = sanitize_identifier(grp);
                let grp_type = columns.iter().find(|c| c.name == grp).map(|c| &c.column_type);
                let grp_is_temporal = matches!(grp_type, Some(DatasetColumnType::Date) | Some(DatasetColumnType::Timestamp));
                let grp_expr = if grp_is_temporal {
                    format!("CAST(\"{grp_col}\" AS VARCHAR)")
                } else {
                    format!("\"{grp_col}\"")
                };
                format!(
                    "SELECT {grp_expr}, {agg}(\"{y_col}\") FROM {table}{where_clause} GROUP BY \"{grp_col}\" ORDER BY \"{grp_col}\""
                )
            }
            (Some(agg), None) => {
                format!(
                    "SELECT {x_expr}, {agg}(\"{y_col}\") FROM {table}{where_clause} GROUP BY \"{x_col}\" ORDER BY \"{x_col}\""
                )
            }
            _ => {
                format!(
                    "SELECT {x_expr}, \"{y_col}\" FROM {table}{where_clause} ORDER BY \"{x_col}\""
                )
            }
        };

        let mut stmt = conn.prepare(&sql)?;
        let rows: Vec<(serde_json::Value, serde_json::Value)> = stmt
            .query_map([], |row| {
                Ok((row_value_to_json(row, 0), row_value_to_json(row, 1)))
            })?
            .filter_map(|r| r.ok())
            .collect();

        Ok(rows)
    }

    /// Unified SQL entry point: routes through preprocessor, then to read or mutation path.
    pub fn execute_sql_v2(
        &self,
        raw_sql: &str,
        page: i64,
        page_size: i64,
        dry_run: bool,
    ) -> DatasetResult<SqlExecutionResult> {
        use super::preprocessor::preprocess_sql;

        // Strip trailing semicolons — wrapping SQL as a subquery
        // (for DESCRIBE, COUNT, pagination) requires clean SQL without terminators.
        let cleaned = raw_sql.trim().trim_end_matches(';').trim();
        if cleaned.is_empty() {
            return Err(DatasetError::InvalidQuery("Empty SQL".to_string()));
        }

        // Resolve source: aliases
        let datasets = self.list()?;
        let preprocessed = preprocess_sql(cleaned, |name| {
            datasets.iter().find(|d| d.name == name).map(|d| d.id.clone())
        })?;

        match preprocessed.statement_type {
            crate::types::StatementType::Select => {
                let result = self.execute_raw_query(&preprocessed.sql, page, page_size)?;
                Ok(SqlExecutionResult::Query(result))
            }
            _ => {
                let result = self.execute_mutation(&preprocessed.sql, dry_run)?;
                Ok(SqlExecutionResult::Mutation(result))
            }
        }
    }
}
