namespace PrivStack.Sdk.Capabilities;

/// <summary>
/// A named group of data source entries (e.g. "Datasets", "Projects").
/// </summary>
public sealed record DataSourceGroup
{
    /// <summary>Display name for the group header.</summary>
    public required string Name { get; init; }

    /// <summary>Icon key for the group header (matches plugin icon convention).</summary>
    public string? Icon { get; init; }

    /// <summary>The entries within this group.</summary>
    public required IReadOnlyList<DataSourceEntry> Entries { get; init; }
}

/// <summary>
/// A single browsable/draggable data source entry within a group.
/// </summary>
public sealed record DataSourceEntry
{
    /// <summary>Unique ID for this entry (dataset ID, project ID, etc.).</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Plugin that owns this entry.</summary>
    public required string PluginId { get; init; }

    /// <summary>Provider-defined key passed to <see cref="IPluginDataSourceProvider.QueryItemAsync"/>.</summary>
    public required string QueryKey { get; init; }

    /// <summary>Approximate row count (0 if unknown).</summary>
    public long RowCount { get; init; }

    /// <summary>Column count (0 if unknown).</summary>
    public int ColumnCount { get; init; }

    /// <summary>Short detail text (e.g. "12 tasks", "View").</summary>
    public string? Detail { get; init; }

    /// <summary>Whether this entry supports chart visualization.</summary>
    public bool SupportsChart { get; init; }
}
