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

        sb.AppendLine("You extract ACTIONS the user needs to do from text. Only pick actions that are clearly stated.");
        sb.AppendLine("Do NOT list actions just because they exist. Most text has 0 or 1 actions. Be selective.");
        sb.AppendLine("You MUST include slot values extracted from the text. Always fill the slots.");
        sb.AppendLine();

        // Compact action list
        sb.AppendLine("Available action IDs:");
        foreach (var intent in intents)
        {
            var slots = string.Join(", ", intent.Slots.Where(s => s.Required).Select(s => s.Name));
            sb.AppendLine($"- {intent.IntentId} [{slots}]");
        }

        sb.AppendLine();
        sb.AppendLine($"Today: {now:yyyy-MM-dd dddd}");

        // Few-shot examples — concrete demonstrations are critical for small models
        sb.AppendLine();
        sb.AppendLine("--- Example 1 ---");
        sb.AppendLine("Text: \"I need to meet with Sarah on Friday at 3pm\"");
        AppendExample(sb, "calendar.create_event", 0.9,
            "Meet with Sarah on Friday at 3pm",
            ("title", "Meet with Sarah"), ("start_time", FutureDay(now, DayOfWeek.Friday, 15)));

        sb.AppendLine("--- Example 2 ---");
        sb.AppendLine("Text: \"Remember to buy groceries after work\"");
        AppendExample(sb, "tasks.create_task", 0.85,
            "Buy groceries after work",
            ("title", "Buy groceries after work"));

        sb.AppendLine("--- Example 3 ---");
        sb.AppendLine("Text: \"Had a great day at the park. The sunset was beautiful.\"");
        sb.AppendLine("{\"intents\":[]}");
        sb.AppendLine();

        sb.AppendLine("--- Example 4 ---");
        sb.AppendLine("Text: \"Call the dentist to schedule a cleaning next Monday. Also the project report looks good.\"");
        AppendExample(sb, "calendar.create_event", 0.9,
            "Call dentist for cleaning next Monday",
            ("title", "Dentist cleaning"), ("start_time", FutureDay(now, DayOfWeek.Monday, 9)));

        sb.AppendLine("Now output JSON for the text below. Only real actions with filled slots.");

        return sb.ToString();
    }

    public static string BuildUserPrompt(string content, string? entityType, string? entityTitle)
    {
        var sb = new StringBuilder(content.Length + 64);
        sb.AppendLine("Text:");
        sb.Append(content.Length > 2000 ? content[..2000] + "..." : content);
        return sb.ToString();
    }

    private static void AppendExample(
        StringBuilder sb, string intentId, double confidence, string summary,
        params (string name, string value)[] slots)
    {
        var slotJson = string.Join(",", slots.Select(s => $"\"{s.name}\":\"{s.value}\""));
        sb.AppendLine($"{{\"intents\":[{{\"intent_id\":\"{intentId}\",\"confidence\":{confidence},\"summary\":\"{summary}\",\"slots\":{{{slotJson}}}}}]}}");
        sb.AppendLine();
    }

    private static string FutureDay(DateTimeOffset now, DayOfWeek target, int hour)
    {
        var daysAhead = ((int)target - (int)now.DayOfWeek + 7) % 7;
        if (daysAhead == 0) daysAhead = 7;
        var date = now.Date.AddDays(daysAhead).AddHours(hour);
        return date.ToString("yyyy-MM-ddTHH:mm:ss");
    }
}
