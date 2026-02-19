using System.Text;
using PrivStack.Sdk.Capabilities;

namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// Builds the AI system/user prompts for intent classification.
/// Dynamically constructed from the available intent descriptors.
/// </summary>
internal static class IntentPromptBuilder
{
    /// <summary>
    /// Builds the system prompt that describes all available intents and their slots.
    /// </summary>
    public static string BuildSystemPrompt(IReadOnlyList<IntentDescriptor> intents, DateTimeOffset now)
    {
        var sb = new StringBuilder(2048);

        sb.AppendLine("You are an intent classification engine for a productivity application.");
        sb.AppendLine("Given user content, identify actionable intents from the available actions below.");
        sb.AppendLine("Return JSON only — no explanations, no markdown fences.");
        sb.AppendLine();
        sb.AppendLine("Available actions:");

        for (var i = 0; i < intents.Count; i++)
        {
            var intent = intents[i];
            sb.AppendLine($"{i + 1}. {intent.IntentId} — {intent.Description}");

            var required = intent.Slots.Where(s => s.Required).ToList();
            var optional = intent.Slots.Where(s => !s.Required).ToList();

            if (required.Count > 0)
                sb.AppendLine($"   Required: {string.Join(", ", required.Select(s => $"{s.Name} ({SlotTypeName(s.Type)})"))}");
            if (optional.Count > 0)
                sb.AppendLine($"   Optional: {string.Join(", ", optional.Select(s => $"{s.Name} ({SlotTypeName(s.Type)})"))}");
        }

        sb.AppendLine();
        sb.AppendLine($"Current date/time: {now:yyyy-MM-ddTHH:mm:sszzz}");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Only suggest intents that are clearly implied by the content.");
        sb.AppendLine("- For datetime slots, use ISO 8601 format.");
        sb.AppendLine("- Confidence must be between 0.0 and 1.0.");
        sb.AppendLine("- Return at most 3 intents per analysis.");
        sb.AppendLine();
        sb.AppendLine("Respond with exactly this JSON structure:");
        sb.AppendLine("{");
        sb.AppendLine("  \"intents\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"intent_id\": \"<intent_id>\",");
        sb.AppendLine("      \"confidence\": 0.92,");
        sb.AppendLine("      \"summary\": \"<human readable summary of the action>\",");
        sb.AppendLine("      \"slots\": { \"<slot_name>\": \"<value>\", ... }");
        sb.AppendLine("    }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("If no actionable intents found, respond: { \"intents\": [] }");

        return sb.ToString();
    }

    /// <summary>
    /// Builds the user prompt containing the signal content to analyze.
    /// </summary>
    public static string BuildUserPrompt(string content, string? entityType, string? entityTitle)
    {
        var sb = new StringBuilder(content.Length + 128);

        if (!string.IsNullOrEmpty(entityType) || !string.IsNullOrEmpty(entityTitle))
        {
            sb.Append("Context: ");
            if (!string.IsNullOrEmpty(entityType))
                sb.Append($"[{entityType}] ");
            if (!string.IsNullOrEmpty(entityTitle))
                sb.Append($"\"{entityTitle}\" ");
            sb.AppendLine();
            sb.AppendLine();
        }

        sb.AppendLine("Content to analyze:");
        // Truncate content to avoid excessive token usage
        const int maxContentLength = 4000;
        sb.Append(content.Length > maxContentLength
            ? content[..maxContentLength] + "..."
            : content);

        return sb.ToString();
    }

    private static string SlotTypeName(IntentSlotType type) => type switch
    {
        IntentSlotType.String => "string",
        IntentSlotType.Text => "text",
        IntentSlotType.DateTime => "datetime",
        IntentSlotType.Date => "date",
        IntentSlotType.Time => "time",
        IntentSlotType.Duration => "duration",
        IntentSlotType.Integer => "integer",
        IntentSlotType.Boolean => "boolean",
        IntentSlotType.Email => "email",
        IntentSlotType.Url => "url",
        IntentSlotType.EntityReference => "entity_reference",
        _ => type.ToString().ToLowerInvariant(),
    };
}
