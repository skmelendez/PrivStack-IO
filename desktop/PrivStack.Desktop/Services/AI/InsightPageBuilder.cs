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
}
