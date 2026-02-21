using System.Globalization;
using System.Text;
using PrivStack.Sdk.Capabilities;
using Serilog;

namespace PrivStack.Desktop.Services.AI;

/// <summary>
/// Generates small derived datasets from sample data for AI insight charts.
/// Each chart gets its own dataset with pre-aggregated values, avoiding column
/// mismatch errors when the AI references computed or renamed columns.
/// </summary>
internal sealed class ChartDatasetGenerator
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ChartDatasetGenerator>();

    private readonly IDatasetService _datasetService;

    public ChartDatasetGenerator(IDatasetService datasetService)
    {
        _datasetService = datasetService;
    }

    /// <summary>
    /// Creates a generated dataset from sample rows for the given chart specification.
    /// Returns the new dataset ID, or null if generation fails.
    /// </summary>
    public async Task<string?> GenerateAsync(
        ChartSuggestion chart,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> sampleRows,
        string sourceDatasetName,
        CancellationToken ct = default)
    {
        try
        {
            var xIdx = FindColumnIndex(columns, chart.XColumn);
            var yIdx = FindColumnIndex(columns, chart.YColumn);
            if (xIdx < 0 || yIdx < 0)
            {
                Log.Debug("Chart columns not found in sample: x={X} y={Y}", chart.XColumn, chart.YColumn);
                return null;
            }

            var groupIdx = chart.GroupBy != null ? FindColumnIndex(columns, chart.GroupBy) : -1;

            string csv;
            if (groupIdx >= 0)
                csv = AggregateGrouped(sampleRows, xIdx, yIdx, groupIdx, chart);
            else
                csv = AggregateSingle(sampleRows, xIdx, yIdx, chart);

            if (string.IsNullOrEmpty(csv))
                return null;

            var datasetName = $"[AI] {SanitizeName(chart.Title)} — {sourceDatasetName}";
            var dataset = await _datasetService.ImportFromContentAsync(csv, datasetName, ct);
            Log.Information("Created chart dataset {Id} ({Name}) with aggregated data", dataset.Id, datasetName);
            return dataset.Id.Value;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to generate chart dataset for '{Title}'", chart.Title);
            return null;
        }
    }

    private static string AggregateSingle(
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int xIdx, int yIdx, ChartSuggestion chart)
    {
        var groups = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (xIdx >= row.Count || yIdx >= row.Count) continue;
            var xVal = row[xIdx]?.ToString() ?? "(null)";
            var yVal = TryParseDouble(row[yIdx]);

            if (!groups.TryGetValue(xVal, out var list))
            {
                list = [];
                groups[xVal] = list;
            }

            if (yVal.HasValue)
                list.Add(yVal.Value);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{EscapeCsv(chart.XColumn)},{EscapeCsv(chart.YColumn)}");

        foreach (var (label, values) in groups)
        {
            var agg = ComputeAggregation(values, chart.Aggregation);
            sb.AppendLine($"{EscapeCsv(label)},{agg.ToString(CultureInfo.InvariantCulture)}");
        }

        return sb.ToString();
    }

    private static string AggregateGrouped(
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int xIdx, int yIdx, int groupIdx, ChartSuggestion chart)
    {
        // Key: (x, group) → values
        var groups = new Dictionary<(string x, string g), List<double>>();

        foreach (var row in rows)
        {
            if (xIdx >= row.Count || yIdx >= row.Count || groupIdx >= row.Count) continue;
            var xVal = row[xIdx]?.ToString() ?? "(null)";
            var gVal = row[groupIdx]?.ToString() ?? "(null)";
            var yVal = TryParseDouble(row[yIdx]);

            var key = (xVal, gVal);
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }

            if (yVal.HasValue)
                list.Add(yVal.Value);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{EscapeCsv(chart.XColumn)},{EscapeCsv(chart.GroupBy!)},{EscapeCsv(chart.YColumn)}");

        foreach (var ((x, g), values) in groups.OrderBy(kv => kv.Key.x).ThenBy(kv => kv.Key.g))
        {
            var agg = ComputeAggregation(values, chart.Aggregation);
            sb.AppendLine($"{EscapeCsv(x)},{EscapeCsv(g)},{agg.ToString(CultureInfo.InvariantCulture)}");
        }

        return sb.ToString();
    }

    private static double ComputeAggregation(List<double> values, string? aggregation)
    {
        if (values.Count == 0) return 0;
        return (aggregation?.ToLowerInvariant()) switch
        {
            "sum" => values.Sum(),
            "avg" or "average" => values.Average(),
            "min" => values.Min(),
            "max" => values.Max(),
            "count" => values.Count,
            _ => values.Sum(), // default to sum for unspecified aggregation
        };
    }

    private static int FindColumnIndex(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static double? TryParseDouble(object? value)
    {
        if (value is null) return null;
        if (value is double d) return d;
        if (value is int i) return i;
        if (value is long l) return l;
        if (value is float f) return f;
        if (value is decimal m) return (double)m;
        if (double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private static string SanitizeName(string title) =>
        title.Length > 60 ? title[..60] : title;
}
