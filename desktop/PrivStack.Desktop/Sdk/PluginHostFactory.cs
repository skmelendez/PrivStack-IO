using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.Plugin;
using PrivStack.Sdk;
using PrivStack.Sdk.Capabilities;
using PrivStack.Desktop.Services.Connections;

namespace PrivStack.Desktop.Sdk;

/// <summary>
/// Creates IPluginHost instances for plugins. Used by PluginRegistry during
/// plugin initialization for SDK-based plugins.
/// </summary>
internal sealed class PluginHostFactory
{
    private readonly SdkHost _sdkHost;
    private readonly CapabilityBroker _capabilityBroker = new();
    private readonly ISdkDialogService _dialogService;
    private readonly IAppSettingsService _appSettings;
    private readonly IPluginRegistry _pluginRegistry;
    private readonly IUiDispatcher _dispatcher;
    private readonly InfoPanelService _infoPanelService;
    private readonly IFocusModeService _focusModeService;
    private readonly IConnectionService _connectionService;
    private readonly IPropertyService _propertyService;
    private readonly IToastService _toastService;

    public ICapabilityBroker CapabilityBroker => _capabilityBroker;

    public PluginHostFactory()
    {
        _sdkHost = App.Services.GetRequiredService<SdkHost>();
        _appSettings = App.Services.GetRequiredService<IAppSettingsService>();
        _pluginRegistry = App.Services.GetRequiredService<IPluginRegistry>();
        _dialogService = new SdkDialogServiceAdapter(App.Services.GetRequiredService<IDialogService>());
        _dispatcher = App.Services.GetRequiredService<IUiDispatcher>();
        _infoPanelService = App.Services.GetRequiredService<InfoPanelService>();
        _focusModeService = App.Services.GetRequiredService<IFocusModeService>();
        _connectionService = App.Services.GetRequiredService<IConnectionService>();
        _propertyService = App.Services.GetRequiredService<EntityMetadataService>();
        _toastService = App.Services.GetRequiredService<IToastService>();

        // Register the default local filesystem storage provider
        _capabilityBroker.Register<IStorageProvider>(new LocalStorageProvider());

        // Register dataset service for cross-plugin access
        var datasetService = App.Services.GetRequiredService<IDatasetService>();
        _capabilityBroker.Register<IDatasetService>(datasetService);
    }

    public IPluginHost CreateHost(string pluginId)
    {
        return new PluginHost(_sdkHost, _capabilityBroker, pluginId, _dialogService, _appSettings, _pluginRegistry, _dispatcher, _infoPanelService, _focusModeService, _toastService, _connectionService, _propertyService);
    }
}
