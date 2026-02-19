using System.Text;
using PrivStack.Sdk.Capabilities;

namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// Builds the AI system/user prompts for intent classification.
/// Uses few-shot examples to guide small local models (1B–3B).
/// </summary>
internal static class IntentPromptBuilder
{
    public static string BuildSystemPrompt(IReadOnlyList<IntentDescriptor> intents, DateTimeOffset now)
    {
        var sb = new StringBuilder(2048);

        sb.AppendLine("You classify text into actions from the list below. Use ONLY these action IDs. Output JSON.");
        sb.AppendLine();

        // Compact action list
        sb.AppendLine("Actions:");
        foreach (var intent in intents)
        {
            var slots = string.Join(", ", intent.Slots.Where(s => s.Required).Select(s => s.Name));
            sb.AppendLine($"- {intent.IntentId}: {intent.Description} [{slots}]");
        }

        sb.AppendLine();
        sb.AppendLine($"Today: {now:yyyy-MM-dd dddd}");

        // Few-shot examples — this is what makes small models work
        sb.AppendLine();
        sb.AppendLine("Examples:");
        sb.AppendLine();
        sb.AppendLine("Text: \"Meet with Sarah on Friday at 3pm to discuss the project\"");
        AppendExampleIntent(sb, "calendar.create_event", 0.95,
            "Schedule meeting with Sarah on Friday at 3pm",
            ("title", "Meet with Sarah"), ("start_time", FutureDay(now, DayOfWeek.Friday, 15)));
        sb.AppendLine();
        sb.AppendLine("Text: \"Buy groceries and pick up dry cleaning\"");
        AppendExampleIntent(sb, "tasks.create_task", 0.85,
            "Create task: Buy groceries and pick up dry cleaning",
            ("title", "Buy groceries and pick up dry cleaning"));
        sb.AppendLine();
        sb.AppendLine("Text: \"The weather was nice today. I went for a walk.\"");
        sb.AppendLine("{\"intents\":[]}");

        sb.AppendLine();
        sb.AppendLine("Now classify the following text. ONLY use intent_id values from the Actions list above.");

        return sb.ToString();
    }

    public static string BuildUserPrompt(string content, string? entityType, string? entityTitle)
    {
        var sb = new StringBuilder(content.Length + 64);

        if (!string.IsNullOrEmpty(entityTitle))
            sb.AppendLine($"From \"{entityTitle}\":");

        const int maxContentLength = 2000;
        sb.Append(content.Length > maxContentLength
            ? content[..maxContentLength] + "..."
            : content);

        return sb.ToString();
    }

    private static void AppendExampleIntent(
        StringBuilder sb, string intentId, double confidence, string summary,
        params (string name, string value)[] slots)
    {
        var slotJson = string.Join(",", slots.Select(s => $"\"{s.name}\":\"{s.value}\""));
        sb.AppendLine($"{{\"intents\":[{{\"intent_id\":\"{intentId}\",\"confidence\":{confidence},\"summary\":\"{summary}\",\"slots\":{{{slotJson}}}}}]}}");
    }

    /// <summary>
    /// Returns an ISO datetime string for the next occurrence of a given weekday at the specified hour.
    /// Used in few-shot examples so relative dates are correct.
    /// </summary>
    private static string FutureDay(DateTimeOffset now, DayOfWeek target, int hour)
    {
        var daysAhead = ((int)target - (int)now.DayOfWeek + 7) % 7;
        if (daysAhead == 0) daysAhead = 7; // always next week
        var date = now.Date.AddDays(daysAhead).AddHours(hour);
        return date.ToString("yyyy-MM-ddTHH:mm:ss");
    }
}
