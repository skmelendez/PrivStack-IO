using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Messaging;
using PrivStack.Sdk;
using PrivStack.Sdk.Capabilities;
using PrivStack.Sdk.Messaging;
using PrivStack.Sdk.Services;
using Serilog;

namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// Shell-side orchestrator that receives dataset insight requests from the Data plugin,
/// runs AI analysis, and saves results as structured Notes pages with charts.
/// </summary>
internal sealed class DatasetInsightOrchestrator
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DatasetInsightOrchestrator>();

    private readonly AiService _aiService;
    private readonly IAiSuggestionService _suggestionService;
    private readonly IPrivStackSdk _sdk;
    private readonly IToastService _toast;
    private readonly INavigationService _navigation;

    private readonly ConcurrentDictionary<string, DatasetInsightResult> _results = new();

    public DatasetInsightOrchestrator(
        AiService aiService,
        IAiSuggestionService suggestionService,
        IPrivStackSdk sdk,
        IToastService toast,
        INavigationService navigation)
    {
        _aiService = aiService;
        _suggestionService = suggestionService;
        _sdk = sdk;
        _toast = toast;
        _navigation = navigation;

        WeakReferenceMessenger.Default.Register<DatasetInsightRequestMessage>(this, OnInsightRequested);
        WeakReferenceMessenger.Default.Register<ContentSuggestionActionRequestedMessage>(this, OnActionRequested);
    }

    private async void OnInsightRequested(object recipient, DatasetInsightRequestMessage msg)
    {
        try
        {
            await GenerateInsightsAsync(msg);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Dataset insight generation failed for {DatasetId}", msg.DatasetId);
            _suggestionService.Update(msg.SuggestionId, "privstack.data",
                newState: ContentSuggestionState.Error,
                errorMessage: $"Analysis failed: {ex.Message}");
        }
    }

    private async Task GenerateInsightsAsync(DatasetInsightRequestMessage msg)
    {
        var modelInfo = _aiService.GetActiveModelInfo();
        var contextWindow = modelInfo?.ContextWindowTokens ?? 4_096;
        var isLargeContext = contextWindow >= 100_000;

        // Scale sample data budget and output tokens to model capability
        var sampleBudget = isLargeContext ? 20_000 : 6_000;
        var maxOutputTokens = isLargeContext ? 12_000 : 2_048;

        var tabularText = BuildTabularText(msg.Columns, msg.ColumnTypes, msg.SampleRows, sampleBudget);
        var columnList = string.Join(", ", msg.Columns.Select((c, i) =>
            i < msg.ColumnTypes.Count ? $"{c} ({msg.ColumnTypes[i]})" : c));

        // When insights come from a SQL view, charts must reference the underlying dataset columns
        var chartColumns = msg.ChartEligibleColumns ?? msg.Columns;
        var chartColumnTypes = msg.ChartEligibleColumnTypes ?? msg.ColumnTypes;
        var chartColumnList = ReferenceEquals(chartColumns, msg.Columns)
            ? columnList
            : string.Join(", ", chartColumns.Select((c, i) =>
                i < chartColumnTypes.Count ? $"{c} ({chartColumnTypes[i]})" : c));

        var systemPrompt = isLargeContext
            ? BuildCloudSystemPrompt(columnList, chartColumnList)
            : BuildLocalSystemPrompt(columnList, chartColumnList);

        var request = new AiRequest
        {
            SystemPrompt = systemPrompt,
            UserPrompt = $"""
                Dataset: "{msg.DatasetName}"
                Total rows: {msg.TotalRowCount:N0}
                Columns: {columnList}
                Sample ({Math.Min(msg.SampleRows.Count, 100)} rows shown):

                {tabularText}
                """,
            MaxTokens = maxOutputTokens,
            Temperature = 0.3,
            FeatureId = "data.insights",
        };

        var response = await _aiService.CompleteAsync(request);

        if (!response.Success || string.IsNullOrWhiteSpace(response.Content))
        {
            _suggestionService.Update(msg.SuggestionId, "privstack.data",
                newState: ContentSuggestionState.Error,
                errorMessage: response.ErrorMessage ?? "AI returned empty response");
            return;
        }

        var sections = ParseSections(response.Content);
        var result = new DatasetInsightResult(
            msg.DatasetId, msg.DatasetName, msg.SuggestionId,
            response.Content, sections,
            msg.Columns.ToList(), msg.ColumnTypes.ToList())
        {
            ChartColumns = msg.ChartEligibleColumns?.ToList(),
        };

        _results[msg.SuggestionId] = result;

        var preview = sections.Count > 0
            ? string.Join(", ", sections.Take(3).Select(s => s.Title))
            : response.Content[..Math.Min(200, response.Content.Length)];

        _suggestionService.Update(msg.SuggestionId, "privstack.data",
            newState: ContentSuggestionState.Ready,
            newContent: preview,
            newActions:
            [
                new SuggestionAction
                {
                    ActionId = "save_insight_notes",
                    DisplayName = "Save as Notes",
                    IsPrimary = true,
                },
            ]);
    }

    // ── Action handling ─────────────────────────────────────────────────

    private async void OnActionRequested(object recipient, ContentSuggestionActionRequestedMessage msg)
    {
        if (msg.ActionId == "save_insight_notes")
        {
            if (!_results.TryGetValue(msg.SuggestionId, out var result)) return;

            try
            {
                await CreateInsightNotesAsync(result);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save insight notes for {DatasetId}", result.DatasetId);
                _suggestionService.Update(msg.SuggestionId, "privstack.data",
                    newState: ContentSuggestionState.Error,
                    errorMessage: $"Failed to save notes: {ex.Message}");
            }
        }
        else if (msg.ActionId.StartsWith("view_page:", StringComparison.Ordinal))
        {
            var pageId = msg.ActionId[10..];
            await _navigation.NavigateToItemAsync("page", pageId);
        }
    }

    private async Task CreateInsightNotesAsync(DatasetInsightResult result)
    {
        var parentTitle = $"{AiPersona.Name} Generated Data Insights";
        var parentId = await FindOrCreateParentPageAsync(parentTitle);

        var subPageTitle = $"{result.DatasetName} Insights — {DateTime.Now:yyyy-MM-dd}";
        var subPageId = Guid.NewGuid().ToString();

        var blocks = new List<JsonObject>
        {
            InsightPageBuilder.BuildParagraphBlock(
                $"Auto-generated insights for dataset \"{result.DatasetName}\" on {DateTime.Now:MMMM d, yyyy}."),
        };

        foreach (var section in result.Sections)
        {
            blocks.Add(InsightPageBuilder.BuildHeadingBlock(2, section.Title));
            BuildSectionBlocks(section.Content, result, blocks);
        }

        // If no parsed sections, add raw content as paragraphs
        if (result.Sections.Count == 0)
            BuildSectionBlocks(result.RawContent, result, blocks);

        var payload = InsightPageBuilder.BuildCreatePayload(subPageId, subPageTitle, parentId, blocks);

        var createResponse = await _sdk.SendAsync(new SdkMessage
        {
            PluginId = "privstack.notes",
            Action = SdkAction.Create,
            EntityType = "page",
            EntityId = subPageId,
            Payload = payload,
        });

        if (!createResponse.Success)
        {
            Log.Warning("Failed to create insight sub-page: {Error}", createResponse.ErrorMessage);
            _suggestionService.Update(result.SuggestionId, "privstack.data",
                newState: ContentSuggestionState.Error,
                errorMessage: $"Failed to create page: {createResponse.ErrorMessage}");
            return;
        }

        _suggestionService.Update(result.SuggestionId, "privstack.data",
            newState: ContentSuggestionState.Applied,
            newContent: $"Insights saved to \"{subPageTitle}\"",
            newActions:
            [
                new SuggestionAction
                {
                    ActionId = $"view_page:{subPageId}",
                    DisplayName = "View in Notes",
                    IsPrimary = true,
                },
            ]);

        _toast.Show($"Insights saved to \"{subPageTitle}\"", ToastType.Success);
        _results.TryRemove(result.SuggestionId, out _);
    }

    /// <summary>
    /// Converts section content text into paragraph, chart, divider, and table blocks.
    /// Recognizes [CHART:] markers, --- dividers, and markdown pipe tables.
    /// </summary>
    private static void BuildSectionBlocks(
        string content, DatasetInsightResult result, List<JsonObject> blocks)
    {
        var paragraph = new StringBuilder();
        var lines = content.Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            // Chart marker — validate against chart-eligible columns (underlying dataset)
            var chart = TryParseChartMarker(line, result.EffectiveChartColumns);
            if (chart != null)
            {
                FlushParagraph(paragraph, blocks);
                blocks.Add(InsightPageBuilder.BuildChartBlock(chart, result.DatasetId));
                i++;
                continue;
            }

            // Horizontal rule / divider (---, ***, ___ with optional spaces)
            if (DividerRegex.IsMatch(line))
            {
                FlushParagraph(paragraph, blocks);
                blocks.Add(InsightPageBuilder.BuildDividerBlock());
                i++;
                continue;
            }

            // Markdown pipe table detection
            if (IsTableRow(line) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                FlushParagraph(paragraph, blocks);
                var (headers, dataRows, consumed) = ParseMarkdownTable(lines, i);
                if (headers.Count > 0)
                    blocks.Add(InsightPageBuilder.BuildTableBlock(headers, dataRows));
                i += consumed;
                continue;
            }

            paragraph.AppendLine(line);
            i++;
        }

        FlushParagraph(paragraph, blocks);
    }

    private static readonly Regex DividerRegex = new(
        @"^\s*[-*_]{3,}\s*$", RegexOptions.Compiled);

    private static bool IsTableRow(string line)
    {
        var trimmed = line.Trim();
        // Must start with pipe and have at least one more pipe (column separator)
        return trimmed.StartsWith('|') && trimmed.IndexOf('|', 1) > 0;
    }

    private static bool IsTableSeparator(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('|')) return false;
        // Strip pipes and check remainder is only dashes, colons, and whitespace
        var inner = trimmed.Replace("|", "").Trim();
        return inner.Length > 0 && inner.All(c => c is '-' or ':' or ' ');
    }

    private static (List<string> headers, List<List<string>> rows, int linesConsumed)
        ParseMarkdownTable(string[] lines, int startIndex)
    {
        var headers = new List<string>();
        var rows = new List<List<string>>();
        var idx = startIndex;

        // Header row
        if (idx < lines.Length && IsTableRow(lines[idx]))
        {
            headers = ParseTableCells(lines[idx]);
            idx++;
        }

        // Separator row (skip it)
        if (idx < lines.Length && IsTableSeparator(lines[idx]))
            idx++;

        // Data rows
        while (idx < lines.Length && IsTableRow(lines[idx]))
        {
            rows.Add(ParseTableCells(lines[idx]));
            idx++;
        }

        return (headers, rows, idx - startIndex);
    }

    private static List<string> ParseTableCells(string line)
    {
        var trimmed = line.Trim().Trim('|');
        return trimmed.Split('|').Select(c => c.Trim()).ToList();
    }

    private static void FlushParagraph(StringBuilder sb, List<JsonObject> blocks)
    {
        var text = sb.ToString().Trim();
        if (text.Length > 0)
            blocks.Add(InsightPageBuilder.BuildParagraphBlock(text));
        sb.Clear();
    }

    // ── Parent page management ──────────────────────────────────────────

    private async Task<string> FindOrCreateParentPageAsync(string parentTitle)
    {
        var listResponse = await _sdk.SendAsync<List<JsonElement>>(new SdkMessage
        {
            PluginId = "privstack.notes",
            Action = SdkAction.ReadList,
            EntityType = "page",
        });

        if (listResponse.Data != null)
        {
            foreach (var el in listResponse.Data)
            {
                if (el.TryGetProperty("title", out var titleProp) &&
                    titleProp.GetString() == parentTitle &&
                    el.TryGetProperty("id", out var idProp))
                {
                    var isTrashed = el.TryGetProperty("is_trashed", out var t) && t.GetBoolean();
                    var isArchived = el.TryGetProperty("is_archived", out var a) && a.GetBoolean();
                    if (!isTrashed && !isArchived)
                        return idProp.GetString()!;
                }
            }
        }

        var parentId = Guid.NewGuid().ToString();
        var tocBlocks = new List<JsonObject>
        {
            InsightPageBuilder.BuildParagraphBlock(
                "This page collects AI-generated dataset insights. Sub-pages are created automatically."),
            InsightPageBuilder.BuildTocBlock(),
        };

        var parentPayload = InsightPageBuilder.BuildCreatePayload(parentId, parentTitle, null, tocBlocks);

        var createResp = await _sdk.SendAsync(new SdkMessage
        {
            PluginId = "privstack.notes",
            Action = SdkAction.Create,
            EntityType = "page",
            EntityId = parentId,
            Payload = parentPayload,
        });

        if (!createResp.Success)
            Log.Warning("Failed to create parent insight page: {Error}", createResp.ErrorMessage);

        return parentId;
    }

    // ── Prompt builders ────────────────────────────────────────────────

    /// <summary>
    /// Full-featured prompt for cloud models with large context windows.
    /// Includes all chart types, formatting instructions, and detailed guidance.
    /// </summary>
    private static string BuildCloudSystemPrompt(string columnList, string chartColumnList) => $"""
        You are a data analyst. Analyze the dataset below and provide structured insights.
        Organize your response into sections using ## headers.
        Include: data quality observations, key statistics, patterns, anomalies, and actionable recommendations.
        Be specific and reference actual column names and values from the data.

        CHART SUGGESTIONS:
        Where a chart would help visualize an insight, include a chart marker on its own line:
        [CHART: type=CHART_TYPE | title=Chart Title | x=column_name | y=column_name | agg=sum/count/avg/min/max | group=column_name]

        Available chart types and when to use each:
        - bar: Compare values across categories (e.g., revenue by product)
        - line: Show trends over time or ordered sequences
        - pie: Show proportions of a whole (best with <8 categories)
        - donut: Same as pie but visually lighter — prefer for fewer categories
        - area: Like line but emphasizes volume/magnitude of change over time
        - scatter: Show correlation between two numeric columns
        - stacked_bar: Compare totals AND their sub-component breakdown (requires group=column)
        - grouped_bar: Side-by-side comparison of sub-groups within categories (requires group=column)
        - horizontal_bar: Like bar but horizontal — better for long category labels or ranking
        - timeline: NOT for insights (requires start/end date columns) — do not use

        Rules for chart markers:
        - type must be one of: {ChartTypeList}
        - IMPORTANT: x, y, and group in chart markers must be from these CHART-ELIGIBLE columns ONLY: {chartColumnList}
        - The analysis columns ({columnList}) may include computed/derived columns that cannot be used in charts
        - agg is optional (use when y needs aggregation, e.g., sum of budget grouped by status)
        - group is required for stacked_bar and grouped_bar (categorical column to split series)
        - group is optional for bar and line (creates multi-series version)
        - For pie/donut: x is the category/label column, y is the value column
        - For scatter: x and y should both be numeric columns
        - Place the chart marker right after the paragraph that describes the insight it visualizes
        - Only suggest charts where they genuinely add clarity — not every section needs one
        - Prefer variety: use different chart types across sections when appropriate

        FORMATTING:
        - Use --- on its own line to insert a visual divider between major sections
        - When presenting tabular data (e.g., top N values, comparisons, statistics), use markdown pipe tables:
          | Column A | Column B |
          |----------|----------|
          | value1   | value2   |
        - Tables are rendered as rich interactive tables in the output — prefer them over bullet lists for structured data
        """;

    /// <summary>
    /// Compact prompt for local models with limited context windows.
    /// Strips advanced chart types and formatting to stay within budget.
    /// </summary>
    private static string BuildLocalSystemPrompt(string columnList, string chartColumnList) => $"""
        You are a data analyst. Analyze the dataset and provide insights using ## headers.
        Include: data quality, key statistics, patterns, and recommendations.
        Reference actual column names.

        CHARTS (optional — include on own line):
        [CHART: type=TYPE | title=Title | x=column | y=column | agg=sum/count/avg]
        Types: bar, line, pie. Chart columns must be from: {chartColumnList}
        """;

    // ── Parsing helpers ─────────────────────────────────────────────────

    private static string BuildTabularText(
        IReadOnlyList<string> columns,
        IReadOnlyList<string> columnTypes,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int charBudget = 6_000)
    {
        var sb = new StringBuilder();

        sb.AppendLine(string.Join(" | ", columns.Select((c, i) =>
            i < columnTypes.Count ? $"{c} ({columnTypes[i]})" : c)));
        sb.AppendLine(new string('-', Math.Min(sb.Length, 120)));

        foreach (var row in rows)
        {
            var line = string.Join(" | ", row.Select(v => v?.ToString() ?? "NULL"));
            sb.AppendLine(line);
            if (sb.Length > charBudget) break;
        }

        return sb.ToString();
    }

    private static IReadOnlyList<InsightSection> ParseSections(string content)
    {
        var sections = new List<InsightSection>();
        var lines = content.Split('\n');
        string? currentTitle = null;
        var currentContent = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.StartsWith("## "))
            {
                if (currentTitle != null)
                {
                    sections.Add(new InsightSection(currentTitle, currentContent.ToString().Trim()));
                    currentContent.Clear();
                }
                currentTitle = line[3..].Trim();
            }
            else if (currentTitle != null)
            {
                currentContent.AppendLine(line);
            }
        }

        if (currentTitle != null)
            sections.Add(new InsightSection(currentTitle, currentContent.ToString().Trim()));

        return sections;
    }

    private static readonly Regex ChartMarkerRegex = new(
        @"^\s*\[CHART:\s*(.+?)\]\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] ValidChartTypes =
        ["bar", "line", "pie", "donut", "area", "scatter", "stacked_bar", "grouped_bar", "horizontal_bar"];
    private static readonly string[] ValidAggregations = ["count", "sum", "avg", "min", "max"];

    /// <summary>
    /// Parses a [CHART: type=bar | x=col | y=col | ...] marker line.
    /// Returns null if the line isn't a chart marker or has invalid columns.
    /// </summary>
    private static ChartSuggestion? TryParseChartMarker(string line, IReadOnlyList<string> validColumns)
    {
        var match = ChartMarkerRegex.Match(line);
        if (!match.Success) return null;

        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in match.Groups[1].Value.Split('|'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2)
                props[kv[0].Trim()] = kv[1].Trim();
        }

        if (!props.TryGetValue("type", out var chartType) ||
            !ValidChartTypes.Contains(chartType.ToLowerInvariant()))
            return null;

        if (!props.TryGetValue("x", out var xCol) || !props.TryGetValue("y", out var yCol))
            return null;

        // Validate column names against actual dataset columns (case-insensitive)
        var colSet = new HashSet<string>(validColumns, StringComparer.OrdinalIgnoreCase);
        if (!colSet.Contains(xCol) || !colSet.Contains(yCol))
        {
            Log.Debug("Chart marker references unknown columns: x={X}, y={Y}", xCol, yCol);
            return null;
        }

        // Normalize to exact column names
        xCol = validColumns.First(c => c.Equals(xCol, StringComparison.OrdinalIgnoreCase));
        yCol = validColumns.First(c => c.Equals(yCol, StringComparison.OrdinalIgnoreCase));

        props.TryGetValue("title", out var title);
        if (title != null)
            title = title.Trim('"', '\'');

        props.TryGetValue("agg", out var agg);
        props.TryGetValue("group", out var groupBy);

        if (agg != null && !ValidAggregations.Contains(agg.ToLowerInvariant()))
            agg = null;
        else if (agg != null)
            agg = agg.ToLowerInvariant();

        if (groupBy != null && !colSet.Contains(groupBy))
            groupBy = null;
        else if (groupBy != null)
            groupBy = validColumns.First(c => c.Equals(groupBy, StringComparison.OrdinalIgnoreCase));

        return new ChartSuggestion(
            chartType.ToLowerInvariant(),
            title ?? $"{yCol} by {xCol}",
            xCol, yCol, agg, groupBy);
    }

    private const string ChartTypeList = "bar, line, pie, donut, area, scatter, stacked_bar, grouped_bar, horizontal_bar";
}
