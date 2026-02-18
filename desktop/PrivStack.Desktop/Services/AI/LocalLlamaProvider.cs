using System.Text;
using PrivStack.Sdk.Services;
using Serilog;

namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// Local LLM inference provider using LLamaSharp with GGUF models.
/// Thread-safe via SemaphoreSlim(1) — single inference context at a time.
/// </summary>
internal sealed class LocalLlamaProvider : IAiProvider
{
    private static readonly ILogger _log = Log.ForContext<LocalLlamaProvider>();
    private readonly AiModelManager _modelManager;
    private readonly SemaphoreSlim _inferLock = new(1, 1);

    private LLama.LLamaWeights? _weights;
    private LLama.LLamaContext? _context;
    private string? _loadedModelPath;

    public LocalLlamaProvider(AiModelManager modelManager) => _modelManager = modelManager;

    public string Id => "local";
    public string DisplayName => "Local (LLamaSharp)";
    public bool IsLocal => true;

    public bool IsConfigured
    {
        get
        {
            var models = _modelManager.AvailableModels;
            return models.Any(m => _modelManager.IsModelDownloaded(m));
        }
    }

    public IReadOnlyList<AiModelInfo> AvailableModels =>
        _modelManager.AvailableModels.Select(m => new AiModelInfo
        {
            Id = m,
            DisplayName = m,
            SizeBytes = _modelManager.GetModelSize(m),
            IsDownloaded = _modelManager.IsModelDownloaded(m)
        }).ToList();

    public Task<bool> ValidateAsync(CancellationToken ct = default)
    {
        return Task.FromResult(IsConfigured);
    }

    public async Task<AiResponse> CompleteAsync(AiRequest request, string? modelOverride, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await _inferLock.WaitAsync(ct);
        try
        {
            var modelName = modelOverride ?? GetDefaultModelName();
            if (modelName == null || !_modelManager.IsModelDownloaded(modelName))
            {
                return AiResponse.Failure("No local model downloaded") with
                {
                    ProviderUsed = Id, Duration = sw.Elapsed
                };
            }

            var modelPath = _modelManager.GetModelPath(modelName);
            await EnsureModelLoadedAsync(modelPath);

            if (_context == null)
            {
                return AiResponse.Failure("Failed to load local model") with
                {
                    ProviderUsed = Id, Duration = sw.Elapsed
                };
            }

            var (prompt, antiPrompts) = FormatPrompt(modelName, request.SystemPrompt, request.UserPrompt);
            _log.Debug("Local inference using model {Model}, prompt length {Len}, anti-prompts: [{Anti}]",
                modelName, prompt.Length, string.Join(", ", antiPrompts));

            var executor = new LLama.StatelessExecutor(_weights!, _context.Params);
            var inferParams = new LLama.Common.InferenceParams
            {
                MaxTokens = request.MaxTokens,
                AntiPrompts = antiPrompts,
                SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline
                {
                    Temperature = (float)request.Temperature
                }
            };

            var sb = new StringBuilder();
            await foreach (var token in executor.InferAsync(prompt, inferParams, ct))
            {
                sb.Append(token);
            }

            sw.Stop();
            return new AiResponse
            {
                Success = true,
                Content = sb.ToString().Trim(),
                ProviderUsed = Id,
                ModelUsed = modelName,
                Duration = sw.Elapsed
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            sw.Stop();
            _log.Error(ex, "Local inference failed");
            return AiResponse.Failure(ex.Message) with
            {
                ProviderUsed = Id, Duration = sw.Elapsed
            };
        }
        finally
        {
            _inferLock.Release();
        }
    }

    private async Task EnsureModelLoadedAsync(string modelPath)
    {
        if (_loadedModelPath == modelPath && _context != null)
            return;

        // Dispose previous model
        _context?.Dispose();
        _weights?.Dispose();
        _context = null;
        _weights = null;
        _loadedModelPath = null;

        _log.Information("Loading local LLM model from {Path}", modelPath);

        await Task.Run(() =>
        {
            var modelParams = new LLama.Common.ModelParams(modelPath)
            {
                ContextSize = 4096,
                GpuLayerCount = 0, // CPU only
                Threads = Math.Min(Environment.ProcessorCount, 4)
            };

            _weights = LLama.LLamaWeights.LoadFromFile(modelParams);
            _context = _weights.CreateContext(modelParams);
        });

        _loadedModelPath = modelPath;
        _log.Information("Local LLM model loaded: {Path}", modelPath);
    }

    private static (string Prompt, List<string> AntiPrompts) FormatPrompt(
        string modelName, string systemPrompt, string userPrompt)
    {
        if (modelName.StartsWith("llama", StringComparison.OrdinalIgnoreCase))
        {
            // Llama 3.x Instruct chat template
            var prompt = $"<|begin_of_text|><|start_header_id|>system<|end_header_id|>\n\n{systemPrompt}<|eot_id|>" +
                         $"<|start_header_id|>user<|end_header_id|>\n\n{userPrompt}<|eot_id|>" +
                         "<|start_header_id|>assistant<|end_header_id|>\n\n";
            return (prompt, ["<|eot_id|>", "<|end_of_text|>"]);
        }

        if (modelName.StartsWith("mistral", StringComparison.OrdinalIgnoreCase))
        {
            // Mistral Instruct v0.2 chat template
            var prompt = $"[INST] {systemPrompt}\n\n{userPrompt} [/INST]";
            return (prompt, ["</s>", "[INST]"]);
        }

        // Phi-3 chat template (default)
        {
            var prompt = $"<|system|>\n{systemPrompt}<|end|>\n<|user|>\n{userPrompt}<|end|>\n<|assistant|>\n";
            return (prompt, ["<|end|>", "<|user|>"]);
        }
    }

    private string? GetDefaultModelName()
    {
        return _modelManager.AvailableModels
            .FirstOrDefault(m => _modelManager.IsModelDownloaded(m));
    }

    public void Dispose()
    {
        _context?.Dispose();
        _weights?.Dispose();
        _inferLock.Dispose();
    }
}
