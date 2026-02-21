namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// Stores the parsed result of an AI dataset analysis, keyed by suggestion ID.
/// </summary>
internal sealed record DatasetInsightResult(
    string DatasetId,
    string DatasetName,
    string SuggestionId,
    string RawContent,
    IReadOnlyList<InsightSection> Sections,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> ColumnTypes)
{
    /// <summary>
    /// Columns valid for chart aggregation queries. When insights come from a SQL view,
    /// the view may produce computed columns that don't exist in the underlying dataset.
    /// Falls back to <see cref="Columns"/> when null.
    /// </summary>
    public IReadOnlyList<string>? ChartColumns { get; init; }

    /// <summary>Effective columns for chart marker validation.</summary>
    public IReadOnlyList<string> EffectiveChartColumns => ChartColumns ?? Columns;

    /// <summary>Sample rows from the original query, used for generating chart datasets.</summary>
    public IReadOnlyList<IReadOnlyList<object?>>? SampleRows { get; init; }
}

/// <summary>
/// A single section extracted from the AI's structured response (split on ## headers).
/// </summary>
internal sealed record InsightSection(string Title, string Content);

/// <summary>
/// A chart suggestion parsed from the AI response's structured chart markers.
/// </summary>
internal sealed record ChartSuggestion(
    string ChartType,
    string Title,
    string XColumn,
    string YColumn,
    string? Aggregation,
    string? GroupBy);
