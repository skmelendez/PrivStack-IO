using System.Text;
using PrivStack.Sdk.Capabilities;

namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// Builds the AI system/user prompts for intent classification.
/// Dynamically constructed from the available intent descriptors.
/// Optimized for small local models (1B–3B) with concise prompts.
/// </summary>
internal static class IntentPromptBuilder
{
    /// <summary>
    /// Builds a compact system prompt listing available intents.
    /// Keeps token count low for small local models.
    /// </summary>
    public static string BuildSystemPrompt(IReadOnlyList<IntentDescriptor> intents, DateTimeOffset now)
    {
        var sb = new StringBuilder(1024);

        sb.AppendLine("Classify the user's text into actions. Pick ONLY actions clearly stated in the text. Output JSON.");
        sb.AppendLine();
        sb.AppendLine("Actions:");

        foreach (var intent in intents)
        {
            var requiredSlots = intent.Slots.Where(s => s.Required).Select(s => s.Name);
            sb.AppendLine($"- {intent.IntentId}: {intent.Description} (needs: {string.Join(", ", requiredSlots)})");
        }

        sb.AppendLine();
        sb.AppendLine($"Today: {now:yyyy-MM-dd dddd}");
        sb.AppendLine();
        sb.AppendLine("Output format:");
        sb.AppendLine("{\"intents\":[{\"intent_id\":\"<id>\",\"confidence\":0.9,\"summary\":\"<what to do>\",\"slots\":{\"<name>\":\"<value>\"}}]}");
        sb.AppendLine();
        sb.AppendLine("If nothing actionable: {\"intents\":[]}");

        return sb.ToString();
    }

    /// <summary>
    /// Builds the user prompt containing the signal content to analyze.
    /// </summary>
    public static string BuildUserPrompt(string content, string? entityType, string? entityTitle)
    {
        var sb = new StringBuilder(content.Length + 64);

        if (!string.IsNullOrEmpty(entityTitle))
            sb.AppendLine($"From \"{entityTitle}\":");

        // Truncate content to avoid excessive token usage
        const int maxContentLength = 3000;
        sb.Append(content.Length > maxContentLength
            ? content[..maxContentLength] + "..."
            : content);

        return sb.ToString();
    }
}
