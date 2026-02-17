using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.UI.Adaptive;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.Plugin;
using PrivStack.Desktop.ViewModels;
using PrivStack.Sdk.Capabilities;
using Serilog;
using NativeLib = PrivStack.Desktop.Native.NativeLibrary;

namespace PrivStack.Desktop.Views;

public partial class WasmPluginView : UserControl
{
    private static readonly ILogger _log = Serilog.Log.ForContext<WasmPluginView>();

    private Action? _refreshHandler;
    private Action<string, string>? _paletteHandler;
    private Action<string, string, string>? _pluginCommandHandler;
    private Action<bool>? _viewStateChangingHandler;
    private WasmViewModelProxy? _previousVm;
    private MainWindowViewModel? _cachedMainVm;

    public WasmPluginView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? GetMainViewModel()
    {
        _cachedMainVm ??= this.FindAncestorOfType<MainWindow>()?.DataContext as MainWindowViewModel;
        return _cachedMainVm;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        WireUp();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WireUp();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // Unsubscribe all event handlers to prevent rooting this view
        var renderer = MainRenderer;
        if (renderer != null)
        {
            if (_refreshHandler != null)
                renderer.ViewStateRefreshRequested -= _refreshHandler;
            if (_paletteHandler != null)
                renderer.PaletteRequested -= _paletteHandler;

            // Clear all delegate properties to break closure reference chains.
            // Closures capture vm, this, services etc. and can delay GC of large object graphs.
            renderer.CommandSender = null;
            renderer.ViewStateProvider = null;
            renderer.RawViewDataProvider = null;
            renderer.SettingsReader = null;
            renderer.SettingsWriter = null;
            renderer.NetworkFetcher = null;
            renderer.ImageFilePicker = null;
            renderer.GeneralFilePicker = null;
            renderer.SdkRouter = null;
            renderer.PermissionChecker = null;
            renderer.PermissionPrompter = null;
            renderer.ToastNotifier = null;
            renderer.LinkableItemSearcher = null;
            renderer.InternalLinkActivated = null;
            renderer.PrefetchRequested = null;
            renderer.PrefetchCancelled = null;
        }

        // Unsubscribe from the tracked previous VM, not current DataContext
        if (_viewStateChangingHandler != null && _previousVm != null)
            _previousVm.ViewStateChanging -= _viewStateChangingHandler;

        if (_pluginCommandHandler != null && _cachedMainVm != null)
            _cachedMainVm.CommandPaletteVM.PluginCommandRequested -= _pluginCommandHandler;

        _refreshHandler = null;
        _paletteHandler = null;
        _pluginCommandHandler = null;
        _viewStateChangingHandler = null;
        _previousVm = null;
        _cachedMainVm = null;
    }

    /// <summary>
    /// Prepares for navigation. Previously cleared shadow state here, but that caused
    /// timing issues (the posted action ran after navigation completed and cleared the
    /// NEW page's state). Shadow state is now correctly handled by page-change detection
    /// in RenderBlockEditor, so this is a no-op.
    /// </summary>
    private void ShowNavigationLoading(string? message = null)
    {
        // No-op: Shadow state is handled by page-change detection in RenderBlockEditor.
        // Don't use Dispatcher.Post here - it runs too late and clears the new page's state.
    }

    /// <summary>
    /// Called after navigation completes. Currently a no-op since we keep the view
    /// visible during navigation, but can be extended if needed.
    /// </summary>
    private void HideNavigationLoading()
    {
        // No-op: we keep the current view visible until new content renders
    }

