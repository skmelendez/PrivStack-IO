namespace PrivStack.Sdk.Capabilities;

/// <summary>
/// Service interface for dataset CRUD, import, and query operations.
/// Implemented by the desktop shell, consumed by plugins.
/// </summary>
public interface IDatasetService
{
    /// <summary>List all datasets in the current workspace.</summary>
    Task<IReadOnlyList<DatasetInfo>> ListDatasetsAsync(CancellationToken ct = default);

    /// <summary>Get a single dataset's metadata by ID.</summary>
    Task<DatasetInfo?> GetDatasetAsync(string datasetId, CancellationToken ct = default);

    /// <summary>Import a CSV file as a new dataset.</summary>
    Task<DatasetInfo> ImportCsvAsync(string filePath, string name, CancellationToken ct = default);

    /// <summary>Delete a dataset and its backing table.</summary>
    Task DeleteDatasetAsync(string datasetId, CancellationToken ct = default);

    /// <summary>Rename a dataset.</summary>
    Task RenameDatasetAsync(string datasetId, string newName, CancellationToken ct = default);

    /// <summary>Execute a paginated query against a dataset.</summary>
    Task<DatasetQueryResult> QueryAsync(DatasetQuery query, CancellationToken ct = default);

    /// <summary>Get column metadata for a dataset.</summary>
    Task<IReadOnlyList<DatasetColumnInfo>> GetColumnsAsync(string datasetId, CancellationToken ct = default);

    /// <summary>Create a cross-dataset relation.</summary>
    Task<DatasetRelation> CreateRelationAsync(
        string sourceDatasetId, string sourceColumn,
        string targetDatasetId, string targetColumn,
        CancellationToken ct = default);

    /// <summary>Delete a cross-dataset relation.</summary>
    Task DeleteRelationAsync(string relationId, CancellationToken ct = default);

    /// <summary>List relations for a dataset (as source or target).</summary>
    Task<IReadOnlyList<DatasetRelation>> ListRelationsAsync(string datasetId, CancellationToken ct = default);

    /// <summary>Link a dataset row to a Notes page.</summary>
    Task LinkRowToPageAsync(string datasetId, string rowKey, string pageId, CancellationToken ct = default);

    /// <summary>Get the page ID linked to a dataset row.</summary>
    Task<string?> GetPageForRowAsync(string datasetId, string rowKey, CancellationToken ct = default);

    /// <summary>Get the dataset/row linked to a page.</summary>
    Task<(string DatasetId, string RowKey)?> GetRowForPageAsync(string pageId, CancellationToken ct = default);

    /// <summary>Create a saved view for a dataset.</summary>
    Task<DatasetView> CreateViewAsync(string datasetId, string name, ViewConfig config, CancellationToken ct = default);

    /// <summary>Update a saved view's configuration.</summary>
    Task UpdateViewAsync(string viewId, ViewConfig config, CancellationToken ct = default);

    /// <summary>Delete a saved view.</summary>
    Task DeleteViewAsync(string viewId, CancellationToken ct = default);

    /// <summary>List saved views for a dataset.</summary>
    Task<IReadOnlyList<DatasetView>> ListViewsAsync(string datasetId, CancellationToken ct = default);

    /// <summary>Execute an aggregate query for charts/visualizations.</summary>
    Task<AggregateQueryResult> AggregateAsync(AggregateQuery query, CancellationToken ct = default);

    /// <summary>Execute arbitrary read-only SQL against the datasets database.</summary>
    Task<DatasetQueryResult> ExecuteSqlAsync(string sql, int page, int pageSize, CancellationToken ct = default);

    // ── SQL v2 (source: preprocessing + mutations + dry-run) ────────────

    /// <summary>Execute SQL v2 with source: alias resolution, mutations, and dry-run support.</summary>
    Task<SqlExecutionResponse> ExecuteSqlV2Async(string sql, int page, int pageSize, bool dryRun = false, CancellationToken ct = default);

    // ── Dataset creation ────────────────────────────────────────────────

    /// <summary>Create an empty dataset with a defined schema.</summary>
    Task<DatasetInfo> CreateEmptyDatasetAsync(string name, IReadOnlyList<DatasetColumnDef> columns, CancellationToken ct = default);

    /// <summary>Duplicate an existing dataset (schema + data).</summary>
    Task<DatasetInfo> DuplicateDatasetAsync(string sourceDatasetId, string newName, CancellationToken ct = default);

    /// <summary>Import a dataset from CSV/TSV content string (clipboard paste).</summary>
    Task<DatasetInfo> ImportFromContentAsync(string content, string name, CancellationToken ct = default);

    // ── Row CRUD ────────────────────────────────────────────────────────

    /// <summary>Insert a new row into a dataset.</summary>
    Task InsertRowAsync(string datasetId, IDictionary<string, object?> values, CancellationToken ct = default);

    /// <summary>Update a single cell value in a dataset.</summary>
    Task UpdateCellAsync(string datasetId, long rowIndex, string column, object? value, CancellationToken ct = default);

    /// <summary>Delete rows by their indices.</summary>
    Task DeleteRowsAsync(string datasetId, IReadOnlyList<long> rowIndices, CancellationToken ct = default);

    // ── Column CRUD ─────────────────────────────────────────────────────

    /// <summary>Add a column to a dataset.</summary>
    Task AddColumnAsync(string datasetId, string columnName, string columnType, string? defaultValue = null, CancellationToken ct = default);

    /// <summary>Drop a column from a dataset.</summary>
    Task DropColumnAsync(string datasetId, string columnName, CancellationToken ct = default);

    /// <summary>Rename a column in a dataset.</summary>
    Task RenameColumnAsync(string datasetId, string oldName, string newName, CancellationToken ct = default);

    /// <summary>Change a column's data type.</summary>
    Task AlterColumnTypeAsync(string datasetId, string columnName, string newType, CancellationToken ct = default);

    // ── Saved Queries (persisted) ───────────────────────────────────────

    /// <summary>Create a saved query (or view when <paramref name="isView"/> is true).</summary>
    Task<SavedQueryInfo> CreateSavedQueryAsync(string name, string sql, string? description = null, bool isView = false, CancellationToken ct = default);

    /// <summary>Update a saved query's name, SQL, description, or view flag.</summary>
    Task UpdateSavedQueryAsync(string queryId, string name, string sql, string? description = null, bool isView = false, CancellationToken ct = default);

    /// <summary>Delete a saved query.</summary>
    Task DeleteSavedQueryAsync(string queryId, CancellationToken ct = default);

    /// <summary>List all saved queries.</summary>
    Task<IReadOnlyList<SavedQueryInfo>> ListSavedQueriesAsync(CancellationToken ct = default);
}
