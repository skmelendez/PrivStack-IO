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
    IReadOnlyList<string> ColumnTypes);

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