    private void WireUp()
    {
        // Find the renderer (now inside a Grid wrapper)
        var renderer = MainRenderer;
        if (renderer == null || DataContext is not WasmViewModelProxy vm)
            return;

        // If switching to a different plugin VM, tear down the old control tree
        // and caches immediately so memory is freed rather than waiting for GC.
        if (_previousVm != null && _previousVm != vm)
        {
            renderer.ResetForPluginSwitch();
        }

        // Wire up partial refresh notification - this allows the renderer to prepare
        // for a partial update when navigating within the same plugin.
        // Unsubscribe from the PREVIOUS vm (not the new one) to avoid accumulating handlers.
        if (_viewStateChangingHandler != null && _previousVm != null)
            _previousVm.ViewStateChanging -= _viewStateChangingHandler;

        _viewStateChangingHandler = (usePartialRefresh) =>
        {
            if (usePartialRefresh)
            {
                renderer.RequestPartialRefresh();
            }
        };
        vm.ViewStateChanging += _viewStateChangingHandler;
        _previousVm = vm;

        renderer.CommandSender = (pluginId, commandName, argsJson) =>
        {
            _log.Debug("Pipeline: CommandSender → FFI cmd={Cmd} args={Args}",
                commandName, argsJson.Length > 200 ? argsJson[..200] + "..." : argsJson);
            var resultPtr = NativeLib.PluginSendCommand(pluginId, commandName, argsJson);
            if (resultPtr != nint.Zero)
            {
                var result = Marshal.PtrToStringUTF8(resultPtr);
                _log.Debug("Pipeline: FFI result for {Cmd}: {Result}",
                    commandName, result?.Length > 300 ? result[..300] + "..." : result);

                // Check if result contains open_palette directive
                if (!string.IsNullOrEmpty(result))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(result);
                        if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
                            dataEl.TryGetProperty("open_palette", out var paletteEl))
                        {
                            var paletteId = paletteEl.GetString();
                            if (!string.IsNullOrEmpty(paletteId))
                            {
                                _log.Debug("Pipeline: Command result requests palette open: {PaletteId}", paletteId);
                                renderer.RaisePaletteRequested(pluginId, paletteId);
                            }
                        }
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // Not valid JSON, ignore
                    }
                }

