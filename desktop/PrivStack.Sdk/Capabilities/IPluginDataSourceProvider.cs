namespace PrivStack.Sdk.Capabilities;

/// <summary>
/// Cross-plugin capability: allows any plugin to expose named groups of
/// data source entries (datasets, views, projects, etc.) that other plugins
/// can discover, browse, and query.
/// </summary>
public interface IPluginDataSourceProvider
{
    /// <summary>Source plugin identifier (e.g. "privstack.data").</summary>
    string PluginId { get; }

    /// <summary>
    /// The <see cref="IDeepLinkTarget.LinkType"/> used to navigate into this provider's plugin.
    /// </summary>
    string NavigationLinkType { get; }

    /// <summary>Returns the hierarchical groups of data source entries this plugin exposes.</summary>
    Task<IReadOnlyList<DataSourceGroup>> GetDataSourceGroupsAsync(CancellationToken ct = default);

    /// <summary>
    /// Executes a paginated query against a single entry identified by <paramref name="queryKey"/>.
    /// The key format is provider-defined (e.g. dataset ID, project ID, "all").
    /// </summary>
    Task<DatasetQueryResult> QueryItemAsync(
        string queryKey,
        int page = 0,
        int pageSize = 100,
        string? filterText = null,
        CancellationToken ct = default);
}
