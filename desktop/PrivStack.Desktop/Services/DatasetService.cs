using System.Runtime.InteropServices;
using System.Text.Json;
using PrivStack.Sdk.Capabilities;
using Serilog;
using NativeLib = PrivStack.Desktop.Native.NativeLibrary;
using DatasetNative = PrivStack.Desktop.Native.DatasetNativeLibrary;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Implements <see cref="IDatasetService"/> by wrapping P/Invoke calls
/// to the native Rust dataset FFI layer.
/// </summary>
public sealed partial class DatasetService : IDatasetService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DatasetService>();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    // ── Phase 1: Core CRUD ──────────────────────────────────────────────

    public Task<IReadOnlyList<DatasetInfo>> ListDatasetsAsync(CancellationToken ct)
    {
        var ptr = DatasetNative.List();
        try
        {
            var json = MarshalAndFree(ptr);
            var list = JsonSerializer.Deserialize<List<DatasetInfo>>(json, JsonOptions);
            return Task.FromResult<IReadOnlyList<DatasetInfo>>(list ?? []);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ListDatasetsAsync failed");
            return Task.FromResult<IReadOnlyList<DatasetInfo>>([]);
        }
    }

    public Task<DatasetInfo?> GetDatasetAsync(string datasetId, CancellationToken ct)
    {
        var ptr = DatasetNative.Get(datasetId);
        try
        {
            var json = MarshalAndFree(ptr);
            if (json.Contains("\"error\""))
            {
                Log.Warning("GetDatasetAsync error: {Json}", json);
                return Task.FromResult<DatasetInfo?>(null);
            }
            var info = JsonSerializer.Deserialize<DatasetInfo>(json, JsonOptions);
            return Task.FromResult(info);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GetDatasetAsync failed for {DatasetId}", datasetId);
            return Task.FromResult<DatasetInfo?>(null);
        }
    }

    public Task<DatasetInfo> ImportCsvAsync(string filePath, string name, CancellationToken ct)
    {
        var ptr = DatasetNative.ImportCsv(filePath, name);
        try
        {
            var json = MarshalAndFree(ptr);
            if (json.Contains("\"error\""))
                throw new InvalidOperationException($"Import failed: {json}");

            var info = JsonSerializer.Deserialize<DatasetInfo>(json, JsonOptions)
                       ?? throw new InvalidOperationException("Deserialize returned null");
            return Task.FromResult(info);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            Log.Error(ex, "ImportCsvAsync failed for {FilePath}", filePath);
            throw;
        }
    }

    public Task DeleteDatasetAsync(string datasetId, CancellationToken ct)
    {
        var result = DatasetNative.Delete(datasetId);
        if (result != Native.PrivStackError.Ok)
            Log.Warning("DeleteDatasetAsync returned {Error} for {DatasetId}", result, datasetId);
        return Task.CompletedTask;
    }

    public Task RenameDatasetAsync(string datasetId, string newName, CancellationToken ct)
    {
        var result = DatasetNative.Rename(datasetId, newName);
        if (result != Native.PrivStackError.Ok)
            Log.Warning("RenameDatasetAsync returned {Error} for {DatasetId}", result, datasetId);
        return Task.CompletedTask;
    }

    public Task<DatasetQueryResult> QueryAsync(DatasetQuery query, CancellationToken ct)
    {
        var queryJson = JsonSerializer.Serialize(query, JsonOptions);
        var ptr = DatasetNative.Query(queryJson);
        try
        {
            var json = MarshalAndFree(ptr);
            var result = JsonSerializer.Deserialize<DatasetQueryResult>(json, JsonOptions)
                         ?? new DatasetQueryResult();
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "QueryAsync failed");
            return Task.FromResult(new DatasetQueryResult());
        }
    }

    public Task<IReadOnlyList<DatasetColumnInfo>> GetColumnsAsync(string datasetId, CancellationToken ct)
    {
        var ptr = DatasetNative.GetColumns(datasetId);
        try
        {
            var json = MarshalAndFree(ptr);
            var list = JsonSerializer.Deserialize<List<DatasetColumnInfo>>(json, JsonOptions);
            return Task.FromResult<IReadOnlyList<DatasetColumnInfo>>(list ?? []);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GetColumnsAsync failed for {DatasetId}", datasetId);
            return Task.FromResult<IReadOnlyList<DatasetColumnInfo>>([]);
        }
    }

    // ── Phase 5: Relations ──────────────────────────────────────────────

    public Task<DatasetRelation> CreateRelationAsync(
        string sourceDatasetId, string sourceColumn,
        string targetDatasetId, string targetColumn,
        CancellationToken ct)
    {
        var request = new
        {
            source_dataset_id = sourceDatasetId,
            source_column = sourceColumn,
            target_dataset_id = targetDatasetId,
            target_column = targetColumn,
        };
        var ptr = DatasetNative.CreateRelation(JsonSerializer.Serialize(request));
        try
        {
            var json = MarshalAndFree(ptr);
            if (json.Contains("\"error\""))
                throw new InvalidOperationException($"CreateRelation failed: {json}");

            var rel = JsonSerializer.Deserialize<DatasetRelation>(json, JsonOptions)
                      ?? throw new InvalidOperationException("Deserialize returned null");
            return Task.FromResult(rel);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            Log.Error(ex, "CreateRelationAsync failed");
            throw;
        }
    }

    public Task DeleteRelationAsync(string relationId, CancellationToken ct)
    {
        var result = DatasetNative.DeleteRelation(relationId);
        if (result != Native.PrivStackError.Ok)
            Log.Warning("DeleteRelationAsync returned {Error} for {RelationId}", result, relationId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DatasetRelation>> ListRelationsAsync(string datasetId, CancellationToken ct)
    {
        var ptr = DatasetNative.ListRelations(datasetId);
        try
        {
            var json = MarshalAndFree(ptr);
            var list = JsonSerializer.Deserialize<List<DatasetRelation>>(json, JsonOptions);
            return Task.FromResult<IReadOnlyList<DatasetRelation>>(list ?? []);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ListRelationsAsync failed for {DatasetId}", datasetId);
            return Task.FromResult<IReadOnlyList<DatasetRelation>>([]);
        }
    }

    // ── Phase 6: Row-Page Linking ───────────────────────────────────────

    public Task LinkRowToPageAsync(string datasetId, string rowKey, string pageId, CancellationToken ct)
    {
        var result = DatasetNative.LinkRowPage(datasetId, rowKey, pageId);
        if (result != Native.PrivStackError.Ok)
            Log.Warning("LinkRowToPageAsync returned {Error}", result);
        return Task.CompletedTask;
    }

    public Task<string?> GetPageForRowAsync(string datasetId, string rowKey, CancellationToken ct)
    {
        var ptr = DatasetNative.GetPageForRow(datasetId, rowKey);
        try
        {
            var json = MarshalAndFree(ptr);
            if (json == "null" || string.IsNullOrEmpty(json))
                return Task.FromResult<string?>(null);

            // FFI returns JSON string like "\"page-id\"", strip quotes
            var pageId = json.Trim('"');
            return Task.FromResult<string?>(pageId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GetPageForRowAsync failed");
            return Task.FromResult<string?>(null);
        }
    }

    public Task<(string DatasetId, string RowKey)?> GetRowForPageAsync(string pageId, CancellationToken ct)
    {
        var ptr = DatasetNative.GetRowForPage(pageId);
        try
        {
            var json = MarshalAndFree(ptr);
            if (json == "null" || string.IsNullOrEmpty(json))
                return Task.FromResult<(string, string)?>(null);

            using var doc = JsonDocument.Parse(json);
            var dsId = doc.RootElement.GetProperty("dataset_id").GetString()!;
            var rowKey = doc.RootElement.GetProperty("row_key").GetString()!;
            return Task.FromResult<(string, string)?>((dsId, rowKey));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GetRowForPageAsync failed");
            return Task.FromResult<(string, string)?>(null);
        }
    }

    // ── Phase 8: Views ──────────────────────────────────────────────────

    public Task<DatasetView> CreateViewAsync(string datasetId, string name, ViewConfig config, CancellationToken ct)
    {
        var request = new { dataset_id = datasetId, name, config };
        var ptr = DatasetNative.CreateView(JsonSerializer.Serialize(request, JsonOptions));
        try
        {
            var json = MarshalAndFree(ptr);
            if (json.Contains("\"error\""))
                throw new InvalidOperationException($"CreateView failed: {json}");

            var view = JsonSerializer.Deserialize<DatasetView>(json, JsonOptions)
                       ?? throw new InvalidOperationException("Deserialize returned null");
            return Task.FromResult(view);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            Log.Error(ex, "CreateViewAsync failed");
            throw;
        }
    }

    public Task UpdateViewAsync(string viewId, ViewConfig config, CancellationToken ct)
    {
        var request = new { view_id = viewId, config };
        var result = DatasetNative.UpdateView(JsonSerializer.Serialize(request, JsonOptions));
        if (result != Native.PrivStackError.Ok)
            Log.Warning("UpdateViewAsync returned {Error} for {ViewId}", result, viewId);
        return Task.CompletedTask;
    }

    public Task DeleteViewAsync(string viewId, CancellationToken ct)
    {
        var result = DatasetNative.DeleteView(viewId);
        if (result != Native.PrivStackError.Ok)
            Log.Warning("DeleteViewAsync returned {Error} for {ViewId}", result, viewId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DatasetView>> ListViewsAsync(string datasetId, CancellationToken ct)
    {
        var ptr = DatasetNative.ListViews(datasetId);
        try
        {
            var json = MarshalAndFree(ptr);
            var list = JsonSerializer.Deserialize<List<DatasetView>>(json, JsonOptions);
            return Task.FromResult<IReadOnlyList<DatasetView>>(list ?? []);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ListViewsAsync failed for {DatasetId}", datasetId);
            return Task.FromResult<IReadOnlyList<DatasetView>>([]);
        }
    }

    // ── Phase 9: Aggregation ────────────────────────────────────────────

    public Task<AggregateQueryResult> AggregateAsync(AggregateQuery query, CancellationToken ct)
    {
        var queryJson = JsonSerializer.Serialize(query, JsonOptions);
        var ptr = DatasetNative.Aggregate(queryJson);
        try
        {
            var json = MarshalAndFree(ptr);
            if (json.Contains("\"error\""))
            {
                Log.Warning("AggregateAsync error: {Json}", json);
                return Task.FromResult(new AggregateQueryResult());
            }

            var result = JsonSerializer.Deserialize<AggregateQueryResult>(json, JsonOptions)
                         ?? new AggregateQueryResult();
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AggregateAsync failed");
            return Task.FromResult(new AggregateQueryResult());
        }
    }

    // ── Phase 10: Raw SQL & Saved Queries ───────────────────────────────

    public Task<DatasetQueryResult> ExecuteSqlAsync(string sql, int page, int pageSize, CancellationToken ct)
    {
        var request = new { sql, page, page_size = pageSize };
        var ptr = DatasetNative.ExecuteSql(JsonSerializer.Serialize(request));
        try
        {
            var json = MarshalAndFree(ptr);
            if (json.Contains("\"error\""))
            {
                Log.Warning("ExecuteSqlAsync error: {Json}", json);
                return Task.FromResult(new DatasetQueryResult());
            }

            var result = JsonSerializer.Deserialize<DatasetQueryResult>(json, JsonOptions)
                         ?? new DatasetQueryResult();
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ExecuteSqlAsync failed");
            return Task.FromResult(new DatasetQueryResult());
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string MarshalAndFree(nint ptr)
    {
        if (ptr == nint.Zero)
            return "{}";

        try
        {
            return Marshal.PtrToStringUTF8(ptr) ?? "{}";
        }
        finally
        {
            NativeLib.FreeString(ptr);
        }
    }
}
