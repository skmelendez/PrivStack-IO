using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Sdk;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.Plugin;
using PrivStack.Desktop.Services.Update;
using PrivStack.Desktop.ViewModels;
using PrivStack.Sdk;

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
        services.AddSingleton<IWorkspaceService, WorkspaceService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<ISensitiveLockService, SensitiveLockService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IFontScaleService, FontScaleService>();
        services.AddSingleton<IResponsiveLayoutService, ResponsiveLayoutService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<ISyncIngestionService, SyncIngestionService>();
        services.AddSingleton<IPluginRegistry, PluginRegistry>();

        services.AddSingleton<FocusModeService>();
        services.AddSingleton<IFocusModeService>(sp => sp.GetRequiredService<FocusModeService>());
        services.AddSingleton<SdkHost>();
        services.AddSingleton<IPrivStackSdk>(sp => sp.GetRequiredService<SdkHost>());
        services.AddSingleton<ISyncOutboundService, SyncOutboundService>();
        services.AddSingleton<SeedDataService>();
        services.AddSingleton<InfoPanelService>();
        services.AddSingleton<BacklinkService>();
        services.AddSingleton<EntityMetadataService>();

        services.AddSingleton<ISystemNotificationService, SystemNotificationService>();
        services.AddSingleton<ReminderSchedulerService>();
        services.AddSingleton<PrivStackApiClient>();
        services.AddSingleton<OAuthLoginService>();
        services.AddSingleton<IPluginInstallService, PluginInstallService>();
        services.AddSingleton<IUpdateService, RegistryUpdateService>();

        // Services without interfaces (used directly)
        services.AddSingleton<CustomThemeStore>();
        services.AddSingleton<WhisperService>();
        services.AddSingleton<WhisperModelManager>();
        services.AddSingleton<ViewStatePrefetchService>();
        services.AddSingleton<LinkProviderCacheService>();

        // ViewModels (transient — created fresh each resolution)
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<SetupWizardViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<UpdateViewModel>();

        var provider = services.BuildServiceProvider();

        // Wire SyncOutbound into SdkHost (cross-singleton dependency resolved after build)
        provider.GetRequiredService<SdkHost>()
            .SetSyncOutbound(provider.GetRequiredService<ISyncOutboundService>());

        return provider;
    }
}