                NativeLib.FreeString(resultPtr);
            }
            else
            {
                _log.Warning("Pipeline: FFI returned null for {Cmd}", commandName);
            }
        };

        // Wire up settings persistence for pane widths
        var settingsService = App.Services.GetService<IAppSettingsService>();
        if (settingsService != null)
        {
            renderer.SettingsReader = (pluginId, key) =>
            {
                var fullKey = $"{pluginId}.{key}";
                return settingsService.Settings.PluginSettings.GetValueOrDefault(fullKey);
            };
            renderer.SettingsWriter = (pluginId, key, value) =>
            {
                var fullKey = $"{pluginId}.{key}";
                settingsService.Settings.PluginSettings[fullKey] = value;
                settingsService.SaveDebounced();
            };
        }

        // Wire up permission-checked network fetching through the Rust core
        var pluginId = vm.PluginId;
        renderer.NetworkFetcher = url => Task.Run(() =>
            NativeLib.PluginFetchUrlManaged(pluginId, url));

        // Wire up image storage path from workspace service
        var workspaceService = App.Services.GetService<IWorkspaceService>();
        if (workspaceService != null)
        {
            var activeWorkspace = workspaceService.GetActiveWorkspace();
            if (activeWorkspace != null)
            {
                var workspaceDir = System.IO.Path.GetDirectoryName(workspaceService.GetDataPath(activeWorkspace.Id))!;
                var imageDir = System.IO.Path.Combine(workspaceDir, "Files", "Notes", "Images");
                System.IO.Directory.CreateDirectory(imageDir);
                renderer.ImageStoragePath = imageDir;
            }
        }

        // Wire up file picker for image blocks
        renderer.ImageFilePicker = async () =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null) return null;

            var imageFilter = new Avalonia.Platform.Storage.FilePickerFileType("Images")
            {
                Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.svg" },
            };
            var result = await topLevel.StorageProvider.OpenFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Choose an image",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { imageFilter },
                });
            return result.Count > 0 ? result[0].TryGetLocalPath() : null;
        };

        // Wire up SDK routing for host-side entity creation (Archive file import)
        renderer.SdkRouter = (pluginId, sdkMessageJson) =>
        {
            var resultPtr = NativeLib.PluginRouteSdk(pluginId, sdkMessageJson);
            if (resultPtr != nint.Zero)
                NativeLib.FreeString(resultPtr);
        };

        // Wire up general file picker for host-intercepted imports (Archive plugin, multi-select)
        renderer.GeneralFilePicker = async () =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null) return Array.Empty<string>();

            var result = await topLevel.StorageProvider.OpenFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Import Files",
                    AllowMultiple = true,
                });
            var paths = new List<string>();
            foreach (var file in result)
            {
                var path = file.TryGetLocalPath();
                if (!string.IsNullOrEmpty(path))
                    paths.Add(path);
            }
            return paths;
        };

        // Wire up permission checking for host-intercepted operations
        renderer.PermissionChecker = (pId, capability) =>
        {
            var wsConfig = settingsService?.GetWorkspacePluginConfig();
            if (wsConfig is null) return false;
            return wsConfig.PluginPermissions.TryGetValue(pId, out var state)
                && state.Granted.Contains(capability);
        };

        // Wire up interactive permission prompt (Allow/Deny banner)
        renderer.PermissionPrompter = (pId, pluginName, capability, capDisplayName) =>
        {
            return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                ShowPermissionPrompt(pId, pluginName, capability, capDisplayName, settingsService));
        };

        // Wire up toast notifications via a temporary overlay on this control
        renderer.ToastNotifier = (message, isError) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ShowToast(message, isError);
            });
        };

        // Get the cached link provider service and plugin registry for native plugin search
        var linkProviderCache = App.Services.GetService<LinkProviderCacheService>();
        var pluginRegistry = App.Services.GetService<IPluginRegistry>();

        // Wire up link picker search - searches both FFI (WASM) and native C# plugins
        renderer.LinkableItemSearcher = async (query, maxResults) =>
        {
            var results = new List<PrivStack.UI.Adaptive.Models.LinkableItemResult>();

            // Parse prefix filter (e.g., "journal:", "notes:", "tasks:")
            // Also supports just the type name without colon (e.g., "journal" shows all journal entries)
            string? filterLinkType = null;
            var searchQuery = query;
            var providers = linkProviderCache?.GetAll() ?? [];

            _log.Debug("LinkSearch: Query='{Query}', Cache has {Count} providers: [{Types}]",
                query, providers.Count, string.Join(", ", providers.Select(p => p.LinkType)));

            var colonIndex = query.IndexOf(':');
            if (colonIndex > 0)
            {
                var prefix = query[..colonIndex].ToLowerInvariant();
                // Check if prefix matches a known link type
                var matchedProvider = providers.FirstOrDefault(p =>
                    p.LinkType.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                    p.DisplayName.Equals(prefix, StringComparison.OrdinalIgnoreCase));

                if (matchedProvider != null)
                {
                    filterLinkType = matchedProvider.LinkType;
                    searchQuery = colonIndex < query.Length - 1 ? query[(colonIndex + 1)..].TrimStart() : "";
                    _log.Debug("LinkSearch: Matched prefix '{Prefix}' to LinkType '{LinkType}', searchQuery='{SearchQuery}'",
                        prefix, filterLinkType, searchQuery);
                }
                else
                {
                    _log.Debug("LinkSearch: No match for prefix '{Prefix}'", prefix);
                }
            }
            else
            {
                // No colon - check if entire query matches a link type or display name
                var trimmedQuery = query.Trim().ToLowerInvariant();
                var exactMatch = providers.FirstOrDefault(p =>
                    p.LinkType.Equals(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
                    p.DisplayName.Equals(trimmedQuery, StringComparison.OrdinalIgnoreCase));

                if (exactMatch != null)
                {
                    filterLinkType = exactMatch.LinkType;
                    searchQuery = ""; // Show all items of this type
                    _log.Debug("LinkSearch: Exact match for '{Query}' to LinkType '{LinkType}'", trimmedQuery, filterLinkType);
                }
                else
                {
                    _log.Debug("LinkSearch: No exact match for '{Query}'", trimmedQuery);
                }
            }

            // Build icon lookup dictionary for link types
            var iconByLinkType = (linkProviderCache?.GetAll() ?? [])
                .ToDictionary(p => p.LinkType, p => p.Icon, StringComparer.OrdinalIgnoreCase);

            // Search native C# plugins via ILinkableItemProvider
            if (pluginRegistry != null)
            {
                var nativeProviders = pluginRegistry.GetCapabilityProviders<ILinkableItemProvider>();
                foreach (var provider in nativeProviders)
                {
                    // Skip if filtering to a specific type that doesn't match
                    if (filterLinkType != null && !provider.LinkType.Equals(filterLinkType, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        var items = await provider.SearchItemsAsync(searchQuery, maxResults);
                        foreach (var item in items)
                        {
                            results.Add(new PrivStack.UI.Adaptive.Models.LinkableItemResult(
                                Id: item.Id,
                                LinkType: item.LinkType,
                                LinkTypeDisplayName: provider.LinkTypeDisplayName,
                                Title: item.Title,
                                Subtitle: item.Subtitle,
                                Icon: provider.LinkTypeIcon));

                            if (results.Count >= maxResults) break;
                        }
                    }
                    catch { /* ignore search errors from individual providers */ }

                    if (results.Count >= maxResults) break;
                }
            }

            // If we already have enough results from native plugins, return early
            if (results.Count >= maxResults)
                return (IReadOnlyList<PrivStack.UI.Adaptive.Models.LinkableItemResult>)results;

            // Also search FFI (WASM plugins) if not filtering to a native-only type
            var resultPtr = NativeLib.PluginSearchItems(searchQuery, maxResults * 2);
            if (resultPtr != nint.Zero)
            {
                var json = Marshal.PtrToStringUTF8(resultPtr);
                NativeLib.FreeString(resultPtr);

                if (!string.IsNullOrEmpty(json))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            var linkType = item.GetProperty("link_type").GetString() ?? "";

                            // Apply link type filter if specified
                            if (filterLinkType != null && !linkType.Equals(filterLinkType, StringComparison.OrdinalIgnoreCase))
                                continue;

                            var displayName = linkProviderCache?.GetDisplayNameForLinkType(linkType) ?? linkType;
                            var icon = iconByLinkType.TryGetValue(linkType, out var typeIcon) ? typeIcon : null;

                            results.Add(new PrivStack.UI.Adaptive.Models.LinkableItemResult(
                                Id: item.GetProperty("id").GetString() ?? "",
                                LinkType: linkType,
                                LinkTypeDisplayName: displayName,
                                Title: item.GetProperty("title").GetString() ?? "",
                                Subtitle: item.TryGetProperty("subtitle", out var sub) ? sub.GetString() : null,
                                Icon: icon));

                            if (results.Count >= maxResults) break;
                        }
                    }
                    catch { /* ignore parse errors */ }
                }
            }

            return (IReadOnlyList<PrivStack.UI.Adaptive.Models.LinkableItemResult>)results;
        };

        // Wire up internal link navigation (privstack:// URIs)
        renderer.InternalLinkActivated = (linkType, itemId) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _log.Debug("InternalLink: [T+{T}ms] START linkType={LinkType}, itemId={ItemId}, currentPlugin={CurrentPlugin}",
                sw.ElapsedMilliseconds, linkType, itemId, pluginId);

            // IMMEDIATELY clear shadow state for CURRENT page (synchronously, not via Post)
            // This must happen before navigation starts so we don't clear the NEW page's state
            if (MainRenderer != null)
            {
                MainRenderer.ClearShadowState();
                _log.Debug("InternalLink: [T+{T}ms] Cleared shadow state for current page", sw.ElapsedMilliseconds);
            }

            // Invalidate prefetch cache for the CURRENT page (not target) to ensure fresh data on back-navigation
            var prefetchSvc = App.Services.GetService<ViewStatePrefetchService>();
            if (vm.CurrentEntityId != null)
            {
                prefetchSvc?.Invalidate(pluginId, vm.CurrentEntityId);
                _log.Debug("InternalLink: [T+{T}ms] Invalidated prefetch cache for current page {CurrentEntity}",
                    sw.ElapsedMilliseconds, vm.CurrentEntityId);
            }

            // Resolve linkType -> target pluginId via cached link providers
            var targetPluginId = linkProviderCache?.GetPluginIdForLinkType(linkType);
            _log.Debug("InternalLink: [T+{T}ms] Cache lookup: linkType={LinkType} -> pluginId={PluginId}",
                sw.ElapsedMilliseconds, linkType, targetPluginId ?? "(null)");

            // Fallback: if provider lookup didn't match, search for the item by ID
            // to discover which plugin owns it
            if (targetPluginId == null)
            {
                _log.Debug("InternalLink: [T+{T}ms] Provider lookup failed, searching by item ID={ItemId}", sw.ElapsedMilliseconds, itemId);
                var searchPtr = NativeLib.PluginSearchItems(itemId, 1);
                _log.Debug("InternalLink: [T+{T}ms] Search FFI returned ptr={Ptr}", sw.ElapsedMilliseconds, searchPtr);
                if (searchPtr != nint.Zero)
                {
                    var searchJson = Marshal.PtrToStringUTF8(searchPtr);
                    NativeLib.FreeString(searchPtr);
                    if (!string.IsNullOrEmpty(searchJson))
                    {
                        try
                        {
                            using var searchDoc = System.Text.Json.JsonDocument.Parse(searchJson);
                            foreach (var result in searchDoc.RootElement.EnumerateArray())
                            {
                                var rid = result.GetProperty("id").GetString() ?? "";
                                if (string.Equals(rid, itemId, StringComparison.OrdinalIgnoreCase))
                                {
                                    targetPluginId = result.TryGetProperty("plugin_id", out var pid)
                                        ? pid.GetString() : null;
                                    _log.Debug("InternalLink: [T+{T}ms] Found via search: pluginId={PluginId}", sw.ElapsedMilliseconds, targetPluginId);
                                    break;
                                }
                            }
                        }
                        catch { /* ignore parse errors */ }
                    }
                }
            }

            // Helper to handle navigation completion (hide loading overlay)
            void OnNavigationComplete(Task navTask)
            {
                _log.Debug("InternalLink: Navigation task completed, hiding loading overlay");
                HideNavigationLoading();
            }

            if (targetPluginId != null && targetPluginId != pluginId)
            {
                // Cross-plugin navigation: switch tab and navigate via MainWindowViewModel
                _log.Debug("InternalLink: [T+{T}ms] CROSS-PLUGIN navigation: {Current} -> {Target}, itemId={ItemId}",
                    sw.ElapsedMilliseconds, pluginId, targetPluginId, itemId);
                var mainVm = GetMainViewModel();
                if (mainVm != null)
                {
                    _log.Debug("InternalLink: [T+{T}ms] Calling NavigateToPluginItemAsync", sw.ElapsedMilliseconds);
                    mainVm.NavigateToPluginItemAsync(targetPluginId, itemId)
                        .ContinueWith(OnNavigationComplete, TaskScheduler.FromCurrentSynchronizationContext());
                    _log.Debug("InternalLink: [T+{T}ms] NavigateToPluginItemAsync call returned (async)", sw.ElapsedMilliseconds);
                }
                else
                {
                    _log.Warning("InternalLink: [T+{T}ms] MainViewModel is NULL - cannot navigate!", sw.ElapsedMilliseconds);
                    HideNavigationLoading();
                }
            }
            else
            {
                // Same plugin or unknown — use navigate_to_item via the FFI,
                // which calls the plugin's own NavigableItemProvider implementation
                var resolvedPlugin = targetPluginId ?? pluginId;
                _log.Debug("InternalLink: [T+{T}ms] SAME-PLUGIN navigation: plugin={Plugin}, itemId={ItemId}",
                    sw.ElapsedMilliseconds, resolvedPlugin, itemId);
                var mainVm = GetMainViewModel();
                if (mainVm != null)
                {
                    _log.Debug("InternalLink: [T+{T}ms] Calling NavigateToPluginItemAsync (same plugin)", sw.ElapsedMilliseconds);
                    mainVm.NavigateToPluginItemAsync(resolvedPlugin, itemId)
                        .ContinueWith(OnNavigationComplete, TaskScheduler.FromCurrentSynchronizationContext());
                    _log.Debug("InternalLink: [T+{T}ms] NavigateToPluginItemAsync call returned (async)", sw.ElapsedMilliseconds);
                }
                else
                {
                    // Fallback: direct command
                    _log.Debug("InternalLink: [T+{T}ms] MainViewModel NULL, using fallback direct command", sw.ElapsedMilliseconds);
                    var argsJson = System.Text.Json.JsonSerializer.Serialize(new { id = itemId });
                    var resultPtr = NativeLib.PluginSendCommand(pluginId, "select_page", argsJson);
                    if (resultPtr != nint.Zero)
                        NativeLib.FreeString(resultPtr);
                    _log.Debug("InternalLink: [T+{T}ms] Direct command sent, refreshing view state", sw.ElapsedMilliseconds);
                    vm.RefreshViewState();
                    HideNavigationLoading();
                }
            }
            _log.Debug("InternalLink: [T+{T}ms] END", sw.ElapsedMilliseconds);
        };

        // Wire up hover prefetch for internal links (backlinks, graph nodes, etc.)
        var prefetchService = App.Services.GetService<ViewStatePrefetchService>();
        if (prefetchService != null)
        {
            renderer.PrefetchRequested = (linkType, itemId) =>
            {
                // Resolve linkType -> pluginId via cached link providers
                var targetPluginId = linkProviderCache?.GetPluginIdForLinkType(linkType);
                if (targetPluginId != null)
                    prefetchService.RequestPrefetch(targetPluginId, itemId);
            };

            renderer.PrefetchCancelled = (linkType, itemId) =>
            {
                // Resolve linkType -> pluginId via cached link providers
                var targetPluginId = linkProviderCache?.GetPluginIdForLinkType(linkType);
                if (targetPluginId != null)
                    prefetchService.CancelPrefetch(targetPluginId, itemId);
            };
        }

        // Provide fresh view state JSON for block panel refresh (split view)
        renderer.ViewStateProvider = () =>
        {
            var vs = vm.GetRenderedViewState();
            _log.Debug("Pipeline: ViewStateProvider called, JSON length={Len}", vs?.Length ?? 0);
            return vs;
        };

        // Provide raw plugin view data (pre-template) for reading plugin state
        renderer.RawViewDataProvider = () => vm.GetRawViewData();

        // Unsubscribe previous handlers to avoid duplicate subscriptions
        if (_refreshHandler != null)
            renderer.ViewStateRefreshRequested -= _refreshHandler;

        _refreshHandler = () =>
        {
            _log.Debug("Pipeline: ViewStateRefreshRequested → RefreshViewState()");
            vm.RefreshViewState();
        };
        renderer.ViewStateRefreshRequested += _refreshHandler;

        // Wire palette requests from __open_palette pseudo-command
        if (_paletteHandler != null)
            renderer.PaletteRequested -= _paletteHandler;

        _paletteHandler = (pId, paletteId) =>
        {
            var mainVm = GetMainViewModel();
            mainVm?.CommandPaletteVM.OpenPluginPalette(pId, paletteId);
        };
        renderer.PaletteRequested += _paletteHandler;

        // Wire PluginCommandRequested — needs MainWindowViewModel from visual tree
        WirePluginCommandHandler(vm, pluginId);
    }

    private void WirePluginCommandHandler(WasmViewModelProxy vm, string pluginId)
    {
        var mainViewModel = GetMainViewModel();
        if (mainViewModel is null) return;
        var renderer = Content as AdaptiveViewRenderer;

        // Unsubscribe previous handler
        if (_pluginCommandHandler != null)
            mainViewModel.CommandPaletteVM.PluginCommandRequested -= _pluginCommandHandler;

        _pluginCommandHandler = (targetPluginId, command, argsJson) =>
        {
            if (targetPluginId != pluginId) return;

            // Inject after_id for add_block commands from palette
            if (command == "add_block" && renderer?.FocusedBlockId is { } focusedId)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
                    if (!doc.RootElement.TryGetProperty("after_id", out _))
                    {
                        var dict = new Dictionary<string, object?>();
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            dict[prop.Name] = prop.Value.ValueKind switch
                            {
                                System.Text.Json.JsonValueKind.String => prop.Value.GetString(),
                                System.Text.Json.JsonValueKind.Number => prop.Value.GetInt64(),
                                System.Text.Json.JsonValueKind.True => true,
                                System.Text.Json.JsonValueKind.False => false,
                                _ => prop.Value.GetRawText(),
                            };
                        }
                        dict["after_id"] = focusedId;
                        argsJson = System.Text.Json.JsonSerializer.Serialize(dict);
                    }
                }
                catch { /* use original argsJson */ }
            }

            _log.Debug("Pipeline: PaletteCommand → FFI cmd={Cmd} args={Args}",
                command, argsJson.Length > 200 ? argsJson[..200] + "..." : argsJson);
            var resultPtr = NativeLib.PluginSendCommand(targetPluginId, command, argsJson);
            if (resultPtr != nint.Zero)
            {
                var result = Marshal.PtrToStringUTF8(resultPtr);
                _log.Debug("Pipeline: PaletteCommand FFI result for {Cmd}: {Result}",
                    command, result?.Length > 300 ? result[..300] + "..." : result);
                NativeLib.FreeString(resultPtr);
            }

            _log.Debug("Pipeline: PaletteCommand → RefreshViewState()");
            vm.RefreshViewState();
        };
        mainViewModel.CommandPaletteVM.PluginCommandRequested += _pluginCommandHandler;
    }

    /// <summary>
    /// Shows an interactive permission prompt banner with Allow/Deny buttons.
    /// Returns true if user clicks Allow, false if Deny or dismisses.
    /// Persists the choice to settings so Allow is permanent and Deny re-prompts each time.
    /// </summary>
    private Task<bool> ShowPermissionPrompt(
        string pluginId, string pluginName, string capability,
        string capDisplayName, IAppSettingsService? settingsService)
    {
        var tcs = new TaskCompletionSource<bool>();

        var renderer = Content is Panel p && p.Children.Count > 0
            ? p.Children[0] as AdaptiveViewRenderer
            : Content as AdaptiveViewRenderer;
        if (renderer is null)
        {
            tcs.SetResult(false);
            return tcs.Task;
        }

        // Ensure we have an overlay panel
        Panel rootPanel;
        if (Content is Panel existingPanel && existingPanel.Children.Contains(renderer))
        {
            rootPanel = existingPanel;
        }
        else
        {
            rootPanel = new Panel();
            Content = null;
            rootPanel.Children.Add(renderer);
            Content = rootPanel;
        }

        var messageBlock = new TextBlock
        {
            Text = $"{pluginName} is requesting permission for {capDisplayName}.",
            Foreground = GetThemeBrush("ThemeTextOnAccentBrush") ?? Avalonia.Media.Brushes.White,
            FontSize = ThemeDouble("ThemeFontSizeSmMd", 13),
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var allowBtn = new Button
        {
            Content = "Allow",
            FontSize = ThemeDouble("ThemeFontSizeSm", 12),
            Foreground = GetThemeBrush("ThemeTextOnAccentBrush") ?? Avalonia.Media.Brushes.White,
            Background = GetThemeBrush("ThemeSuccessBrush") ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(40, 140, 70)),
            Padding = new Avalonia.Thickness(14, 6),
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
            CornerRadius = new Avalonia.CornerRadius(4),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        var denyBtn = new Button
        {
            Content = "Deny",
            FontSize = ThemeDouble("ThemeFontSizeSm", 12),
            Foreground = GetThemeBrush("ThemeTextOnAccentBrush") ?? Avalonia.Media.Brushes.White,
            Background = GetThemeBrush("ThemeDangerBrush") ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(160, 50, 50)),
            Padding = new Avalonia.Thickness(14, 6),
            Margin = new Avalonia.Thickness(4, 0, 0, 0),
            CornerRadius = new Avalonia.CornerRadius(4),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children = { allowBtn, denyBtn },
        };

        var contentPanel = new DockPanel();
        DockPanel.SetDock(buttonPanel, Avalonia.Controls.Dock.Right);
        contentPanel.Children.Add(buttonPanel);
        contentPanel.Children.Add(messageBlock);

        var banner = new Border
        {
            Background = GetThemeBrush("ThemeSurfaceElevatedBrush") ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(50, 50, 60)),
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(16, 12),
            Margin = new Avalonia.Thickness(16, 0, 16, 16),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            MaxWidth = 550,
            Child = contentPanel,
        };

        void Cleanup(bool granted)
        {
            rootPanel.Children.Remove(banner);

            if (granted && settingsService != null)
            {
                var wsConfig = settingsService.GetWorkspacePluginConfig();
                if (!wsConfig.PluginPermissions.TryGetValue(pluginId, out var permState))
                {
                    permState = new PluginPermissionState();
                    wsConfig.PluginPermissions[pluginId] = permState;
                }
                permState.Granted.Add(capability);
                permState.Denied.Remove(capability);
                settingsService.Save();
            }
            // Deny: don't persist — re-prompt every time

            tcs.TrySetResult(granted);
        }

        allowBtn.Click += (_, _) => Cleanup(true);
        denyBtn.Click += (_, _) => Cleanup(false);

        rootPanel.Children.Add(banner);
        return tcs.Task;
    }

    /// <summary>
    /// Shows a temporary toast notification overlaid at the bottom of the plugin view.
    /// Auto-dismisses after 3 seconds.
    /// </summary>
    private void ShowToast(string message, bool isError)
    {
        // Find or create an overlay panel on this control
        var renderer = Content as AdaptiveViewRenderer;
        if (renderer is null) return;

        // Wrap the renderer in a panel if not already wrapped
        if (Content is not Panel rootPanel)
        {
            rootPanel = new Panel();
            Content = null;
            rootPanel.Children.Add(renderer);
            Content = rootPanel;
        }

        var toast = new Border
        {
            Background = isError
                ? GetThemeBrush("ThemeDangerBrush") ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(180, 40, 40))
                : GetThemeBrush("ThemeSuccessBrush") ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(40, 140, 70)),
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(16, 10),
            Margin = new Avalonia.Thickness(0, 0, 0, 16),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Opacity = 0.95,
            Child = new TextBlock
            {
                Text = message,
                Foreground = GetThemeBrush("ThemeTextOnAccentBrush") ?? Avalonia.Media.Brushes.White,
                FontSize = ThemeDouble("ThemeFontSizeSmMd", 13),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                MaxWidth = 400,
            },
        };

        rootPanel.Children.Add(toast);

        // Auto-dismiss after 3 seconds
        var timer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            rootPanel.Children.Remove(toast);
        };
        timer.Start();
    }

    private static Avalonia.Media.IBrush? GetThemeBrush(string resourceKey)
    {
        return Avalonia.Application.Current?.FindResource(resourceKey) as Avalonia.Media.IBrush;
    }

    private static double ThemeDouble(string key, double fallback)
    {
        var app = Avalonia.Application.Current;
        if (app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var v) == true && v is double d)
            return d;
        return fallback;
    }
}
