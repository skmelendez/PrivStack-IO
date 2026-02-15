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
using PrivStack.Desktop.Services.FileSync;
using PrivStack.Desktop.Services.Plugin;
using PrivStack.Sdk.Capabilities;
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

            // Check if first-run setup is needed, or if workspaces are missing
            var workspaceService = Services.GetRequiredService<IWorkspaceService>();
            if (!SetupWizardViewModel.IsSetupComplete() || !workspaceService.HasWorkspaces)
            {
                // For first-run or missing workspaces, DON'T initialize service yet.
                // Setup wizard will initialize it after user picks data directory.
                Log.Information("Setup required (first-run or no workspaces), showing setup wizard");
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
            var workspaceService = Services.GetRequiredService<IWorkspaceService>();
            var active = workspaceService.GetActiveWorkspace();

            if (active != null)
            {
                // Ensure DataPaths is workspace-aware before anything touches paths
                var resolvedDir = workspaceService.ResolveWorkspaceDir(active);
                DataPaths.SetActiveWorkspace(active.Id, resolvedDir);

                // Reconfigure logger to write to workspace-specific log directory
                Log.ReconfigureForWorkspace(active.Id);

                // Run one-time data migration for existing installs
                WorkspaceDataMigration.MigrateIfNeeded(active.Id, resolvedDir);
            }

            var dbPath = workspaceService.GetActiveDataPath();
            Log.Information("Using workspace database path: {DbPath}", dbPath);

            var dir = Path.GetDirectoryName(dbPath)!;
            Directory.CreateDirectory(dir);

            // Diagnostic logging for storage state before init
            LogStorageDiagnostics(dbPath);

            Services.GetRequiredService<IPrivStackRuntime>().Initialize(dbPath);
            Log.Information("Native service initialized successfully");

            // Clean up orphaned root-level DB files from pre-workspace-scoping
            CleanupOrphanedRootFiles();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize native service");
            Serilog.Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Removes orphaned data.*.duckdb and data.peer_id files from the root BaseDir
    /// that were created by the old setup wizard initializing at root level.
    /// </summary>
    private static void CleanupOrphanedRootFiles()
    {
        try
        {
            var baseDir = DataPaths.BaseDir;
            var orphanPatterns = new[] { "data.*.duckdb", "data.*.duckdb.wal", "data.peer_id" };

            foreach (var pattern in orphanPatterns)
            {
                foreach (var file in Directory.GetFiles(baseDir, pattern))
                {
                    try
                    {
                        File.Delete(file);
                        Log.Information("Cleaned up orphaned root file: {File}", Path.GetFileName(file));
                    }
                    catch (Exception ex)
                    {
                        Log.Logger.Warning(ex, "Could not delete orphaned root file: {File}", file);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "Failed to clean up orphaned root files");
        }
    }

    /// <summary>
    /// Logs detailed diagnostics about DuckDB file state before initialization.
    /// </summary>
    private static void LogStorageDiagnostics(string dbPath)
    {
        try
        {
            var basePath = Path.ChangeExtension(dbPath, null); // strip .duckdb
            var dbDir = Path.GetDirectoryName(dbPath)!;

            string[] suffixes = ["vault.duckdb", "blobs.duckdb", "entities.duckdb", "events.duckdb"];

            Log.Information("[StorageDiag] Base path: {BasePath}", basePath);
            Log.Information("[StorageDiag] Directory exists: {Exists}, writable: {Writable}",
                Directory.Exists(dbDir),
                IsDirectoryWritable(dbDir));

            foreach (var suffix in suffixes)
            {
                var filePath = $"{basePath}.{suffix}";
                var walPath = $"{filePath}.wal";

                if (File.Exists(filePath))
                {
                    var info = new FileInfo(filePath);
                    Log.Information("[StorageDiag] {File}: size={Size}, modified={Modified}",
                        Path.GetFileName(filePath),
                        info.Length,
                        info.LastWriteTimeUtc.ToString("o"));
                    if (info.IsReadOnly)
                        Log.Warning("[StorageDiag] {File}: IS READ-ONLY!", Path.GetFileName(filePath));
                }
                else
                {
                    Log.Warning("[StorageDiag] {File}: DOES NOT EXIST", Path.GetFileName(filePath));
                }

                if (File.Exists(walPath))
                {
                    var walInfo = new FileInfo(walPath);
                    Log.Warning("[StorageDiag] {WalFile}: WAL EXISTS! size={Size}",
                        Path.GetFileName(walPath),
                        walInfo.Length);
                }
            }

            // Check peer_id
            var peerIdPath = $"{basePath}.peer_id";
            if (File.Exists(peerIdPath))
            {
                var peerId = File.ReadAllText(peerIdPath).Trim();
                Log.Information("[StorageDiag] peer_id: {PeerId}", peerId);
            }
            else
            {
                Log.Warning("[StorageDiag] peer_id file DOES NOT EXIST");
            }

            // List all files in directory for completeness
            var allFiles = Directory.GetFiles(dbDir);
            Log.Information("[StorageDiag] Directory contains {Count} files: {Files}",
                allFiles.Length,
                string.Join(", ", allFiles.Select(Path.GetFileName)));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[StorageDiag] Failed to collect storage diagnostics");
        }
    }

    private static bool IsDirectoryWritable(string path)
    {
        try
        {
            var testFile = Path.Combine(path, $".privstack_write_test_{Guid.NewGuid():N}");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
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
            Services.GetRequiredService<IWorkspaceService>(),
            Services.GetRequiredService<IMasterPasswordCache>());
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

    /// <summary>
    /// Locks the app and transitions back to the unlock screen.
    /// Called from Settings → Logout.
    /// </summary>
    public void RequestLogout()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        Services.GetService<Services.Abstractions.IMasterPasswordCache>()?.Clear();

        var currentWindow = desktop.MainWindow;
        ShowUnlockScreen(desktop);
        currentWindow?.Close();
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

        // Start file-based event sync (cloud/NAS — no-op if workspace is local-only)
        Services.GetRequiredService<IFileEventSyncService>().Start();

        // Start snapshot sync: imports latest peer snapshot, then exports on close
        _ = Services.GetRequiredService<ISnapshotSyncService>().StartAsync();

        // Scan shared files dir for dataset imports from other peers
        _ = Task.Run(() => DatasetFileSyncHelper.ScanAndImportAsync(
            Services.GetRequiredService<IWorkspaceService>(),
            Services.GetRequiredService<IDatasetService>()));

        // Start the reminder scheduler for OS notifications
        Services.GetRequiredService<ReminderSchedulerService>().Start();

        // Check license expiration state for read-only enforcement banner
        var licensing = Services.GetRequiredService<ILicensingService>();
        Services.GetRequiredService<LicenseExpirationService>().CheckLicenseStatus(licensing);

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