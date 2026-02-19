using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Sdk;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.AI;
using PrivStack.Desktop.Services.Connections;
using PrivStack.Desktop.Services.FileSync;
using PrivStack.Desktop.Services.Plugin;
using PrivStack.Desktop.Services.Ipc;
using PrivStack.Desktop.Services.Update;
using PrivStack.Desktop.ViewModels;
using PrivStack.Sdk;
using PrivStack.Sdk.Capabilities;
using PrivStack.Sdk.Services;
using IAiService = PrivStack.Sdk.Services.IAiService;
using IToastService = PrivStack.Sdk.IToastService;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Configures the DI container with all services and ViewModels.
/// </summary>
public static class ServiceRegistration
{
    public static IServiceProvider Configure()
    {
        var services = new ServiceCollection();

        // Core services (singletons — same lifetime as previous .Instance pattern)
        services.AddSingleton<IAppSettingsService, AppSettingsService>();
        services.AddSingleton<PrivStackService>();
        services.AddSingleton<IPrivStackNative>(sp => sp.GetRequiredService<PrivStackService>());
        services.AddSingleton<IPrivStackRuntime>(sp => sp.GetRequiredService<PrivStackService>());
        services.AddSingleton<IAuthService>(sp => sp.GetRequiredService<PrivStackService>());
        services.AddSingleton<ISyncService>(sp => sp.GetRequiredService<PrivStackService>());
        services.AddSingleton<IPairingService>(sp => sp.GetRequiredService<PrivStackService>());
        services.AddSingleton<ICloudStorageService>(sp => sp.GetRequiredService<PrivStackService>());
        services.AddSingleton<ILicensingService>(sp => sp.GetRequiredService<PrivStackService>());
        services.AddSingleton<ICloudSyncService, CloudSyncService>();
        services.AddSingleton<IWorkspaceService, WorkspaceService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<ISensitiveLockService, SensitiveLockService>();
        services.AddSingleton<IMasterPasswordCache, MasterPasswordCache>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IFontScaleService, FontScaleService>();
        services.AddSingleton<IResponsiveLayoutService, ResponsiveLayoutService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<ISyncIngestionService, SyncIngestionService>();
        services.AddSingleton<IPluginRegistry, PluginRegistry>();

        services.AddSingleton<ToastService>();
        services.AddSingleton<IToastService>(sp => sp.GetRequiredService<ToastService>());

        services.AddSingleton<FocusModeService>();
        services.AddSingleton<IFocusModeService>(sp => sp.GetRequiredService<FocusModeService>());
        services.AddSingleton<SdkHost>();
        services.AddSingleton<IPrivStackSdk>(sp => sp.GetRequiredService<SdkHost>());
        services.AddSingleton<ISyncOutboundService, SyncOutboundService>();
        services.AddSingleton<IFileEventSyncService, FileEventSyncService>();
        services.AddSingleton<ISnapshotSyncService, SnapshotSyncService>();
        services.AddSingleton<SeedDataService>();
        services.AddSingleton<InfoPanelService>();
        services.AddSingleton<BacklinkService>();
        services.AddSingleton<EntityMetadataService>();

        services.AddSingleton<LicenseExpirationService>();
        services.AddSingleton<SubscriptionValidationService>();
        services.AddSingleton<ISystemNotificationService, SystemNotificationService>();
        services.AddSingleton<ReminderSchedulerService>();
        services.AddSingleton<PrivStackApiClient>();
        services.AddSingleton<OAuthLoginService>();
        services.AddSingleton<IPluginInstallService, PluginInstallService>();
        services.AddSingleton<IUpdateService, RegistryUpdateService>();

        // External connections (GitHub, etc.)
        services.AddSingleton<GitHubDeviceFlowService>();
        services.AddSingleton<ConnectionService>();
        services.AddSingleton<IConnectionService>(sp => sp.GetRequiredService<ConnectionService>());

        // Services without interfaces (used directly)
        services.AddSingleton<CustomThemeStore>();
        services.AddSingleton<WhisperService>();
        services.AddSingleton<WhisperModelManager>();
        services.AddSingleton<AiModelManager>();
        services.AddSingleton<AiService>();
        services.AddSingleton<IAiService>(sp => sp.GetRequiredService<AiService>());
        services.AddSingleton<ViewStatePrefetchService>();
        services.AddSingleton<LinkProviderCacheService>();
        services.AddSingleton<IDatasetService, DatasetService>();

        // IPC server for browser extension bridge
        services.AddSingleton<IpcMessageRouter>();
        services.AddSingleton<IpcServer>();
        services.AddSingleton<IIpcServer>(sp => sp.GetRequiredService<IpcServer>());

        // ViewModels (transient — created fresh each resolution)
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<SetupWizardViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<UpdateViewModel>();

        var provider = services.BuildServiceProvider();

        // Wire SyncOutbound into SdkHost (cross-singleton dependency resolved after build)
        var sdkHost = provider.GetRequiredService<SdkHost>();
        sdkHost.SetSyncOutbound(provider.GetRequiredService<ISyncOutboundService>());

        // Wire file-based event sync into outbound service
        if (provider.GetRequiredService<ISyncOutboundService>() is SyncOutboundService outbound)
            outbound.SetFileEventSync(provider.GetRequiredService<IFileEventSyncService>());

        // Wire vault unlock prompt — plugins call RequestVaultUnlockAsync to trigger this
        var dialogService = provider.GetRequiredService<DialogService>();
        sdkHost.SetVaultUnlockPrompt(async (vaultId, ct) =>
        {
            return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                string? pluginName = null;
                string? pluginIcon = null;

                var pluginRegistry = provider.GetRequiredService<IPluginRegistry>();
                var vaultConsumers = pluginRegistry.GetCapabilityProviders<IVaultConsumer>();
                foreach (var consumer in vaultConsumers)
                {
                    if (consumer.VaultIds.Contains(vaultId) && consumer is IAppPlugin plugin)
                    {
                        pluginName = plugin.Metadata.Name;
                        pluginIcon = plugin.Metadata.Icon;
                        break;
                    }
                }

                return await dialogService.ShowVaultUnlockAsync(pluginName, pluginIcon);
            });
        });

        // Wire license read-only detection from SdkHost into the expiration service
        var expirationService = provider.GetRequiredService<LicenseExpirationService>();
        sdkHost.LicenseReadOnlyBlocked += (_, _) => expirationService.OnMutationBlocked();

        return provider;
    }
}
