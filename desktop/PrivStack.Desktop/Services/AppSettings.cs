using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.Connections;
using Serilog;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Application settings that are persisted between sessions.
/// </summary>
public class AppSettings
{
    [JsonPropertyName("window_width")]
    public double WindowWidth { get; set; } = 1400;

    [JsonPropertyName("window_height")]
    public double WindowHeight { get; set; } = 900;

    [JsonPropertyName("window_x")]
    public double? WindowX { get; set; }

    [JsonPropertyName("window_y")]
    public double? WindowY { get; set; }

    [JsonPropertyName("window_state")]
    public string WindowState { get; set; } = "Normal";

    [JsonPropertyName("last_active_tab")]
    public string LastActiveTab { get; set; } = "Notes";

    [JsonPropertyName("sidebar_width")]
    public double SidebarWidth { get; set; } = 200;

    [JsonPropertyName("sidebar_collapsed")]
    public bool SidebarCollapsed { get; set; }

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "Dark";

    [JsonPropertyName("calendar_view_mode")]
    public string CalendarViewMode { get; set; } = "month";

    [JsonPropertyName("tasks_view_mode")]
    public string TasksViewMode { get; set; } = "list";

    [JsonPropertyName("tasks_simple_mode")]
    public bool TasksSimpleMode { get; set; }

    [JsonPropertyName("plugin_order")]
    public List<string> PluginOrder { get; set; } = [];

    [JsonPropertyName("disabled_plugins")]
    public HashSet<string> DisabledPlugins { get; set; } = [];

    [JsonPropertyName("experimental_plugins_enabled")]
    public bool ExperimentalPluginsEnabled { get; set; }

    [JsonPropertyName("user_display_name")]
    public string? UserDisplayName { get; set; }

    [JsonPropertyName("profile_image_path")]
    public string? ProfileImagePath { get; set; }

    // Data & Backup settings (legacy — storage location is now per-workspace via Workspace.StorageLocation)
    [Obsolete("Use Workspace.StorageLocation instead. Kept for JSON deserialization backward compat.")]
    [JsonPropertyName("data_directory_type")]
    public string DataDirectoryType { get; set; } = "Default";

    [Obsolete("Use Workspace.StorageLocation instead. Kept for JSON deserialization backward compat.")]
    [JsonPropertyName("custom_data_directory")]
    public string? CustomDataDirectory { get; set; }

    [JsonPropertyName("backup_directory")]
    public string? BackupDirectory { get; set; }

    [JsonPropertyName("backup_frequency")]
    public string BackupFrequency { get; set; } = "Daily";

    [JsonPropertyName("backup_type")]
    public string BackupType { get; set; } = "Rolling";

    [JsonPropertyName("max_backups")]
    public int MaxBackups { get; set; } = 7;

    // Security settings
    [JsonPropertyName("sensitive_lockout_minutes")]
    public int SensitiveLockoutMinutes { get; set; } = 5; // Default 5 minutes

    // Graph view filters
    [JsonPropertyName("graph_show_notes")]
    public bool GraphShowNotes { get; set; } = true;

    [JsonPropertyName("graph_show_tasks")]
    public bool GraphShowTasks { get; set; } = true;

    [JsonPropertyName("graph_show_contacts")]
    public bool GraphShowContacts { get; set; } = true;

    [JsonPropertyName("graph_show_events")]
    public bool GraphShowEvents { get; set; } = true;

    [JsonPropertyName("graph_show_journal")]
    public bool GraphShowJournal { get; set; } = true;

    [JsonPropertyName("graph_show_tags")]
    public bool GraphShowTags { get; set; } = true;

    [JsonPropertyName("graph_show_orphaned_tags")]
    public bool GraphShowOrphanedTags { get; set; } = true;

    [JsonPropertyName("graph_orphan_mode")]
    public string GraphOrphanMode { get; set; } = "Show";

