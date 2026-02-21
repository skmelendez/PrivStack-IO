namespace PrivStack.Desktop;

/// <summary>
/// Single source of truth for the AI assistant's persona and behavior.
/// Change <see cref="Name"/> to rebrand the AI everywhere in the app.
/// </summary>
public static class AiPersona
{
    public const string Name = "Duncan";

    /// <summary>
    /// Response budget tiers. The classifier picks one based on the user's message,
    /// which controls MaxTokens and the length guidance injected into the system prompt.
    /// </summary>
    public enum ResponseTier
    {
        /// <summary>Greetings, yes/no questions, quick factual lookups. 1-2 sentences.</summary>
        Short,
        /// <summary>Explanations, how-to answers, short creative writing. 3-5 sentences.</summary>
        Medium,
        /// <summary>Summarize a page, draft an email, long-form writing. Up to a few paragraphs.</summary>
        Long,
    }

    /// <summary>Token budget per tier.</summary>
    public static int MaxTokensFor(ResponseTier tier) => tier switch
    {
        ResponseTier.Short  => 100,
        ResponseTier.Medium => 300,
        ResponseTier.Long   => 800,
        _ => 200,
    };

    /// <summary>Length guidance sentence injected into the system prompt per tier.</summary>
    private static string LengthRule(ResponseTier tier) => tier switch
    {
        ResponseTier.Short  => "Reply in 1-2 short sentences max.",
        ResponseTier.Medium => "Reply in 3-5 sentences. Be thorough but not verbose.",
        ResponseTier.Long   => "You may use multiple paragraphs. Be thorough and well-structured, but don't pad with filler.",
        _ => "Reply in 1-2 short sentences max.",
    };

    // ── Keyword sets for classification ─────────────────────────────

    private static readonly string[] LongKeywords =
    [
        "summarize this page", "summarize the page", "summarize this note",
        "summarize this document", "summarize everything",
        "write me", "write a", "draft a", "draft an", "draft me",
        "compose", "rewrite this", "rewrite the",
        "explain in detail", "explain thoroughly",
        "full summary", "detailed summary", "long summary",
        "break down", "break this down",
        "list all", "list every", "outline",
    ];

    private static readonly string[] MediumKeywords =
    [
        "summarize", "explain", "how do", "how does", "how can", "how to",
        "what is", "what are", "what does", "why is", "why does", "why do",
        "tell me about", "describe", "compare",
        "help me with", "can you help",
        "suggest", "recommend", "brainstorm", "ideas for",
        "rephrase", "reword", "shorten", "expand",
    ];

    private static readonly string[] ShortKeywords =
    [
        "hey", "hi", "hello", "sup", "yo", "thanks", "thank you",
        "yes", "no", "ok", "okay", "sure", "nah",
        "what time", "what day", "what date",
        "weather", "temperature",
        "ping", "test", "are you there",
    ];

    /// <summary>
    /// Classifies the user's message into a response tier based on intent signals.
    /// Uses keyword matching — longest match wins, with fallback to Medium.
    /// </summary>
    public static ResponseTier Classify(string userMessage)
    {
        var lower = userMessage.ToLowerInvariant();

        // Check Long first (most specific phrases)
        foreach (var kw in LongKeywords)
            if (lower.Contains(kw)) return ResponseTier.Long;

        // Short signals (greetings, one-word messages, can't-do topics)
        if (userMessage.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3)
        {
            foreach (var kw in ShortKeywords)
                if (lower.Contains(kw)) return ResponseTier.Short;
        }

        // Medium signals
        foreach (var kw in MediumKeywords)
            if (lower.Contains(kw)) return ResponseTier.Medium;

        // Very short messages default to Short
        if (userMessage.Length < 20)
            return ResponseTier.Short;

        // Default
        return ResponseTier.Medium;
    }

    /// <summary>
    /// Builds the full system prompt with tier-appropriate length guidance.
    /// </summary>
    public static string GetSystemPrompt(ResponseTier tier) => $"""
        You are {Name}, the built-in assistant for PrivStack (a local-first productivity app).

        RULES — follow these strictly:
        1. {LengthRule(tier)}
        2. Plain text only. No markdown, no bullet lists, no headers, no formatting.
        3. Never start with "I'm an AI" or describe what you are. Just answer.
        4. Never repeat yourself across messages. If you already said something, don't restate it.
        5. No filler phrases ("Great question!", "Sure!", "Of course!", "Absolutely!"). Get to the point.
        6. If you can't do something (web access, real-time data, external lookups), say so in one line and offer what you CAN help with locally.
        7. You run locally inside PrivStack. No internet access. Cannot browse, fetch URLs, check weather, or access any live data.
        8. You CAN help with: writing, summarizing, brainstorming, formatting, answering knowledge questions, and working with the user's local PrivStack data.
        9. Match the user's energy — casual gets casual, serious gets focused.
        10. When unsure, ask a short clarifying question instead of guessing.
        """;
}
