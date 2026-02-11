using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivStack.Sdk.Capabilities;

/// <summary>
/// Metadata for a stored dataset.
/// </summary>
public sealed record DatasetInfo
{
    [JsonPropertyName("id")] public required DatasetId Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("source_file_name")] public string? SourceFileName { get; init; }
    [JsonPropertyName("row_count")] public long RowCount { get; init; }
    [JsonPropertyName("columns")] public IReadOnlyList<DatasetColumnInfo> Columns { get; init; } = [];
    [JsonPropertyName("created_at")] public long CreatedAt { get; init; }
    [JsonPropertyName("modified_at")] public long ModifiedAt { get; init; }
}

/// <summary>
/// Strongly-typed dataset identifier.
/// Rust serializes DatasetId(Uuid) as a plain string; this converter handles that.
/// </summary>
[JsonConverter(typeof(DatasetIdConverter))]
public sealed record DatasetId
{
    public required string Value { get; init; }

    public override string ToString() => Value;

    public static implicit operator string(DatasetId id) => id.Value;
}

internal sealed class DatasetIdConverter : JsonConverter<DatasetId>
{
    public override DatasetId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new DatasetId { Value = reader.GetString()! };

        // Fallback: read as object with "0" key (legacy format)
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            string? value = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.GetString() == "0")
                {
                    reader.Read();
                    value = reader.GetString();
                }
            }
            return new DatasetId { Value = value ?? "" };
        }

        throw new JsonException($"Cannot convert {reader.TokenType} to DatasetId");
    }

    public override void Write(Utf8JsonWriter writer, DatasetId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

/// <summary>
/// A single column in a dataset.
/// </summary>
public sealed record DatasetColumnInfo
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("column_type")] public required string ColumnType { get; init; }
    [JsonPropertyName("ordinal")] public int Ordinal { get; init; }
}

/// <summary>
/// Query parameters for paginated dataset browsing.
/// </summary>
public sealed record DatasetQuery
{
    [JsonPropertyName("dataset_id")] public required string DatasetId { get; init; }
    [JsonPropertyName("page")] public int Page { get; init; }
    [JsonPropertyName("page_size")] public int PageSize { get; init; } = 100;
    [JsonPropertyName("filter_text")] public string? FilterText { get; init; }
    [JsonPropertyName("sort_column")] public string? SortColumn { get; init; }
    [JsonPropertyName("sort_desc")] public bool SortDesc { get; init; }
}

/// <summary>
/// Result of a paginated dataset query.
/// </summary>
public sealed record DatasetQueryResult
{
    [JsonPropertyName("columns")] public IReadOnlyList<string> Columns { get; init; } = [];
    [JsonPropertyName("column_types")] public IReadOnlyList<string> ColumnTypes { get; init; } = [];
    [JsonPropertyName("rows")] public IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; } = [];
    [JsonPropertyName("total_count")] public long TotalCount { get; init; }
    [JsonPropertyName("page")] public long Page { get; init; }
    [JsonPropertyName("page_size")] public long PageSize { get; init; }
}

/// <summary>
/// Cross-dataset relation metadata.
/// </summary>
public sealed record DatasetRelation
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("source_dataset_id")] public required string SourceDatasetId { get; init; }
    [JsonPropertyName("source_column")] public required string SourceColumn { get; init; }
    [JsonPropertyName("target_dataset_id")] public required string TargetDatasetId { get; init; }
    [JsonPropertyName("target_column")] public required string TargetColumn { get; init; }
    [JsonPropertyName("relation_type")] public string RelationType { get; init; } = "many_to_one";
}

/// <summary>
/// A saved view configuration for a dataset.
/// </summary>
public sealed record DatasetView
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("dataset_id")] public required string DatasetId { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("config")] public required ViewConfig Config { get; init; }
    [JsonPropertyName("is_default")] public bool IsDefault { get; init; }
    [JsonPropertyName("sort_order")] public int SortOrder { get; init; }
}

