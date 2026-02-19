using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Plugin;
using PrivStack.Sdk;
using PrivStack.Sdk.Services;
using Serilog;

namespace PrivStack.Desktop.Sdk;

/// <summary>
/// Implements IPluginHost by composing the SdkHost, CapabilityBroker, and other services.
/// One instance per plugin, with plugin-scoped settings and logger.
/// </summary>
internal sealed class PluginHost : IPluginHost
{
    public PluginHost(
        IPrivStackSdk sdk,
        ICapabilityBroker capabilities,
        string pluginId,
        ISdkDialogService dialogService,
        Services.Abstractions.IAppSettingsService appSettings,
        IPluginRegistry pluginRegistry,
        Services.Abstractions.IUiDispatcher dispatcher,
        IInfoPanelService infoPanelService,
        IFocusModeService focusModeService,
        IToastService toastService,
        IConnectionService? connectionService = null,
        IPropertyService? propertyService = null,
        IAiService? aiService = null)
    {
        Sdk = sdk;
        Capabilities = capabilities;
        Settings = new PluginSettingsAdapter(pluginId, appSettings);
        Logger = new SerilogPluginLogger(pluginId);
        Navigation = new NavigationServiceAdapter(pluginRegistry, dispatcher);
        DialogService = dialogService;
        InfoPanel = infoPanelService;
        FocusMode = focusModeService;
        Toast = toastService;
        Connections = connectionService;
        Properties = propertyService;
        AI = aiService;
        Messenger = WeakReferenceMessenger.Default;
        AppVersion = typeof(PluginHost).Assembly.GetName().Version ?? new Version(1, 0, 0);
    }

    public IPrivStackSdk Sdk { get; }
    public ICapabilityBroker Capabilities { get; }
    public IPluginSettings Settings { get; }
    public IPluginLogger Logger { get; }
    public INavigationService Navigation { get; }
    public ISdkDialogService? DialogService { get; }
    public IInfoPanelService InfoPanel { get; }
    public IFocusModeService FocusMode { get; }
    public IToastService Toast { get; }
    public IConnectionService? Connections { get; }
    public IPropertyService? Properties { get; }
    public IAiService? AI { get; }
    public IMessenger Messenger { get; }
    public Version AppVersion { get; }

    public string WorkspaceDataPath =>
        Services.DataPaths.WorkspaceDataDir
        ?? throw new InvalidOperationException(
            "No active workspace. Plugins cannot access WorkspaceDataPath before workspace selection.");
}

/// <summary>
/// Plugin-namespaced settings backed by AppSettingsService.
/// Keys are stored as "plugin.{id}.{key}" in the flat PluginSettings dictionary.
/// </summary>
internal sealed class PluginSettingsAdapter : IPluginSettings
{
    private readonly string _prefix;
    private readonly Services.Abstractions.IAppSettingsService _appSettings;

    public PluginSettingsAdapter(string pluginId, Services.Abstractions.IAppSettingsService appSettings)
    {
        _prefix = $"plugin.{pluginId}.";
        _appSettings = appSettings;
    }

    public T Get<T>(string key, T defaultValue)
    {
        var fullKey = _prefix + key;
        var dict = _appSettings.Settings.PluginSettings;

        if (!dict.TryGetValue(fullKey, out var json))
            return defaultValue;

        try
        {
            return JsonSerializer.Deserialize<T>(json) ?? defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    public void Set<T>(string key, T value)
    {
        var fullKey = _prefix + key;
        var json = JsonSerializer.Serialize(value);
        _appSettings.Settings.PluginSettings[fullKey] = json;
        _appSettings.SaveDebounced();
    }
}

/// <summary>
/// Wraps Serilog with plugin ID in context.
/// </summary>
internal sealed class SerilogPluginLogger : IPluginLogger
{
    private readonly ILogger _log;

    public SerilogPluginLogger(string pluginId)
    {
        _log = Serilog.Log.ForContext("PluginId", pluginId);
    }

    public void Debug(string messageTemplate, params object[] args) => _log.Debug(messageTemplate, args);
    public void Info(string messageTemplate, params object[] args) => _log.Information(messageTemplate, args);
    public void Warn(string messageTemplate, params object[] args) => _log.Warning(messageTemplate, args);
    public void Error(string messageTemplate, params object[] args) => _log.Error(messageTemplate, args);
    public void Error(Exception ex, string messageTemplate, params object[] args) => _log.Error(ex, messageTemplate, args);
}

/// <summary>
/// Cross-plugin navigation wired to MainWindowViewModel via PluginRegistry.
/// </summary>
internal sealed class NavigationServiceAdapter : INavigationService
{
    private static string? _previousNavItemId;
    private readonly IPluginRegistry _pluginRegistry;
    private readonly Services.Abstractions.IUiDispatcher _dispatcher;

    public NavigationServiceAdapter(IPluginRegistry pluginRegistry, Services.Abstractions.IUiDispatcher dispatcher)
    {
        _pluginRegistry = pluginRegistry;
        _dispatcher = dispatcher;
    }

    public void NavigateTo(string pluginId)
    {
        var plugin = _pluginRegistry.GetPlugin(pluginId);
        var navItemId = plugin?.NavigationItem?.Id;
        if (navItemId == null) return;

        NavigateToNavItem(navItemId);
    }

    public void NavigateBack()
    {
        if (_previousNavItemId != null)
            NavigateToNavItem(_previousNavItemId);
    }

    public Task NavigateToItemAsync(string linkType, string itemId)
    {
        var mainVm = _pluginRegistry.GetMainViewModel();
        if (mainVm == null) return Task.CompletedTask;
        return _dispatcher.InvokeAsync(() => mainVm.NavigateToLinkedItemAsync(linkType, itemId));
    }

    private void NavigateToNavItem(string navItemId)
    {
        var mainVm = _pluginRegistry.GetMainViewModel();
        if (mainVm == null) return;

        _previousNavItemId = mainVm.SelectedTab;

        _dispatcher.Post(() => mainVm.SelectTabCommand.Execute(navItemId));
    }
}
