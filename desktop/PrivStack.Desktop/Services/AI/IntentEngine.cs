using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.Messaging;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.Plugin;
using PrivStack.Sdk.Capabilities;
using PrivStack.Sdk.Messaging;
using PrivStack.Sdk.Services;
using Serilog;

namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// Shell-side intent classification engine. Subscribes to IntentSignalMessage,
/// collects IIntentProvider descriptors, uses IAiService to classify signals,
/// and surfaces IntentSuggestion objects for the UI.
/// </summary>
internal sealed class IntentEngine : IIntentEngine, IRecipient<IntentSignalMessage>, IDisposable
{
    private static readonly ILogger _log = Log.ForContext<IntentEngine>();
    private const int MaxSuggestions = 50;
    private const int QueueCapacity = 20;

    private static readonly TimeSpan DebounceInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DeduplicationWindow = TimeSpan.FromMinutes(5);

    private readonly IAppSettingsService _appSettings;
    private readonly IAiService _aiService;
    private readonly IPluginRegistry _pluginRegistry;
    private readonly Channel<IntentSignalMessage> _signalChannel;
    private readonly List<IntentSuggestion> _suggestions = [];
    private readonly object _suggestionsLock = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentAnalyses = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastPluginSignal = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _consumerTask;

    public IntentEngine(
        IAppSettingsService appSettings,
        IAiService aiService,
        IPluginRegistry pluginRegistry)
    {
        _appSettings = appSettings;
        _aiService = aiService;
        _pluginRegistry = pluginRegistry;
        _signalChannel = Channel.CreateBounded<IntentSignalMessage>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        WeakReferenceMessenger.Default.Register<IntentSignalMessage>(this);
        _consumerTask = Task.Run(() => ConsumeSignalsAsync(_disposeCts.Token));
    }

    // ── IIntentEngine ─────────────────────────────────────────────────

    public bool IsEnabled =>
        _appSettings.Settings.AiEnabled &&
        _appSettings.Settings.AiIntentEnabled &&
        _aiService.IsAvailable;

    public IReadOnlyList<IntentSuggestion> PendingSuggestions
    {
        get
        {
            lock (_suggestionsLock)
                return _suggestions.ToList().AsReadOnly();
        }
    }

    public event EventHandler<IntentSuggestion>? SuggestionAdded;
    public event EventHandler<string>? SuggestionRemoved;
    public event EventHandler? SuggestionsCleared;

    public async Task AnalyzeAsync(IntentSignalMessage signal, CancellationToken ct = default)
    {
        if (!IsEnabled) return;
        if (string.IsNullOrWhiteSpace(signal.Content)) return;

        var intents = GetAllAvailableIntents();
        if (intents.Count == 0) return;

        try
        {
            var systemPrompt = IntentPromptBuilder.BuildSystemPrompt(intents, DateTimeOffset.Now);
            var userPrompt = IntentPromptBuilder.BuildUserPrompt(
                signal.Content, signal.EntityType, signal.EntityTitle);

            var response = await _aiService.CompleteAsync(new AiRequest
            {
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt,
                MaxTokens = 1024,
                Temperature = 0.3,
                FeatureId = "intent.classify",
            }, ct);

            if (!response.Success || string.IsNullOrWhiteSpace(response.Content))
            {
                _log.Debug("Intent classification returned no results for signal from {Plugin}",
                    signal.SourcePluginId);
                return;
            }

            ParseAndAddSuggestions(response.Content, signal, intents);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Error(ex, "Intent classification failed for signal from {Plugin}", signal.SourcePluginId);
        }
    }

    public async Task<IntentResult> ExecuteAsync(
        string suggestionId,
        IReadOnlyDictionary<string, string>? slotOverrides = null,
        CancellationToken ct = default)
    {
        IntentSuggestion? suggestion;
        lock (_suggestionsLock)
        {
            suggestion = _suggestions.FirstOrDefault(s => s.SuggestionId == suggestionId);
        }

        if (suggestion == null)
            return IntentResult.Failure("Suggestion not found or already dismissed.");

        var provider = _pluginRegistry
            .GetCapabilityProviders<IIntentProvider>()
            .FirstOrDefault(p => p.GetSupportedIntents()
                .Any(i => i.IntentId == suggestion.MatchedIntent.IntentId));

        if (provider == null)
            return IntentResult.Failure($"No provider found for intent '{suggestion.MatchedIntent.IntentId}'.");

        var finalSlots = new Dictionary<string, string>(suggestion.ExtractedSlots);
        if (slotOverrides != null)
        {
            foreach (var (key, value) in slotOverrides)
                finalSlots[key] = value;
        }

        var request = new IntentRequest
        {
            IntentId = suggestion.MatchedIntent.IntentId,
            Slots = finalSlots,
            SourceEntityId = suggestion.SourceSignal.EntityId,
            SourcePluginId = suggestion.SourceSignal.SourcePluginId,
        };

        try
        {
            var result = await provider.ExecuteIntentAsync(request, ct);
            RemoveSuggestion(suggestionId);
            return result;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Intent execution failed: {IntentId}", suggestion.MatchedIntent.IntentId);
            return IntentResult.Failure(ex.Message);
        }
    }

    public void Dismiss(string suggestionId) => RemoveSuggestion(suggestionId);

    public void ClearAll()
    {
        lock (_suggestionsLock)
            _suggestions.Clear();
        SuggestionsCleared?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<IntentDescriptor> GetAllAvailableIntents()
    {
        var providers = _pluginRegistry.GetCapabilityProviders<IIntentProvider>();
        return providers.SelectMany(p =>
        {
            try { return p.GetSupportedIntents(); }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to get intents from provider");
                return [];
            }
        }).ToList().AsReadOnly();
    }

