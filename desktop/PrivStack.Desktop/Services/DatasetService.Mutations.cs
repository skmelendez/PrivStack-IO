using System.Text.Json;
using PrivStack.Sdk.Capabilities;
using Serilog;
using NativeLib = PrivStack.Desktop.Native.NativeLibrary;
using DatasetNative = PrivStack.Desktop.Native.DatasetNativeLibrary;

namespace PrivStack.Desktop.Services;

public sealed partial class DatasetService
{
    // ── Dataset Creation ────────────────────────────────────────────────

    public Task<DatasetInfo> CreateEmptyDatasetAsync(
        string name, IReadOnlyList<DatasetColumnDef> columns, CancellationToken ct)
    {
        var request = new { name, columns };
        var ptr = DatasetNative.CreateEmpty(JsonSerializer.Serialize(request, JsonOptions));
        try
        {
            var json = MarshalAndFree(ptr);
            if (json.Contains("\"error\""))
            {
                Log.Warning("CreateEmptyDatasetAsync error: {Json}", json);
                throw new InvalidOperationException($"Create empty dataset failed: {json}");
            }
            var info = JsonSerializer.Deserialize<DatasetInfo>(json, JsonOptions)
                       ?? throw new InvalidOperationException("Deserialize returned null");
            return Task.FromResult(info);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            Log.Error(ex, "CreateEmptyDatasetAsync failed");
            throw;
        }
    }

    public Task<DatasetInfo> DuplicateDatasetAsync(
        string sourceDatasetId, string newName, CancellationToken ct)
    {
        var request = new { source_dataset_id = sourceDatasetId, new_name = newName };
        var ptr = DatasetNative.Duplicate(JsonSerializer.Serialize(request, JsonOptions));
        try
        {
            var json = MarshalAndFree(ptr);
            if (json.Contains("\"error\""))
            {
                Log.Warning("DuplicateDatasetAsync error: {Json}", json);
                throw new InvalidOperationException($"Duplicate dataset failed: {json}");
            }
            var info = JsonSerializer.Deserialize<DatasetInfo>(json, JsonOptions)
                       ?? throw new InvalidOperationException("Deserialize returned null");
            return Task.FromResult(info);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            Log.Error(ex, "DuplicateDatasetAsync failed");
            throw;
        }
    }

    public Task<DatasetInfo> ImportFromContentAsync(string content, string name, CancellationToken ct)
    {
        var request = new { content, name };
        var ptr = DatasetNative.ImportContent(JsonSerializer.Serialize(request, JsonOptions));
        try
        {
            var json = MarshalAndFree(ptr);
            if (json.Contains("\"error\""))
            {
                Log.Warning("ImportFromContentAsync error: {Json}", json);
                throw new InvalidOperationException($"Import from content failed: {json}");
            }
            var info = JsonSerializer.Deserialize<DatasetInfo>(json, JsonOptions)
                       ?? throw new InvalidOperationException("Deserialize returned null");
            return Task.FromResult(info);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            Log.Error(ex, "ImportFromContentAsync failed");
            throw;
        }
    }

    // ── Row CRUD ────────────────────────────────────────────────────────

    public Task InsertRowAsync(string datasetId, IDictionary<string, object?> values, CancellationToken ct)
    {
        var request = new { dataset_id = datasetId, values };
        var ptr = DatasetNative.InsertRow(JsonSerializer.Serialize(request, JsonOptions));
        try
        {
            var json = MarshalAndFree(ptr);
            if (json.Contains("\"error\""))
                Log.Warning("InsertRowAsync error: {Json}", json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "InsertRowAsync failed");
        }
        return Task.CompletedTask;
    }

    public Task UpdateCellAsync(string datasetId, long rowIndex, string column, object? value, CancellationToken ct)
    {
        var request = new { dataset_id = datasetId, row_index = rowIndex, column, value };
        var ptr = DatasetNative.UpdateCell(JsonSerializer.Serialize(request, JsonOptions));
        try
        {
            var json = MarshalAndFree(ptr);
            if (json.Contains("\"error\""))
                Log.Warning("UpdateCellAsync error: {Json}", json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UpdateCellAsync failed");
        }
        return Task.CompletedTask;
    }

    public Task DeleteRowsAsync(string datasetId, IReadOnlyList<long> rowIndices, CancellationToken ct)
    {
        var request = new { dataset_id = datasetId, row_indices = rowIndices };
        var ptr = DatasetNative.DeleteRows(JsonSerializer.Serialize(request, JsonOptions));
        try
        {
            var json = MarshalAndFree(ptr);
            if (json.Contains("\"error\""))
                Log.Warning("DeleteRowsAsync error: {Json}", json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DeleteRowsAsync failed");
        }
        return Task.CompletedTask;
    }

    // ── Column CRUD ─────────────────────────────────────────────────────

    public Task AddColumnAsync(string datasetId, string columnName, string columnType, string? defaultValue, CancellationToken ct)
    {
        var request = new { dataset_id = datasetId, column_name = columnName, column_type = columnType, default_value = defaultValue };
        var ptr = DatasetNative.AddColumn(JsonSerializer.Serialize(request, JsonOptions));
        try
        {
            var json = MarshalAndFree(ptr);
            if (json.Contains("\"error\""))
                Log.Warning("AddColumnAsync error: {Json}", json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AddColumnAsync failed");
        }
        return Task.CompletedTask;
    }

    public Task DropColumnAsync(string datasetId, string columnName, CancellationToken ct)
    {
        var request = new { dataset_id = datasetId, column_name = columnName };
        var result = DatasetNative.DropColumn(JsonSerializer.Serialize(request, JsonOptions));
        if (result != Native.PrivStackError.Ok)
            Log.Warning("DropColumnAsync returned {Error}", result);
        return Task.CompletedTask;
    }

    public Task RenameColumnAsync(string datasetId, string oldName, string newName, CancellationToken ct)
    {
        var request = new { dataset_id = datasetId, column_name = oldName, new_name = newName };
        var result = DatasetNative.RenameColumn(JsonSerializer.Serialize(request, JsonOptions));
        if (result != Native.PrivStackError.Ok)
            Log.Warning("RenameColumnAsync returned {Error}", result);
        return Task.CompletedTask;
    }
}
