using System.Text.Json;
using PrivStack.Sdk.Capabilities;
using Serilog;
using NativeLib = PrivStack.Desktop.Native.NativeLibrary;
using DatasetNative = PrivStack.Desktop.Native.DatasetNativeLibrary;

namespace PrivStack.Desktop.Services;

public sealed partial class DatasetService
{
    // ── SQL v2 (source: preprocessing + mutations + dry-run) ──────────

    public Task<SqlExecutionResponse> ExecuteSqlV2Async(
        string sql, int page, int pageSize, bool dryRun, CancellationToken ct)
    {
        var request = new { sql, page, page_size = pageSize, dry_run = dryRun };
        var ptr = DatasetNative.ExecuteSqlV2(JsonSerializer.Serialize(request, JsonOptions));
        try
        {
            var json = MarshalAndFree(ptr);
            var result = JsonSerializer.Deserialize<SqlExecutionResponse>(json, JsonOptions)
                         ?? new SqlExecutionResponse { Error = "Deserialize returned null" };

            if (result.Error != null)
                Log.Warning("ExecuteSqlV2Async error: {Error}", result.Error);

            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ExecuteSqlV2Async failed");
            return Task.FromResult(new SqlExecutionResponse
            {
                Error = ex.Message
            });
        }
    }

    // ── Saved Queries ─────────────────────────────────────────────────

    public Task<SavedQueryInfo> CreateSavedQueryAsync(
        string name, string sql, string? description, bool isView, CancellationToken ct)
    {
        var request = new { name, sql, description, is_view = isView };
        var ptr = DatasetNative.CreateSavedQuery(JsonSerializer.Serialize(request, JsonOptions));
        try
        {
            var json = MarshalAndFree(ptr);
            if (json.Contains("\"error\""))
                throw new InvalidOperationException($"CreateSavedQuery failed: {json}");

            var info = JsonSerializer.Deserialize<SavedQueryInfo>(json, JsonOptions)
                       ?? throw new InvalidOperationException("Deserialize returned null");
            return Task.FromResult(info);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            Log.Error(ex, "CreateSavedQueryAsync failed");
            throw;
        }
    }

    public Task UpdateSavedQueryAsync(
        string queryId, string name, string sql, string? description, bool isView, CancellationToken ct)
    {
        var request = new { id = queryId, name, sql, description, is_view = isView };
        var result = DatasetNative.UpdateSavedQuery(JsonSerializer.Serialize(request, JsonOptions));
        if (result != Native.PrivStackError.Ok)
            Log.Warning("UpdateSavedQueryAsync returned {Error} for {QueryId}", result, queryId);
        return Task.CompletedTask;
    }

    public Task DeleteSavedQueryAsync(string queryId, CancellationToken ct)
    {
        var result = DatasetNative.DeleteSavedQuery(queryId);
        if (result != Native.PrivStackError.Ok)
            Log.Warning("DeleteSavedQueryAsync returned {Error} for {QueryId}", result, queryId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SavedQueryInfo>> ListSavedQueriesAsync(CancellationToken ct)
    {
        var ptr = DatasetNative.ListSavedQueries();
        try
        {
            var json = MarshalAndFree(ptr);
            var list = JsonSerializer.Deserialize<List<SavedQueryInfo>>(json, JsonOptions);
            return Task.FromResult<IReadOnlyList<SavedQueryInfo>>(list ?? []);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ListSavedQueriesAsync failed");
            return Task.FromResult<IReadOnlyList<SavedQueryInfo>>([]);
        }
    }
}
