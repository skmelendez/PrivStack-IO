using System.Collections.Concurrent;
using PrivStack.Sdk;

namespace PrivStack.Desktop.Sdk;

/// <summary>
/// Thread-safe capability registry. Plugins register capabilities, others discover them.
/// </summary>
internal sealed class CapabilityBroker : ICapabilityBroker
{
    private readonly ConcurrentDictionary<Type, List<object>> _providers = new();
    private readonly object _lock = new();

    public void Register<TCapability>(TCapability provider) where TCapability : class
    {
        ArgumentNullException.ThrowIfNull(provider);
        var list = _providers.GetOrAdd(typeof(TCapability), _ => []);
        lock (_lock)
        {
            if (!list.Contains(provider))
            {
                list.Add(provider);
            }
        }
    }

    public void Unregister<TCapability>(TCapability provider) where TCapability : class
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (_providers.TryGetValue(typeof(TCapability), out var list))
        {
            lock (_lock) { list.Remove(provider); }
        }
    }

    public void UnregisterAll(object provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_lock)
        {
            foreach (var list in _providers.Values)
            {
                list.Remove(provider);
            }
        }
    }

    public IReadOnlyList<TCapability> GetProviders<TCapability>() where TCapability : class
    {
        if (_providers.TryGetValue(typeof(TCapability), out var list))
        {
            lock (_lock)
            {
                return list.Cast<TCapability>().ToList().AsReadOnly();
            }
        }
        return Array.Empty<TCapability>();
    }

    public TCapability? GetProvider<TCapability>(string identifier, Func<TCapability, string> selector)
        where TCapability : class
    {
        ArgumentNullException.ThrowIfNull(selector);
        return GetProviders<TCapability>()
            .FirstOrDefault(p => string.Equals(selector(p), identifier, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<TResult>> QueryAllAsync<TCapability, TResult>(
        Func<TCapability, Task<IReadOnlyList<TResult>>> query,
        CancellationToken ct = default) where TCapability : class
    {
        var providers = GetProviders<TCapability>();
        var results = new List<TResult>();

        foreach (var provider in providers)
        {
            ct.ThrowIfCancellationRequested();
            var items = await query(provider);
            results.AddRange(items);
        }

        return results.AsReadOnly();
    }
}
