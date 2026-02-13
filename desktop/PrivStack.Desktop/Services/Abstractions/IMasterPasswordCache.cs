namespace PrivStack.Desktop.Services.Abstractions;

/// <summary>
/// Caches the master password in memory for seamless workspace switching.
/// The password is held in pinned memory to prevent GC from scattering copies.
/// </summary>
public interface IMasterPasswordCache
{
    bool HasCachedPassword { get; }
    void Set(string password);
    string? Get();
    void Clear();
}
