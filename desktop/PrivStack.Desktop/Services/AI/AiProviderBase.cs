using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PrivStack.Sdk.Services;
using Serilog;

namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// Base class for cloud AI providers. Handles HttpClient construction,
/// retry with exponential backoff, response timing, and Serilog logging.
/// </summary>
internal abstract class AiProviderBase : IAiProvider
{
    private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays = [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4)
    ];

    protected readonly ILogger Log;
    private readonly Lazy<HttpClient> _httpClient;

    protected AiProviderBase()
    {
        Log = Serilog.Log.ForContext(GetType());
        _httpClient = new Lazy<HttpClient>(CreateHttpClient);
    }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract bool IsConfigured { get; }
    public virtual bool IsLocal => false;
    public abstract IReadOnlyList<AiModelInfo> AvailableModels { get; }
    public abstract Task<bool> ValidateAsync(CancellationToken ct = default);

    protected HttpClient Http => _httpClient.Value;

    public async Task<AiResponse> CompleteAsync(AiRequest request, string? modelOverride, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var response = await ExecuteCompletionAsync(request, modelOverride, ct);
                sw.Stop();

                return response with { Duration = sw.Elapsed };
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries && IsRetryable(ex))
            {
                Log.Warning("AI request attempt {Attempt} failed, retrying in {Delay}ms: {Error}",
                    attempt + 1, RetryDelays[attempt].TotalMilliseconds, ex.Message);

                await Task.Delay(RetryDelays[attempt], ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sw.Stop();
                Log.Error(ex, "AI completion failed for provider {Provider}", Id);

                return AiResponse.Failure(ex.Message) with
                {
                    ProviderUsed = Id,
                    Duration = sw.Elapsed
                };
            }
        }

        sw.Stop();
        return AiResponse.Failure("Max retries exceeded") with
        {
            ProviderUsed = Id,
            Duration = sw.Elapsed
        };
    }

    protected abstract Task<AiResponse> ExecuteCompletionAsync(
        AiRequest request, string? modelOverride, CancellationToken ct);

    protected async Task<JsonDocument> PostJsonAsync(
        string url, object payload, Dictionary<string, string> headers, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        foreach (var (key, value) in headers)
            httpRequest.Headers.TryAddWithoutValidation(key, value);

        using var response = await Http.SendAsync(httpRequest, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            Log.Error("AI API error {StatusCode}: {Body}", response.StatusCode, responseBody);
            throw new HttpRequestException($"API returned {response.StatusCode}: {responseBody}");
        }

        return JsonDocument.Parse(responseBody);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PrivStack", "1.0"));
        return client;
    }

    private static bool IsRetryable(HttpRequestException ex)
    {
        return ex.StatusCode is
            System.Net.HttpStatusCode.TooManyRequests or
            System.Net.HttpStatusCode.ServiceUnavailable or
            System.Net.HttpStatusCode.GatewayTimeout or
            System.Net.HttpStatusCode.InternalServerError;
    }
}
