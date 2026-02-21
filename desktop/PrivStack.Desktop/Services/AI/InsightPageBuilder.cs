using System.Text.Json.Nodes;

namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// Static helper that builds JSON payloads for creating Notes pages via SdkMessage,
/// avoiding any direct reference to Notes plugin models.
/// </summary>
internal static class InsightPageBuilder
{
    /// <summary>
    /// Builds a full page creation JSON payload compatible with the Notes plugin's Page schema.
    /// </summary>
    public static string BuildCreatePayload(string id, string title, string? parentId, List<JsonObject> blocks)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var page = new JsonObject
        {
            ["id"] = id,
            ["title"] = title,
            ["sort_order"] = 0,
            ["is_archived"] = false,
            ["is_trashed"] = false,
            ["is_locked"] = false,
            ["created_at"] = now,
            ["modified_at"] = now,
            ["content"] = new JsonObject
            {
                ["type"] = "doc",
                ["content"] = new JsonArray(blocks.Select(b => (JsonNode)b).ToArray()),
            },
        };

        if (parentId != null)
            page["parent_id"] = parentId;

        return page.ToJsonString();
    }

    /// <summary>Builds a table_of_contents block.</summary>
    public static JsonObject BuildTocBlock()
    {
        return new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["type"] = "table_of_contents",
            ["max_depth"] = 3,
        };
    }

    /// <summary>Builds a heading block at the specified level (1-6).</summary>
    public static JsonObject BuildHeadingBlock(int level, string text)
    {
        return new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["type"] = "heading",
            ["level"] = level,
            ["text"] = text,
        };
    }

    /// <summary>Builds a paragraph block.</summary>
    public static JsonObject BuildParagraphBlock(string text)
    {
        return new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["type"] = "paragraph",
            ["text"] = text,
        };
    }

    /// <summary>
    /// Builds a dataset-backed chart block. Derives orientation, inner_radius_ratio,
    /// and show_legend from the chart type.
    /// </summary>
    public static JsonObject BuildChartBlock(ChartSuggestion chart, string datasetId)
    {
        var showLegend = chart.ChartType is "pie" or "donut"
            || chart.GroupBy != null
            || chart.ChartType is "stacked_bar" or "grouped_bar";

        var block = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["type"] = "chart",
            ["dataset_id"] = datasetId,
            ["chart_type"] = chart.ChartType,
            ["x_column"] = chart.XColumn,
            ["y_column"] = chart.YColumn,
            ["title"] = chart.Title,
            ["width"] = 600,
            ["height"] = 400,
            ["show_legend"] = showLegend,
        };

        if (chart.Aggregation != null)
            block["aggregation"] = chart.Aggregation;

        if (chart.GroupBy != null)
            block["group_by"] = chart.GroupBy;

        if (chart.ChartType == "horizontal_bar")
            block["orientation"] = "horizontal";

        if (chart.ChartType == "donut")
            block["inner_radius_ratio"] = 0.5;

        return block;
    }
}
