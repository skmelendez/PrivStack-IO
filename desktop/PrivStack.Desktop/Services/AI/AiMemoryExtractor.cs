using System.Text.Json;
using PrivStack.Sdk.Services;
using Serilog;

namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// After each successful cloud AI response, evaluates whether anything should be
/// remembered about the user. Runs fire-and-forget — never blocks the UI.
/// </summary>
internal sealed class AiMemoryExtractor
{
    private static readonly ILogger _log = Log.ForContext<AiMemoryExtractor>();
    private readonly IAiService _aiService;
    private readonly AiMemoryService _memoryService;

    public AiMemoryExtractor(IAiService aiService, AiMemoryService memoryService)
    {
        _aiService = aiService;
        _memoryService = memoryService;
    }

    /// <summary>
    /// Evaluates a user+assistant exchange for memorable facts. Fire-and-forget safe.
    /// </summary>
    public async Task EvaluateAsync(string userMessage, string assistantResponse)
    {
        try
        {
            var existingMemories = _memoryService.FormatForPrompt() ?? "No existing memories.";

            var jsonExample = """{"remember": true, "content": "fact to remember", "category": "preference|personal|work|other", "update_id": "id-of-memory-to-update-or-null"}""";
            var prompt = $"""
                You are a memory evaluator. Given this exchange and existing memories, decide if anything new
                should be remembered about the user (personal facts, preferences, important info they shared).
                Do NOT remember: greetings, transient requests, opinions about AI, or task-specific details.

                Existing memories:
                {existingMemories}

                User said: {userMessage}
                Assistant replied: {assistantResponse}

                Respond ONLY with JSON (no markdown, no explanation):
                {jsonExample}
                """;

            var request = new AiRequest
            {
                SystemPrompt = "You extract personal facts from conversations. Output only valid JSON.",
                UserPrompt = prompt,
                MaxTokens = 100,
                Temperature = 0.1,
                FeatureId = "ai.memory_extract"
            };

            var response = await _aiService.CompleteAsync(request);
            if (!response.Success || string.IsNullOrEmpty(response.Content))
                return;

            var json = response.Content.Trim();
            // Strip markdown code fences if present
            if (json.StartsWith("```"))
            {
                var firstNewline = json.IndexOf('\n');
                var lastFence = json.LastIndexOf("```");
                if (firstNewline > 0 && lastFence > firstNewline)
                    json = json[(firstNewline + 1)..lastFence].Trim();
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("remember", out var rememberProp) || !rememberProp.GetBoolean())
                return;

            var content = root.TryGetProperty("content", out var contentProp)
                ? contentProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(content))
                return;

            var category = root.TryGetProperty("category", out var catProp)
                ? catProp.GetString() : null;

            var updateId = root.TryGetProperty("update_id", out var updateProp)
                ? updateProp.GetString() : null;

            if (!string.IsNullOrEmpty(updateId) && updateId != "null")
                _memoryService.Update(updateId, content);
            else
                _memoryService.Add(content, category);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Memory extraction failed (non-critical)");
        }
    }
}