    // Graph timeline settings
    [JsonPropertyName("graph_timeline_enabled")]
    public bool GraphTimelineEnabled { get; set; }

    [JsonPropertyName("graph_timeline_lower")]
    public double GraphTimelineLower { get; set; }

    [JsonPropertyName("graph_timeline_upper")]
    public double GraphTimelineUpper { get; set; } = 100;

    // Graph physics settings
    [JsonPropertyName("graph_repulsion")]
    public double GraphRepulsion { get; set; } = 56;

    [JsonPropertyName("graph_link_distance")]
    public double GraphLinkDistance { get; set; } = 41;

    [JsonPropertyName("graph_link_strength")]
    public double GraphLinkStrength { get; set; } = 50;

    [JsonPropertyName("graph_collision")]
    public double GraphCollision { get; set; } = 70;

    [JsonPropertyName("graph_center")]
    public double GraphCenter { get; set; } = 25;

    // Notes expanded nodes
    [JsonPropertyName("notes_expanded_node_ids")]
    public HashSet<string> NotesExpandedNodeIds { get; set; } = [];

    // Snippets expanded collections
    [JsonPropertyName("snippets_expanded_collection_ids")]
    public HashSet<string> SnippetsExpandedCollectionIds { get; set; } = [];

    // Accessibility settings
    [JsonPropertyName("font_scale_multiplier")]
    public double FontScaleMultiplier { get; set; } = 1.0;

    [JsonPropertyName("font_family")]
    public string FontFamily { get; set; } = "system";

    // Speech-to-text settings
    [JsonPropertyName("speech_to_text_enabled")]
    public bool SpeechToTextEnabled { get; set; } = true;

    [JsonPropertyName("whisper_model_size")]
    public string WhisperModelSize { get; set; } = "base.en";

    [JsonPropertyName("audio_input_device")]
    public string? AudioInputDevice { get; set; }

    [JsonPropertyName("whisper_beam_search")]
    public bool WhisperBeamSearch { get; set; }

    // Sync settings
    [JsonPropertyName("sync_device_name")]
    public string? SyncDeviceName { get; set; }

    [JsonPropertyName("sync_pairing_state")]
    public string? SyncPairingState { get; set; }

    [JsonPropertyName("sync_auto_start")]
    public bool SyncAutoStart { get; set; }

    // Active timer state for crash recovery (legacy single timer)
    [JsonPropertyName("active_timer")]
    public ActiveTimerState? ActiveTimer { get; set; }

    // Multi-timer state for crash recovery
    [JsonPropertyName("active_timers")]
    public List<ActiveTimerState> ActiveTimers { get; set; } = [];

    // Seed data version - tracks which version of seed pages have been created
    [JsonPropertyName("seed_data_version")]
    public int SeedDataVersion { get; set; }

    // Whether sample/dummy data has been seeded (separate from doc page seed version)
    [JsonPropertyName("sample_data_seeded")]
    public bool SampleDataSeeded { get; set; }

    [JsonPropertyName("recent_emojis")]
    public List<string> RecentEmojis { get; set; } = [];

    // Notification settings
    [JsonPropertyName("notifications_enabled")]
    public bool NotificationsEnabled { get; set; } = true;

    [JsonPropertyName("notification_sound_enabled")]
    public bool NotificationSoundEnabled { get; set; } = true;

    [JsonPropertyName("info_panel_open")]
    public bool InfoPanelOpen { get; set; }

    [JsonPropertyName("info_panel_tab")]
    public string InfoPanelTab { get; set; } = "Info";

    [JsonPropertyName("info_panel_width")]
    public double InfoPanelWidth { get; set; } = 320;

    [JsonPropertyName("info_panel_graph_depth")]
    public int InfoPanelGraphDepth { get; set; } = 1;

    [JsonPropertyName("info_panel_graph_hidden_types")]
    public List<string> InfoPanelGraphHiddenTypes { get; set; } = [];