    // ── IRecipient<IntentSignalMessage> ──────────────────────────────

    public void Receive(IntentSignalMessage message)
    {
        if (!IsEnabled) return;
        if (!_appSettings.Settings.AiIntentAutoAnalyze &&
            message.SignalType != IntentSignalType.UserRequest)
            return;

        _signalChannel.Writer.TryWrite(message);
    }

    // ── Signal Consumer ──────────────────────────────────────────────

    private async Task ConsumeSignalsAsync(CancellationToken ct)
    {
        await foreach (var signal in _signalChannel.Reader.ReadAllAsync(ct))
        {
            try
            {
                // UserRequest signals bypass debounce
                if (signal.SignalType != IntentSignalType.UserRequest)
                {
                    // Per-plugin debounce
                    var now = DateTimeOffset.UtcNow;
                    if (_lastPluginSignal.TryGetValue(signal.SourcePluginId, out var lastTime) &&
                        now - lastTime < DebounceInterval)
                    {
                        await Task.Delay(DebounceInterval, ct);
                    }
                    _lastPluginSignal[signal.SourcePluginId] = DateTimeOffset.UtcNow;

                    // Deduplication
                    var dedupKey = ComputeDeduplicationKey(signal);
                    if (_recentAnalyses.TryGetValue(dedupKey, out var analyzedAt) &&
                        DateTimeOffset.UtcNow - analyzedAt < DeduplicationWindow)
                    {
                        continue;
                    }
                    _recentAnalyses[dedupKey] = DateTimeOffset.UtcNow;
                }

                await AnalyzeAsync(signal, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Error(ex, "Error processing intent signal from {Plugin}", signal.SourcePluginId);
            }
        }
    }

    // ── Private Helpers ──────────────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private void ParseAndAddSuggestions(
        string aiContent,
        IntentSignalMessage signal,
        IReadOnlyList<IntentDescriptor> intents)
    {
        try
        {
            // Strip markdown fences if present
            var json = aiContent.Trim();
            if (json.StartsWith("```"))
            {
                var firstNewline = json.IndexOf('\n');
                if (firstNewline >= 0) json = json[(firstNewline + 1)..];
                if (json.EndsWith("```")) json = json[..^3];
                json = json.Trim();
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("intents", out var intentsArray))
                return;

            foreach (var item in intentsArray.EnumerateArray())
            {
                var intentId = item.GetProperty("intent_id").GetString();
                if (string.IsNullOrEmpty(intentId)) continue;

                var descriptor = intents.FirstOrDefault(i => i.IntentId == intentId);
                if (descriptor == null)
                {
                    _log.Debug("AI returned unknown intent ID: {IntentId}", intentId);
                    continue;
                }

                var confidence = item.TryGetProperty("confidence", out var confProp)
                    ? confProp.GetDouble() : 0.5;
                var summary = item.TryGetProperty("summary", out var sumProp)
                    ? sumProp.GetString() ?? descriptor.DisplayName
                    : descriptor.DisplayName;

                var slots = new Dictionary<string, string>();
                if (item.TryGetProperty("slots", out var slotsObj) &&
                    slotsObj.ValueKind == JsonValueKind.Object)
                {
                    foreach (var slot in slotsObj.EnumerateObject())
                    {
                        var value = slot.Value.ValueKind == JsonValueKind.String
                            ? slot.Value.GetString()
                            : slot.Value.GetRawText();
                        if (!string.IsNullOrEmpty(value))
                            slots[slot.Name] = value;
                    }
                }

                var suggestion = new IntentSuggestion
                {
                    SuggestionId = Guid.NewGuid().ToString("N"),
                    MatchedIntent = descriptor,
                    Summary = summary,
                    Confidence = confidence,
                    SourceSignal = signal,
                    ExtractedSlots = slots,
                };

                AddSuggestion(suggestion);
            }
        }
        catch (JsonException ex)
        {
            _log.Warning(ex, "Failed to parse intent classification response");
        }
    }

    private void AddSuggestion(IntentSuggestion suggestion)
    {
        lock (_suggestionsLock)
        {
            // FIFO eviction
            while (_suggestions.Count >= MaxSuggestions)
            {
                var oldest = _suggestions[0];
                _suggestions.RemoveAt(0);
                SuggestionRemoved?.Invoke(this, oldest.SuggestionId);
            }
            _suggestions.Add(suggestion);
        }
        SuggestionAdded?.Invoke(this, suggestion);
    }

    private void RemoveSuggestion(string suggestionId)
    {
        lock (_suggestionsLock)
        {
            var idx = _suggestions.FindIndex(s => s.SuggestionId == suggestionId);
            if (idx >= 0) _suggestions.RemoveAt(idx);
        }
        SuggestionRemoved?.Invoke(this, suggestionId);
    }

    private static string ComputeDeduplicationKey(IntentSignalMessage signal)
    {
        var raw = $"{signal.SourcePluginId}:{signal.EntityId}:{signal.Content}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash[..8]);
    }

    // ── IDisposable ──────────────────────────────────────────────────

    public void Dispose()
    {
        _disposeCts.Cancel();
        _signalChannel.Writer.TryComplete();
        WeakReferenceMessenger.Default.Unregister<IntentSignalMessage>(this);
        _disposeCts.Dispose();
    }
}
