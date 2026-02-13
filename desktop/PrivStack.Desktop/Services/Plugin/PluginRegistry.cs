// ============================================================================
// File: PluginRegistry.cs
// Description: Central registry for discovering, initializing, and managing
//              application plugins. Stores Sdk.IAppPlugin instances directly.
// ============================================================================

using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Models;
using PrivStack.Desktop.Sdk;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.ViewModels;
using PrivStack.Sdk;
using PrivStack.Sdk.Capabilities;
using Serilog;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using PrivStack.Desktop.Views.Dialogs;
using PrivStack.Desktop.Plugins.Dashboard;
using PrivStack.Desktop.Plugins.Graph;
using NativeLib = PrivStack.Desktop.Native.NativeLibrary;
using IAppPlugin = PrivStack.Sdk.IAppPlugin;
using NavigationItem = PrivStack.Sdk.NavigationItem;
using PluginState = PrivStack.Sdk.PluginState;

namespace PrivStack.Desktop.Services.Plugin;

/// <summary>
/// Central registry for discovering, initializing, and managing application plugins.
/// All plugins are stored as <see cref="PrivStack.Sdk.IAppPlugin"/> — no adapters.
/// </summary>
public sealed partial class PluginRegistry : ObservableObject, IPluginRegistry, IDisposable
{
    private static readonly ILogger _log = Log.ForContext<PluginRegistry>();

    private readonly List<IAppPlugin> _plugins = [];
    private readonly Dictionary<string, IAppPlugin> _pluginById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IAppPlugin> _pluginByNavId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<NavigationItem> _navigationItems = [];

    private PluginHostFactory? _hostFactory;
    private MainWindowViewModel? _mainViewModel;
    private bool _disposed;

    /// <summary>
    /// Caches plugin types from the first discovery so that Reinitialize() can
    /// re-instantiate plugins without creating new AssemblyLoadContexts (which
    /// would produce duplicate type identities and cause InvalidCastException).
    /// </summary>
    private List<Type>? _cachedPluginTypes;

    private PluginHostFactory HostFactory => _hostFactory ??= new PluginHostFactory();

    public PluginRegistry()
    {
        _log.Information("PluginRegistry created");
    }

    // ========================================================================
    // IPluginRegistry Implementation
    // ========================================================================

    public IReadOnlyList<IAppPlugin> Plugins => _plugins.AsReadOnly();

    public IReadOnlyList<IAppPlugin> ActivePlugins =>
        _plugins.Where(p => p.State == PluginState.Active).ToList().AsReadOnly();

    public IReadOnlyList<NavigationItem> NavigationItems => _navigationItems;

    public ObservableCollection<NavigationItem> NavigationItemsObservable => _navigationItems;

    public event EventHandler<PluginStateChangedEventArgs>? PluginStateChanged;

    public event EventHandler? NavigationItemsChanged;

    public IAppPlugin? GetPlugin(string pluginId)
    {
        ThrowIfDisposed();
        return _pluginById.GetValueOrDefault(pluginId);
    }

    public IAppPlugin? GetPluginForNavItem(string navItemId)
    {
        ThrowIfDisposed();
        return _pluginByNavId.GetValueOrDefault(navItemId);
    }

    /// <summary>
    /// Gets capability providers from both plugin interfaces and the CapabilityBroker.
    /// </summary>
    public IReadOnlyList<TCapability> GetCapabilityProviders<TCapability>() where TCapability : class
    {
        ThrowIfDisposed();
        var fromPlugins = ActivePlugins.OfType<TCapability>();
        // Broker providers that are IAppPlugin instances are already handled by ActivePlugins
        // (which filters by state). Excluding them here prevents disabled plugins from being
        // returned via stale broker registrations.
        var fromBroker = HostFactory.CapabilityBroker.GetProviders<TCapability>()
            .Where(p => p is not IAppPlugin);
        return fromPlugins.Concat(fromBroker).Distinct().ToList().AsReadOnly();
    }

    public TCapability? GetCapabilityProvider<TCapability>(
        string identifier,
        Func<TCapability, string> identifierSelector) where TCapability : class
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(identifierSelector);

