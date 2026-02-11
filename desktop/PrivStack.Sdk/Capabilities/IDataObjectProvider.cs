namespace PrivStack.Sdk.Capabilities;

/// <summary>
/// Cross-plugin capability: allows any plugin to query datasets
/// without depending on the Data plugin directly.
/// </summary>
public interface IDataObjectProvider
{
    /// <summary>Get all available datasets.</summary>
    Task<IReadOnlyList<DatasetInfo>> GetAvailableDatasetsAsync(CancellationToken ct = default);

    /// <summary>Get a dataset by ID.</summary>
    Task<DatasetInfo?> GetDatasetByIdAsync(string datasetId, CancellationToken ct = default);

    /// <summary>Execute a paginated query against a dataset.</summary>
    Task<DatasetQueryResult> QueryDatasetAsync(DatasetQuery query, CancellationToken ct = default);

    /// <summary>Execute an aggregate query for visualizations.</summary>
    Task<AggregateQueryResult> AggregateAsync(AggregateQuery query, CancellationToken ct = default);

    /// <summary>Get all saved query views (queries with IsView=true).</summary>
    Task<IReadOnlyList<SavedQueryInfo>> GetSavedQueryViewsAsync(CancellationToken ct = default);
}
