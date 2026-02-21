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

    private readonly ConcurrentDictionary<string, DatasetInsightResult> _results = new();

    public DatasetInsightOrchestrator(
        AiService aiService,
        IAiSuggestionService suggestionService,
        IPrivStackSdk sdk,
        IToastService toast)
    {
        _aiService = aiService;
        _suggestionService = suggestionService;
        _sdk = sdk;
        _toast = toast;

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
        var tabularText = BuildTabularText(msg.Columns, msg.ColumnTypes, msg.SampleRows);
        var columnList = string.Join(", ", msg.Columns.Select((c, i) =>
            i < msg.ColumnTypes.Count ? $"{c} ({msg.ColumnTypes[i]})" : c));

        var request = new AiRequest
        {
            SystemPrompt = $"""
                You are a data analyst. Analyze the dataset below and provide structured insights.
                Organize your response into sections using ## headers.
                Include: data quality observations, key statistics, patterns, anomalies, and actionable recommendations.
                Be specific and reference actual column names and values from the data.

                CHART SUGGESTIONS:
                Where a chart would help visualize an insight, include a chart marker on its own line:
                [CHART: type={ChartTypeList} | title=Chart Title | x=column_name | y=column_name | agg=sum/count/avg/min/max | group=column_name]

                Rules for chart markers:
                - type must be one of: bar, line, pie
                - x and y must be exact column names from: {columnList}
                - agg is optional (use when y needs aggregation, e.g., sum of budget grouped by status)
                - group is optional (use to group data by a categorical column)
                - For pie charts: x is the category/label column, y is the value column
                - Place the chart marker right after the paragraph that describes the insight it visualizes
                - Only suggest charts where they genuinely add clarity — not every section needs one
                """,
            UserPrompt = $"""
                Dataset: "{msg.DatasetName}"
                Total rows: {msg.TotalRowCount:N0}
                Columns: {columnList}
                Sample ({Math.Min(msg.SampleRows.Count, 100)} rows shown):

                {tabularText}
                """,
            MaxTokens = 4096,
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
            msg.Columns.ToList(), msg.ColumnTypes.ToList());

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
        if (msg.ActionId != "save_insight_notes") return;
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
            newContent: $"Insights saved to \"{subPageTitle}\"");

        _toast.Show($"Insights saved to \"{subPageTitle}\"", ToastType.Success);
        _results.TryRemove(result.SuggestionId, out _);
    }

    /// <summary>
    /// Converts section content text into paragraph and chart blocks.
    /// Lines matching [CHART: ...] are converted to dataset-backed chart blocks.
    /// </summary>
    private static void BuildSectionBlocks(
        string content, DatasetInsightResult result, List<JsonObject> blocks)
    {
        var paragraph = new StringBuilder();

        foreach (var line in content.Split('\n'))
        {
            var chart = TryParseChartMarker(line, result.Columns);
            if (chart != null)
            {
                // Flush accumulated paragraph text before the chart
                FlushParagraph(paragraph, blocks);
                blocks.Add(InsightPageBuilder.BuildChartBlock(chart, result.DatasetId));
            }
            else
            {
                paragraph.AppendLine(line);
            }
        }

        FlushParagraph(paragraph, blocks);
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

    // ── Parsing helpers ─────────────────────────────────────────────────

    private static string BuildTabularText(
        IReadOnlyList<string> columns,
        IReadOnlyList<string> columnTypes,
        IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var sb = new StringBuilder();

        sb.AppendLine(string.Join(" | ", columns.Select((c, i) =>
            i < columnTypes.Count ? $"{c} ({columnTypes[i]})" : c)));
        sb.AppendLine(new string('-', Math.Min(sb.Length, 120)));

        foreach (var row in rows)
        {
            var line = string.Join(" | ", row.Select(v => v?.ToString() ?? "NULL"));
            sb.AppendLine(line);
            if (sb.Length > 6000) break;
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

    private static readonly string[] ValidChartTypes = ["bar", "line", "pie"];
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
        props.TryGetValue("agg", out var agg);
        props.TryGetValue("group", out var groupBy);

        if (agg != null && !ValidAggregations.Contains(agg.ToLowerInvariant()))
            agg = null;

        if (groupBy != null && !colSet.Contains(groupBy))
            groupBy = null;
        else if (groupBy != null)
            groupBy = validColumns.First(c => c.Equals(groupBy, StringComparison.OrdinalIgnoreCase));

        return new ChartSuggestion(
            chartType.ToLowerInvariant(),
            title ?? $"{yCol} by {xCol}",
            xCol, yCol, agg, groupBy);
    }

    private const string ChartTypeList = "bar, line, pie";
}
