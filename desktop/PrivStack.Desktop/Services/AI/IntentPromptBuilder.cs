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

        sb.AppendLine("Extract ACTIONS from text. Most text has 0 or 1 actions. Be selective.");
        sb.AppendLine("calendar.create_event = a CONFIRMED meeting/event at a specific time.");
        sb.AppendLine("tasks.create_task = something to DO (call, buy, send, fix, book, schedule).");
        sb.AppendLine("Generate a short clear title. Fill description with details from the text. Fill all applicable slots.");
        sb.AppendLine();

        sb.AppendLine("Action IDs and slots:");
        foreach (var intent in intents)
        {
            var allSlots = intent.Slots.Select(s =>
                s.Required ? s.Name : $"{s.Name}?");
            sb.AppendLine($"- {intent.IntentId} [{string.Join(", ", allSlots)}]");
        }

        sb.AppendLine();
        sb.AppendLine($"Today: {now:yyyy-MM-dd dddd}");
        sb.AppendLine();

        // Few-shot examples with rich slot filling
        sb.AppendLine("--- Example 1 ---");
        sb.AppendLine("Text: \"Team standup meeting every Monday at 9am in the conference room\"");
        AppendExample(sb, "calendar.create_event", 0.9,
            "Team standup Monday 9am",
            ("title", "Team Standup"), ("start_time", FutureDay(now, DayOfWeek.Monday, 9)),
            ("location", "Conference room"), ("description", "Weekly team standup meeting"));

        sb.AppendLine("--- Example 2 ---");
        sb.AppendLine("Text: \"Call the dentist to book a cleaning appointment next week\"");
        AppendExample(sb, "tasks.create_task", 0.9,
            "Call dentist to book cleaning",
            ("title", "Call dentist for cleaning appointment"),
            ("description", "Call the dentist office to schedule a cleaning appointment for next week"),
            ("priority", "medium"));

        sb.AppendLine("--- Example 3 ---");
        sb.AppendLine("Text: \"Had a great day at the park. The sunset was beautiful.\"");
        sb.AppendLine("{\"intents\":[]}");
        sb.AppendLine();

        sb.AppendLine("--- Example 4 ---");
        sb.AppendLine("Text: \"Send the quarterly report to Sarah by Friday, it's urgent\"");
        AppendExample(sb, "tasks.create_task", 0.9,
            "Send quarterly report to Sarah by Friday",
            ("title", "Send quarterly report to Sarah"),
            ("description", "Send the quarterly report to Sarah before end of day Friday"),
            ("due_date", FutureDayDate(now, DayOfWeek.Friday)),
            ("priority", "high"));

        sb.Append("VALID intent_id: ");
        sb.AppendLine(string.Join(", ", intents.Select(i => i.IntentId)));
        sb.AppendLine("Output JSON only.");

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

    private static string FutureDayDate(DateTimeOffset now, DayOfWeek target)
    {
        var daysAhead = ((int)target - (int)now.DayOfWeek + 7) % 7;
        if (daysAhead == 0) daysAhead = 7;
        return now.Date.AddDays(daysAhead).ToString("yyyy-MM-dd");
    }
}
