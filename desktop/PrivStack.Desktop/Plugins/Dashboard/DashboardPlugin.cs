using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Plugins.Dashboard.Services;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.Plugin;
using PrivStack.Sdk;

namespace PrivStack.Desktop.Plugins.Dashboard;

/// <summary>
/// Built-in Dashboard plugin — always present, cannot be disabled.
/// Shows the official plugin catalog, system metrics, and install/update/uninstall capabilities.
/// </summary>
public sealed class DashboardPlugin : PluginBase<DashboardViewModel>
{
    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "privstack.dashboard",
        Name = "Dashboard",
        Description = "System overview, plugin marketplace, and management dashboard",
        Version = new Version(1, 2, 0),
        Author = "PrivStack",
        Icon = "LayoutDashboard",
        NavigationOrder = 50,
        Category = PluginCategory.Utility,
        CanDisable = false,
        SupportsInfoPanel = false
    };

    public override NavigationItem NavigationItem => new()
    {
        Id = "Dashboard",
        DisplayName = "Dashboard",
        Subtitle = "System overview & plugins",
        Icon = "LayoutDashboard",
        Tooltip = "Dashboard — System metrics & plugin management",
        Order = Metadata.NavigationOrder
    };

    protected override DashboardViewModel CreateViewModelCore()
    {
        var installService = App.Services.GetRequiredService<IPluginInstallService>();
        var pluginRegistry = App.Services.GetRequiredService<IPluginRegistry>();
        var sdk = App.Services.GetRequiredService<IPrivStackSdk>();
        var metricsService = new SystemMetricsService();
        var entityMetadataService = App.Services.GetRequiredService<EntityMetadataService>();
        var linkProviderCache = App.Services.GetRequiredService<LinkProviderCacheService>();
        return new DashboardViewModel(installService, pluginRegistry, metricsService, sdk,
            entityMetadataService, linkProviderCache);
    }

    public override async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        if (ViewModel is { HasLoadedOnce: false })
        {
            await ViewModel.RefreshAsync();
        }
    }
}