        return GetCapabilityProviders<TCapability>()
            .FirstOrDefault(p => string.Equals(
                identifierSelector(p),
                identifier,
                StringComparison.OrdinalIgnoreCase));
    }

    // ========================================================================
    // Public Methods
    // ========================================================================

    /// <summary>
    /// Discovers and initializes all plugins. Scans the executing assembly and
    /// external plugin directories for types implementing Sdk.IAppPlugin.
    /// </summary>
    public void DiscoverAndInitialize()
    {
        ThrowIfDisposed();
        _log.Information("Starting SYNC plugin discovery and initialization");

        // Register system entity types before any plugins
        RegisterSystemEntitySchemas();

        // Reuse cached types on reinitialize to avoid duplicate AssemblyLoadContexts
        var pluginTypes = _cachedPluginTypes ?? DiscoverPluginTypes();
        _cachedPluginTypes ??= pluginTypes;
        _log.Information("Discovered {Count} plugin types", pluginTypes.Count);

        // Register built-in Dashboard plugin (always first, hard-locked)
        RegisterBuiltInDashboard();
        RegisterBuiltInGraph();

        foreach (var type in pluginTypes)
        {
            try
            {
                if (Activator.CreateInstance(type) is IAppPlugin plugin)
                {
                    // Skip if already loaded (e.g. same plugin in bundled + user directories)
                    if (_pluginById.ContainsKey(plugin.Metadata.Id))
                    {
                        _log.Debug("Plugin {PluginId} already registered, skipping duplicate", plugin.Metadata.Id);
                        continue;
                    }

                    _plugins.Add(plugin);
                    _pluginById[plugin.Metadata.Id] = plugin;
                    _log.Debug("Instantiated plugin: {PluginId} ({PluginName})",
                        plugin.Metadata.Id, plugin.Metadata.Name);
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to instantiate plugin type: {Type}", type.FullName);
            }
        }

        _plugins.Sort((a, b) => a.Metadata.NavigationOrder.CompareTo(b.Metadata.NavigationOrder));

        foreach (var plugin in _plugins)
        {
            try
            {
                var host = HostFactory.CreateHost(plugin.Metadata.Id);
                RegisterEntitySchemas(plugin);
                var success = plugin.InitializeAsync(host, CancellationToken.None).GetAwaiter().GetResult();
                if (success)
                {
                    _log.Information("Plugin initialized: {PluginId}", plugin.Metadata.Id);
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Plugin initialization failed: {PluginId}", plugin.Metadata.Id);
            }
        }

        var syncWsConfig = App.Services.GetRequiredService<IAppSettingsService>().GetWorkspacePluginConfig();
        foreach (var plugin in _plugins.Where(p => p.State == PluginState.Initialized))
        {
            // Core shell plugins (CanDisable=false / IsHardLocked) always activate
            if (!plugin.Metadata.CanDisable || plugin.Metadata.IsHardLocked)
            {
                ActivatePlugin(plugin);
                continue;
            }
            if (plugin.Metadata.IsExperimental &&
                !App.Services.GetRequiredService<IAppSettingsService>().Settings.ExperimentalPluginsEnabled)
                continue;
            if (IsPluginDisabledByConfig(syncWsConfig, plugin.Metadata.Id))
                continue;
            ActivatePlugin(plugin);
        }

        RebuildNavigationItems();

        // Load link provider cache now that all plugins are initialized
        App.Services.GetService<LinkProviderCacheService>()?.Load();

        _log.Information("Plugin initialization complete. Active: {Active}/{Total}",
            ActivePlugins.Count, _plugins.Count);
    }

    public async Task DiscoverAndInitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _log.Information("Starting plugin discovery and initialization");

        // Reuse cached types on reinitialize to avoid duplicate AssemblyLoadContexts
        var pluginTypes = _cachedPluginTypes ?? DiscoverPluginTypes();
        _cachedPluginTypes ??= pluginTypes;
        _log.Information("Discovered {Count} plugin types", pluginTypes.Count);

        // Register built-in Dashboard plugin (always first, hard-locked)
        RegisterBuiltInDashboard();
        RegisterBuiltInGraph();

        // Instantiate plugins
        foreach (var type in pluginTypes)
        {
            try
            {
                if (Activator.CreateInstance(type) is IAppPlugin plugin)
                {
                    // Skip if already loaded (e.g. same plugin in bundled + user directories)
                    if (_pluginById.ContainsKey(plugin.Metadata.Id))
                    {
                        _log.Debug("Plugin {PluginId} already registered, skipping duplicate", plugin.Metadata.Id);
                        continue;
                    }

                    _plugins.Add(plugin);
                    _pluginById[plugin.Metadata.Id] = plugin;
                    _log.Debug("Instantiated plugin: {PluginId} ({PluginName})",
                        plugin.Metadata.Id, plugin.Metadata.Name);
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to instantiate plugin type: {Type}", type.FullName);
            }
        }

        // Sort by navigation order
        _plugins.Sort((a, b) => a.Metadata.NavigationOrder.CompareTo(b.Metadata.NavigationOrder));

        // Initialize plugins — each gets its own IPluginHost
        foreach (var plugin in _plugins)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _log.Warning("Plugin initialization cancelled");
                break;
            }

            await InitializePluginAsync(plugin, cancellationToken);
        }

        // Activate initialized plugins — core plugins always, optional plugins check workspace config
        var asyncWsConfig = App.Services.GetRequiredService<IAppSettingsService>().GetWorkspacePluginConfig();
        foreach (var plugin in _plugins.Where(p => p.State == PluginState.Initialized))
        {
            // Core shell plugins (CanDisable=false / IsHardLocked) always activate
            if (!plugin.Metadata.CanDisable || plugin.Metadata.IsHardLocked)
            {
                ActivatePlugin(plugin);
                continue;
            }

            if (plugin.Metadata.IsExperimental &&
                !App.Services.GetRequiredService<IAppSettingsService>().Settings.ExperimentalPluginsEnabled)
            {
                _log.Debug("Skipping experimental plugin (not enabled): {PluginId}", plugin.Metadata.Id);
                continue;
            }

            if (IsPluginDisabledByConfig(asyncWsConfig, plugin.Metadata.Id))
            {
                _log.Debug("Skipping user-disabled plugin: {PluginId}", plugin.Metadata.Id);
                continue;
            }

            ActivatePlugin(plugin);
        }

        RebuildNavigationItems();

        // Load link provider cache now that all plugins are initialized
        App.Services.GetService<LinkProviderCacheService>()?.Load();

        _log.Information("Plugin initialization complete. Active: {Active}/{Total}",
            ActivePlugins.Count, _plugins.Count);
    }

    /// <summary>
    /// Tears down all loaded plugins and rediscovers from scratch.
    /// Called when switching workspaces so the Rust plugin host gets fresh instances.
    /// </summary>
    public void Reinitialize()
    {
        _log.Information("Reinitializing PluginRegistry (workspace switch)");

        // Deactivate and dispose all current plugins
        foreach (var plugin in _plugins)
        {
            try
            {
                if (plugin.State == PluginState.Active)
                    plugin.Deactivate();
                plugin.Dispose();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error tearing down plugin during reinitialize: {PluginId}", plugin.Metadata.Id);
            }
        }

        // Clear all state
        _plugins.Clear();
        _pluginById.Clear();
        _pluginByNavId.Clear();
        _navigationItems.Clear();
        _hostFactory = null;

        // Rediscover with fresh workspace settings
        DiscoverAndInitialize();

        // Re-register command providers if main ViewModel is available
        if (_mainViewModel != null)
        {
            foreach (var plugin in ActivePlugins)
            {
                RegisterCommandProvider(plugin, _mainViewModel);
            }
        }

        _log.Information("PluginRegistry reinitialized. Active: {Active}/{Total}", ActivePlugins.Count, _plugins.Count);
    }

    /// <summary>
    /// Async version of Reinitialize — runs heavy plugin work on a background thread
    /// to avoid freezing the UI during workspace switches.
    /// Phase 1 (caller/UI thread): teardown + clear state.
    /// Phase 2 (background): DiscoverAndInitialize (assembly scanning, FFI, plugin init).
    /// Phase 3 (UI thread): re-register command providers, rebuild nav.
    /// </summary>
    public async Task ReinitializeAsync()
    {
        _log.Information("ReinitializeAsync: starting (workspace switch)");

        // Phase 1 — teardown on caller thread (UI)
        foreach (var plugin in _plugins)
        {
            try
            {
                if (plugin.State == PluginState.Active)
                    plugin.Deactivate();
                plugin.Dispose();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error tearing down plugin during reinitialize: {PluginId}", plugin.Metadata.Id);
            }
        }

        _plugins.Clear();
        _pluginById.Clear();
        _pluginByNavId.Clear();

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => _navigationItems.Clear());

        _hostFactory = null;

        // Phase 2 — heavy work on background thread
        await Task.Run(() => DiscoverAndInitialize());

        // Phase 3 — UI-bound work back on UI thread
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_mainViewModel != null)
            {
                foreach (var plugin in ActivePlugins)
                {
                    RegisterCommandProvider(plugin, _mainViewModel);
                }
            }
        });

        _log.Information("ReinitializeAsync complete. Active: {Active}/{Total}", ActivePlugins.Count, _plugins.Count);
    }

    public void SetMainViewModel(MainWindowViewModel mainViewModel)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(mainViewModel);

        _mainViewModel = mainViewModel;

        foreach (var plugin in ActivePlugins)
        {
            RegisterCommandProvider(plugin, mainViewModel);
        }

        _log.Debug("MainViewModel set, command providers registered");
    }

    /// <summary>
    /// Gets the MainWindowViewModel for cross-plugin navigation.
    /// </summary>
    public MainWindowViewModel? GetMainViewModel()
    {
        ThrowIfDisposed();
        return _mainViewModel;
    }

    /// <summary>
    /// Updates the IsSelected state for navigation items based on the selected tab.
    /// </summary>
    public void UpdateSelectedNavItem(string selectedNavItemId)
    {
        foreach (var navItem in _navigationItems)
        {
            navItem.IsSelected = string.Equals(navItem.Id, selectedNavItemId, StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool IsPluginEnabled(string pluginId)
    {
        var plugin = GetPlugin(pluginId);
        if (plugin == null) return false;

        // Core shell plugins are always enabled
        if (!plugin.Metadata.CanDisable || plugin.Metadata.IsHardLocked)
            return true;

        if (plugin.Metadata.IsExperimental &&
            !App.Services.GetRequiredService<IAppSettingsService>().Settings.ExperimentalPluginsEnabled)
            return false;

        var wsConfig = App.Services.GetRequiredService<IAppSettingsService>().GetWorkspacePluginConfig();
        return wsConfig.IsWhitelistMode
            ? wsConfig.EnabledPlugins!.Contains(pluginId)
            : !wsConfig.DisabledPlugins.Contains(pluginId);
    }

    public bool EnablePlugin(string pluginId)
    {
        ThrowIfDisposed();

        var plugin = GetPlugin(pluginId);
        if (plugin == null)
        {
            _log.Warning("Cannot enable unknown plugin: {PluginId}", pluginId);
            return false;
        }

        if (!plugin.Metadata.CanDisable)
        {
            _log.Debug("Plugin cannot be toggled: {PluginId}", pluginId);
            return false;
        }

        if (plugin.Metadata.IsHardLocked)
        {
            _log.Debug("Cannot enable hard-locked plugin: {PluginId}", pluginId);
            return false;
        }

        if (plugin.Metadata.IsExperimental &&
            !App.Services.GetRequiredService<IAppSettingsService>().Settings.ExperimentalPluginsEnabled)
        {
            _log.Debug("Cannot enable experimental plugin without experimental mode: {PluginId}", pluginId);
            return false;
        }

        var settingsService = App.Services.GetRequiredService<IAppSettingsService>();
        var wsConfig = settingsService.GetWorkspacePluginConfig();
        if (wsConfig.IsWhitelistMode)
            wsConfig.EnabledPlugins!.Add(pluginId);
        else
            wsConfig.DisabledPlugins.Remove(pluginId);
        settingsService.Save();

        if (plugin.State == PluginState.Deactivated || plugin.State == PluginState.Initialized)
        {
            ActivatePlugin(plugin);

            if (_mainViewModel != null)
            {
                RegisterCommandProvider(plugin, _mainViewModel);
            }
        }

        RebuildNavigationItems();
        _log.Information("Plugin enabled: {PluginId}", pluginId);
        return true;
    }

    public bool DisablePlugin(string pluginId)
    {
        ThrowIfDisposed();

        var plugin = GetPlugin(pluginId);
        if (plugin == null)
        {
            _log.Warning("Cannot disable unknown plugin: {PluginId}", pluginId);
            return false;
        }

        if (!plugin.Metadata.CanDisable)
        {
            _log.Debug("Plugin cannot be disabled: {PluginId}", pluginId);
            return false;
        }

        var settingsService = App.Services.GetRequiredService<IAppSettingsService>();
        var wsConfig = settingsService.GetWorkspacePluginConfig();
        if (wsConfig.IsWhitelistMode)
            wsConfig.EnabledPlugins!.Remove(pluginId);
        else
            wsConfig.DisabledPlugins.Add(pluginId);
        settingsService.Save();

        if (plugin.State == PluginState.Active)
        {
            try
            {
                if (_mainViewModel != null && plugin.CommandProvider != null)
                {
                    // Unwrap SDK command provider if needed
                    var cmdProvider = plugin.CommandProvider;
                    _mainViewModel.CommandPaletteVM.UnregisterProvider(
                        new SdkCommandProviderAdapter(cmdProvider));
                }

                if (plugin.NavigationItem != null)
                {
                    _pluginByNavId.Remove(plugin.NavigationItem.Id);
                }

                plugin.Deactivate();
                RaisePluginStateChanged(plugin, PluginState.Deactivated);

                // Free ViewModel memory and evict from MainWindowViewModel cache
                plugin.ResetViewModel();
                if (_mainViewModel != null && plugin.NavigationItem != null)
                {
                    _mainViewModel.EvictPluginCache(plugin.NavigationItem.Id);
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error deactivating plugin: {PluginId}", pluginId);
            }
        }

        RebuildNavigationItems();
        _log.Information("Plugin disabled: {PluginId}", pluginId);
        return true;
    }

    public bool TogglePlugin(string pluginId)
    {
        if (IsPluginEnabled(pluginId))
        {
            DisablePlugin(pluginId);
            return false;
        }
        else
        {
            EnablePlugin(pluginId);
            return true;
        }
    }

    public void SetExperimentalPluginsEnabled(bool enabled)
    {
        ThrowIfDisposed();

        App.Services.GetRequiredService<IAppSettingsService>().Settings.ExperimentalPluginsEnabled = enabled;
        App.Services.GetRequiredService<IAppSettingsService>().Save();

        RebuildNavigationItems();

        _log.Information("Experimental plugins {State}", enabled ? "enabled" : "disabled");
    }

    /// <summary>
    /// Hot-loads a plugin from an extracted directory. Called after install/update
    /// to add the plugin to the running app without requiring a restart.
    /// </summary>
    public async Task<bool> LoadPluginFromDirectoryAsync(string pluginDirectory, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        try
        {
            var pluginTypes = ScanAssemblyPlugins(pluginDirectory);
            if (pluginTypes.Count == 0)
            {
                _log.Warning("No IAppPlugin types found in {Dir}", pluginDirectory);
                return false;
            }

            foreach (var type in pluginTypes)
            {
                if (ct.IsCancellationRequested) break;

                if (Activator.CreateInstance(type) is not IAppPlugin plugin) continue;

                // Skip if already loaded (e.g. from bundled directory)
                if (_pluginById.ContainsKey(plugin.Metadata.Id))
                {
                    _log.Debug("Plugin {PluginId} already loaded, skipping", plugin.Metadata.Id);
                    continue;
                }

                _plugins.Add(plugin);
                _pluginById[plugin.Metadata.Id] = plugin;

                // Initialize
                var host = HostFactory.CreateHost(plugin.Metadata.Id);
                var success = await plugin.InitializeAsync(host, ct);
                if (!success)
                {
                    _log.Warning("Hot-loaded plugin failed to initialize: {PluginId}", plugin.Metadata.Id);
                    continue;
                }

                RegisterEntitySchemas(plugin);

                // Activate
                ActivatePlugin(plugin);

                // Register command provider
                if (_mainViewModel != null)
                {
                    RegisterCommandProvider(plugin, _mainViewModel);
                }

                _log.Information("Hot-loaded plugin: {PluginId} ({PluginName})",
                    plugin.Metadata.Id, plugin.Metadata.Name);
            }

            // Re-sort and rebuild navigation
            _plugins.Sort((a, b) => a.Metadata.NavigationOrder.CompareTo(b.Metadata.NavigationOrder));
            RebuildNavigationItems();

            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to hot-load plugin from {Dir}", pluginDirectory);
            return false;
        }
    }

    /// <summary>
    /// Unloads a plugin by ID — deactivates, disposes, and removes from all registries.
    /// </summary>
    public bool UnloadPlugin(string pluginId)
    {
        ThrowIfDisposed();

        if (!_pluginById.TryGetValue(pluginId, out var plugin))
        {
            _log.Warning("Cannot unload unknown plugin: {PluginId}", pluginId);
            return false;
        }

        try
        {
            // Unregister command provider
            if (_mainViewModel != null && plugin.CommandProvider != null)
            {
                _mainViewModel.CommandPaletteVM.UnregisterProvider(
                    new SdkCommandProviderAdapter(plugin.CommandProvider));
            }

            // Remove from nav map
            if (plugin.NavigationItem != null)
            {
                _pluginByNavId.Remove(plugin.NavigationItem.Id);
            }

            // Deactivate + dispose
            if (plugin.State == PluginState.Active)
                plugin.Deactivate();
            plugin.Dispose();

            // Remove from all collections
            _plugins.Remove(plugin);
            _pluginById.Remove(pluginId);

            RebuildNavigationItems();

            _log.Information("Unloaded plugin: {PluginId}", pluginId);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to unload plugin: {PluginId}", pluginId);
            return false;
        }
    }

    // ========================================================================
    // Private Methods
    // ========================================================================

    /// Pending wasm plugin collected during discovery, before batch FFI loading.
    private record PendingWasmPlugin(
        string WasmPath,
        WasmPluginMetadataDto? Metadata,
        List<WasmEntitySchemaDto>? Schemas,
        string PermissionsJson,
        string PluginDir);

    /// Result element from the batch FFI call.
    private sealed class BatchLoadResult
    {
        public string? PluginId { get; set; }
        public string? Error { get; set; }
    }

    private static readonly JsonSerializerOptions _batchJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Registers the built-in Dashboard plugin if not already present.
    /// Called at the start of discovery to ensure Dashboard is always available.
    /// </summary>
    private void RegisterBuiltInDashboard()
    {
        const string dashboardId = "privstack.dashboard";
        if (_pluginById.ContainsKey(dashboardId)) return;

        try
        {
            var dashboard = new DashboardPlugin();
            _plugins.Add(dashboard);
            _pluginById[dashboardId] = dashboard;
            _log.Information("Registered built-in Dashboard plugin");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to register built-in Dashboard plugin");
        }
    }

    /// <summary>
    /// Registers the built-in Graph plugin if not already present.
    /// Called after Dashboard to ensure Graph is always available as core functionality.
    /// </summary>
    private void RegisterBuiltInGraph()
    {
        const string graphId = "privstack.graph";
        if (_pluginById.ContainsKey(graphId)) return;

        try
        {
            var graph = new GraphPlugin();
            _plugins.Add(graph);
            _pluginById[graphId] = graph;
            _log.Information("Registered built-in Graph plugin");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to register built-in Graph plugin");
        }
    }

    /// <summary>
    /// Discovers plugins from external plugin directories.
    /// Scans for C# assemblies (.dll), Wasm components (.wasm), and packages (.ppk).
    /// C# plugin types are returned for instantiation by the caller; Wasm plugins
    /// are added directly to <see cref="_plugins"/> via proxy objects.
    /// </summary>
    private List<Type> DiscoverPluginTypes()
    {
        var pluginTypes = new List<Type>();
        var pending = new List<PendingWasmPlugin>();

        var pluginDirs = GetPluginDirectories();
        foreach (var pluginDir in pluginDirs)
        {
            if (!Directory.Exists(pluginDir)) continue;
            _log.Information("Scanning for plugins in: {PluginDir}", pluginDir);

            // Scan for standalone .ppk files dropped directly into the plugins directory
            foreach (var ppkFile in Directory.GetFiles(pluginDir, "*.ppk"))
            {
                try
                {
                    TryLoadPpkFile(ppkFile);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Failed to load .ppk file: {Path}", ppkFile);
                }
            }

            // Scan subdirectories for C# assemblies and Wasm plugins
            foreach (var dir in Directory.GetDirectories(pluginDir))
            {
                try
                {
                    // C# assembly plugins: look for DLLs matching PrivStack.Plugin.*.dll
                    var nativePluginTypes = ScanAssemblyPlugins(dir);
                    if (nativePluginTypes.Count > 0)
                    {
                        pluginTypes.AddRange(nativePluginTypes);
                        continue; // Directory handled as a C# plugin, skip Wasm check
                    }

                    // Wasm plugins
                    var collected = CollectWasmPlugin(dir);
                    if (collected != null)
                        pending.Add(collected);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Failed to collect plugin from: {Dir}", dir);
                }
            }
        }

        // Batch load all collected wasm plugins in parallel
        if (pending.Count > 0)
        {
            BatchLoadWasmPlugins(pending);
        }

        return pluginTypes;
    }

    /// <summary>
    /// Persisted plugin load contexts — prevents GC from collecting them while
    /// Avalonia still needs to resolve types from plugin assemblies.
    /// </summary>
    private static readonly List<PluginLoadContext> _pluginContexts = [];

    /// <summary>
    /// Scans a directory for .NET assemblies containing IAppPlugin implementations.
    /// Uses a custom AssemblyLoadContext per plugin that delegates shared dependencies
    /// (SDK, Avalonia, etc.) to the host's default context, ensuring type identity
    /// for IAppPlugin and other shared interfaces while isolating plugin-specific deps.
    /// </summary>
    private static List<Type> ScanAssemblyPlugins(string dir)
    {
        var results = new List<Type>();
        var pluginDlls = Directory.GetFiles(dir, "PrivStack.Plugin.*.dll");
        if (pluginDlls.Length == 0) return results;

        foreach (var dllPath in pluginDlls)
        {
            try
            {
                var fullPath = Path.GetFullPath(dllPath);
                var context = new PluginLoadContext(fullPath);
                _pluginContexts.Add(context);

                var assembly = context.LoadFromAssemblyPath(fullPath);

                var pluginInterface = typeof(IAppPlugin);
                foreach (var type in assembly.GetExportedTypes())
                {
                    if (pluginInterface.IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
                    {
                        results.Add(type);
                        _log.Information("Discovered C# plugin type: {Type} in {Path}",
                            type.FullName, dllPath);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to scan assembly for plugins: {Path}", dllPath);
            }
        }

        return results;
    }

    /// <summary>
    /// Custom AssemblyLoadContext for C# plugins. Delegates resolution of assemblies
    /// already loaded by the host (SDK, Avalonia, CommunityToolkit, etc.) to the default
    /// context so that shared types like IAppPlugin have a single identity. Plugin-specific
    /// dependencies are resolved from the plugin's directory.
    /// </summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginPath) : base(isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // If the host already has this assembly loaded, delegate to the default
            // context. This ensures shared types (IAppPlugin, ViewModelBase, Avalonia
            // controls, etc.) have a single identity across host and plugins.
            foreach (var loaded in Default.Assemblies)
            {
                if (string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                    return null;
            }

            // Plugin-specific dependency: resolve from plugin directory
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }

    /// <summary>
    /// Collects a wasm plugin's metadata without loading it. Returns null if the
    /// directory doesn't contain a wasm plugin or only has a .ppk.
    /// </summary>
    private PendingWasmPlugin? CollectWasmPlugin(string dir)
    {
        var ppkFile = Directory.GetFiles(dir, "*.ppk").FirstOrDefault();
        var wasmFile = Directory.GetFiles(dir, "*.wasm").FirstOrDefault();

        // .ppk-only dirs handled by TryLoadPpkFile above
        if (ppkFile != null && wasmFile == null)
        {
            TryLoadPpkFile(ppkFile);
            return null;
        }

        if (wasmFile == null) return null;

        // Read sidecar metadata if present
        WasmPluginMetadataDto? metadata = null;
        List<WasmEntitySchemaDto>? schemas = null;

        var metadataPath = Path.Combine(dir, "metadata.json");
        if (!File.Exists(metadataPath))
            metadataPath = Path.Combine(dir, "manifest.json");

        if (File.Exists(metadataPath))
        {
            try
            {
                var metadataJson = File.ReadAllText(metadataPath);
                metadata = JsonSerializer.Deserialize<WasmPluginMetadataDto>(metadataJson, _wasmJsonOptions);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to parse metadata for: {Dir}", dir);
            }

            var schemasPath = Path.Combine(dir, "schemas.json");
            if (File.Exists(schemasPath))
            {
                try
                {
                    var schemasJson = File.ReadAllText(schemasPath);
                    schemas = JsonSerializer.Deserialize<List<WasmEntitySchemaDto>>(schemasJson, _wasmJsonOptions);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Failed to parse schemas for: {Dir}", dir);
                }
            }
        }

        var pluginId = metadata?.Id ?? Path.GetFileNameWithoutExtension(wasmFile);
        var permissionsJson = BuildPermissionsJson(pluginId, metadata?.Capabilities, metadata?.Name);

        return new PendingWasmPlugin(wasmFile, metadata, schemas, permissionsJson, dir);
    }

    /// <summary>
    /// Sends all pending wasm plugins to the batch FFI for parallel compilation,
    /// then creates WasmPluginProxy for each success.
    /// </summary>
    private void BatchLoadWasmPlugins(List<PendingWasmPlugin> pending)
    {
        _log.Information("Batch loading {Count} Wasm plugins in parallel", pending.Count);

        // Build the JSON array for the FFI call
        var batchEntries = pending.Select(p => new
        {
            path = p.WasmPath,
            permissions = JsonSerializer.Deserialize<JsonElement>(p.PermissionsJson),
        }).ToList();

        var batchJson = JsonSerializer.Serialize(batchEntries, _batchJsonOptions);
        var resultPtr = NativeLib.PluginLoadWasmBatch(batchJson);

        List<BatchLoadResult>? results = null;
        if (resultPtr != nint.Zero)
        {
            var resultJson = Marshal.PtrToStringUTF8(resultPtr);
            NativeLib.FreeString(resultPtr);

            if (!string.IsNullOrEmpty(resultJson))
            {
                results = JsonSerializer.Deserialize<List<BatchLoadResult>>(resultJson, _batchJsonOptions);
            }
        }

        if (results == null || results.Count != pending.Count)
        {
            _log.Error("Batch load returned unexpected result count ({Actual} vs {Expected}), falling back to sequential",
                results?.Count ?? 0, pending.Count);
            // Fallback to sequential loading
            foreach (var p in pending)
                TryLoadWasmPlugin(p.PluginDir);
            return;
        }

        for (var i = 0; i < pending.Count; i++)
        {
            var p = pending[i];
            var r = results[i];

            if (r.Error != null)
            {
                _log.Error("Batch load failed for {Path}: {Error}", p.WasmPath, r.Error);
                continue;
            }

            var pluginId = r.PluginId;
            if (string.IsNullOrEmpty(pluginId))
            {
                _log.Error("Batch load returned empty plugin ID for {Path}", p.WasmPath);
                continue;
            }

            // Skip if already registered (e.g. from a .ppk)
            if (_pluginById.ContainsKey(pluginId))
            {
                _log.Debug("Plugin already registered, skipping batch result: {Id}", pluginId);
                continue;
            }

            var metadata = p.Metadata ?? new WasmPluginMetadataDto { Id = pluginId, Name = pluginId };
            var templateJson = LoadTemplateSidecar(p.PluginDir);
            var palettes = LoadCommandPalettesSidecar(p.PluginDir, pluginId);
            var proxy = new WasmPluginProxy(metadata, p.Schemas, templateJson) { CommandPalettes = palettes };
            _plugins.Add(proxy);
            _pluginById[proxy.Metadata.Id] = proxy;

            _log.Information("Loaded Wasm plugin (parallel): {Id}{Template}",
                pluginId, templateJson is not null ? " (with template)" : "");
        }

        _log.Information("Batch load complete: {Success}/{Total} plugins loaded",
            results.Count(r => r.Error == null), pending.Count);
    }

    /// <summary>
    /// Attempts to load a Wasm plugin from a directory containing .wasm or .ppk files.
    /// Returns true if a Wasm plugin was found and loaded (whether successfully or not).
    /// </summary>
    private bool TryLoadWasmPlugin(string dir)
    {
        // Look for .ppk (PrivStack Plugin Package) or .wasm files
        var ppkFile = Directory.GetFiles(dir, "*.ppk").FirstOrDefault();
        var wasmFile = Directory.GetFiles(dir, "*.wasm").FirstOrDefault();
        var pluginFile = ppkFile ?? wasmFile;

        if (pluginFile == null) return false;

        _log.Information("Found Wasm plugin package: {Path}", pluginFile);

        // For .ppk files, use the install path
        if (ppkFile != null && wasmFile == null)
        {
            return TryLoadPpkFile(ppkFile);
        }

        // Look for metadata.json alongside the plugin
        var metadataPath = Path.Combine(dir, "metadata.json");
        if (!File.Exists(metadataPath))
            metadataPath = Path.Combine(dir, "manifest.json");

        if (!File.Exists(metadataPath))
        {
            // Try loading directly via Wasm runtime (it calls get_metadata() export)
            if (wasmFile != null)
            {
                return TryLoadWasmDirect(wasmFile);
            }
            _log.Warning("Wasm plugin missing metadata.json: {Dir}", dir);
            return true;
        }

        try
        {
            var metadataJson = File.ReadAllText(metadataPath);
            var metadata = JsonSerializer.Deserialize<WasmPluginMetadataDto>(metadataJson, _wasmJsonOptions);
            if (metadata == null)
            {
                _log.Warning("Failed to parse Wasm plugin metadata: {Path}", metadataPath);
                return true;
            }

            // Load schemas if present
            List<WasmEntitySchemaDto>? schemas = null;
            var schemasPath = Path.Combine(dir, "schemas.json");
            if (File.Exists(schemasPath))
            {
                var schemasJson = File.ReadAllText(schemasPath);
                schemas = JsonSerializer.Deserialize<List<WasmEntitySchemaDto>>(schemasJson, _wasmJsonOptions);
            }

            // Try Wasm runtime path first (if .wasm file exists)
            if (wasmFile != null)
            {
                return TryLoadWasmWithMetadata(wasmFile, metadata, schemas);
            }

            // Fallback: metadata-only loading via FFI
            var schemasJsonStr = schemas != null
                ? JsonSerializer.Serialize(schemas, _wasmJsonOptions)
                : "[]";

            var permissionsJson = BuildPermissionsJson(metadata.Id, metadata.Capabilities, metadata.Name);

            var result = NativeLib.PluginLoad(
                JsonSerializer.Serialize(metadata, _wasmJsonOptions),
                schemasJsonStr,
                permissionsJson);

            if (result != Native.PrivStackError.Ok)
            {
                _log.Error("Failed to load Wasm plugin {Id} via FFI: {Error}", metadata.Id, result);
                return true;
            }

            var templateJson = LoadTemplateSidecar(dir);
            var palettes = LoadCommandPalettesSidecar(dir, metadata.Id);
            var proxy = new WasmPluginProxy(metadata, schemas, templateJson) { CommandPalettes = palettes };
            _plugins.Add(proxy);
            _pluginById[proxy.Metadata.Id] = proxy;

            _log.Information("Loaded Wasm plugin: {Id} v{Version}{Template}", metadata.Id, metadata.Version,
                templateJson is not null ? " (with template)" : "");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to load Wasm plugin from: {Dir}", dir);
            return true;
        }
    }

    /// <summary>
    /// Loads a .wasm component directly using the Wasmtime runtime.
    /// The runtime calls get_metadata() and get_entity_schemas() exports.
    /// </summary>
    private bool TryLoadWasmDirect(string wasmPath)
    {
        try
        {
            var permissionsJson = BuildPermissionsJson(Path.GetFileNameWithoutExtension(wasmPath));

            var result = NativeLib.PluginLoadWasm(wasmPath, permissionsJson, out var pluginIdPtr);
            if (result != Native.PrivStackError.Ok)
            {
                _log.Error("Failed to load Wasm component {Path}: {Error}", wasmPath, result);
                return true;
            }

            string? pluginId = null;
            if (pluginIdPtr != nint.Zero)
            {
                pluginId = Marshal.PtrToStringUTF8(pluginIdPtr);
                NativeLib.FreeString(pluginIdPtr);
            }

            if (string.IsNullOrEmpty(pluginId))
            {
                _log.Error("Wasm component returned empty plugin ID: {Path}", wasmPath);
                return true;
            }

            // Create a minimal metadata DTO from the plugin ID
            var metadata = new WasmPluginMetadataDto
            {
                Id = pluginId,
                Name = pluginId,
            };

            // Check for template.json and command_palettes.json sidecars
            var pluginDir = Path.GetDirectoryName(wasmPath);
            var templateJson = LoadTemplateSidecar(pluginDir);
            var palettes = LoadCommandPalettesSidecar(pluginDir, pluginId);

            var proxy = new WasmPluginProxy(metadata, schemas: null, templateJson) { CommandPalettes = palettes };
            _plugins.Add(proxy);
            _pluginById[proxy.Metadata.Id] = proxy;

            _log.Information("Loaded Wasm component directly: {Id}{Template}", pluginId,
                templateJson is not null ? " (with template)" : "");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to load Wasm component: {Path}", wasmPath);
            return true;
        }
    }

    /// <summary>
    /// Loads a .wasm component with pre-existing sidecar metadata.
    /// Uses metadata-only FFI path but the Wasm file is available for future runtime use.
    /// </summary>
    private bool TryLoadWasmWithMetadata(
        string wasmPath,
        WasmPluginMetadataDto metadata,
        List<WasmEntitySchemaDto>? schemas)
    {
        try
        {
            var permissionsJson = BuildPermissionsJson(metadata.Id, metadata.Capabilities, metadata.Name);

            // Load via real Wasm runtime so get_view_state() and handle_command() work
            var result = NativeLib.PluginLoadWasm(wasmPath, permissionsJson, out var pluginIdPtr);

            string? pluginId = null;
            if (pluginIdPtr != nint.Zero)
            {
                pluginId = Marshal.PtrToStringUTF8(pluginIdPtr);
                NativeLib.FreeString(pluginIdPtr);
            }

            if (result != Native.PrivStackError.Ok)
            {
                // Do NOT fall back to metadata-only — that creates a broken plugin
                // with no runtime (get_view_state/handle_command will always fail).
                _log.Error(
                    "Wasm runtime load FAILED for {Id} ({Path}): {Error}. " +
                    "Plugin will not be available. Check stderr for Wasmtime details.",
                    metadata.Id, wasmPath, result);
                return true;
            }

            var wasmDir = Path.GetDirectoryName(wasmPath);
            var templateJson = LoadTemplateSidecar(wasmDir);
            var palettes = LoadCommandPalettesSidecar(wasmDir, metadata.Id);
            var proxy = new WasmPluginProxy(metadata, schemas, templateJson) { CommandPalettes = palettes };
            _plugins.Add(proxy);
            _pluginById[proxy.Metadata.Id] = proxy;

            _log.Information("Loaded Wasm plugin with sidecar: {Id} v{Version} (wasm: {Path}){Template}",
                metadata.Id, metadata.Version, wasmPath,
                templateJson is not null ? " (with template)" : "");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to load Wasm plugin {Id}", metadata.Id);
            return true;
        }
    }

    /// <summary>
    /// Loads a plugin directly from a .ppk file using the FFI install path.
    /// Extracts metadata from the embedded manifest.toml without needing a sidecar metadata.json.
    /// </summary>
    /// <summary>
    /// Loads command_palettes.json sidecar from a plugin directory if present.
    /// </summary>
    private static List<PluginPaletteDefinition>? LoadCommandPalettesSidecar(string? dir, string pluginId)
    {
        if (dir is null) return null;
        var path = Path.Combine(dir, "command_palettes.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var palettes = JsonSerializer.Deserialize<List<PluginPaletteDefinition>>(json);
            if (palettes is null) return null;

            // Stamp the plugin ID on each palette
            for (var i = 0; i < palettes.Count; i++)
                palettes[i] = palettes[i] with { PluginId = pluginId };

            _log.Information("Loaded {Count} command palette(s) for plugin {Id}", palettes.Count, pluginId);
            return palettes;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to read command_palettes.json from {Dir}", dir);
            return null;
        }
    }

    /// <summary>
    /// Loads a template.json sidecar file from a plugin directory if present.
    /// Returns null if not found or on error.
    /// </summary>
    private static string? LoadTemplateSidecar(string? dir)
    {
        if (dir is null) return null;
        var templatePath = Path.Combine(dir, "template.json");
        if (!File.Exists(templatePath)) return null;
        try
        {
            return File.ReadAllText(templatePath);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to read template.json from {Dir}", dir);
            return null;
        }
    }

    private bool TryLoadPpkFile(string ppkPath)
    {
        _log.Information("Found .ppk file: {Path}", ppkPath);

        // Inspect the .ppk to get manifest metadata
        var metadataPtr = NativeLib.PpkInspect(ppkPath);
        if (metadataPtr == nint.Zero) return false;

        try
        {
            var metadataJson = Marshal.PtrToStringUTF8(metadataPtr);
            if (string.IsNullOrEmpty(metadataJson) || metadataJson == "{}")
            {
                _log.Warning("Failed to inspect .ppk file: {Path}", ppkPath);
                return false;
            }

            var metadata = JsonSerializer.Deserialize<WasmPluginMetadataDto>(metadataJson, _wasmJsonOptions);
            if (metadata == null)
            {
                _log.Warning("Failed to parse .ppk metadata: {Path}", ppkPath);
                return false;
            }

            // Skip if already loaded
            if (_pluginById.ContainsKey(metadata.Id))
            {
                _log.Debug("Plugin already loaded, skipping .ppk: {Id}", metadata.Id);
                return true;
            }

            // Install via FFI (validates manifest, loads wasm into sandbox)
            var result = NativeLib.PluginInstallPpk(ppkPath);
            if (result != Native.PrivStackError.Ok)
            {
                _log.Error("Failed to install .ppk {Id}: {Error}", metadata.Id, result);
                return false;
            }

            // Create proxy and register
            var proxy = new WasmPluginProxy(metadata, schemas: null);
            _plugins.Add(proxy);
            _pluginById[proxy.Metadata.Id] = proxy;

            _log.Information("Installed .ppk plugin: {Id} v{Version}", metadata.Id, metadata.Version);
            return true;
        }
        finally
        {
            NativeLib.FreeString(metadataPtr);
        }
    }

    private static readonly JsonSerializerOptions _wasmJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // ========================================================================
    // Permission helpers
    // ========================================================================

    /// <summary>
    /// Tier 1 permissions always granted to every plugin.
    /// </summary>
    internal static readonly string[] Tier1Permissions =
        ["sdk", "settings", "logger", "navigation", "state-notify"];

    /// <summary>
    /// Builds the permissions JSON for a plugin based on saved state in AppSettings.
    /// Prompts the user for any non-Tier-1 capabilities not already granted or denied.
    /// </summary>
    private static string BuildPermissionsJson(
        string pluginId,
        List<string>? declaredCapabilities = null,
        string? pluginName = null)
    {
        var wsConfig = App.Services.GetRequiredService<IAppSettingsService>().GetWorkspacePluginConfig();
        var granted = new List<string>(Tier1Permissions);
        var denied = new List<string>();

        if (wsConfig.PluginPermissions.TryGetValue(pluginId, out var saved))
        {
            granted.AddRange(saved.Granted);
            denied.AddRange(saved.Denied);

            // Check for NEW capabilities not yet in granted or denied (plugin update scenario)
            if (declaredCapabilities is { Count: > 0 })
            {
                var alreadyDecided = new HashSet<string>(granted.Concat(denied));
                var newCaps = declaredCapabilities
                    .Where(c => !alreadyDecided.Contains(c))
                    .ToList();

                if (newCaps.Count > 0)
                {
                    var promptResults = PromptForCapabilities(
                        pluginId, newCaps, pluginName ?? pluginId);
                    foreach (var (cap, wasGranted) in promptResults)
                    {
                        if (wasGranted)
                            granted.Add(cap);
                        else
                            denied.Add(cap);
                    }
                }
            }
        }
        else if (declaredCapabilities is { Count: > 0 })
        {
            // First load — prompt for each declared capability not in Tier 1
            var promptResults = PromptForCapabilities(
                pluginId, declaredCapabilities, pluginName ?? pluginId);
            foreach (var (cap, wasGranted) in promptResults)
            {
                if (wasGranted)
                    granted.Add(cap);
                else
                    denied.Add(cap);
            }
        }

        // Deduplicate
        granted = granted.Distinct().ToList();
        denied = denied.Distinct().ToList();

        var permObj = new { granted, denied, pending_jit = Array.Empty<string>() };
        return JsonSerializer.Serialize(permObj);
    }

    /// <summary>
    /// Permission display metadata for the prompt window.
    /// </summary>
    internal static readonly Dictionary<string, (string DisplayName, string Description)> PermissionDisplayInfo = new()
    {
        ["sdk"] = ("Data Storage", "Read and write plugin data"),
        ["settings"] = ("Settings", "Store plugin preferences"),
        ["logger"] = ("Logging", "Write diagnostic logs"),
        ["navigation"] = ("Navigation", "Navigate between plugins"),
        ["state-notify"] = ("View Updates", "Refresh the plugin view"),
        ["linking"] = ("Cross-Plugin Linking", "Search and link items from other plugins"),
        ["dialogs"] = ("Dialogs", "Show confirmation and file dialogs"),
        ["vault"] = ("Encrypted Vault", "Store and retrieve encrypted data"),
        ["cross-entity-read"] = ("Cross-Entity Read", "Read data from other entity types"),
        ["cross-plugin-command"] = ("Cross-Plugin Commands", "Send commands to other plugins"),
        ["filesystem"] = ("File System", "Access files on disk"),
        ["network"] = ("Network Access", "Make HTTP requests to fetch data"),
        ["agent"] = ("Agent Analytics", "Run queries and analytics across plugins"),
    };

    /// <summary>
    /// Prompts the user for each non-Tier-1 capability via a confirmation dialog.
    /// Granted capabilities are saved; denied ones are persisted so the user isn't asked again.
    /// </summary>
    private static List<(string capability, bool granted)> PromptForCapabilities(
        string pluginId, List<string> capabilities, string pluginName)
    {
        var results = new List<(string, bool)>();
        var tier1 = new HashSet<string>(Tier1Permissions);
        var settingsService = App.Services.GetRequiredService<IAppSettingsService>();
        var wsConfig = settingsService.GetWorkspacePluginConfig();

        if (!wsConfig.PluginPermissions.TryGetValue(pluginId, out var state))
        {
            state = new PluginPermissionState();
            wsConfig.PluginPermissions[pluginId] = state;
        }

        var dialogService = App.Services.GetService<IDialogService>();

        foreach (var cap in capabilities)
        {
            if (tier1.Contains(cap)) continue;

            var (displayName, description) = PermissionDisplayInfo.TryGetValue(cap, out var info)
                ? info
                : (cap, $"Grant the '{cap}' capability");

            bool userGranted = false;

            if (dialogService != null)
            {
                try
                {
                    // Marshal to UI thread since plugin loading may run off-thread
                    userGranted = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                        await dialogService.ShowConfirmationAsync(
                            $"Permission Request — {pluginName}",
                            $"'{pluginName}' requests: {displayName}\n{description}",
                            "Allow")).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Permission dialog failed for {PluginId}/{Cap}, denying", pluginId, cap);
                }
            }

            if (userGranted)
            {
                state.Granted.Add(cap);
                results.Add((cap, true));
            }
            else
            {
                state.Denied.Add(cap);
                results.Add((cap, false));
            }
        }

        settingsService.Save();
        return results;
    }

    /// <summary>
    /// Returns plugin search directories in priority order.
    /// </summary>
    private static List<string> GetPluginDirectories()
    {
        var dirs = new List<string>();

        // Bundled plugins (next to the app)
        var bundledDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (Directory.Exists(bundledDir))
        {
            dirs.Add(bundledDir);
        }
        else
        {
            // Dev-time fallback: repo root is 5 levels up from bin/Debug/net9.0/
            var devDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "plugins");
            if (Directory.Exists(devDir))
                dirs.Add(Path.GetFullPath(devDir));
        }

        // User-installed plugins (~/.privstack/plugins/)
        var userPluginDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".privstack", "plugins");
        if (Directory.Exists(userPluginDir))
            dirs.Add(userPluginDir);

        return dirs;
    }

    /// <summary>
    /// Initializes a single plugin asynchronously. Creates an IPluginHost for it.
    /// </summary>
    private async Task InitializePluginAsync(IAppPlugin plugin, CancellationToken cancellationToken)
    {
        _log.Debug("Initializing plugin: {PluginId}", plugin.Metadata.Id);

        try
        {
            var host = HostFactory.CreateHost(plugin.Metadata.Id);
            var success = await plugin.InitializeAsync(host, cancellationToken);

            if (success)
            {
                _log.Information("Plugin initialized: {PluginId} v{Version}",
                    plugin.Metadata.Id, plugin.Metadata.Version);

                // Register entity schemas with the core engine
                RegisterEntitySchemas(plugin);
            }
            else
            {
                _log.Warning("Plugin initialization returned false: {PluginId}", plugin.Metadata.Id);
                RaisePluginStateChanged(plugin, PluginState.Failed, "Initialization returned false");
            }
        }
        catch (OperationCanceledException)
        {
            _log.Debug("Plugin initialization cancelled: {PluginId}", plugin.Metadata.Id);
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Plugin initialization failed: {PluginId}", plugin.Metadata.Id);
            RaisePluginStateChanged(plugin, PluginState.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Checks whether a plugin is disabled by the given workspace config.
    /// Whitelist mode: disabled if NOT in EnabledPlugins.
    /// Blacklist mode: disabled if IN DisabledPlugins.
    /// </summary>
    private static bool IsPluginDisabledByConfig(WorkspacePluginConfig config, string pluginId)
    {
        return config.IsWhitelistMode
            ? !config.EnabledPlugins!.Contains(pluginId)
            : config.DisabledPlugins.Contains(pluginId);
    }

    private void ActivatePlugin(IAppPlugin plugin)
    {
        try
        {
            plugin.Activate();

            if (plugin.NavigationItem != null)
            {
                _pluginByNavId[plugin.NavigationItem.Id] = plugin;
            }

            // Auto-register linkable item providers with the capability broker
            if (plugin is ILinkableItemProvider linkProvider)
            {
                HostFactory.CapabilityBroker.Register<ILinkableItemProvider>(linkProvider);
            }

            _log.Debug("Plugin activated: {PluginId}", plugin.Metadata.Id);
            RaisePluginStateChanged(plugin, PluginState.Active);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to activate plugin: {PluginId}", plugin.Metadata.Id);
            RaisePluginStateChanged(plugin, PluginState.Failed, $"Activation failed: {ex.Message}");
        }
    }

    private static void RegisterCommandProvider(IAppPlugin plugin, MainWindowViewModel mainViewModel)
    {
        if (plugin.CommandProvider != null)
        {
            try
            {
                // Wrap SDK ICommandProvider as Desktop ICommandProvider
                mainViewModel.CommandPaletteVM.RegisterProvider(
                    new SdkCommandProviderAdapter(plugin.CommandProvider));
                _log.Debug("Registered command provider for plugin: {PluginId}", plugin.Metadata.Id);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to register command provider for plugin: {PluginId}",
                    plugin.Metadata.Id);
            }
        }
    }

    private static readonly JsonSerializerOptions _schemaJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// Registers system entity schemas (property definitions, groups, templates, entity metadata)
    /// with the Rust core. These are not owned by any plugin but used by EntityMetadataService.
    /// Must be called before plugin initialization so system types are available immediately.
    /// </summary>
    private static void RegisterSystemEntitySchemas()
    {
        EntitySchema[] systemSchemas =
        [
            new EntitySchema
            {
                EntityType = "entity_metadata",
                MergeStrategy = MergeStrategy.LwwPerField,
                IndexedFields =
                [
                    IndexedField.Text("/target_type", searchable: false),
                    IndexedField.Text("/target_id", searchable: false),
                    IndexedField.Tag("/tags"),
                    IndexedField.Json("/properties"),
                ]
            },
            new EntitySchema
            {
                EntityType = "property_definition",
                MergeStrategy = MergeStrategy.LwwPerField,
                IndexedFields =
                [
                    IndexedField.Text("/name"),
                    IndexedField.Text("/type", searchable: false),
                    IndexedField.Text("/group_id", searchable: false),
                    IndexedField.Number("/sort_order"),
                ]
            },
            new EntitySchema
            {
                EntityType = "property_group",
                MergeStrategy = MergeStrategy.LwwPerField,
                IndexedFields =
                [
                    IndexedField.Text("/name"),
                    IndexedField.Number("/sort_order"),
                ]
            },
            new EntitySchema
            {
                EntityType = "property_template",
                MergeStrategy = MergeStrategy.LwwPerField,
                IndexedFields =
                [
                    IndexedField.Text("/name"),
                    IndexedField.Json("/entries"),
                ]
            },
        ];

        foreach (var schema in systemSchemas)
        {
            try
            {
                var json = JsonSerializer.Serialize(schema, _schemaJsonOptions);
                var result = NativeLib.RegisterEntityType(json);

                if (result == 0)
                    _log.Information("Registered system entity type '{EntityType}'", schema.EntityType);
                else
                    _log.Error("Failed to register system entity type '{EntityType}': error code {Code}",
                        schema.EntityType, result);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Exception registering system entity type '{EntityType}'", schema.EntityType);
            }
        }
    }

    /// <summary>
    /// Registers a plugin's entity schemas with the Rust core via FFI.
    /// </summary>
    private static void RegisterEntitySchemas(IAppPlugin plugin)
    {
        var schemas = plugin.EntitySchemas;
        if (schemas.Count == 0) return;

        foreach (var schema in schemas)
        {
            try
            {
                var json = JsonSerializer.Serialize(schema, _schemaJsonOptions);
                var result = NativeLib.RegisterEntityType(json);

                if (result == 0)
                {
                    _log.Information("Registered entity type '{EntityType}' from plugin {PluginId}",
                        schema.EntityType, plugin.Metadata.Id);
                }
                else
                {
                    _log.Error("Failed to register entity type '{EntityType}' from plugin {PluginId}: error code {Code}",
                        schema.EntityType, plugin.Metadata.Id, result);
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Exception registering entity type '{EntityType}' from plugin {PluginId}",
                    schema.EntityType, plugin.Metadata.Id);
            }
        }
    }

    private void RebuildNavigationItems()
    {
        // ObservableCollection is bound to the sidebar — must run on UI thread
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RebuildNavigationItems).Wait();
            return;
        }

        _navigationItems.Clear();

        var savedOrder = App.Services.GetRequiredService<IAppSettingsService>().GetWorkspacePluginConfig().PluginOrder;

        var items = _plugins
            .Where(p => p.NavigationItem != null && p.State == PluginState.Active)
            .Select(p => p.NavigationItem!)
            .ToList();

        if (savedOrder.Count > 0)
        {
            var orderedItems = new List<NavigationItem>();

            foreach (var id in savedOrder)
            {
                var item = items.FirstOrDefault(i => i.Id == id);
                if (item != null)
                {
                    orderedItems.Add(item);
                }
            }

            foreach (var item in items.OrderBy(i => i.Order))
            {
                if (!orderedItems.Contains(item))
                {
                    orderedItems.Add(item);
                }
            }

            items = orderedItems;
        }
        else
        {
            items = items.OrderBy(i => i.Order).ToList();
        }

        foreach (var item in items)
        {
            _navigationItems.Add(item);
        }

        NavigationItemsChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(NavigationItems));

        _log.Debug("Navigation items rebuilt, count: {Count}", _navigationItems.Count);
    }

    public void MoveNavigationItem(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _navigationItems.Count ||
            toIndex < 0 || toIndex >= _navigationItems.Count ||
            fromIndex == toIndex)
        {
            return;
        }

        var item = _navigationItems[fromIndex];
        _navigationItems.RemoveAt(fromIndex);
        _navigationItems.Insert(toIndex, item);

        SaveNavigationOrder();

        _log.Debug("Navigation item moved from {From} to {To}: {Id}", fromIndex, toIndex, item.Id);
    }

    private void SaveNavigationOrder()
    {
        var order = _navigationItems.Select(n => n.Id).ToList();
        App.Services.GetRequiredService<IAppSettingsService>().UpdatePluginOrder(order);
    }

    private void RaisePluginStateChanged(IAppPlugin plugin, PluginState newState, string? message = null)
    {
        PluginStateChanged?.Invoke(this, new PluginStateChangedEventArgs(plugin, newState, message));

        // Invalidate link provider cache when plugins are activated or deactivated
        // so the cache reflects the current set of available link types
        if (newState is PluginState.Active or PluginState.Deactivated)
        {
            App.Services.GetService<LinkProviderCacheService>()?.Invalidate();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    // ========================================================================
    // IDisposable
    // ========================================================================

    public void Dispose()
    {
        if (_disposed) return;

        _log.Information("Disposing PluginRegistry");

        foreach (var plugin in _plugins)
        {
            try
            {
                if (plugin.State == PluginState.Active)
                {
                    plugin.Deactivate();
                }
                plugin.Dispose();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error disposing plugin: {PluginId}", plugin.Metadata.Id);
            }
        }

        _plugins.Clear();
        _pluginById.Clear();
        _pluginByNavId.Clear();
        _navigationItems.Clear();

        _disposed = true;
        _log.Information("PluginRegistry disposed");
    }

}