    // Info panel graph physics (slider values 0-100)
    [JsonPropertyName("info_panel_repel_slider")]
    public double InfoPanelRepelSlider { get; set; } = 50;

    [JsonPropertyName("info_panel_center_force_slider")]
    public double InfoPanelCenterForceSlider { get; set; } = 50;

    [JsonPropertyName("info_panel_link_distance_slider")]
    public double InfoPanelLinkDistanceSlider { get; set; }

    [JsonPropertyName("info_panel_link_force_slider")]
    public double InfoPanelLinkForceSlider { get; set; } = 50;

    // Update settings
    [JsonPropertyName("auto_check_for_updates")]
    public bool AutoCheckForUpdates { get; set; } = true;

    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    // Cloud sync auth persistence — restored on app startup after vault unlock
    [JsonPropertyName("cloud_sync_access_token")]
    public string? CloudSyncAccessToken { get; set; }

    [JsonPropertyName("cloud_sync_refresh_token")]
    public string? CloudSyncRefreshToken { get; set; }

    [JsonPropertyName("cloud_sync_user_id")]
    public long? CloudSyncUserId { get; set; }

    [JsonPropertyName("cloud_sync_config")]
    public string? CloudSyncConfigJson { get; set; }

    // Connection metadata (non-sensitive, keyed by provider e.g. "github")
    [JsonPropertyName("connection_metadata")]
    public Dictionary<string, ConnectionMetadataEntry> ConnectionMetadata { get; set; } = [];

    [JsonPropertyName("bridge_auth_token")]
    public string? BridgeAuthToken { get; set; }

    [JsonPropertyName("plugin_settings")]
    public Dictionary<string, string> PluginSettings { get; set; } = [];

    [JsonPropertyName("plugin_permissions")]
    public Dictionary<string, PluginPermissionState> PluginPermissions { get; set; } = [];

    /// <summary>
    /// Per-workspace plugin configuration (disabled plugins, permissions, order).
    /// Keyed by workspace ID.
    /// </summary>
    [JsonPropertyName("workspace_plugin_settings")]
    public Dictionary<string, WorkspacePluginConfig> WorkspacePluginSettings { get; set; } = [];
}

/// <summary>
/// Per-workspace plugin configuration: which plugins are enabled, their permissions, and nav order.
/// </summary>
public class WorkspacePluginConfig
{
    /// <summary>
    /// Whitelist of enabled plugin IDs. When non-null, takes precedence over DisabledPlugins.
    /// Null means legacy blacklist mode (all plugins enabled except those in DisabledPlugins).
    /// </summary>
    [JsonPropertyName("enabled_plugins")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HashSet<string>? EnabledPlugins { get; set; }

    [JsonPropertyName("disabled_plugins")]
    public HashSet<string> DisabledPlugins { get; set; } = [];

    [JsonPropertyName("plugin_permissions")]
    public Dictionary<string, PluginPermissionState> PluginPermissions { get; set; } = [];

    [JsonPropertyName("plugin_order")]
    public List<string> PluginOrder { get; set; } = [];

    /// <summary>
    /// Whether this config uses whitelist mode (EnabledPlugins != null).
    /// </summary>
    [JsonIgnore]
    public bool IsWhitelistMode => EnabledPlugins != null;
}

/// <summary>
/// Persisted permission grants and denials for a single plugin.
/// Keys are kebab-case permission names matching Rust's Permission serde.
/// </summary>
public class PluginPermissionState
{
    [JsonPropertyName("granted")]
    public HashSet<string> Granted { get; set; } = [];

    [JsonPropertyName("denied")]
    public HashSet<string> Denied { get; set; } = [];
}

/// <summary>
/// Persisted state for the task timer, enabling recovery after crash or restart.
/// </summary>
public class ActiveTimerState
{
    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("task_title")]
    public string TaskTitle { get; set; } = string.Empty;

