using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// Persists AI assistant learned facts about the user across sessions.
/// Stores in <see cref="DataPaths.BaseDir"/>/ai-memories.json (global, not per-workspace).
/// </summary>
internal sealed class AiMemoryService
{
    private const int MaxMemories = 50;
    private static readonly ILogger _log = Log.ForContext<AiMemoryService>();

    private readonly string _filePath;
    private List<AiMemoryEntry> _memories = [];
    private bool _isDirty;
    private System.Timers.Timer? _saveTimer;

    public AiMemoryService()
    {
        _filePath = Path.Combine(DataPaths.BaseDir, "ai-memories.json");
        Load();
    }

    public IReadOnlyList<AiMemoryEntry> Memories => _memories;

    public void Add(string content, string? category = null)
    {
        // Check for duplicate/similar content before adding
        var existing = _memories.FirstOrDefault(m =>
            m.Content.Equals(content, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.UpdatedAt = DateTime.UtcNow;
            SaveDebounced();
            return;
        }

        if (_memories.Count >= MaxMemories)
        {
            // Remove the oldest memory
            _memories.RemoveAt(0);
        }

        _memories.Add(new AiMemoryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Content = content,
            Category = category,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        SaveDebounced();
        _log.Debug("AI memory added: {Content}", content);
    }

    public void Update(string id, string newContent)
    {
        var entry = _memories.FirstOrDefault(m => m.Id == id);
        if (entry == null) return;

        entry.Content = newContent;
        entry.UpdatedAt = DateTime.UtcNow;
        SaveDebounced();
    }

    public void Remove(string id)
    {
        _memories.RemoveAll(m => m.Id == id);
        SaveDebounced();
    }

    /// <summary>
    /// Formats all memories as a text block for injection into the system prompt.
    /// </summary>
    public string? FormatForPrompt()
    {
        if (_memories.Count == 0) return null;

        var lines = _memories.Select(m =>
            string.IsNullOrEmpty(m.Category)
                ? $"- {m.Content}"
                : $"- [{m.Category}] {m.Content}");

        return $"Things you remember about the user:\n{string.Join('\n', lines)}";
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;

            var json = File.ReadAllText(_filePath);
            _memories = JsonSerializer.Deserialize<List<AiMemoryEntry>>(json) ?? [];
            _log.Debug("Loaded {Count} AI memories", _memories.Count);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to load AI memories from {Path}", _filePath);
            _memories = [];
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var json = JsonSerializer.Serialize(_memories, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
            _isDirty = false;
            _log.Debug("Saved {Count} AI memories", _memories.Count);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to save AI memories to {Path}", _filePath);
        }
    }

    private void SaveDebounced()
    {
        _isDirty = true;
        _saveTimer?.Stop();
        _saveTimer?.Dispose();

        _saveTimer = new System.Timers.Timer(1000);
        _saveTimer.AutoReset = false;
        _saveTimer.Elapsed += (_, _) =>
        {
            if (_isDirty) Save();
        };
        _saveTimer.Start();
    }

    public void Flush()
    {
        _saveTimer?.Stop();
        _saveTimer?.Dispose();
        if (_isDirty) Save();
    }
}

internal sealed class AiMemoryEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
