using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Messaging;
using PrivStack.Sdk;
using PrivStack.Sdk.Capabilities;
using PrivStack.Sdk.Messaging;
using PrivStack.Sdk.Services;
using Serilog;

namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// Shell-side orchestrator that receives dataset insight requests from the Data plugin,
/// runs AI analysis, and saves results as structured Notes pages.
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
        var tabularText = BuildTabularText(msg.Columns, msg.ColumnTypes, msg.SampleRows, msg.TotalRowCount);

        var request = new AiRequest
        {
            SystemPrompt = """
                You are a data analyst. Analyze the dataset below and provide structured insights.
                Organize your response into sections using ## headers.
                Include: data quality observations, key statistics, patterns, anomalies, and actionable recommendations.
                Be specific and reference actual column names and values from the data.
                """,
            UserPrompt = $"""
                Dataset: "{msg.DatasetName}"
                Total rows: {msg.TotalRowCount:N0}
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
            response.Content, sections);

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
            InsightPageBuilder.BuildHeadingBlock(1, subPageTitle),
            InsightPageBuilder.BuildParagraphBlock(
                $"Auto-generated insights for dataset \"{result.DatasetName}\" on {DateTime.Now:MMMM d, yyyy}."),
        };

        foreach (var section in result.Sections)
        {
            blocks.Add(InsightPageBuilder.BuildHeadingBlock(2, section.Title));

            foreach (var paragraph in section.Content.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                blocks.Add(InsightPageBuilder.BuildParagraphBlock(paragraph.Trim()));
            }
        }

        // If no parsed sections, add raw content as paragraphs
        if (result.Sections.Count == 0)
        {
            foreach (var paragraph in result.RawContent.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                blocks.Add(InsightPageBuilder.BuildParagraphBlock(paragraph.Trim()));
            }
        }

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

    private async Task<string> FindOrCreateParentPageAsync(string parentTitle)
    {
        // Search existing pages for the parent
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
                    // Ensure it's not trashed or archived
                    var isTrashed = el.TryGetProperty("is_trashed", out var t) && t.GetBoolean();
                    var isArchived = el.TryGetProperty("is_archived", out var a) && a.GetBoolean();
                    if (!isTrashed && !isArchived)
                        return idProp.GetString()!;
                }
            }
        }

        // Create parent page with table of contents
        var parentId = Guid.NewGuid().ToString();
        var tocBlocks = new List<JsonObject>
        {
            InsightPageBuilder.BuildHeadingBlock(1, parentTitle),
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

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string BuildTabularText(
        IReadOnlyList<string> columns,
        IReadOnlyList<string> columnTypes,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        long totalRows)
    {
        var sb = new StringBuilder();

        // Header with types
        sb.AppendLine(string.Join(" | ", columns.Select((c, i) =>
            i < columnTypes.Count ? $"{c} ({columnTypes[i]})" : c)));
        sb.AppendLine(new string('-', Math.Min(sb.Length, 120)));

        // Rows (cap output at ~6000 chars)
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
}
