using System.Text.Json;
using System.Text.RegularExpressions;

namespace PrivStack.Sdk.Helpers;

/// <summary>
/// Shared wiki-link and privstack:// URL parsing utilities.
/// Consolidates regex logic previously duplicated in GraphDataService and BacklinkService.
/// </summary>
public static class WikiLinkParser
{
    /// <summary>Wiki-link format: [[type:id|Title]]</summary>
    public static readonly Regex WikiLinkPattern = new(
        @"\[\[([a-z]+(?:-[a-z]+)*):([^|]+)\|[^\]]+\]\]",
        RegexOptions.Compiled);

    /// <summary>RTE markdown link format: [Title](privstack://type/id)</summary>
    public static readonly Regex PrivstackUrlPattern = new(
        @"privstack://([a-z]+(?:-[a-z]+)*)/([a-f0-9-]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses all wiki-links and privstack:// URLs from the given content.
    /// </summary>
    public static IReadOnlyList<ParsedLink> ParseLinks(string content)
    {
        var results = new List<ParsedLink>();

        foreach (Match match in WikiLinkPattern.Matches(content))
        {
            results.Add(new ParsedLink(
                match.Groups[1].Value,
                match.Groups[2].Value,
                match.Index,
                match.Length));
        }

        foreach (Match match in PrivstackUrlPattern.Matches(content))
        {
            results.Add(new ParsedLink(
                match.Groups[1].Value,
                match.Groups[2].Value,
                match.Index,
                match.Length));
        }

        return results;
    }

    /// <summary>
    /// Extracts all text content from an entity JSON element.
    /// Handles flat string fields and structured block-based content (Notes pages).
    /// </summary>
    public static string? ExtractContentFromEntity(JsonElement item, string[]? fields = null)
    {
        var parts = new List<string>();
        var fieldNames = fields ?? ["content", "description", "notes", "body"];

        foreach (var field in fieldNames)
        {
            if (!item.TryGetProperty(field, out var prop)) continue;

            if (prop.ValueKind == JsonValueKind.String)
            {
                var text = prop.GetString();
                if (!string.IsNullOrEmpty(text))
                    parts.Add(text);
            }
            else if (prop.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                ExtractStringsFromJson(prop, parts);
            }
        }

        return parts.Count > 0 ? string.Join("\n", parts) : null;
    }

    /// <summary>
    /// Extracts a context snippet around a match position for backlink display.
    /// </summary>
    public static string? ExtractSnippet(string content, int matchIndex, int radius = 60)
    {
        var start = Math.Max(0, matchIndex - radius);
        var end = Math.Min(content.Length, matchIndex + radius);

        // Extend to word boundaries
        while (start > 0 && content[start] != ' ' && content[start] != '\n') start--;
        while (end < content.Length && content[end] != ' ' && content[end] != '\n') end++;

        var snippet = content[start..end].Trim();
        if (start > 0) snippet = "..." + snippet;
        if (end < content.Length) snippet += "...";

        // Remove the link markup for cleaner display
        snippet = WikiLinkPattern.Replace(snippet, m =>
        {
            var display = m.Value;
            var pipeIdx = display.IndexOf('|');
            return pipeIdx >= 0 ? display[(pipeIdx + 1)..^2] : display;
        });

        return string.IsNullOrWhiteSpace(snippet) ? null : snippet;
    }

    /// <summary>
    /// Recursively extracts all string values from a JSON element tree.
    /// Captures text from block-based content structures (paragraphs, headings,
    /// list items, etc.) as well as privstack:// URLs embedded in content.
    /// </summary>
    private static void ExtractStringsFromJson(JsonElement element, List<string> results)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                if (!string.IsNullOrEmpty(text))
                    results.Add(text);
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    ExtractStringsFromJson(property.Value, results);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    ExtractStringsFromJson(item, results);
                break;
        }
    }
}

/// <summary>
/// A parsed wiki-link or privstack:// URL extracted from content.
/// </summary>
public readonly record struct ParsedLink(
    string LinkType,
    string EntityId,
    int MatchIndex,
    int MatchLength)
{
    /// <summary>Composite key in "linkType:entityId" format.</summary>
    public string CompositeKey => $"{LinkType}:{EntityId}";
}
