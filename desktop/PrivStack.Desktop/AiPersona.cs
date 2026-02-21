using System.Text.RegularExpressions;

namespace PrivStack.Desktop;

/// <summary>
/// Single source of truth for the AI assistant's persona and behavior.
/// Change <see cref="Name"/> to rebrand the AI everywhere in the app.
/// </summary>
public static partial class AiPersona
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

    /// <summary>Token budget per tier (local models).</summary>
    public static int MaxTokensFor(ResponseTier tier) => tier switch
    {
        ResponseTier.Short  => 60,
        ResponseTier.Medium => 250,
        ResponseTier.Long   => 800,
        _ => 150,
    };

    /// <summary>Token budget per tier for cloud models (larger budgets).</summary>
    public static int CloudMaxTokensFor(ResponseTier tier) => tier switch
    {
        ResponseTier.Short  => 200,
        ResponseTier.Medium => 800,
        ResponseTier.Long   => 2000,
        _ => 400,
    };

    /// <summary>Max sentences to keep per tier during post-processing truncation.</summary>
    private static int MaxSentences(ResponseTier tier) => tier switch
    {
        ResponseTier.Short  => 2,
        ResponseTier.Medium => 5,
        ResponseTier.Long   => 30,
        _ => 3,
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
    /// </summary>
    public static ResponseTier Classify(string userMessage)
    {
        var lower = userMessage.ToLowerInvariant();

        foreach (var kw in LongKeywords)
            if (lower.Contains(kw)) return ResponseTier.Long;

        if (userMessage.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3)
        {
            foreach (var kw in ShortKeywords)
                if (lower.Contains(kw)) return ResponseTier.Short;
        }

        foreach (var kw in MediumKeywords)
            if (lower.Contains(kw)) return ResponseTier.Medium;

        if (userMessage.Length < 20)
            return ResponseTier.Short;

        return ResponseTier.Medium;
    }

    /// <summary>
    /// Builds a richer system prompt for cloud models (Anthropic, OpenAI, Gemini).
    /// Includes persona detail, memory context, and relaxed formatting constraints.
    /// </summary>
    public static string GetCloudSystemPrompt(ResponseTier tier, string userName, string? memoryContext)
    {
        var brevity = tier switch
        {
            ResponseTier.Short  => "Keep your answer to 1-2 sentences.",
            ResponseTier.Medium => "Answer in a few sentences, up to a short paragraph.",
            ResponseTier.Long   => "Give a thorough, detailed answer. Use paragraphs if helpful.",
            _ => "Be concise.",
        };

        var memoryBlock = string.IsNullOrEmpty(memoryContext)
            ? ""
            : $"\n\n{memoryContext}";

        return $"""
            You are {Name}, a knowledgeable personal assistant built into PrivStack (a privacy-first productivity app). The user is {userName}. {brevity}

            You have personality — you're friendly, direct, and occasionally witty. You remember things about the user across conversations. Never mention being an AI or a language model. You are {Name}.{memoryBlock}
            """;
    }

    /// <summary>
    /// Builds the system prompt. Kept extremely minimal for local LLM compatibility.
    /// </summary>
    /// <remarks>
    /// Small local models (Phi-3, Mistral 7B) cannot follow complex multi-rule prompts.
    /// Keep this to the absolute minimum number of short, direct sentences.
    /// Do NOT add examples — local models parrot them verbatim.
    /// Do NOT add numbered rules — local models echo the list back.
    /// </remarks>
    public static string GetSystemPrompt(ResponseTier tier, string userName)
    {
        var brevity = tier switch
        {
            ResponseTier.Short  => "Answer in one sentence only.",
            ResponseTier.Medium => "Keep your answer to a few sentences.",
            ResponseTier.Long   => "Give a thorough answer.",
            _ => "Be brief.",
        };

        return $"""
            You are {Name}, a concise offline assistant. The user is {userName}. {brevity} Never mention being an AI. No markdown. No lists. No notes or disclaimers.
            """;
    }

    // ── Response sanitization ────────────────────────────────────────

    [GeneratedRegex(@"<\|?/?(system|user|assistant|end|im_start|im_end|eot_id|start_header_id|end_header_id|begin_of_text|end_of_text|endoftext)\|?>", RegexOptions.IgnoreCase)]
    private static partial Regex ChatTokenPattern();

    [GeneratedRegex(@"^\s*-?\s*(User|Assistant|Duncan|System|Note)\s*:.*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex RolePrefixLinePattern();

    [GeneratedRegex(@"\*{1,2}([^*]+)\*{1,2}")]
    private static partial Regex MarkdownBoldPattern();

    [GeneratedRegex(@"^[\s\-\*•]+", RegexOptions.Multiline)]
    private static partial Regex BulletPrefixPattern();

    [GeneratedRegex(@"^#{1,6}\s+", RegexOptions.Multiline)]
    private static partial Regex MarkdownHeaderPattern();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessiveNewlines();

    /// <summary>
    /// Strips chat tokens, markdown, role prefixes, self-referential lines,
    /// and enforces sentence count limits per tier.
    /// </summary>
    public static string Sanitize(string response, ResponseTier tier = ResponseTier.Medium)
    {
        if (string.IsNullOrEmpty(response)) return response;

        // Strip raw chat tokens
        var cleaned = ChatTokenPattern().Replace(response, "");

        // Strip entire lines that are role prefixes or "**Note:**" disclaimers
        cleaned = RolePrefixLinePattern().Replace(cleaned, "");

        // Strip markdown bold/italic
        cleaned = MarkdownBoldPattern().Replace(cleaned, "$1");

        // Strip markdown headers
        cleaned = MarkdownHeaderPattern().Replace(cleaned, "");

        // Strip bullet prefixes (convert to plain sentences)
        cleaned = BulletPrefixPattern().Replace(cleaned, "");

        // Collapse excessive newlines
        cleaned = ExcessiveNewlines().Replace(cleaned, "\n\n");

        cleaned = cleaned.Trim();

        // Hard sentence-count cap per tier
        cleaned = TruncateToSentences(cleaned, MaxSentences(tier));

        return cleaned;
    }

    /// <summary>
    /// Truncates text to a maximum number of sentences.
    /// </summary>
    private static string TruncateToSentences(string text, int maxSentences)
    {
        if (maxSentences <= 0 || string.IsNullOrEmpty(text)) return text;

        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '.' or '!' or '?')
            {
                // Skip consecutive punctuation (e.g., "..." or "?!")
                while (i + 1 < text.Length && text[i + 1] is '.' or '!' or '?')
                    i++;

                count++;
                if (count >= maxSentences)
                    return text[..(i + 1)].Trim();
            }
        }

        return text;
    }
}
