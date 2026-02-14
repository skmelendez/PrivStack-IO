using CommunityToolkit.Mvvm.ComponentModel;

namespace PrivStack.Sdk;

/// <summary>
/// Convenience base class for first-party C#/Avalonia plugins.
/// Manages lifecycle state transitions, lazy ViewModel creation, and host storage
/// so concrete plugins only need to override the few members they care about.
/// </summary>
/// <typeparam name="TViewModel">The plugin's main ViewModel type.</typeparam>
public abstract class PluginBase<TViewModel> : ObservableObject, IAppPlugin
    where TViewModel : ViewModelBase
{
    private PluginState _state = PluginState.Discovered;
    private TViewModel? _viewModel;
    private bool _disposed;

    /// <summary>
    /// The host services provided during initialization. Guaranteed non-null after
    /// <see cref="InitializeAsync"/> completes successfully.
    /// </summary>
    protected IPluginHost? Host { get; private set; }

    /// <summary>
    /// The cached ViewModel instance, created lazily via <see cref="CreateViewModelCore"/>.
    /// Null until the first call to <see cref="CreateViewModel"/>.
    /// </summary>
    protected TViewModel? ViewModel => _viewModel;

    // ── Required overrides ──────────────────────────────────────────────

    public abstract PluginMetadata Metadata { get; }
    public abstract NavigationItem? NavigationItem { get; }

    /// <summary>
    /// Factory method for the plugin's ViewModel. Called once, result is cached.
    /// </summary>
    protected abstract TViewModel CreateViewModelCore();

    // ── Optional overrides ──────────────────────────────────────────────

    public virtual ICommandProvider? CommandProvider => null;
    public virtual IReadOnlyList<EntitySchema> EntitySchemas => [];

    /// <summary>
    /// Called after <see cref="Host"/> is set. Perform service creation here.
    /// </summary>
    protected virtual Task<bool> OnInitializeAsync(CancellationToken cancellationToken)
        => Task.FromResult(true);

    /// <summary>Called when the plugin transitions to <see cref="PluginState.Active"/>.</summary>
    protected virtual void OnActivate() { }

    /// <summary>Called when the plugin transitions to <see cref="PluginState.Deactivated"/>.</summary>
    protected virtual void OnDeactivate() { }

    /// <summary>Called once during <see cref="Dispose"/>. Release unmanaged resources here.</summary>
    protected virtual void OnDispose() { }

    // ── IAppPlugin implementation ───────────────────────────────────────

    public PluginState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public async Task<bool> InitializeAsync(IPluginHost host, CancellationToken cancellationToken = default)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        State = PluginState.Initializing;
        try
        {
            var result = await OnInitializeAsync(cancellationToken);
            State = result ? PluginState.Initialized : PluginState.Failed;
            return result;
        }
        catch
        {
            State = PluginState.Failed;
            throw;
        }
    }

    public void Activate()
    {
        State = PluginState.Active;
        OnActivate();
    }

    public void Deactivate()
    {
        State = PluginState.Deactivated;
        OnDeactivate();
        Host?.Capabilities.UnregisterAll(this);
    }

    public ViewModelBase CreateViewModel()
    {
        _viewModel ??= CreateViewModelCore();
        return _viewModel;
    }

    public void ResetViewModel()
    {
        if (_viewModel is IDisposable disposable)
            disposable.Dispose();
        _viewModel = null;
    }

    public virtual Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public virtual void OnNavigatedFrom() { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Host?.Capabilities.UnregisterAll(this);
        OnDispose();
        ResetViewModel();
        State = PluginState.Disposed;
        GC.SuppressFinalize(this);
    }
}