    [JsonPropertyName("started_at_utc")]
    public DateTime StartedAtUtc { get; set; }

    [JsonPropertyName("elapsed_seconds_before")]
    public double ElapsedSecondsBefore { get; set; }

    [JsonPropertyName("is_paused")]
    public bool IsPaused { get; set; }

    [JsonPropertyName("active_entry_id")]
    public string ActiveEntryId { get; set; } = string.Empty;
}

/// <summary>
/// Service for loading and saving application settings.
/// </summary>
public class AppSettingsService : IAppSettingsService
{
    private static readonly ILogger _log = Log.ForContext<AppSettingsService>();
    private readonly string _settingsPath;
    private AppSettings _settings = new();
    private bool _isDirty;
    private System.Timers.Timer? _saveTimer;

    public AppSettings Settings => _settings;

    public event Action? ProfileChanged;

    public void NotifyProfileChanged() => ProfileChanged?.Invoke();

    public AppSettingsService()
    {
        var settingsFolder = DataPaths.BaseDir;
        Directory.CreateDirectory(settingsFolder);
        _settingsPath = Path.Combine(settingsFolder, "window-settings.json");

        Load();
    }

    /// <summary>
    /// Loads settings from disk.
    /// </summary>
    public void Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                _log.Debug("Settings loaded from {Path}", _settingsPath);
            }
            else
            {
                _settings = new AppSettings();
                _log.Debug("No settings file found, using defaults");
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to load settings from {Path}", _settingsPath);
            _settings = new AppSettings();
        }
    }

    /// <summary>
    /// Saves settings to disk immediately.
    /// </summary>
    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
            _isDirty = false;
            _log.Debug("Settings saved to {Path}", _settingsPath);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to save settings to {Path}", _settingsPath);
        }
    }

    /// <summary>
    /// Marks settings as dirty and schedules a debounced save.
    /// </summary>
    public void SaveDebounced()
    {
        _isDirty = true;
        _saveTimer?.Stop();
        _saveTimer?.Dispose();

        _saveTimer = new System.Timers.Timer(500); // 500ms debounce
        _saveTimer.AutoReset = false;
        _saveTimer.Elapsed += (_, _) =>
        {
            if (_isDirty)
            {
                Save();
            }
        };
        _saveTimer.Start();
    }

    /// <summary>
    /// Updates window bounds from a Window instance.
    /// </summary>
    public void UpdateWindowBounds(Window window)
    {
        if (window.WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = window.Width;
            _settings.WindowHeight = window.Height;
            _settings.WindowX = window.Position.X;
            _settings.WindowY = window.Position.Y;
        }
        _settings.WindowState = window.WindowState.ToString();
        SaveDebounced();
    }

    /// <summary>
    /// Updates the last active tab.
    /// </summary>
    public void UpdateLastActiveTab(string tabName)
    {
        if (_settings.LastActiveTab != tabName)
        {
            _settings.LastActiveTab = tabName;
            SaveDebounced();
        }
    }

    /// <summary>
    /// Updates the calendar view mode.
    /// </summary>
    public void UpdateCalendarViewMode(string viewMode)
    {
        if (_settings.CalendarViewMode != viewMode)
        {
            _settings.CalendarViewMode = viewMode;
            SaveDebounced();
        }
    }

    /// <summary>
    /// Updates the tasks view mode.
    /// </summary>
    public void UpdateTasksViewMode(string viewMode)
    {
        if (_settings.TasksViewMode != viewMode)
        {
            _settings.TasksViewMode = viewMode;
            SaveDebounced();
        }
    }

    /// <summary>
    /// Updates the tasks simple mode setting.
    /// </summary>
    public void UpdateTasksSimpleMode(bool simpleMode)
    {
        if (_settings.TasksSimpleMode != simpleMode)
        {
            _settings.TasksSimpleMode = simpleMode;
            SaveDebounced();
        }
    }

    /// <summary>
    /// Updates the plugin order.
    /// </summary>
    public void UpdatePluginOrder(IEnumerable<string> pluginIds)
    {
        var wsConfig = GetWorkspacePluginConfig();
        wsConfig.PluginOrder = pluginIds.ToList();
        SaveDebounced();
    }

    /// <summary>
    /// Applies saved settings to a Window instance.
    /// </summary>
    public void ApplyToWindow(Window window)
    {
        try
        {
            // Set size
            window.Width = Math.Max(800, _settings.WindowWidth);
            window.Height = Math.Max(600, _settings.WindowHeight);

            // Set position if saved
            if (_settings.WindowX.HasValue && _settings.WindowY.HasValue)
            {
                window.Position = new PixelPoint((int)_settings.WindowX.Value, (int)_settings.WindowY.Value);
            }

            // Set window state (but never restore as Minimized - that would make the app appear blank)
            if (Enum.TryParse<WindowState>(_settings.WindowState, out var state) && state != WindowState.Minimized)
            {
                window.WindowState = state;
            }

            _log.Debug("Applied window settings: {Width}x{Height} at ({X},{Y}), State={State}",
                _settings.WindowWidth, _settings.WindowHeight,
                _settings.WindowX, _settings.WindowY, _settings.WindowState);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to apply window settings");
        }
    }

    /// <summary>
    /// Gets the plugin config for the current workspace, creating it if needed.
    /// Existing workspaces get legacy blacklist migration; brand-new workspaces get whitelist mode.
    /// </summary>
    public WorkspacePluginConfig GetWorkspacePluginConfig()
    {
        var workspaceService = App.Services.GetRequiredService<Abstractions.IWorkspaceService>();
        var workspace = workspaceService.GetActiveWorkspace();
        var workspaceId = workspace?.Id ?? "default";

        if (!_settings.WorkspacePluginSettings.TryGetValue(workspaceId, out var config))
        {
            // First access for this workspace — migrate from global settings (legacy blacklist mode)
            config = new WorkspacePluginConfig
            {
                DisabledPlugins = new HashSet<string>(_settings.DisabledPlugins),
                PluginPermissions = _settings.PluginPermissions
                    .ToDictionary(kv => kv.Key, kv => new PluginPermissionState
                    {
                        Granted = new HashSet<string>(kv.Value.Granted),
                        Denied = new HashSet<string>(kv.Value.Denied),
                    }),
                PluginOrder = new List<string>(_settings.PluginOrder),
            };
            _settings.WorkspacePluginSettings[workspaceId] = config;
            Save();
            _log.Debug("Migrated plugin settings to workspace {WorkspaceId}", workspaceId);
        }

        return config;
    }

    /// <summary>
    /// Creates a whitelist-mode plugin config for a workspace.
    /// If no enabledPluginIds are provided, defaults to an empty set (no optional plugins).
    /// Hard-locked plugins are always activated regardless of the whitelist.
    /// </summary>
    public WorkspacePluginConfig InitializeWorkspacePluginConfig(
        string workspaceId,
        IEnumerable<string>? enabledPluginIds = null)
    {
        var enabled = enabledPluginIds != null
            ? new HashSet<string>(enabledPluginIds, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var config = new WorkspacePluginConfig
        {
            EnabledPlugins = enabled,
            PluginOrder = [],
        };

        _settings.WorkspacePluginSettings[workspaceId] = config;
        Save();
        _log.Debug("Initialized whitelist plugin config for workspace {WorkspaceId} with {Count} plugins",
            workspaceId, enabled.Count);

        return config;
    }

    /// <summary>
    /// Ensures any pending saves are flushed.
    /// </summary>
    public void Flush()
    {
        _saveTimer?.Stop();
        _saveTimer?.Dispose();
        if (_isDirty)
        {
            Save();
        }
    }
}
