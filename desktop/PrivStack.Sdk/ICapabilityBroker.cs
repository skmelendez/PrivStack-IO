namespace PrivStack.Sdk;

/// <summary>
/// Thread-safe capability registry for cross-plugin discovery.
/// Plugins register capabilities (e.g., ILinkableItemProvider) and other
/// plugins can discover and query them at runtime.
/// </summary>
public interface ICapabilityBroker
{
    /// <summary>
    /// Registers a capability provider instance.
    /// </summary>
    void Register<TCapability>(TCapability provider) where TCapability : class;

    /// <summary>
    /// Removes a capability provider instance.
    /// </summary>
    void Unregister<TCapability>(TCapability provider) where TCapability : class;

    /// <summary>
    /// Removes a provider from all capability type registrations at once.
    /// Used during plugin disable to cleanly remove all registrations.
    /// </summary>
    void UnregisterAll(object provider);

    /// <summary>
    /// Gets all registered providers of the specified capability type.
    /// </summary>
    IReadOnlyList<TCapability> GetProviders<TCapability>() where TCapability : class;

    /// <summary>
    /// Gets a specific provider by a string identifier extracted via <paramref name="selector"/>.
    /// </summary>
    TCapability? GetProvider<TCapability>(string identifier, Func<TCapability, string> selector)
        where TCapability : class;

    /// <summary>
    /// Queries all providers of a capability for results and aggregates them.
    /// </summary>
    Task<IReadOnlyList<TResult>> QueryAllAsync<TCapability, TResult>(
        Func<TCapability, Task<IReadOnlyList<TResult>>> query,
        CancellationToken ct = default) where TCapability : class;
}
