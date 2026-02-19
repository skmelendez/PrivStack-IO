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

        sb.AppendLine("You extract ACTIONS from text. Be very selective — most text has 0 or 1 actions.");
        sb.AppendLine("calendar.create_event = a CONFIRMED meeting/event at a specific time.");
        sb.AppendLine("tasks.create_task = something the user needs TO DO (call, buy, send, fix, book, schedule).");
        sb.AppendLine("Fill all slots with values from the text. Output JSON only.");
        sb.AppendLine();

        sb.AppendLine("Action IDs:");
        foreach (var intent in intents)
        {
            var slots = string.Join(", ", intent.Slots.Where(s => s.Required).Select(s => s.Name));
            sb.AppendLine($"- {intent.IntentId} [{slots}]");
        }

        sb.AppendLine();
        sb.AppendLine($"Today: {now:yyyy-MM-dd dddd}");
        sb.AppendLine();

        // Few-shot examples teaching correct task vs event distinction
        sb.AppendLine("--- Example 1 ---");
        sb.AppendLine("Text: \"Team standup meeting every Monday at 9am\"");
        AppendExample(sb, "calendar.create_event", 0.9,
            "Team standup Monday 9am",
            ("title", "Team standup"), ("start_time", FutureDay(now, DayOfWeek.Monday, 9)));

        sb.AppendLine("--- Example 2 ---");
        sb.AppendLine("Text: \"Call the dentist to book an appointment next week\"");
        AppendExample(sb, "tasks.create_task", 0.9,
            "Call dentist to book appointment",
            ("title", "Call dentist to book appointment"));

        sb.AppendLine("--- Example 3 ---");
        sb.AppendLine("Text: \"Had a great day at the park. The sunset was beautiful.\"");
        sb.AppendLine("{\"intents\":[]}");
        sb.AppendLine();

        sb.AppendLine("--- Example 4 ---");
        sb.AppendLine("Text: \"Send the report to Sarah by Friday\"");
        AppendExample(sb, "tasks.create_task", 0.9,
            "Send report to Sarah by Friday",
            ("title", "Send report to Sarah"));

        sb.AppendLine("Now output JSON for the text below.");

        return sb.ToString();
    }

    public static string BuildUserPrompt(string content, string? entityType, string? entityTitle)
    {
        var sb = new StringBuilder(content.Length + 16);
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
