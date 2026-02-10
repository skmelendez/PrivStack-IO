using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using AvaloniaWebView;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Native;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.Plugin;
using PrivStack.Desktop.ViewModels;
using PrivStack.Desktop.Views;
using PrivStack.Sdk;
using PrivStack.UI.Adaptive;

namespace PrivStack.Desktop;

public partial class App : Application
{
    /// <summary>
    /// DI container — available for the transition period while Views still need service locator.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public override void RegisterServices()
    {
        base.RegisterServices();

        // Only initialize WebView on Windows - macOS has .NET 9 compatibility issues
        if (OperatingSystem.IsWindows())
        {
            AvaloniaWebViewBuilder.Initialize(default);
        }
    }

    public override void Initialize()
    {
        // Build the DI container before anything else
        Services = ServiceRegistration.Configure();

        Log.Debug("Avalonia XAML loader starting");
        AvaloniaXamlLoader.Load(this);
        Log.Debug("Avalonia XAML loader completed");

        // Initialize theme service after XAML is loaded
        Log.Debug("Initializing theme service");
        Services.GetRequiredService<IThemeService>().Initialize();

        // Initialize font scale service after theme (font scale is reapplied after theme changes)
        Log.Debug("Initializing font scale service");
        Services.GetRequiredService<IFontScaleService>().Initialize();

        // Initialize responsive layout service after font scale
        Log.Debug("Initializing responsive layout service");
        Services.GetRequiredService<IResponsiveLayoutService>().Initialize();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Log.Information("Framework initialization completed");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
            DisableAvaloniaDataAnnotationValidation();

            // Check if first-run setup is needed
            if (!SetupWizardViewModel.IsSetupComplete())
            {
                // For first-run, DON'T initialize service yet.
                // Setup wizard will initialize it after user picks data directory.
                Log.Information("First-run setup required, showing setup wizard");
                ShowSetupWizard(desktop);
            }
            else
            {
                // Setup is complete - initialize service at configured directory
                Log.Information("Initializing PrivStack native service");
                InitializeService();

                // Show unlock screen
                Log.Information("Setup complete, showing unlock screen");
                ShowUnlockScreen(desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitializeService()
    {
        try
        {
            string dbPath;

            // Use workspace system if workspaces exist
            var workspaceService = Services.GetRequiredService<IWorkspaceService>();
            if (workspaceService.HasWorkspaces)
            {
                dbPath = workspaceService.GetActiveDataPath();
                Log.Information("Using workspace database path: {DbPath}", dbPath);
            }
            else
            {
                // Legacy path for pre-workspace installations
                var dataDir = GetConfiguredDataDirectory();
                dbPath = Path.Combine(dataDir, "data.duckdb");
                Log.Information("Using legacy database path: {DbPath}", dbPath);
                Directory.CreateDirectory(dataDir);
            }

            var dir = Path.GetDirectoryName(dbPath)!;
            Directory.CreateDirectory(dir);
            Services.GetRequiredService<IPrivStackRuntime>().Initialize(dbPath);
            Log.Information("Native service initialized successfully");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize native service");
        }
    }

    /// <summary>
    /// Gets the configured data directory, or the default if not configured.
    /// </summary>
    private static string GetConfiguredDataDirectory()
    {
        var defaultDir = DataPaths.BaseDir;

        var customDir = Services.GetRequiredService<IAppSettingsService>().Settings.CustomDataDirectory;
        if (!string.IsNullOrEmpty(customDir))
        {
            Log.Information("Using custom data directory: {Dir}", customDir);
            return customDir;
        }

        Log.Information("Using default data directory: {Dir}", defaultDir);
        return defaultDir;
    }

    private void ShowSetupWizard(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var setupVm = Services.GetRequiredService<SetupWizardViewModel>();
        var setupWindow = new SetupWindow(setupVm);

        setupVm.SetupCompleted += async (_, _) =>
        {
            // Show loading state on the Complete step while app initializes
            setupVm.IsAppLoading = true;
            setupVm.LoadingMessage = "Loading plugins...";

            await Task.Delay(50);

            var pluginRegistry = Services.GetRequiredService<IPluginRegistry>();
            await Task.Run(() => pluginRegistry.DiscoverAndInitialize());

            setupVm.LoadingMessage = "Starting up...";
            await Task.Delay(30);

            // IMPORTANT: Set the new MainWindow BEFORE closing setup window
            // Otherwise Avalonia shuts down when it sees MainWindow closed
            ShowMainWindow(desktop, skipPluginInit: true);
            setupWindow.Close();
        };

        desktop.MainWindow = setupWindow;
    }

    private void ShowUnlockScreen(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var unlockVm = new UnlockViewModel(
            Services.GetRequiredService<IAuthService>(),
            Services.GetRequiredService<IPrivStackRuntime>(),
            Services.GetRequiredService<IWorkspaceService>());
        var unlockWindow = new UnlockWindow(unlockVm);

        unlockVm.AppUnlocked += async (_, _) =>
        {
            Log.Information("App unlocked, loading application...");
            unlockVm.IsAppLoading = true;
            unlockVm.LoadingMessage = "Loading plugins...";

            // Yield to let the UI update before heavy work
            await Task.Delay(50);

            // Plugin discovery and heavy init on background thread
            var pluginRegistry = Services.GetRequiredService<IPluginRegistry>();
            await Task.Run(() => pluginRegistry.DiscoverAndInitialize());

            unlockVm.LoadingMessage = "Starting up...";
            await Task.Delay(30);

            // The rest must happen on the UI thread (window creation)
            ShowMainWindow(desktop, skipPluginInit: true);
            unlockWindow.Close();
        };

        unlockVm.DataResetRequested += (_, _) =>
        {
            Log.Information("Data reset requested, returning to setup wizard");
            ShowSetupWizard(desktop);
            unlockWindow.Close();
        };

        desktop.MainWindow = unlockWindow;
    }

    private void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop, bool skipPluginInit = false)
    {
        if (!skipPluginInit)
        {
            // Discover and initialize plugins before showing the main window
            Log.Information("Discovering and initializing plugins");
            var pluginRegistry = Services.GetRequiredService<IPluginRegistry>();
            pluginRegistry.DiscoverAndInitialize();
            Log.Information("Plugin initialization complete");
        }

        // Auto-seeding disabled — use "Reseed Sample Data" in Settings if needed
        // _ = Task.Run(async () =>
        // {
        //     try
        //     {
        //         var seedService = Services.GetRequiredService<SeedDataService>();
        //         await seedService.SeedIfNeededAsync();
        //     }
        //     catch (Exception ex)
        //     {
        //         Log.Error(ex, "Sample data seed failed");
        //     }
        // });

        // Initialize backup service to start scheduled backups
        Log.Information("Initializing backup service");
        _ = Services.GetRequiredService<IBackupService>(); // Touch to trigger initialization
        Log.Information("Backup service initialized");

        // Initialize sensitive lock service with saved settings
        // Sensitive features (Passwords, Vault) require separate unlock even after app unlock
        Log.Information("Initializing sensitive lock service");
        var appSettings = Services.GetRequiredService<IAppSettingsService>();
        var lockoutMinutes = appSettings.Settings.SensitiveLockoutMinutes;
        var sensitiveLock = Services.GetRequiredService<ISensitiveLockService>();
        sensitiveLock.LockoutMinutes = lockoutMinutes;
        // Do NOT auto-unlock - user must explicitly unlock sensitive features
        Log.Information("Sensitive lock service initialized with {Minutes} minute lockout (locked)", lockoutMinutes);

        // Wire emoji picker recent emojis persistence
        AdaptiveViewRenderer.LoadRecentEmojis(appSettings.Settings.RecentEmojis);
        AdaptiveViewRenderer.RecentEmojisSaved = recents =>
        {
            appSettings.Settings.RecentEmojis = [..recents];
            appSettings.SaveDebounced();
        };

        // Start the reminder scheduler for OS notifications
        Services.GetRequiredService<ReminderSchedulerService>().Start();

        var mainWindow = new MainWindow
        {
            DataContext = Services.GetRequiredService<MainWindowViewModel>(),
        };
        desktop.MainWindow = mainWindow;
        mainWindow.Show();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}