/// <summary>
/// View configuration: column visibility, filters, sorts, grouping.
/// </summary>
public sealed record ViewConfig
{
    [JsonPropertyName("visible_columns")] public IReadOnlyList<string>? VisibleColumns { get; init; }
    [JsonPropertyName("filters")] public IReadOnlyList<ViewFilter> Filters { get; init; } = [];
    [JsonPropertyName("sorts")] public IReadOnlyList<ViewSort> Sorts { get; init; } = [];
    [JsonPropertyName("group_by")] public string? GroupBy { get; init; }
}

/// <summary>
/// A single filter condition within a view.
/// </summary>
public sealed record ViewFilter
{
    [JsonPropertyName("column")] public required string Column { get; init; }
    [JsonPropertyName("operator")] public required string Operator { get; init; }
    [JsonPropertyName("value")] public required string Value { get; init; }
}

/// <summary>
/// A single sort directive within a view.
/// </summary>
public sealed record ViewSort
{
    [JsonPropertyName("column")] public required string Column { get; init; }
    [JsonPropertyName("direction")] public required string Direction { get; init; }
}

/// <summary>
/// Aggregate query parameters for chart/visualization blocks.
/// </summary>
public sealed record AggregateQuery
{
    [JsonPropertyName("dataset_id")] public required string DatasetId { get; init; }
    [JsonPropertyName("x_column")] public required string XColumn { get; init; }
    [JsonPropertyName("y_column")] public required string YColumn { get; init; }
    [JsonPropertyName("aggregation")] public string? Aggregation { get; init; }
    [JsonPropertyName("group_by")] public string? GroupBy { get; init; }
    [JsonPropertyName("filter_text")] public string? FilterText { get; init; }
}

/// <summary>
/// Result of an aggregate query.
/// </summary>
public sealed record AggregateQueryResult
{
    [JsonPropertyName("labels")] public IReadOnlyList<string> Labels { get; init; } = [];
    [JsonPropertyName("values")] public IReadOnlyList<double> Values { get; init; } = [];
}

// ── Mutations & SQL v2 ──────────────────────────────────────────────────

/// <summary>Column definition for creating empty datasets.</summary>
public sealed record DatasetColumnDef
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("column_type")] public required string ColumnType { get; init; }
}

/// <summary>Result of a SQL mutation (INSERT/UPDATE/DELETE/CREATE/ALTER).</summary>
public sealed record MutationResult
{
    [JsonPropertyName("affected_rows")] public long AffectedRows { get; init; }
    [JsonPropertyName("statement_type")] public string StatementType { get; init; } = "";
    [JsonPropertyName("committed")] public bool Committed { get; init; }
    [JsonPropertyName("preview")] public DatasetQueryResult? Preview { get; init; }
}

/// <summary>
/// Unified response from ExecuteSqlV2: either a query result or a mutation result.
/// Rust sends a tagged enum with "type":"query" or "type":"mutation".
/// </summary>
[JsonConverter(typeof(SqlExecutionResponseConverter))]
public sealed record SqlExecutionResponse
{
    public DatasetQueryResult? Query { get; init; }
    public MutationResult? Mutation { get; init; }
    public string? Error { get; init; }
}

internal sealed class SqlExecutionResponseConverter : JsonConverter<SqlExecutionResponse>
{
    public override SqlExecutionResponse Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errProp))
            return new SqlExecutionResponse { Error = errProp.GetString() };

        if (root.TryGetProperty("type", out var typeProp))
        {
            var type = typeProp.GetString();
            if (type == "query")
            {
                var query = root.Deserialize<DatasetQueryResult>(options);
                return new SqlExecutionResponse { Query = query };
            }
            if (type == "mutation")
            {
                var mutation = root.Deserialize<MutationResult>(options);
                return new SqlExecutionResponse { Mutation = mutation };
            }
        }

        // Fallback: try as query result
        var fallbackQuery = root.Deserialize<DatasetQueryResult>(options);
        return new SqlExecutionResponse { Query = fallbackQuery };
    }

    public override void Write(Utf8JsonWriter writer, SqlExecutionResponse value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, options);
    }
}

/// <summary>Saved query info returned from the backend.</summary>
public sealed record SavedQueryInfo
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("sql")] public required string Sql { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("is_view")] public bool IsView { get; init; }
    [JsonPropertyName("created_at")] public long CreatedAt { get; init; }
    [JsonPropertyName("modified_at")] public long ModifiedAt { get; init; }
}
