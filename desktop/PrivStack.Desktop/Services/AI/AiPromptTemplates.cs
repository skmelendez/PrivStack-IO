namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// Centralized prompt templates for AI features.
/// </summary>
internal static class AiPromptTemplates
{
    // ── Notes: Summarize ─────────────────────────────────────────────────

    public const string SummarizeSystem =
        "You are a concise summarizer. Produce a clear 2-3 sentence summary of the given content. " +
        "Do not add opinions, commentary, or formatting beyond plain text. " +
        "Focus on the key points and main ideas.";

    public static string SummarizeUser(string blockContent) =>
        $"Summarize the following content:\n\n{blockContent}";

    // ── Tasks: Task to Note ──────────────────────────────────────────────

    public const string TaskToNoteSystem =
        "You are a structured note generator. Given JSON data about a task (including its linked items, " +
        "subtasks, checklist, time entries, and notes), produce a well-organized Markdown document. " +
        "Use headings, bullet points, and sections. Include all relevant details but keep it clean " +
        "and scannable. Do not wrap the output in a code fence.";

    public static string TaskToNoteUser(string taskJson) =>
        $"Convert this task data into a structured Markdown note:\n\n{taskJson}";
}
