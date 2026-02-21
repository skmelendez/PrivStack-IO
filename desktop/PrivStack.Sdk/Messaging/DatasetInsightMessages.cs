namespace PrivStack.Sdk.Messaging;

/// <summary>
/// Sent by the Data plugin when the user clicks "Generate Insights" on a dataset.
/// The shell's <c>DatasetInsightOrchestrator</c> subscribes and runs AI analysis.
/// </summary>
public sealed record DatasetInsightRequestMessage
{
    /// <summary>Matching suggestion card ID for status updates.</summary>
    public required string SuggestionId { get; init; }

    /// <summary>Dataset identifier.</summary>
    public required string DatasetId { get; init; }

    /// <summary>Human-readable dataset name.</summary>
    public required string DatasetName { get; init; }

    /// <summary>Column header names.</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>Column type names (e.g., "INTEGER", "VARCHAR").</summary>
    public required IReadOnlyList<string> ColumnTypes { get; init; }

    /// <summary>Sample rows (first ~100) as a list of row arrays.</summary>
    public required IReadOnlyList<IReadOnlyList<object?>> SampleRows { get; init; }

    /// <summary>Total row count in the full dataset.</summary>
    public required long TotalRowCount { get; init; }
}
