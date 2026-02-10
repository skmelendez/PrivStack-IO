// ============================================================================
// File: AdaptiveViewRenderer.cs (FluidUI)
// Description: Generic JSON-to-Avalonia renderer for Wasm plugin view states.
//              Recursively builds Avalonia controls from a declarative JSON
//              component tree output by plugins.
//
//              All visual properties (font sizes, colors, spacing, panel widths)
//              are sourced from the application's theme resources so they
//              respond to theme changes, accessibility font scaling, and
//              viewport resizing. No hardcoded pixel values.
// ============================================================================

using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using InlineStyle = PrivStack.UI.Adaptive.Controls.RichTextEditor.InlineStyle;
using TextColor = PrivStack.UI.Adaptive.Controls.RichTextEditor.TextColor;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PrivStack.UI.Adaptive.Controls;
using PrivStack.UI.Adaptive.Services;
using Avalonia.VisualTree;
using Serilog;

namespace PrivStack.UI.Adaptive;

/// <summary>
/// Renders a JSON component tree into live Avalonia controls.
/// Plugins output a JSON view state describing layout + data + interactions.
///
/// Supported component types:
/// - Layout: stack, grid, split_pane, multi_pane, scroll
/// - Data: list, text, badge, icon, image
/// - Input: button, icon_button, text_input, toggle, dropdown
/// - Composite: toolbar, tab_bar, card, modal, status_bar, detail_header,
///              section_header, article_item, feed_item, page_item, spacer
/// - Feedback: loading, empty_state, error
/// - Rich: html_content, block_editor, graph_view, backlinks_list
/// </summary>
public sealed class AdaptiveViewRenderer : UserControl
{
    private static readonly ILogger _log = Log.ForContext<AdaptiveViewRenderer>();

    private const int MaxRecentEmojis = 50;
    private static readonly List<string> _recentEmojis = [];

    /// <summary>
    /// Called whenever the recent emoji list changes. The host app should
    /// persist the list (e.g. to AppSettings) and restore it at startup
    /// via <see cref="LoadRecentEmojis"/>.
    /// </summary>
    public static Action<IReadOnlyList<string>>? RecentEmojisSaved { get; set; }

    /// <summary>
    /// Seed the recent emojis list from persisted storage at startup.
    /// </summary>
    public static void LoadRecentEmojis(IEnumerable<string> emojis)
    {
        _recentEmojis.Clear();
        _recentEmojis.AddRange(emojis.Take(MaxRecentEmojis));
    }

    /// Subscription to a font size resource — used to detect scale changes
    /// and trigger a full re-render so the plugin view stays in sync.
    private IDisposable? _fontScaleSubscription;
    private double _lastObservedFontSize;

    /// Debounced refresh timer — fires ViewStateRefreshRequested after a short
    /// delay so rapid control interactions (checkbox clicks, date picks) batch
    /// into a single re-render instead of tearing down the view on every tap.
    private DispatcherTimer? _deferredRefreshTimer;

    public static readonly StyledProperty<string?> ViewStateJsonProperty =
        AvaloniaProperty.Register<AdaptiveViewRenderer, string?>(nameof(ViewStateJson));

    public static readonly StyledProperty<string?> PluginIdProperty =
        AvaloniaProperty.Register<AdaptiveViewRenderer, string?>(nameof(PluginId));

    /// <summary>
    /// JSON view state from the plugin. Setting this triggers a full re-render.
    /// </summary>
    public string? ViewStateJson
    {
        get => GetValue(ViewStateJsonProperty);
        set => SetValue(ViewStateJsonProperty, value);
    }

    /// <summary>
    /// When true, the next view state change will attempt a partial refresh
    /// (updating existing controls) rather than a full rebuild.
    /// Set by host for same-plugin navigation.
    /// </summary>
    public bool UsePartialRefresh { get; set; }

    /// <summary>
    /// Call this before setting ViewStateJson to request a partial refresh
    /// (updating existing controls rather than full rebuild).
    /// Returns true if partial refresh is possible.
    /// </summary>
    public bool RequestPartialRefresh()
    {
        // Partial refresh is possible if we have existing content and tracked controls
        if (Content != null && _activeGraphControl != null)
        {
            UsePartialRefresh = true;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Clears the current view content immediately for navigation transitions.
    /// Called when user clicks to navigate to a different page to prevent stale content showing.
    /// </summary>
    public void ClearForNavigation()
    {
        _log.Debug("ClearForNavigation: Clearing content, shadow state, and graph control");

        // Clear shadow state for current page
        _shadowState?.Clear();

        // Clear the graph control reference
        _activeGraphControl = null;

        // Clear tracked page list titles and icons
        _pageListTitles.Clear();
        _pageListIcons.Clear();

        // Clear block metadata and table filter state
        _blockMeta.Clear();
        _tableFilterState.Clear();

        // Clear the content to show loading state
        Content = null;

        // Reset partial refresh flag
        UsePartialRefresh = false;
    }

    /// <summary>
    /// Clears internal shadow state and caches WITHOUT clearing the visual content.
    /// Used during navigation to prevent stale state issues while keeping the current
    /// page visible until the new content is ready.
    /// </summary>
    public void ClearShadowState()
    {
        _log.Debug("ClearShadowState: Clearing shadow state and caches (keeping visual content)");

        // Clear shadow state for current page
        _shadowState?.Clear();

        // Clear the graph control reference
        _activeGraphControl = null;

        // Clear tracked page list titles and icons
        _pageListTitles.Clear();
        _pageListIcons.Clear();

        // Clear block metadata and table filter state
        _blockMeta.Clear();
        _tableFilterState.Clear();

        // Reset partial refresh flag - new page should do full render
        UsePartialRefresh = false;
    }

    /// <summary>
    /// Explicitly tears down the old control tree and caches when switching
    /// between Wasm plugins. Called by WasmPluginView.WireUp() on DataContext
    /// change so the previous plugin's heavy control tree is freed immediately
    /// instead of waiting for GC.
    ///
    /// Does NOT touch _syncTimer or _shadowState — those are handled by the
    /// normal render path in OnViewStateChanged / RenderBlockEditor.
    /// </summary>
    public void ResetForPluginSwitch()
    {
        _log.Information("ResetForPluginSwitch: tearing down current state, Content={HasContent}, PluginId={PluginId}",
            Content != null, PluginId);

        // Flush any pending editor text changes so nothing is lost
        FlushAllEditors();

        // Stop the deferred refresh timer (prevents stale Tick firing into new plugin)
        _deferredRefreshTimer?.Stop();
        _deferredRefreshTimer = null;

        // Detach page drag artifacts
        if (_pageDragOverlay?.Parent is Panel ovp)
            ovp.Children.Remove(_pageDragOverlay);
        if (_pageDropIndicator?.Parent is Panel pip)
            pip.Children.Remove(_pageDropIndicator);
        _pageDragOverlay = null;
        _pageDropIndicator = null;
        _pageListPanel = null;
        if (_isPageDragging)
            CleanUpPageDrag();

        // Clear all internal caches
        _activeGraphControl = null;
        _pageListTitles.Clear();
        _pageListIcons.Clear();
        _blockMeta.Clear();
        _tableFilterState.Clear();
        _headerViewToggleHost = null;
        _pendingGraphHydration = null;
        _blockPanel = null;
        _saveStatusText = null;

        // Drop the old control tree so it can be collected
        Content = null;

        UsePartialRefresh = false;
    }

    /// <summary>
    /// Plugin ID for routing user interactions back as commands.
    /// </summary>
    public string? PluginId
    {
        get => GetValue(PluginIdProperty);
        set => SetValue(PluginIdProperty, value);
    }

    /// <summary>
    /// Fired when the view state changes and needs to be re-fetched.
    /// </summary>
    public event Action? ViewStateRefreshRequested;

    /// <summary>
    /// Fired when a plugin requests opening a command palette via __open_palette.
    /// Args: (pluginId, paletteId)
    /// </summary>
    public event Action<string, string>? PaletteRequested;

    /// <summary>
    /// Raises the PaletteRequested event programmatically.
    /// Used when a command result contains an open_palette directive.
    /// </summary>
    public void RaisePaletteRequested(string pluginId, string paletteId)
    {
        PaletteRequested?.Invoke(pluginId, paletteId);
    }

    /// <summary>
    /// Delegate for sending commands to the plugin host.
    /// Must be set by the consuming application before interaction works.
    /// Signature: (pluginId, commandName, argsJson) => void
    /// </summary>
    public Action<string, string, string>? CommandSender { get; set; }

    /// <summary>
    /// Delegate that returns the current rendered view state JSON without
    /// triggering a re-render. Used by split view to refresh blocks from backend.
    /// Signature: () => viewStateJson or null
    /// </summary>
    public Func<string?>? ViewStateProvider { get; set; }

    /// <summary>
    /// Provides the raw plugin view data JSON (before template rendering).
    /// Used to read plugin state like selected_folder_id for file import.
    /// </summary>
    public Func<string?>? RawViewDataProvider { get; set; }

    /// <summary>
    /// Delegate for reading a persisted plugin setting by key.
    /// Signature: (pluginId, settingKey) => value or null
    /// </summary>
    public Func<string, string, string?>? SettingsReader { get; set; }

    /// <summary>
    /// Delegate for fetching a URL through the permission-checked network layer.
    /// All HTTP requests from the renderer MUST go through this delegate.
    /// Signature: (url) => byte[] response body or null
    /// </summary>
    public Func<string, Task<byte[]?>>? NetworkFetcher { get; set; }

    /// <summary>
    /// Delegate for writing a persisted plugin setting.
    /// Signature: (pluginId, settingKey, value) => void
    /// </summary>
    public Action<string, string, string>? SettingsWriter { get; set; }

    /// <summary>
    /// Path to the notes image storage directory. Caller ensures it exists.
    /// </summary>
    public string? ImageStoragePath { get; set; }

    /// <summary>
    /// Delegate to open a file picker. Returns selected file path or null.
    /// </summary>
    public Func<Task<string?>>? ImageFilePicker { get; set; }

    /// <summary>
    /// Delegate to open a general file picker (any file type, multi-select).
    /// Returns list of selected file paths (empty if cancelled).
    /// Used by the Archive plugin's host-intercepted import flow.
    /// </summary>
    public Func<Task<IReadOnlyList<string>>>? GeneralFilePicker { get; set; }

    /// <summary>
    /// Delegate to route an SDK message for a plugin (create/update/delete entities).
    /// Signature: (pluginId, sdkMessageJson) => void
    /// </summary>
    public Action<string, string>? SdkRouter { get; set; }

    /// <summary>
    /// Delegate to check whether a plugin has been granted a specific capability.
    /// Signature: (pluginId, capability) => bool
    /// </summary>
    public Func<string, string, bool>? PermissionChecker { get; set; }

    /// <summary>
    /// Delegate to prompt the user for a permission grant with Allow/Deny UI.
    /// Signature: (pluginId, pluginName, capability, capabilityDisplayName) => Task&lt;bool&gt;
    /// Returns true if the user clicked Allow, false if Deny.
    /// The delegate is responsible for persisting the grant/denial.
    /// </summary>
    public Func<string, string, string, string, Task<bool>>? PermissionPrompter { get; set; }

    /// <summary>
    /// Delegate to show a toast/notification to the user.
    /// Signature: (message, isError) => void
    /// </summary>
    public Action<string, bool>? ToastNotifier { get; set; }

    /// <summary>
    /// Delegate to search for linkable items across all plugins.
    /// Signature: (query, maxResults) => list of results
    /// </summary>
    public Func<string, int, Task<IReadOnlyList<Models.LinkableItemResult>>>? LinkableItemSearcher { get; set; }

    /// <summary>
    /// Delegate invoked when a privstack:// internal link is clicked.
    /// Signature: (linkType, itemId) => void. Host navigates to the target.
    /// </summary>
    public Action<string, string>? InternalLinkActivated { get; set; }

    /// <summary>
    /// Delegate invoked when hovering over a linkable item to prefetch its view state.
    /// Signature: (pluginId, entityId) => void. Host initiates background prefetch.
    /// </summary>
    public Action<string, string>? PrefetchRequested { get; set; }

    /// <summary>
    /// Delegate invoked when hover leaves a linkable item to cancel pending prefetch.
    /// Signature: (pluginId, entityId) => void. Host cancels if still pending.
    /// </summary>
    public Action<string, string>? PrefetchCancelled { get; set; }

    static AdaptiveViewRenderer()
    {
        ViewStateJsonProperty.Changed.AddClassHandler<AdaptiveViewRenderer>(
            (renderer, _) => renderer.OnViewStateChanged());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Observe a canary font-size resource. When FontScaleService updates
        // Application.Resources, this fires, and we re-render with new sizes.
        _lastObservedFontSize = FontSize("ThemeFontSizeMd", 14);
        _fontScaleSubscription = this.GetResourceObservable("ThemeFontSizeMd")
            .Subscribe(new FontScaleObserver(this));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _fontScaleSubscription?.Dispose();
        _fontScaleSubscription = null;
        _syncTimer?.Stop();
        _syncTimer?.Dispose();
        _syncTimer = null;
        if (_shadowState.IsDirty)
        {
            _shadowState.FlushSync(SendCommandSilent);
            SendCommandSilent("save_page", "{}");
        }
        _shadowState.Clear();

        // Stop the deferred refresh timer — a running DispatcherTimer roots the
        // renderer via its Tick handler closure, preventing GC.
        _deferredRefreshTimer?.Stop();
        _deferredRefreshTimer = null;

        // Clear internal caches to release object graphs
        _activeGraphControl = null;
        _pageListTitles.Clear();
        _pageListIcons.Clear();
        _blockMeta.Clear();
        _tableFilterState.Clear();

        Content = null;

        base.OnDetachedFromVisualTree(e);
    }

    /// Observer that triggers a re-render when font scale resources change.
    private sealed class FontScaleObserver(AdaptiveViewRenderer renderer) : IObserver<object?>
    {
        public void OnNext(object? value)
        {
            if (value is double d && Math.Abs(d - renderer._lastObservedFontSize) > 0.01)
            {
                renderer._lastObservedFontSize = d;
                renderer.OnViewStateChanged();
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    // ================================================================
    // Theme resource helpers — single point of access for all visuals
    // ================================================================

    private static object? Res(string key)
    {
        var app = Application.Current;
        if (app is null) return null;

        // Try direct Application.Resources first (FontScaleService writes here)
        if (app.Resources.TryGetResource(key, app.ActualThemeVariant, out var v))
            return v;

        // Fallback: walk merged dictionaries via FindResource
        return app.FindResource(key);
    }

    private static IBrush Brush(string key, IBrush fallback) =>
        Res(key) as IBrush ?? fallback;

    private static new double FontSize(string key, double fallback) =>
        Res(key) is double d ? d : fallback;

    private static double Dbl(string key, double fallback) =>
        Res(key) is double d ? d : fallback;

    private static CornerRadius Radius(string key) =>
        Res(key) is CornerRadius cr ? cr : new CornerRadius(4);

    private static Thickness Thick(string key) =>
        Res(key) is Thickness t ? t : new Thickness(8);

    /// <summary>Parse a JSON array [left, top, right, bottom] into a Thickness.</summary>
    private static Thickness ParseThicknessArray(JsonElement arr)
    {
        var vals = arr.EnumerateArray().Select(v => v.GetDouble()).ToArray();
        return vals.Length switch
        {
            1 => new Thickness(vals[0]),
            2 => new Thickness(vals[0], vals[1]),
            4 => new Thickness(vals[0], vals[1], vals[2], vals[3]),
            _ => new Thickness(0),
        };
    }

    private static FontFamily Font(string key) =>
        Res(key) as FontFamily ?? FontFamily.Default;

    // Semantic brush shortcuts
    private static IBrush TextPrimary => Brush("ThemeTextPrimaryBrush", Brushes.White);
    private static IBrush TextSecondary => Brush("ThemeTextSecondaryBrush", Brushes.LightGray);
    private static IBrush TextMuted => Brush("ThemeTextMutedBrush", Brushes.Gray);
    private static IBrush Surface => Brush("ThemeSurfaceBrush", Brushes.Black);
    private static IBrush SurfaceElevated => Brush("ThemeSurfaceElevatedBrush", Brushes.DarkGray);
    private static IBrush BorderBrush_ => Brush("ThemeBorderBrush", Brushes.Gray);
    private static IBrush BorderSubtle => Brush("ThemeBorderSubtleBrush", Brushes.DarkGray);
    private static IBrush Primary => Brush("ThemePrimaryBrush", Brushes.DodgerBlue);
    private static IBrush PrimaryMuted => Brush("ThemePrimaryMutedBrush", Brushes.DarkCyan);
    private static IBrush HoverBrush => Brush("ThemeHoverBrush", Brushes.DarkGray);
    private static IBrush HoverSubtle => Brush("ThemeHoverSubtleBrush", new SolidColorBrush(Color.FromArgb(20, 128, 128, 128)));
    private static IBrush PrimarySubtle => Brush("ThemePrimaryMutedBrush", new SolidColorBrush(Color.FromArgb(60, 100, 149, 237)));
    private static IBrush SelectedBrush => Brush("ThemeSelectedBrush", Brushes.DarkSlateGray);
    private static IBrush SuccessBrush => Brush("ThemeSuccessBrush", Brushes.Green);
    private static IBrush WarningBrush => Brush("ThemeWarningBrush", Brushes.Orange);
    private static IBrush DangerBrush => Brush("ThemeDangerBrush", Brushes.Red);

    // ================================================================
    // View state parsing
    // ================================================================

    /// <summary>
    /// Flushes all pending debounced text changes from every RichTextEditor
    /// in the current block panel so no edits are lost before a re-render.
    /// </summary>
    private void FlushAllEditors()
    {
        if (_blockPanel is null) return;
        foreach (var child in _blockPanel.Children)
        {
            if (child is not Border { Child: Control inner }) continue;
            FlushEditorsIn(inner);
        }
    }

    private static void FlushEditorsIn(Control control)
    {
        if (control is Controls.RichTextEditor.RichTextEditor rte)
        {
            rte.FlushTextChange();
            return;
        }
        if (control is Panel p)
            foreach (var c in p.Children)
                if (c is Control cc) FlushEditorsIn(cc);
        if (control is Decorator d && d.Child is Control dc) FlushEditorsIn(dc);
    }

    private void OnViewStateChanged()
    {
        var usePartial = UsePartialRefresh;
        UsePartialRefresh = false; // Reset flag

        // Cancel any pending deferred refresh — we're already rebuilding
        _deferredRefreshTimer?.Stop();
        _deferredRefreshTimer = null;

        // Clear any pending graph hydration from a previous render
        _pendingGraphHydration = null;

        try
        {
            var json = ViewStateProvider?.Invoke() ?? ViewStateJson;

            if (string.IsNullOrWhiteSpace(json))
            {
                Content = CreateEmptyState("No data available");
                return;
            }

            // Try partial refresh if requested and we have existing content
            if (usePartial && Content != null && _activeGraphControl != null)
            {
                if (TryPartialRefresh(json))
                    return;
            }

            _log.Debug("Shadow: OnViewStateChanged — shadow pageId={PageId} hasBlocks={HasBlocks} isDirty={IsDirty}",
                _shadowState.PageId, _shadowState.HasBlocks, _shadowState.IsDirty);
            _headerViewToggleHost = null;

            // Detach page drag artifacts before tearing down the old visual tree
            if (_pageDragOverlay?.Parent is Panel ovp)
                ovp.Children.Remove(_pageDragOverlay);
            if (_pageDropIndicator?.Parent is Panel pip)
                pip.Children.Remove(_pageDropIndicator);
            _pageDragOverlay = null;
            _pageDropIndicator = null;
            _pageListPanel = null;
            if (_isPageDragging)
                CleanUpPageDrag();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("components", out var components))
            {
                Content = RenderComponent(components);

                // Schedule deferred graph hydration after main content is rendered
                if (_pendingGraphHydration != null)
                {
                    var hydrationData = _pendingGraphHydration;
                    _pendingGraphHydration = null;
                    Dispatcher.UIThread.Post(() => HydrateGraphDeferred(hydrationData), DispatcherPriority.Background);
                }
            }
            else
            {
                _log.Warning("View state JSON has no 'components' property: {Json}",
                    json?.Length > 500 ? json[..500] + "..." : json);
                Content = CreateEmptyState("No components defined in view state");
            }
        }
        catch (JsonException ex)
        {
            _log.Warning(ex, "Failed to parse view state JSON");
            Content = CreateErrorState("Invalid view state JSON");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to render view state");
            Content = CreateErrorState($"Render error: {ex.Message}");
        }
    }

    /// <summary>
    /// Attempts a partial refresh - updating existing controls with new data
    /// without rebuilding the entire control tree. Used for same-plugin navigation.
    /// </summary>
    private bool TryPartialRefresh(string json)
    {
        try
        {
            // Must have existing block panel to do partial refresh
            if (_blockPanel == null)
                return false;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("components", out var components))
                return false;

            // Extract block editor data
            var blockData = ExtractBlockEditorData(components);
            if (blockData == null)
                return false;

            var (pageId, blocks) = blockData.Value;

            // Update shadow state for the new page
            if (_shadowState.PageId != pageId)
            {
                // Flush outgoing page if dirty
                if (_shadowState.IsDirty)
                {
                    _shadowState.FlushSync(SendCommandSilent);
                    SendCommandSilent("save_page", "{}");
                }
                _shadowState.Clear();
                if (blocks != null && blocks.Value.GetArrayLength() > 0)
                    _shadowState.LoadFromPluginJson(pageId, blocks.Value);
            }

            // Re-render blocks into existing panel
            RefreshBlockPanelInPlace(pageId, blocks);

            // Update the graph with new nodes/edges and center
            if (_activeGraphControl != null)
            {
                var (nodes, edges, centerId) = ExtractGraphData(components);
                if (nodes != null && nodes.Count > 0)
                {
                    // Update center ID if changed
                    if (!string.IsNullOrEmpty(centerId) && _activeGraphControl.CenterId != centerId)
                    {
                        _log.Debug("TryPartialRefresh: Updating graph CenterId from {Old} to {New}",
                            _activeGraphControl.CenterId ?? "(null)", centerId);
                        _activeGraphControl.CenterId = centerId;
                    }
                    _activeGraphControl.UpdateData(nodes, edges ?? [], fullRelayout: false);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "TryPartialRefresh failed");
            return false;
        }
    }

    /// <summary>
    /// Re-renders blocks into the existing _blockPanel without rebuilding the entire control tree.
    /// </summary>
    private void RefreshBlockPanelInPlace(string pageId, JsonElement? pluginBlocks)
    {
        if (_blockPanel == null) return;

        var hasPluginBlocks = pluginBlocks != null
                              && pluginBlocks.Value.ValueKind == JsonValueKind.Array
                              && pluginBlocks.Value.GetArrayLength() > 0;

        // Determine effective block source
        var hasShadowBlocks = _shadowState.PageId == pageId && _shadowState.HasBlocks;

        // If both shadow and plugin have blocks, merge any new plugin blocks
        if (hasShadowBlocks && hasPluginBlocks)
        {
            var newIds = _shadowState.MergeNewBlocksFromPlugin(pluginBlocks!.Value);
            if (newIds.Count > 0)
                _pendingFocusBlockId = newIds[^1];
        }

        // Get blocks to render
        JsonElement blocks = default;
        if (hasShadowBlocks)
        {
            var shadowJson = _shadowState.SerializeBlocksJson();
            using var shadowDoc = JsonDocument.Parse(shadowJson);
            blocks = shadowDoc.RootElement.Clone();
        }
        else if (hasPluginBlocks)
        {
            blocks = pluginBlocks!.Value;
            if (!_shadowState.HasBlocks)
                _shadowState.LoadFromPluginJson(pageId, pluginBlocks!.Value);
        }

        // Clear existing children
        _blockPanel.Children.Clear();

        // Re-render blocks
        var hasBlocks = hasShadowBlocks || hasPluginBlocks;
        if (!hasBlocks)
        {
            var placeholder = new Controls.RichTextEditor.RichTextEditor
            {
                Markdown = "",
                BlockId = "__empty__",
                FontSize = FontSize("ThemeFontSizeMd", 14),
                MinHeight = FontSize("ThemeFontSizeMd", 14) * 1.6,
                Opacity = 0.5,
            };
            var fired = false;
            placeholder.TextChanged += (_, markdown) =>
            {
                if (fired) return;
                fired = true;
                SendCommandSilent("add_block", JsonSerializer.Serialize(new { text = markdown }));
            };
            placeholder.GotFocus += (_, _) => placeholder.Opacity = 1.0;
            placeholder.LostFocus += (_, _) => placeholder.Opacity = 0.5;
            _blockPanel.Children.Add(placeholder);
        }
        else
        {
            var blockArray = new List<JsonElement>();
            foreach (var b in blocks.EnumerateArray()) blockArray.Add(b);

            for (var bi = 0; bi < blockArray.Count; bi++)
            {
                var block = blockArray[bi];
                var blockType = block.GetStringProp("type") ?? "paragraph";
                var pairId = block.GetStringProp("pair_id");
                var layout = block.GetStringProp("layout");

                // Side-by-side detection
                if (layout == "side_by_side" && pairId is not null && bi + 1 < blockArray.Count)
                {
                    var nextBlock = blockArray[bi + 1];
                    var nextPairId = nextBlock.GetStringProp("pair_id");

                    if (nextPairId == pairId)
                    {
                        var nextBlockType = nextBlock.GetStringProp("type") ?? "paragraph";
                        var leftInner = RenderBlock(block, blockType);
                        var rightInner = RenderBlock(nextBlock, nextBlockType);

                        var pairAlign = block.GetStringProp("pair_valign") ?? "top";
                        var valign = pairAlign switch
                        {
                            "center" => VerticalAlignment.Center,
                            "bottom" => VerticalAlignment.Bottom,
                            _ => VerticalAlignment.Top,
                        };

                        leftInner.VerticalAlignment = valign;
                        rightInner.VerticalAlignment = valign;

                        var pairGrid = new Grid
                        {
                            ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(Dbl("ThemeSpacingSm", 8), GridUnitType.Pixel), new ColumnDefinition(1, GridUnitType.Star) },
                        };
                        pairGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                        Grid.SetColumn(leftInner, 0);
                        Grid.SetColumn(rightInner, 2);
                        pairGrid.Children.Add(leftInner);
                        pairGrid.Children.Add(rightInner);
                        _blockPanel.Children.Add(pairGrid);

                        bi++;
                        continue;
                    }
                }

                var rendered = RenderBlock(block, blockType);
                _blockPanel.Children.Add(rendered);
            }
        }

        // Apply lock if needed
        if (_currentPageIsLocked)
        {
            ApplyLockToBlockPanel(_blockPanel);
        }

        // Focus newly added block after render
        if (_pendingFocusBlockId is not null)
        {
            var focusId = _pendingFocusBlockId;
            _pendingFocusBlockId = null;
            Dispatcher.UIThread.Post(() =>
            {
                var editor = FindEditorInBlock(_blockPanel, focusId);
                if (editor is Controls.RichTextEditor.RichTextEditor rte)
                {
                    rte.BringIntoView();
                    rte.SetCaretToStart();
                }
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Recursively searches the view state JSON for a block_editor component and extracts its page_id and blocks.
    /// </summary>
    private static (string PageId, JsonElement? Blocks)? ExtractBlockEditorData(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.GetStringProp("type") == "block_editor")
            {
                var pageId = el.GetStringProp("page_id") ?? "__default__";
                JsonElement? blocks = null;
                if (el.TryGetProperty("blocks", out var b) && b.ValueKind == JsonValueKind.Array)
                    blocks = b;
                return (pageId, blocks);
            }

            // Recurse into known container properties
            foreach (var prop in el.EnumerateObject())
            {
                var result = ExtractBlockEditorData(prop.Value);
                if (result != null) return result;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in el.EnumerateArray())
            {
                var result = ExtractBlockEditorData(child);
                if (result != null) return result;
            }
        }

        return null;
    }

    /// <summary>
    /// Hydrates the graph control with pre-parsed GraphData after the main content has rendered.
    /// Called via Dispatcher.Post with Background priority to not block the UI.
    /// </summary>
    private void HydrateGraphDeferred(DeferredGraphData data)
    {
        try
        {
            // Check if the canvas is still valid (might have been replaced by a new render)
            if (_activeGraphControl != data.Canvas)
            {
                _log.Debug("HydrateGraphDeferred: canvas was replaced, skipping");
                return;
            }

            // Set center ID and start with pre-parsed data
            data.Canvas.CenterId = data.CenterId;
            data.Canvas.StartWithData(data.GraphData);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "HydrateGraphDeferred failed");
        }
    }

    // ================================================================
    // Component rendering dispatch
    // ================================================================

    private Control RenderComponent(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            var panel = new StackPanel { Spacing = Dbl("ThemeSpacingXs", 4) };
            foreach (var child in element.EnumerateArray())
            {
                panel.Children.Add(RenderComponent(child));
            }
            return panel;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return new TextBlock { Text = element.ToString(), Foreground = TextPrimary };
        }

        var type_ = element.GetStringProp("type");

        return type_ switch
        {
            "stack" => RenderStack(element),
            "grid" => RenderGrid(element),
            "split_pane" => RenderSplitPane(element),
            "vertical_split" => RenderVerticalSplit(element),
            "multi_pane" => RenderMultiPane(element),
            "scroll" => RenderScroll(element),
            "list" => RenderList(element),
            "text" => RenderText(element),
            "badge" => RenderBadge(element),
            "button" => RenderButton(element),
            "icon_button" => RenderIconButton(element),
            "text_input" => RenderTextInput(element),
            "toggle" => RenderToggle(element),
            "dropdown" => RenderDropdown(element),
            "toolbar" => RenderToolbar(element),
            "tab_bar" => RenderTabBar(element),
            "card" => RenderCard(element),
            "modal" => RenderModal(element),
            "status_bar" => RenderStatusBar(element),
            "detail_header" => RenderDetailHeader(element),
            "section_header" => RenderSectionHeader(element),
            "article_item" => RenderArticleItem(element),
            "feed_item" => RenderFeedItem(element),
            "spacer" => RenderSpacer(element),
            "loading" => RenderLoading(element),
            "empty_state" => RenderEmptyState(element),
            "error" => RenderErrorComponent(element),
            "html_content" => RenderHtmlContent(element),
            "page" => RenderPage(element),
            "page_item" => RenderPageItem(element),
            "button_group" => RenderButtonGroup(element),
            "cover_image" => RenderCoverImage(element),
            "block_editor" => RenderBlockEditor(element),
            "graph_view" => RenderGraphView(element),
            "backlinks_list" => RenderBacklinksList(element),
            "stepper" => RenderStepper(element),
            "slider" => RenderSlider(element),
            "file_grid" => RenderFileGrid(element),
            "file_list" => RenderFileList(element),
            "file_preview" => RenderFilePreview(element),
            "breadcrumbs" => RenderBreadcrumbs(element),
            "tag_list" => RenderTagList(element),
            "responsive_columns" => RenderResponsiveColumns(element),
            "date_picker" => RenderDatePicker(element),
            "combo_input" => RenderComboInput(element),
            "progress_bar" => RenderProgressBar(element),
            "calendar_grid" => RenderCalendarGrid(element),
            "week_grid" => RenderWeekGrid(element),
            "day_grid" => RenderDayGrid(element),
            "separator" => new Separator
            {
                Margin = new Thickness(0, Dbl("ThemeSpacingXs", 4)),
                Background = BorderSubtle,
            },
            _ => new TextBlock
            {
                Text = $"[Unknown component: {type_}]",
                Foreground = WarningBrush,
                FontSize = FontSize("ThemeFontSizeSm", 12),
            },
        };
    }

    // ================================================================
    // Layout components
    // ================================================================

    private Control RenderStack(JsonElement el)
    {
        var isHorizontal = el.GetStringProp("orientation") == "horizontal";

        if (isHorizontal || !el.TryGetProperty("children", out var children))
        {
            // Check if any horizontal child uses flex — if so, use Grid for even distribution
            var hasFlexChild = false;
            var hChildList = new List<JsonElement>();
            if (isHorizontal && el.TryGetProperty("children", out var hcPeek))
            {
                foreach (var c in hcPeek.EnumerateArray())
                {
                    hChildList.Add(c);
                    if (c.GetBoolProp("flex", false)) hasFlexChild = true;
                }
            }

            if (isHorizontal && hasFlexChild && hChildList.Count > 0)
            {
                // Use Grid with Star columns for flex children, Auto for others
                var spacing = el.GetDoubleProp("spacing", Dbl("ThemeSpacingXs", 4));
                var flexGrid = new Grid();
                if (el.TryGetProperty("padding", out var gPad))
                {
                    flexGrid.Margin = gPad.ValueKind == JsonValueKind.Array
                        ? ParseThicknessArray(gPad)
                        : new Thickness(gPad.GetDouble());
                }

                for (int i = 0; i < hChildList.Count; i++)
                {
                    if (i > 0 && spacing > 0)
                        flexGrid.ColumnDefinitions.Add(new ColumnDefinition(spacing, GridUnitType.Pixel));
                    var isFlex = hChildList[i].GetBoolProp("flex", false);
                    flexGrid.ColumnDefinitions.Add(isFlex
                        ? new ColumnDefinition(1, GridUnitType.Star)
                        : new ColumnDefinition(GridLength.Auto));
                }

                // Check for vertical alignment on the parent stack
                var vAlign = el.GetStringProp("valign") ?? "center";
                var verticalAlignment = vAlign switch
                {
                    "top" => VerticalAlignment.Top,
                    "bottom" => VerticalAlignment.Bottom,
                    "stretch" => VerticalAlignment.Stretch,
                    _ => VerticalAlignment.Center,
                };

                for (int i = 0; i < hChildList.Count; i++)
                {
                    var ctrl = RenderComponent(hChildList[i]);
                    var actualCol = spacing > 0 ? i * 2 : i;
                    Grid.SetColumn(ctrl, actualCol);
                    ctrl.VerticalAlignment = verticalAlignment;
                    flexGrid.Children.Add(ctrl);
                }
                return flexGrid;
            }

            // Check if horizontal stack has a spacer — if so, use Grid to respect width constraints
            var hasSpacer = false;
            if (isHorizontal && !hasFlexChild)
            {
                foreach (var c in hChildList)
                {
                    if (c.GetStringProp("type") == "spacer") { hasSpacer = true; break; }
                }
            }

            if (isHorizontal && hasSpacer && hChildList.Count > 0)
            {
                // Use Grid so content respects width constraints.
                // The element just before the first spacer gets Star (fills & wraps),
                // the spacer itself is skipped (zero-width), everything else is Auto.
                var spacing = el.GetDoubleProp("spacing", Dbl("ThemeSpacingXs", 4));
                var spacerGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
                if (el.TryGetProperty("padding", out var sgPad))
                {
                    spacerGrid.Margin = sgPad.ValueKind == JsonValueKind.Array
                        ? ParseThicknessArray(sgPad)
                        : new Thickness(sgPad.GetDouble());
                }

                // Find index of first spacer so the element before it gets Star
                var spacerIdx = -1;
                for (int i = 0; i < hChildList.Count; i++)
                {
                    if (hChildList[i].GetStringProp("type") == "spacer") { spacerIdx = i; break; }
                }
                var starIdx = spacerIdx > 0 ? spacerIdx - 1 : -1;

                // Build columns and children, skipping the spacer element entirely
                var colIdx = 0;
                for (int i = 0; i < hChildList.Count; i++)
                {
                    if (hChildList[i].GetStringProp("type") == "spacer") continue;
                    if (colIdx > 0 && spacing > 0)
                        spacerGrid.ColumnDefinitions.Add(new ColumnDefinition(spacing, GridUnitType.Pixel));
                    spacerGrid.ColumnDefinitions.Add(i == starIdx
                        ? new ColumnDefinition(1, GridUnitType.Star)
                        : new ColumnDefinition(GridLength.Auto));
                    var ctrl = RenderComponent(hChildList[i]);
                    Grid.SetColumn(ctrl, spacerGrid.ColumnDefinitions.Count - 1);
                    spacerGrid.Children.Add(ctrl);
                    colIdx++;
                }
                return spacerGrid;
            }

            // Horizontal stacks or empty: use StackPanel (no fill behavior needed)
            var panel = new StackPanel
            {
                Orientation = isHorizontal ? Orientation.Horizontal : Orientation.Vertical,
                Spacing = el.GetDoubleProp("spacing", Dbl("ThemeSpacingXs", 4)),
            };
            // Padding: supports a single number or [left, top, right, bottom] array
            if (el.TryGetProperty("padding", out var paddingEl))
            {
                panel.Margin = paddingEl.ValueKind == JsonValueKind.Array
                    ? ParseThicknessArray(paddingEl)
                    : new Thickness(paddingEl.GetDouble());
            }
            if (el.TryGetProperty("children", out var hChildren))
            {
                foreach (var child in hChildren.EnumerateArray())
                    panel.Children.Add(RenderComponent(child));
            }
            return panel;
        }

        // Apply alignment to the stack itself
        var stackAlign = el.GetStringProp("align");

        // Vertical stack: use a Grid so one child can fill remaining space.
        // Children whose type is "multi_pane", "split_pane", or "scroll" get a Star row;
        // all others get Auto rows. This makes the layout fill the viewport.
        var childElements = new List<JsonElement>();
        foreach (var child in children.EnumerateArray())
            childElements.Add(child);

        var grid = new Grid
        {
            RowSpacing = el.GetDoubleProp("spacing", Dbl("ThemeSpacingXs", 4)),
        };
        // Padding: supports a single number or [left, top, right, bottom] array
        if (el.TryGetProperty("padding", out var vPad))
        {
            grid.Margin = vPad.ValueKind == JsonValueKind.Array
                ? ParseThicknessArray(vPad)
                : new Thickness(vPad.GetDouble());
        }
        for (int i = 0; i < childElements.Count; i++)
        {
            var childType = childElements[i].GetStringProp("type");
            var hasFlex = childElements[i].GetBoolProp("flex", false);
            var isFill = hasFlex || childType is "multi_pane" or "split_pane" or "scroll";
            grid.RowDefinitions.Add(isFill
                ? new RowDefinition(1, GridUnitType.Star)
                : new RowDefinition(GridLength.Auto));
        }

        for (int i = 0; i < childElements.Count; i++)
        {
            var control = RenderComponent(childElements[i]);
            if (stackAlign == "center")
                control.HorizontalAlignment = HorizontalAlignment.Center;
            else if (stackAlign == "right")
                control.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(control, i);
            grid.Children.Add(control);
        }

        return grid;
    }

    private Control RenderGrid(JsonElement el)
    {
        // If "columns" is specified (e.g. "1fr 1fr"), render as a proper Grid
        if (el.TryGetProperty("columns", out var colsProp))
        {
            var grid = new Grid();
            var colDefs = colsProp.GetString()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            foreach (var colDef in colDefs)
            {
                if (colDef.EndsWith("fr") && double.TryParse(colDef[..^2], out var fr))
                    grid.ColumnDefinitions.Add(new ColumnDefinition(fr, GridUnitType.Star));
                else if (colDef.EndsWith("px") && double.TryParse(colDef[..^2], out var px))
                    grid.ColumnDefinitions.Add(new ColumnDefinition(px, GridUnitType.Pixel));
                else if (colDef == "auto")
                    grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Auto));
                else
                    grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            }

            var colGap = el.GetDoubleProp("column_gap", 0);
            // Insert gap columns between content columns
            if (colGap > 0 && grid.ColumnDefinitions.Count > 1)
            {
                for (var gi = grid.ColumnDefinitions.Count - 1; gi >= 1; gi--)
                    grid.ColumnDefinitions.Insert(gi, new ColumnDefinition(colGap, GridUnitType.Pixel));
            }

            // Similarly support "rows"
            if (el.TryGetProperty("rows", out var rowsProp))
            {
                var rowDefs = rowsProp.GetString()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
                foreach (var rowDef in rowDefs)
                {
                    if (rowDef.EndsWith("fr") && double.TryParse(rowDef[..^2], out var fr2))
                        grid.RowDefinitions.Add(new RowDefinition(fr2, GridUnitType.Star));
                    else if (rowDef.EndsWith("px") && double.TryParse(rowDef[..^2], out var px2))
                        grid.RowDefinitions.Add(new RowDefinition(px2, GridUnitType.Pixel));
                    else if (rowDef == "auto")
                        grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Auto));
                    else
                        grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
                }
            }

            if (el.TryGetProperty("children", out var gridChildren))
            {
                var col = 0;
                var row = 0;
                var totalCols = colDefs.Length;

                foreach (var child in gridChildren.EnumerateArray())
                {
                    var ctrl = RenderComponent(child);
                    // Map logical column to actual Grid column (accounting for gap columns between content cols)
                    var actualCol = colGap > 0 ? col * 2 : col;
                    Grid.SetColumn(ctrl, actualCol);
                    Grid.SetRow(ctrl, row);
                    grid.Children.Add(ctrl);

                    col++;
                    if (col >= totalCols)
                    {
                        col = 0;
                        row++;
                    }
                }
            }

            return grid;
        }

        // Fallback: WrapPanel for legacy grid usage
        var wrapPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
        };

        if (el.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                wrapPanel.Children.Add(RenderComponent(child));
            }
        }

        return wrapPanel;
    }

    /// Responsive column layout: renders children into N columns based on available width.
    /// JSON: { "type": "responsive_columns", "min_column_width": 280, "max_columns": 3, "gap": 16, "children": [...] }
    private Control RenderResponsiveColumns(JsonElement el)
    {
        var minColWidth = el.GetDoubleProp("min_column_width", 280);
        var maxCols = el.GetIntProp("max_columns", 3);
        var gap = el.GetDoubleProp("gap", Dbl("ThemeSpacingMd", 16));

        var childElements = new List<JsonElement>();
        if (el.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
                childElements.Add(child.Clone());
        }

        var container = new Grid();

        void Relayout(double availableWidth)
        {
            var cols = Math.Max(1, Math.Min(maxCols, (int)((availableWidth + gap) / (minColWidth + gap))));
            if (cols == container.ColumnDefinitions.Count / Math.Max(1, (gap > 0 ? 2 : 1)))
                return; // no change

            container.ColumnDefinitions.Clear();
            container.RowDefinitions.Clear();
            container.Children.Clear();

            for (var c = 0; c < cols; c++)
            {
                if (c > 0 && gap > 0)
                    container.ColumnDefinitions.Add(new ColumnDefinition(gap, GridUnitType.Pixel));
                container.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            }

            var col = 0;
            var row = 0;
            var rowCount = (int)Math.Ceiling((double)childElements.Count / cols);
            for (var r = 0; r < rowCount; r++)
                container.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            foreach (var childEl in childElements)
            {
                var ctrl = RenderComponent(childEl);
                var actualCol = gap > 0 ? col * 2 : col;
                Grid.SetColumn(ctrl, actualCol);
                Grid.SetRow(ctrl, row);
                container.Children.Add(ctrl);

                col++;
                if (col >= cols)
                {
                    col = 0;
                    row++;
                }
            }
        }

        container.SizeChanged += (_, args) =>
        {
            if (args.NewSize.Width > 0)
                Relayout(args.NewSize.Width);
        };

        // Initial layout with a sensible default
        Relayout(minColWidth * maxCols + gap * (maxCols - 1));

        return container;
    }

    private Control RenderSplitPane(JsonElement el)
    {
        var grid = new Grid();

        // Use theme sidebar width as default, allow JSON override
        var leftWidth = el.GetDoubleProp("left_width", Dbl("ThemeSidebarWidth", 260));

        grid.ColumnDefinitions.Add(new ColumnDefinition(leftWidth, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        if (el.TryGetProperty("left", out var left))
        {
            var leftControl = RenderComponent(left);
            Grid.SetColumn(leftControl, 0);
            grid.Children.Add(leftControl);
        }

        var separator = new GridSplitter
        {
            Width = 1,
            Background = BorderSubtle,
        };
        Grid.SetColumn(separator, 1);
        grid.Children.Add(separator);

        if (el.TryGetProperty("right", out var right))
        {
            var rightControl = RenderComponent(right);
            Grid.SetColumn(rightControl, 2);
            grid.Children.Add(rightControl);
        }

        return grid;
    }

    /// <summary>
    /// Two-row vertical split with a draggable horizontal splitter.
    /// "top" gets a pixel-height row, "bottom" fills remaining space (Star).
    /// </summary>
    private Control RenderVerticalSplit(JsonElement el)
    {
        var topHeight = el.GetDoubleProp("top_height", 200);
        var grid = new Grid();

        grid.RowDefinitions.Add(new RowDefinition(topHeight, GridUnitType.Pixel) { MinHeight = 80 });
        grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Pixel));
        grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star) { MinHeight = 80 });

        if (el.TryGetProperty("top", out var top))
        {
            var topControl = RenderComponent(top);
            Grid.SetRow(topControl, 0);
            grid.Children.Add(topControl);
        }

        var splitter = new GridSplitter
        {
            Height = 1,
            Background = BorderSubtle,
            ResizeDirection = GridResizeDirection.Rows,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetRow(splitter, 1);
        grid.Children.Add(splitter);

        if (el.TryGetProperty("bottom", out var bottom))
        {
            var bottomControl = RenderComponent(bottom);
            Grid.SetRow(bottomControl, 2);
            grid.Children.Add(bottomControl);
        }

        return grid;
    }

    /// <summary>
    /// N-column resizable layout. Each pane in the "panes" array can be:
    ///   - Fixed: has a "width" (pixel-sized, collapsible, splitter-resizable)
    ///   - Flex:  has "flex": true (Star-sized, fills remaining space)
    /// If NO pane has flex, ALL panes share space equally (all Star).
    /// Multiple panes can be flex (equal Star weight among them).
    /// Widths and collapse states are persisted per-plugin keyed by pane index.
    /// </summary>
    private Control RenderMultiPane(JsonElement el)
    {
        if (!el.TryGetProperty("panes", out var panesEl) || panesEl.ValueKind != JsonValueKind.Array)
            return new Border();

        var paneList = panesEl.EnumerateArray().ToList();
        if (paneList.Count == 0)
            return new Border();

        var pluginId = PluginId ?? "";
        var grid = new Grid();

        const double collapsedStripWidth = 36;
        const double defaultPaneWidth = 260;
        const double minRestoreWidth = 160;

        // Determine flex status per pane
        var isFlex = new bool[paneList.Count];
        var anyFlex = false;
        for (var i = 0; i < paneList.Count; i++)
        {
            isFlex[i] = paneList[i].GetBoolProp("flex", false);
            if (isFlex[i]) anyFlex = true;
        }
        // No explicit flex → all panes flex equally
        if (!anyFlex)
        {
            for (var i = 0; i < paneList.Count; i++)
                isFlex[i] = true;
        }

        // Build column definitions and state arrays
        // Grid columns: [pane0] [sep0] [pane1] [sep1] ... [paneN-1]
        // Column index for pane i = i * 2, separator i = i * 2 + 1
        var colDefs = new ColumnDefinition[paneList.Count];
        var savedWidths = new double[paneList.Count];
        var isCollapsed = new bool[paneList.Count];
        var collapsible = new bool[paneList.Count];

        for (var i = 0; i < paneList.Count; i++)
        {
            var pane = paneList[i];
            var paneDefaultWidth = pane.GetDoubleProp("width", defaultPaneWidth);
            collapsible[i] = !isFlex[i] && pane.GetBoolProp("collapsible", true);

            savedWidths[i] = ReadPersistedDouble(pluginId, $"pane_{i}_width", paneDefaultWidth);
            isCollapsed[i] = collapsible[i] && ReadPersistedBool(pluginId, $"pane_{i}_collapsed", false);

            if (isFlex[i])
            {
                colDefs[i] = new ColumnDefinition(1, GridUnitType.Star);
            }
            else
            {
                var w = isCollapsed[i] ? collapsedStripWidth : savedWidths[i];
                colDefs[i] = new ColumnDefinition(w, GridUnitType.Pixel);
                if (collapsible[i])
                    colDefs[i].MinWidth = collapsedStripWidth;

                var minWidth = pane.GetDoubleProp("min_width", 0);
                if (minWidth > 0 && !collapsible[i])
                    colDefs[i].MinWidth = minWidth;
            }

            // Add pane column
            grid.ColumnDefinitions.Add(colDefs[i]);

            // Add separator column (except after last pane)
            if (i < paneList.Count - 1)
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Pixel));
        }

        // Debounce timer for width persistence
        System.Timers.Timer? resizeTimer = null;

        void DebounceSaveWidths()
        {
            resizeTimer?.Stop();
            resizeTimer?.Dispose();
            resizeTimer = new System.Timers.Timer(500) { AutoReset = false };
            resizeTimer.Elapsed += (_, _) =>
            {
                for (var j = 0; j < paneList.Count; j++)
                {
                    if (colDefs[j].Width.GridUnitType != GridUnitType.Pixel) continue;
                    var w = colDefs[j].Width.Value;
                    if (w > collapsedStripWidth) savedWidths[j] = w;
                    PersistDouble(pluginId, $"pane_{j}_width", savedWidths[j]);
                }
            };
            resizeTimer.Start();
        }

        // Render each pane
        for (var i = 0; i < paneList.Count; i++)
        {
            var pane = paneList[i];
            var gridCol = i * 2; // pane column index (accounts for separators)

            if (!pane.TryGetProperty("content", out var contentEl))
                continue;

            var content = RenderComponent(contentEl);

            if (collapsible[i])
            {
                // Fixed pane with collapse toggle
                var capturedIdx = i;
                var panel = new DockPanel { Background = Surface, ClipToBounds = true };

                // Determine if this is a right-side panel (any flex pane exists to the left)
                var isRightPanel = Enumerable.Range(0, i).Any(j => isFlex[j]);
                var capturedIsRight = isRightPanel;

                var toggle = CreateCollapseButton(isCollapsed[i], isRightPanel);
                DockPanel.SetDock(toggle, Dock.Bottom);
                toggle.Click += (_, _) =>
                {
                    isCollapsed[capturedIdx] = !isCollapsed[capturedIdx];
                    if (isCollapsed[capturedIdx])
                    {
                        var cur = colDefs[capturedIdx].Width.Value;
                        if (cur > collapsedStripWidth) savedWidths[capturedIdx] = cur;
                        colDefs[capturedIdx].Width = new GridLength(collapsedStripWidth, GridUnitType.Pixel);
                        content.IsVisible = false;
                    }
                    else
                    {
                        colDefs[capturedIdx].Width = new GridLength(
                            Math.Max(minRestoreWidth, savedWidths[capturedIdx]), GridUnitType.Pixel);
                        content.IsVisible = true;
                    }
                    UpdateCollapseButtonContent(toggle, isCollapsed[capturedIdx], capturedIsRight);
                    PersistBool(pluginId, $"pane_{capturedIdx}_collapsed", isCollapsed[capturedIdx]);
                    PersistDouble(pluginId, $"pane_{capturedIdx}_width", savedWidths[capturedIdx]);
                };

                panel.Children.Add(toggle);
                content.IsVisible = !isCollapsed[i];
                panel.Children.Add(content);

                Grid.SetColumn(panel, gridCol);
                grid.Children.Add(panel);

                panel.SizeChanged += (_, _) => DebounceSaveWidths();
            }
            else
            {
                // Flex pane or non-collapsible fixed pane — no wrapper
                Grid.SetColumn(content, gridCol);
                grid.Children.Add(content);
            }

            // Add grid splitter after this pane (except the last)
            if (i < paneList.Count - 1)
            {
                var sep = new GridSplitter
                {
                    Width = 1,
                    Background = BorderSubtle,
                };
                // Save widths immediately when splitter drag completes
                sep.DragCompleted += (_, _) =>
                {
                    for (var j = 0; j < paneList.Count; j++)
                    {
                        if (colDefs[j].Width.GridUnitType != GridUnitType.Pixel) continue;
                        var w = colDefs[j].Width.Value;
                        if (w > collapsedStripWidth) savedWidths[j] = w;
                        PersistDouble(pluginId, $"pane_{j}_width", savedWidths[j]);
                    }
                };
                Grid.SetColumn(sep, gridCol + 1);
                grid.Children.Add(sep);
            }
        }

        return grid;
    }

    /// Creates a collapse/expand toggle button with label.
    /// Left panel: label on left, chevron on right, aligned right, chevron points left to collapse.
    /// Right panel: chevron on left, label on right, aligned left, chevron points right to collapse.
    private static Button CreateCollapseButton(bool isCollapsed, bool isRightPanel)
    {
        var btn = new Button
        {
            Padding = new Thickness(8, 6),
            Background = Brushes.Transparent,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            HorizontalAlignment = isRightPanel ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Opacity = 0.5,
        };
        UpdateCollapseButtonContent(btn, isCollapsed, isRightPanel);
        ToolTip.SetTip(btn, isCollapsed ? "Expand panel" : "Collapse panel");
        btn.PointerEntered += (s, _) => { if (s is Button b) b.Opacity = 1.0; };
        btn.PointerExited += (s, _) => { if (s is Button b) b.Opacity = 0.5; };
        return btn;
    }

    private static void UpdateCollapseButtonContent(Button btn, bool isCollapsed, bool isRightPanel)
    {
        string iconPath;
        if (isRightPanel)
            iconPath = isCollapsed ? IconPaths.PanelRightOpen : IconPaths.PanelRightClose;
        else
            iconPath = isCollapsed ? IconPaths.PanelLeftOpen : IconPaths.PanelLeftClose;

        var icon = CreatePanelIcon(iconPath);

        if (isCollapsed)
        {
            // Collapsed: icon only
            btn.Content = icon;
        }
        else
        {
            var label = new TextBlock
            {
                Text = "Collapse",
                Foreground = TextMuted,
                FontSize = FontSize("ThemeFontSizeXsSm", 11),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
            };

            if (isRightPanel)
            {
                panel.Children.Add(icon);
                panel.Children.Add(label);
            }
            else
            {
                panel.Children.Add(label);
                panel.Children.Add(icon);
            }

            btn.Content = panel;
        }
        ToolTip.SetTip(btn, isCollapsed ? "Expand panel" : "Collapse panel");
    }

    private static Avalonia.Controls.Shapes.Path CreatePanelIcon(string pathData)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(pathData),
            Stroke = TextMuted,
            StrokeThickness = 1.5,
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
        };
        path.SetValue(Avalonia.Controls.Shapes.Shape.StrokeLineCapProperty, PenLineCap.Round);
        path.SetValue(Avalonia.Controls.Shapes.Shape.StrokeJoinProperty, PenLineJoin.Round);
        return path;
    }

    // Inline icon path data matching Icons.cs (no dependency on Desktop project)
    private static class IconPaths
    {
        // Left panel: divider on left side
        public const string PanelLeftClose = "M3 3h18v18H3z M9 3v18 M15 9l-3 3 3 3";  // chevron pointing left
        public const string PanelLeftOpen = "M3 3h18v18H3z M9 3v18 M14 9l3 3-3 3";    // chevron pointing right
        // Right panel: divider on right side
        public const string PanelRightClose = "M3 3h18v18H3z M15 3v18 M9 9l3 3-3 3";  // chevron pointing right
        public const string PanelRightOpen = "M3 3h18v18H3z M15 3v18 M10 9l-3 3 3 3"; // chevron pointing left
    }

    // ---- Settings persistence helpers ----

    private double ReadPersistedDouble(string pluginId, string key, double defaultValue)
    {
        var val = SettingsReader?.Invoke(pluginId, key);
        return val != null && double.TryParse(val, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : defaultValue;
    }

    private bool ReadPersistedBool(string pluginId, string key, bool defaultValue)
    {
        var val = SettingsReader?.Invoke(pluginId, key);
        return val != null && bool.TryParse(val, out var b) ? b : defaultValue;
    }

    private void PersistDouble(string pluginId, string key, double value)
    {
        SettingsWriter?.Invoke(pluginId, key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void PersistBool(string pluginId, string key, bool value)
    {
        SettingsWriter?.Invoke(pluginId, key, value.ToString());
    }

    private Control RenderScroll(JsonElement el)
    {
        var paddingBottom = el.GetDoubleProp("padding_bottom", 0);
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, Dbl("ThemeSpacingSm", 8), 0, 0),
        };

        Control? content = null;
        if (el.TryGetProperty("child", out var child))
        {
            content = RenderComponent(child);
        }
        else if (el.TryGetProperty("children", out var children))
        {
            var panel = new StackPanel { Spacing = Dbl("ThemeSpacingSm", 8) };
            foreach (var c in children.EnumerateArray())
            {
                panel.Children.Add(RenderComponent(c));
            }
            content = panel;
        }

        if (content != null && paddingBottom > 0)
            content.Margin = new Thickness(0, 0, 0, paddingBottom);
        scroll.Content = content;

        // Drop zone support for kanban columns
        var dropZone = el.GetBoolProp("drop_zone", false);
        var dropGroup = el.GetStringProp("drop_group");
        var dropCommand = el.GetStringProp("drop_command");
        var dropTargetId = el.GetStringProp("drop_target_id");

        if (dropZone && dropCommand != null && dropTargetId != null)
        {
            DragDrop.SetAllowDrop(scroll, true);

#pragma warning disable CS0618
            scroll.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            {
                e.DragEffects = e.Data.Contains(dropGroup ?? "drag")
                    ? DragDropEffects.Move
                    : DragDropEffects.None;
            });

            scroll.AddHandler(DragDrop.DropEvent, (_, e) =>
            {
                if (e.Data.Contains(dropGroup ?? "drag"))
                {
                    var taskId = e.Data.Get(dropGroup ?? "drag")?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(taskId))
                    {
                        SendCommand(dropCommand,
                            JsonSerializer.Serialize(new { task_id = taskId, column = dropTargetId }));
                    }
                }
            });
#pragma warning restore CS0618
        }

        return scroll;
    }

    private Control RenderCoverImage(JsonElement el)
    {
        var url = el.GetStringProp("url");
        var editable = el.GetBoolProp("editable", false);
        var setCommand = el.GetStringProp("set_command") ?? "set_cover_image";
        var removeCommand = el.GetStringProp("remove_command") ?? "remove_cover_image";
        var positionCommand = el.GetStringProp("position_command") ?? "set_cover_image_position";
        var coverY = el.GetDoubleProp("cover_y", 0.5);
        var coverHeight = el.GetDoubleProp("height", 250);
        coverHeight = Math.Clamp(coverHeight, 150, 500);

        // No image set — show ghost button if editable
        if (string.IsNullOrEmpty(url) || url == "null")
        {
            if (!editable) return new Panel();

            var addBtn = new Button
            {
                Content = new TextBlock
                {
                    Text = "\uD83D\uDDBC Add cover image",
                    FontSize = FontSize("ThemeFontSizeSm", 12),
                    Foreground = TextMuted,
                },
                Background = Avalonia.Media.Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(Dbl("ThemeSpacingMd", 12), Dbl("ThemeSpacingXs", 4)),
                Margin = new Thickness(Dbl("ThemeSpacingLg", 16), Dbl("ThemeSpacingSm", 8)),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };

            addBtn.Click += async (_, _) =>
            {
                await ShowCoverImagePicker(setCommand);
            };

            return addBtn;
        }

        // Image is set — render cover with crop
        // Negative margins to counteract page Border padding so cover spans edge-to-edge
        var padX = Dbl("ThemeSpacingXl", 24);
        var padTop = Dbl("ThemeSpacingLg", 16);
        var radius = Radius("ThemeRadiusSm");

        var container = new Grid
        {
            Height = coverHeight,
            ClipToBounds = true,
        };

        var imageControl = new Avalonia.Controls.Image
        {
            Stretch = Avalonia.Media.Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };

        container.Children.Add(imageControl);

        // Wrap in a Border with top corner radius and negative margin to span edge-to-edge
        var clipWrapper = new Border
        {
            CornerRadius = new CornerRadius(radius.TopLeft, radius.TopRight, 0, 0),
            ClipToBounds = true,
            Margin = new Thickness(-padX, -padTop, -padX, 0),
            Child = container,
        };

        // Load the image
        var localPath = url;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Download async, then display
            var statusText = new TextBlock
            {
                Text = "Loading cover\u2026",
                Foreground = TextMuted,
                FontSize = FontSize("ThemeFontSizeSm", 12),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            container.Children.Add(statusText);

            _ = Task.Run(async () =>
            {
                try
                {
                    var bytes = NetworkFetcher is not null ? await NetworkFetcher(url) : null;
                    if (bytes is null || bytes.Length == 0)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            statusText.Text = "Could not load cover image.");
                        return;
                    }

                    var ext = ".png";
                    try
                    {
                        var uriPath = new Uri(url).AbsolutePath;
                        var uriExt = System.IO.Path.GetExtension(uriPath);
                        if (!string.IsNullOrEmpty(uriExt) && uriExt.Length <= 5)
                            ext = uriExt;
                    }
                    catch { /* default to .png */ }

                    var storagePath = ImageStoragePath;
                    if (string.IsNullOrEmpty(storagePath)) return;
                    Directory.CreateDirectory(storagePath);
                    var fileName = $"cover_{Guid.NewGuid():N}{ext}";
                    var savedPath = System.IO.Path.Combine(storagePath, fileName);
                    await System.IO.File.WriteAllBytesAsync(savedPath, bytes);

                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            var bitmap = new Avalonia.Media.Imaging.Bitmap(savedPath);
                            imageControl.Source = bitmap;
                            ApplyCoverYOffset(imageControl, container, coverY);
                            statusText.IsVisible = false;
                            // Update the URL to local path
                            SendCommandSilent(setCommand, JsonSerializer.Serialize(new { url = savedPath }));
                        }
                        catch { statusText.Text = "Failed to display cover."; }
                    });
                }
                catch
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        statusText.Text = "Failed to download cover image.");
                }
            });
        }
        else
        {
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try { localPath = new Uri(url).LocalPath; }
                catch { /* use as-is */ }
            }

            if (System.IO.File.Exists(localPath))
            {
                try
                {
                    var bitmap = new Avalonia.Media.Imaging.Bitmap(localPath);
                    imageControl.Source = bitmap;

                    // Apply Y offset after image loads
                    imageControl.LayoutUpdated += OnLayoutUpdated;
                    void OnLayoutUpdated(object? s, EventArgs e)
                    {
                        imageControl.LayoutUpdated -= OnLayoutUpdated;
                        ApplyCoverYOffset(imageControl, container, coverY);
                    }
                }
                catch
                {
                    container.Children.Add(new TextBlock
                    {
                        Text = "Could not load cover image.",
                        Foreground = TextMuted,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
            }
        }

        if (!editable) return clipWrapper;

        // Hover overlay with actions
        var overlay = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = Dbl("ThemeSpacingXs", 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Dbl("ThemeSpacingSm", 8), Dbl("ThemeSpacingSm", 8), 0),
            Opacity = 0,
        };

        var changeBtn = CreateCoverOverlayButton("Change");
        changeBtn.Click += async (_, _) =>
        {
            await ShowCoverImagePicker(setCommand);
        };
        overlay.Children.Add(changeBtn);

        var repositionBtn = CreateCoverOverlayButton("Reposition");
        overlay.Children.Add(repositionBtn);

        var removeBtn = CreateCoverOverlayButton("Remove");
        removeBtn.Click += (_, _) => SendCommand(removeCommand, "{}");
        overlay.Children.Add(removeBtn);

        container.Children.Add(overlay);

        container.PointerEntered += (_, _) => overlay.Opacity = 1;
        container.PointerExited += (_, _) =>
        {
            // Only hide if not in reposition mode
            if (imageControl.Tag is not "repositioning")
                overlay.Opacity = 0;
        };

        // Reposition mode
        repositionBtn.Click += (_, _) =>
        {
            if (imageControl.Tag is "repositioning")
            {
                // Exit reposition mode
                imageControl.Tag = null;
                repositionBtn.Content = new TextBlock
                {
                    Text = "Reposition",
                    FontSize = FontSize("ThemeFontSizeXs", 11),
                    Foreground = Avalonia.Media.Brushes.White,
                };
                container.Cursor = null;
                // Save position
                var currentY = GetCoverYFromTransform(imageControl, container);
                SendCommandSilent(positionCommand, JsonSerializer.Serialize(new { y = currentY, height = container.Height }));
            }
            else
            {
                // Enter reposition mode
                imageControl.Tag = "repositioning";
                repositionBtn.Content = new TextBlock
                {
                    Text = "Done",
                    FontSize = FontSize("ThemeFontSizeXs", 11),
                    Foreground = Avalonia.Media.Brushes.White,
                };
                container.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeNorthSouth);
            }
        };

        // Drag to reposition image Y
        double dragStartY = 0;
        double dragStartOffset = 0;

        container.PointerPressed += (_, e) =>
        {
            if (imageControl.Tag is not "repositioning") return;
            dragStartY = e.GetPosition(container).Y;
            dragStartOffset = imageControl.RenderTransform is Avalonia.Media.TranslateTransform tt ? tt.Y : 0;
            e.Pointer.Capture(container);
        };

        container.PointerMoved += (_, e) =>
        {
            if (imageControl.Tag is not "repositioning") return;
            if (!e.GetCurrentPoint(container).Properties.IsLeftButtonPressed) return;
            var dy = e.GetPosition(container).Y - dragStartY;
            var imgH = imageControl.Bounds.Height;
            var boxH = container.Height;
            if (imgH <= boxH) return;
            var maxOffset = 0.0;
            var minOffset = -(imgH - boxH);
            var newOffset = Math.Clamp(dragStartOffset + dy, minOffset, maxOffset);
            imageControl.RenderTransform = new Avalonia.Media.TranslateTransform(0, newOffset);
        };

        container.PointerReleased += (_, e) =>
        {
            if (imageControl.Tag is not "repositioning") return;
            e.Pointer.Capture(null);
        };

        // Bottom-edge resize handle
        var resizeHandle = new Border
        {
            Height = 6,
            Background = Avalonia.Media.Brushes.Transparent,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeNorthSouth),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var resizeIndicator = new Border
        {
            Height = 3,
            Width = 40,
            CornerRadius = new CornerRadius(2),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(120, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
        };
        resizeHandle.Child = resizeIndicator;

        resizeHandle.PointerEntered += (_, _) => resizeIndicator.Opacity = 1;
        resizeHandle.PointerExited += (_, _) => resizeIndicator.Opacity = 0;

        double resizeStartY2 = 0;
        double resizeStartHeight = 0;

        resizeHandle.PointerPressed += (_, e) =>
        {
            resizeStartY2 = e.GetPosition(container).Y;
            resizeStartHeight = container.Height;
            e.Pointer.Capture(resizeHandle);
            e.Handled = true;
        };

        resizeHandle.PointerMoved += (_, e) =>
        {
            if (!e.GetCurrentPoint(resizeHandle).Properties.IsLeftButtonPressed) return;
            var dy = e.GetPosition(container).Y - resizeStartY2;
            var newH = Math.Clamp(resizeStartHeight + dy, 150, 500);
            container.Height = newH;
            ApplyCoverYOffset(imageControl, container, GetCoverYFromTransform(imageControl, container));
        };

        resizeHandle.PointerReleased += (_, e) =>
        {
            e.Pointer.Capture(null);
            var currentY = GetCoverYFromTransform(imageControl, container);
            SendCommandSilent(positionCommand, JsonSerializer.Serialize(new { y = currentY, height = container.Height }));
        };

        container.Children.Add(resizeHandle);

        return clipWrapper;
    }

    private Button CreateCoverOverlayButton(string label)
    {
        return new Button
        {
            Content = new TextBlock
            {
                Text = label,
                FontSize = FontSize("ThemeFontSizeXs", 11),
                Foreground = Avalonia.Media.Brushes.White,
            },
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(160, 0, 0, 0)),
            CornerRadius = Radius("ThemeRadiusSm"),
            Padding = new Thickness(Dbl("ThemeSpacingSm", 8), Dbl("ThemeSpacingXs", 4)),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            BorderThickness = new Thickness(0),
        };
    }

    private async Task ShowCoverImagePicker(string setCommand)
    {
        if (ImageFilePicker is not null)
        {
            var path = await ImageFilePicker();
            if (!string.IsNullOrEmpty(path))
            {
                var storedPath = CopyImageToStorage(path);
                var finalUrl = storedPath ?? path;
                SendCommand(setCommand, JsonSerializer.Serialize(new { url = finalUrl }));
            }
        }
    }

    private static void ApplyCoverYOffset(Avalonia.Controls.Image img, Control container, double coverY)
    {
        var imgH = img.Bounds.Height > 0 ? img.Bounds.Height :
            (img.Source is Avalonia.Media.Imaging.Bitmap bmp ? bmp.PixelSize.Height : 0);
        var boxH = container.Bounds.Height > 0 ? container.Bounds.Height : 250;
        if (imgH <= boxH)
        {
            img.RenderTransform = new Avalonia.Media.TranslateTransform(0, 0);
            return;
        }
        var maxOffset = imgH - boxH;
        var offset = -(coverY * maxOffset);
        img.RenderTransform = new Avalonia.Media.TranslateTransform(0, offset);
    }

    private static double GetCoverYFromTransform(Avalonia.Controls.Image img, Control container)
    {
        var currentOffset = img.RenderTransform is Avalonia.Media.TranslateTransform tt ? tt.Y : 0;
        var imgH = img.Bounds.Height > 0 ? img.Bounds.Height :
            (img.Source is Avalonia.Media.Imaging.Bitmap bmp ? bmp.PixelSize.Height : 0);
        var boxH = container.Bounds.Height > 0 ? container.Bounds.Height : 250;
        if (imgH <= boxH) return 0.5;
        var maxOffset = imgH - boxH;
        return Math.Clamp(-currentOffset / maxOffset, 0, 1);
    }

    private Control RenderPage(JsonElement el)
    {
        var maxWidth = el.GetDoubleProp("max_width", Dbl("ThemePageMaxWidth", 1000));

        var container = new StackPanel { Spacing = Dbl("ThemeSpacingXs", 4) };

        if (el.TryGetProperty("child", out var child))
        {
            container.Children.Add(RenderComponent(child));
        }
        else if (el.TryGetProperty("children", out var children))
        {
            foreach (var c in children.EnumerateArray())
                container.Children.Add(RenderComponent(c));
        }

        return new Border
        {
            MaxWidth = maxWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = SurfaceElevated,
            CornerRadius = Radius("ThemeRadiusSm"),
            Padding = new Thickness(
                Dbl("ThemeSpacingXl", 24),
                Dbl("ThemeSpacingLg", 16)),
            Margin = new Thickness(
                Dbl("ThemeSpacingLg", 16),
                Dbl("ThemeSpacingMd", 12),
                Dbl("ThemeSpacingLg", 16),
                Dbl("ThemeSpacing2Xl", 32)),
            Child = container,
        };
    }

    // ================================================================
    // Data components
    // ================================================================

    private Control RenderList(JsonElement el)
    {
        var listBox = new ListBox
        {
            SelectionMode = SelectionMode.Single,
        };

        var commandOnSelect = el.GetStringProp("on_select_command");

        if (el.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var listItem = new ListBoxItem();

                if (item.ValueKind == JsonValueKind.Object)
                {
                    var content = RenderListItem(item);
                    listItem.Content = content;
                    listItem.Tag = item.GetStringProp("id");
                }
                else
                {
                    listItem.Content = item.ToString();
                }

                listBox.Items.Add(listItem);
            }
        }

        if (commandOnSelect != null)
        {
            listBox.SelectionChanged += (_, args) =>
            {
                if (args.AddedItems.Count > 0 && args.AddedItems[0] is ListBoxItem selected)
                {
                    var id = selected.Tag as string ?? "";
                    SendCommand(commandOnSelect, JsonSerializer.Serialize(new { id }));
                }
            };
        }

        return listBox;
    }

    private static Control RenderListItem(JsonElement el)
    {
        var panel = new StackPanel { Spacing = 2 };

        var title = el.GetStringProp("title");
        if (title != null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeight.SemiBold,
                FontSize = FontSize("ThemeFontSizeSmMd", 13),
                Foreground = TextPrimary,
            });
        }

        var subtitle = el.GetStringProp("subtitle");
        if (subtitle != null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = FontSize("ThemeFontSizeXsSm", 11),
                Foreground = TextMuted,
            });
        }

        return panel;
    }

    private Control RenderText(JsonElement el)
    {
        var text = el.GetStringProp("value") ?? el.GetStringProp("text") ?? "";
        var style = el.GetStringProp("style") ?? "body";

        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextPrimary,
            FontFamily = Font("ThemeFontSans"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        switch (style)
        {
            case "heading":
            case "h1":
                tb.FontSize = FontSize("ThemeFontSizeXl", 20);
                tb.FontWeight = FontWeight.Bold;
                break;
            case "heading_warning":
                tb.FontSize = FontSize("ThemeFontSizeXl", 20);
                tb.FontWeight = FontWeight.Bold;
                tb.Foreground = WarningBrush;
                break;
            case "heading_success":
                tb.FontSize = FontSize("ThemeFontSizeXl", 20);
                tb.FontWeight = FontWeight.Bold;
                tb.Foreground = SuccessBrush;
                break;
            case "heading_danger":
                tb.FontSize = FontSize("ThemeFontSizeXl", 20);
                tb.FontWeight = FontWeight.Bold;
                tb.Foreground = DangerBrush;
                break;
            case "h2":
                tb.FontSize = FontSize("ThemeFontSizeLg", 16);
                tb.FontWeight = FontWeight.SemiBold;
                tb.Margin = new Thickness(0, 0, 0, Dbl("ThemeSpacingXs", 4));
                break;
            case "h3":
                tb.FontSize = FontSize("ThemeFontSizeMd", 14);
                tb.FontWeight = FontWeight.SemiBold;
                break;
            case "caption":
            case "muted":
                tb.FontSize = FontSize("ThemeFontSizeXsSm", 11);
                tb.Foreground = TextMuted;
                break;
            case "monospace":
            case "mono":
                tb.FontFamily = Font("ThemeFontMono");
                tb.FontSize = FontSize("ThemeFontSizeSm", 12);
                break;
            case "bold":
                tb.FontSize = FontSize("ThemeFontSizeMd", 14);
                tb.FontWeight = FontWeight.SemiBold;
                break;
            case "strikethrough":
                tb.FontSize = FontSize("ThemeFontSizeMd", 14);
                tb.TextDecorations = Avalonia.Media.TextDecorations.Strikethrough;
                tb.Foreground = TextMuted;
                break;
            case "body":
                tb.FontSize = FontSize("ThemeFontSizeMd", 14);
                break;
            default:
                tb.FontSize = FontSize("ThemeFontSizeMd", 14);
                break;
        }

        var minWidth = el.GetDoubleProp("min_width", 0);
        if (minWidth > 0) tb.MinWidth = minWidth;

        // Handle horizontal alignment
        var align = el.GetStringProp("align");
        if (align != null)
        {
            tb.HorizontalAlignment = align switch
            {
                "center" => HorizontalAlignment.Center,
                "right" => HorizontalAlignment.Right,
                "left" => HorizontalAlignment.Left,
                _ => HorizontalAlignment.Stretch,
            };
            tb.TextAlignment = align switch
            {
                "center" => TextAlignment.Center,
                "right" => TextAlignment.Right,
                "left" => TextAlignment.Left,
                _ => TextAlignment.Left,
            };
        }

        // Handle margin from JSON
        var marginLeft = el.GetDoubleProp("margin_left", 0);
        var marginTop = el.GetDoubleProp("margin_top", 0);
        var marginRight = el.GetDoubleProp("margin_right", 0);
        var marginBottom = el.GetDoubleProp("margin_bottom", 0);
        var margin = el.GetDoubleProp("margin", 0);
        if (margin > 0)
            tb.Margin = new Thickness(margin);
        else if (marginLeft > 0 || marginTop > 0 || marginRight > 0 || marginBottom > 0)
            tb.Margin = new Thickness(marginLeft, marginTop, marginRight, marginBottom);

        // Capture the save status TextBlock for live updates from shadow state
        if (text is "Saved" or "Unsaved changes")
            _saveStatusText = tb;

        return tb;
    }

    private static Control RenderBadge(JsonElement el)
    {
        var text = el.GetStringProp("text") ?? "0";
        var variant = el.GetStringProp("variant") ?? "default";

        var bg = variant switch
        {
            "primary" => Primary,
            "success" => SuccessBrush,
            "warning" => WarningBrush,
            "danger" => DangerBrush,
            _ => SurfaceElevated,
        };

        var fg = variant == "default" ? TextSecondary : Brushes.White;

        return new Border
        {
            Background = bg,
            CornerRadius = Radius("ThemeRadiusFull"),
            Padding = new Thickness(Dbl("ThemeSpacingSm", 8) * 0.75, 2),
            Child = new TextBlock
            {
                Text = text,
                FontSize = FontSize("ThemeFontSizeXsSm", 11),
                FontWeight = FontWeight.SemiBold,
                Foreground = fg,
            },
        };
    }

    // ================================================================
    // Input components
    // ================================================================

    private Control RenderButton(JsonElement el)
    {
        var label = el.GetStringProp("label") ?? el.GetStringProp("text") ?? "Button";
        var command = el.GetStringProp("command");
        var args = el.GetStringProp("args") ?? "{}";
        var variant = el.GetStringProp("variant") ?? "default";

        var button = new Button
        {
            Content = label,
            Padding = Thick("ThemeButtonPaddingMd"),
            FontSize = FontSize("ThemeFontSizeSm", 12),
        };

        if (variant == "accent" || variant == "primary")
        {
            button.Classes.Add("accent");
        }

        if (command != null)
        {
            button.Click += (_, _) => SendCommand(command, args);
        }

        return button;
    }

    private Control RenderIconButton(JsonElement el)
    {
        var icon = el.GetStringProp("icon") ?? "";
        var label = el.GetStringProp("label");
        var command = el.GetStringProp("command");
        var args = el.GetStringProp("args") ?? "{}";
        var variant = el.GetStringProp("variant") ?? "default";
        var isActive = el.GetBoolProp("is_active", false);

        // Hide "Add New Block" button when page is locked
        if (_currentPageIsLocked && command == "__open_palette")
            return new Border { IsVisible = false };

        var contentPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Dbl("ThemeSpacingXs", 4),
        };

        contentPanel.Children.Add(new TextBlock
        {
            Text = icon,
            FontSize = FontSize("ThemeFontSizeMd", 14),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TextPrimary,
        });

        if (label != null)
        {
            contentPanel.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = FontSize("ThemeFontSizeSm", 12),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TextPrimary,
            });
        }

        var align = el.GetStringProp("align");

        var button = new Button
        {
            Content = contentPanel,
            Padding = Thick("ThemeButtonPaddingSm"),
            HorizontalAlignment = align switch
            {
                "center" => HorizontalAlignment.Center,
                "right" => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left,
            },
        };

        if (isActive)
        {
            button.Background = Primary;
            button.Foreground = Brushes.White;
        }

        if (variant == "ghost")
        {
            button.Background = Brushes.Transparent;
        }

        // Flyout support: if the element has a "flyout" child, attach it as a left-placed flyout
        if (el.TryGetProperty("flyout", out var flyoutEl))
        {
            var flyoutContent = RenderComponent(flyoutEl);
            var placement = el.GetStringProp("flyout_placement") ?? "left";
            var flyout = new Flyout
            {
                Content = new Border
                {
                    Background = SurfaceElevated,
                    BorderBrush = BorderBrush_,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12),
                    Width = el.GetDoubleProp("flyout_width", 260),
                    Child = flyoutContent,
                },
                Placement = placement switch
                {
                    "right" => PlacementMode.Right,
                    "top" => PlacementMode.Top,
                    "bottom" => PlacementMode.Bottom,
                    _ => PlacementMode.Left,
                },
            };
            button.Flyout = flyout;
        }
        else if (command != null)
        {
            button.Click += (_, _) => SendCommand(command, args);
        }

        return button;
    }

    private Control RenderTextInput(JsonElement el)
    {
        var placeholder = el.GetStringProp("placeholder") ?? "";
        var value = el.GetStringProp("value") ?? "";
        var commandOnSubmit = el.GetStringProp("on_submit_command");
        var commandOnChange = el.GetStringProp("on_change_command");
        var submitButtonLabel = el.GetStringProp("submit_button_label");

        var isMultiline = el.GetBoolProp("multiline", false);
        var rows = el.GetIntProp("rows", 1);

        var textBox = new TextBox
        {
            Watermark = placeholder,
            Text = value,
            FontSize = FontSize("ThemeFontSizeSm", 12),
            AcceptsReturn = isMultiline,
            TextWrapping = isMultiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        if (isMultiline && rows > 1)
        {
            textBox.MinHeight = rows * FontSize("ThemeFontSizeSm", 12) * 1.6;
        }

        void DoSubmit()
        {
            var text = textBox.Text?.Trim();
            if (string.IsNullOrEmpty(text) || commandOnSubmit is null) return;
            SendCommandDeferred(commandOnSubmit, JsonSerializer.Serialize(new { value = text }));
            textBox.Text = "";
        }

        if (commandOnSubmit != null)
        {
            textBox.KeyDown += (_, args) =>
            {
                if (args.Key == Avalonia.Input.Key.Enter)
                    DoSubmit();
            };
        }

        if (commandOnChange != null)
        {
            var lastSentValue = value; // Track last value to avoid re-sending on re-render
            Avalonia.Threading.DispatcherTimer? debounce = null;
            textBox.TextChanged += (_, _) =>
            {
                var text = textBox.Text ?? "";
                if (text == lastSentValue) return; // Skip if unchanged (e.g. visual tree re-attachment)
                lastSentValue = text;
                debounce?.Stop();
                debounce = null;

                // Send immediately when clearing (unfilter), debounce when typing
                if (string.IsNullOrEmpty(text))
                {
                    SendCommandSilent(commandOnChange, JsonSerializer.Serialize(new { value = text }));
                }
                else
                {
                    debounce = new Avalonia.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(150)
                    };
                    debounce.Tick += (_, _) =>
                    {
                        debounce?.Stop();
                        SendCommandSilent(commandOnChange, JsonSerializer.Serialize(new { value = text }));
                    };
                    debounce.Start();
                }
            };
        }

        var inputMargin = new Thickness(
            Dbl("ThemeSpacingMd", 12),  // Align with section headers
            Dbl("ThemeSpacingXs", 4),
            Dbl("ThemeSpacingMd", 12),
            Dbl("ThemeSpacingSm", 8));

        if (submitButtonLabel is null)
        {
            textBox.Margin = inputMargin;
            return textBox;
        }

        // Render as a connected input+button combo (squared inner corners)
        var r = Radius("ThemeRadiusSm").TopLeft;
        textBox.CornerRadius = new CornerRadius(r, 0, 0, r);

        var button = new Button
        {
            Content = submitButtonLabel,
            FontSize = FontSize("ThemeFontSizeSm", 12),
            Padding = Thick("ThemeButtonPaddingSm"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(0, r, r, 0),
        };
        button.Click += (_, _) => DoSubmit();

        var panel = new DockPanel
        {
            LastChildFill = true,
            Margin = inputMargin,
        };
        DockPanel.SetDock(button, Avalonia.Controls.Dock.Right);
        panel.Children.Add(button);
        panel.Children.Add(textBox);
        return panel;
    }

    private Control RenderToggle(JsonElement el)
    {
        var label = el.GetStringProp("label") ?? "";
        var isChecked = el.GetBoolProp("value", false);
        var command = el.GetStringProp("command");
        var templateArgs = el.GetStringProp("args") ?? "";
        var style = el.GetStringProp("style") ?? "";
        var isDisabled = el.GetBoolProp("disabled", false);

        // Build args: merge template args with the toggled value
        string BuildToggleArgs(bool? checkedVal)
        {
            if (!string.IsNullOrEmpty(templateArgs))
            {
                // Merge value into the template args JSON
                try
                {
                    var doc = JsonDocument.Parse(templateArgs);
                    var dict = new Dictionary<string, object?>();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        dict[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.Number => prop.Value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => prop.Value.ToString(),
                        };
                    }
                    dict["value"] = checkedVal;
                    return JsonSerializer.Serialize(dict);
                }
                catch { }
            }
            return JsonSerializer.Serialize(new { value = checkedVal });
        }

        if (style == "checkbox")
        {
            var cb = new CheckBox
            {
                IsChecked = isChecked,
                Content = label,
                FontSize = FontSize("ThemeFontSizeSm", 12),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = !isDisabled,
                IsHitTestVisible = !isDisabled,
            };
            if (command != null && !isDisabled)
            {
                var initialized = false;
                cb.IsCheckedChanged += (_, _) =>
                {
                    if (!initialized) return;
                    SendCommandDeferred(command, BuildToggleArgs(cb.IsChecked));
                };
                initialized = true;
            }
            return cb;
        }

        var toggle = new ToggleSwitch
        {
            IsChecked = isChecked,
            OnContent = label,
            OffContent = label,
            IsEnabled = !isDisabled,
            IsHitTestVisible = !isDisabled,
        };

        if (command != null && !isDisabled)
        {
            var initialized = false;
            toggle.IsCheckedChanged += (_, _) =>
            {
                if (!initialized) return;
                SendCommandDeferred(command, BuildToggleArgs(toggle.IsChecked));
            };
            initialized = true;
        }

        return toggle;
    }

    private Control RenderStepper(JsonElement el)
    {
        var value = el.GetIntProp("value", 1);
        var min = el.GetIntProp("min", 1);
        var max = el.GetIntProp("max", 5);
        var command = el.GetStringProp("command");

        var nud = new NumericUpDown
        {
            Value = value,
            Minimum = min,
            Maximum = max,
            Increment = 1,
            FormatString = "0",
            MinWidth = 70,
            FontSize = FontSize("ThemeFontSizeSm", 12),
        };

        if (command != null)
        {
            var initialized = false;
            nud.ValueChanged += (_, _) =>
            {
                if (!initialized) return;
                if (nud.Value.HasValue)
                    SendCommandDeferred(command, JsonSerializer.Serialize(new { value = (int)nud.Value.Value }));
            };
            initialized = true;
        }

        return nud;
    }

    private Control RenderSlider(JsonElement el)
    {
        var value = el.GetDoubleProp("value", 0);
        var min = el.GetDoubleProp("min", 0);
        var max = el.GetDoubleProp("max", 1);
        var step = el.GetDoubleProp("step", 0.01);
        var command = el.GetStringProp("command");
        var commandArgs = el.GetStringProp("args");

        var slider = new Slider
        {
            Value = value,
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 10,
            MinWidth = 100,
            TickFrequency = step,
            IsSnapToTickEnabled = false,
        };

        if (command != null)
        {
            var initialized = false;
            DispatcherTimer? debounce = null;
            slider.PropertyChanged += (_, args) =>
            {
                if (args.Property != Slider.ValueProperty) return;
                if (!initialized) return;
                debounce?.Stop();
                debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                debounce.Tick += (_, _) =>
                {
                    debounce.Stop();
                    var payload = commandArgs != null
                        ? commandArgs.Replace("__VALUE__", slider.Value.ToString("F4"))
                        : JsonSerializer.Serialize(new { value = Math.Round(slider.Value, 4) });
                    SendCommandDeferred(command, payload);
                };
                debounce.Start();
            };
            initialized = true;
        }

        return slider;
    }

    private Control RenderDropdown(JsonElement el)
    {
        var selected = el.GetStringProp("selected");
        var command = el.GetStringProp("command");
        var minWidth = el.GetDoubleProp("min_width", 0);
        var placeholder = el.GetStringProp("placeholder");

        var combo = new ComboBox
        {
            FontSize = FontSize("ThemeFontSizeSm", 12),
            Padding = new Thickness(8, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (minWidth > 0) combo.MinWidth = minWidth;
        if (placeholder != null) combo.PlaceholderText = placeholder;

        if (el.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var label = item.ValueKind == JsonValueKind.Object
                    ? item.GetStringProp("label") ?? item.GetStringProp("value") ?? ""
                    : item.ToString();
                var val = item.ValueKind == JsonValueKind.Object
                    ? item.GetStringProp("value") ?? label
                    : label;

                var comboItem = new ComboBoxItem { Content = label, Tag = val };
                combo.Items.Add(comboItem);

                if (val == selected)
                {
                    combo.SelectedItem = comboItem;
                }
            }
        }

        if (command != null)
        {
            var initialized = false;
            combo.SelectionChanged += (_, args) =>
            {
                if (!initialized) return;
                if (args.AddedItems.Count > 0 && args.AddedItems[0] is ComboBoxItem item)
                {
                    var val = item.Tag as string ?? "";
                    SendCommandDeferred(command, JsonSerializer.Serialize(new { value = val }));
                }
            };
            initialized = true;
        }

        return combo;
    }

    // ================================================================
    // Composite components
    // ================================================================

    private Control RenderToolbar(JsonElement el)
    {
        var title = el.GetStringProp("title") ?? "";
        var subtitle = el.GetStringProp("subtitle");

        // Title area (left)
        var titlePanel = new StackPanel
        {
            Spacing = Dbl("ThemeSpacingXs", 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        titlePanel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = FontSize("ThemeFontSizeXl", 20),
            FontWeight = FontWeight.Bold,
            Foreground = TextPrimary,
        });
        if (subtitle != null)
        {
            titlePanel.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = FontSize("ThemeFontSizeSm", 12),
                Foreground = TextMuted,
            });
        }

        // Separate text_input actions (center) from other actions (right)
        Control? centerControl = null;
        var rightActions = new List<Control>();

        if (el.TryGetProperty("actions", out var actions))
        {
            foreach (var action in actions.EnumerateArray())
            {
                var actionType = action.GetStringProp("type") ?? "";
                if (actionType == "text_input" && centerControl == null)
                {
                    var input = RenderComponent(action);
                    // Give the search bar a comfortable fixed width and center it
                    centerControl = new Border
                    {
                        Child = input,
                        Width = 280,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    };
                }
                else
                {
                    rightActions.Add(RenderComponent(action));
                }
            }
        }

        // Use a 3-column grid: title | center (search) | right actions
        var grid = new Grid
        {
            Margin = new Thickness(
                Dbl("ThemeSpacingXl", 24),
                Dbl("ThemeSpacingLg", 16),
                Dbl("ThemeSpacingXl", 24),
                Dbl("ThemeSpacingMd", 12)),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(1, GridUnitType.Star),
                new ColumnDefinition(GridLength.Auto),
            },
        };

        Grid.SetColumn(titlePanel, 0);
        grid.Children.Add(titlePanel);

        if (centerControl != null)
        {
            Grid.SetColumn(centerControl, 1);
            grid.Children.Add(centerControl);
        }

        if (rightActions.Count > 0)
        {
            var actionsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Dbl("ThemeSpacingSm", 8),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            foreach (var a in rightActions)
                actionsPanel.Children.Add(a);
            Grid.SetColumn(actionsPanel, 2);
            grid.Children.Add(actionsPanel);
        }

        return new Border
        {
            Child = grid,
            BorderBrush = BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private Control RenderTabBar(JsonElement el)
    {
        var command = el.GetStringProp("command");
        var hMargin = Dbl("ThemeSpacingMd", 12);  // Align with section headers
        var margin = new Thickness(hMargin, 2);
        var r = Radius("ThemeRadiusSm").TopLeft;

        if (!el.TryGetProperty("tabs", out var tabs))
            return new Border { Margin = margin };

        var tabList = tabs.EnumerateArray().ToList();

        // Outer border wrapping the entire tab bar for a well-defined outline
        var outerRadius = new CornerRadius(r);
        var outerBorder = new Border
        {
            Margin = margin,
            BorderBrush = BorderBrush_,
            BorderThickness = new Thickness(1),
            CornerRadius = outerRadius,
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(
                string.Join(",", tabList.Select(_ => "Auto"))),
        };

        for (var i = 0; i < tabList.Count; i++)
        {
            var tab = tabList[i];
            var label = tab.GetStringProp("label") ?? "";
            var value = tab.GetStringProp("value") ?? label;
            var isActive = tab.GetBoolProp("is_active", false);

            // Match corner radii to outer border so active fill reaches edges cleanly
            var cornerRadius = i == 0 && tabList.Count == 1
                ? outerRadius
                : i == 0
                    ? new CornerRadius(r, 0, 0, r)
                    : i == tabList.Count - 1
                        ? new CornerRadius(0, r, r, 0)
                        : new CornerRadius(0);

            var btn = new Button
            {
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = Thick("ThemeButtonPaddingSm"),
                FontSize = FontSize("ThemeFontSizeSm", 12),
                CornerRadius = cornerRadius,
                BorderThickness = new Thickness(0),
            };

            if (isActive)
            {
                btn.Background = Primary;
                btn.Foreground = Brushes.White;
            }

            // Per-tab command overrides bar-level command
            var tabCommand = tab.GetStringProp("command") ?? command;
            var tabArgs = tab.GetStringProp("args");
            if (tabCommand != null)
            {
                var capturedValue = value;
                var capturedCmd = tabCommand;
                var capturedArgs = tabArgs;
                btn.Click += (_, _) => SendCommand(capturedCmd,
                    capturedArgs ?? JsonSerializer.Serialize(new { value = capturedValue }));
            }

            // Add vertical separator between tabs
            if (i > 0)
            {
                var sep = new Border
                {
                    Width = 1,
                    Background = BorderBrush_,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                Grid.SetColumn(sep, i);
                grid.Children.Add(sep);
            }

            Grid.SetColumn(btn, i);
            grid.Children.Add(btn);
        }

        outerBorder.Child = grid;
        return outerBorder;
    }

    private Control RenderCard(JsonElement el)
    {
        var padding = el.GetDoubleProp("padding", Dbl("ThemeSpacingMd", 12));
        var command = el.GetStringProp("command");
        var commandParameter = el.GetStringProp("command_parameter");

        var border = new Border
        {
            Background = SurfaceElevated,
            BorderBrush = BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = Radius("ThemeRadiusSm"),
            Padding = new Thickness(padding),
            Margin = new Thickness(
                Dbl("ThemeSpacingLg", 16),
                Dbl("ThemeSpacingSm", 8)),
        };

        if (el.TryGetProperty("children", out var children))
        {
            var panel = new StackPanel { Spacing = Dbl("ThemeSpacingXs", 4) };
            foreach (var child in children.EnumerateArray())
            {
                panel.Children.Add(RenderComponent(child));
            }
            border.Child = panel;
        }

        // Drag support for kanban cards
        var draggable = el.GetBoolProp("draggable", false);
        var dragData = el.GetStringProp("drag_data");
        var dragGroup = el.GetStringProp("drag_group");

        if (draggable && dragData != null)
        {
            var isDragging = false;
            Point dragStart = default;

            border.PointerPressed += (_, e) =>
            {
                dragStart = e.GetPosition(border);
                isDragging = false;
            };

            border.PointerMoved += async (_, e) =>
            {
                if (e.GetCurrentPoint(border).Properties.IsLeftButtonPressed && !isDragging)
                {
                    var pos = e.GetPosition(border);
                    if (Math.Abs(pos.X - dragStart.X) > 6 || Math.Abs(pos.Y - dragStart.Y) > 6)
                    {
                        isDragging = true;
#pragma warning disable CS0618
                        var data = new DataObject();
                        data.Set(dragGroup ?? "drag", dragData);
                        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
#pragma warning restore CS0618
                    }
                }
            };
        }

        if (command != null && !draggable)
        {
            border.PointerPressed += (_, _) =>
            {
                SendCommand(command, commandParameter ?? "{}");
            };
            border.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        }
        else if (command != null && draggable)
        {
            // For draggable cards, fire command on click only (not drag)
            border.Tapped += (_, _) =>
            {
                SendCommand(command, commandParameter ?? "{}");
            };
            border.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        }

        return border;
    }

    /// <summary>
    /// Renders a modal dialog overlay with backdrop, centered content, title bar, and close button.
    /// The modal overlays the entire plugin view area using a Canvas with absolute positioning.
    /// </summary>
    /// <remarks>
    /// Supported properties:
    /// - title: string - Modal title displayed in header
    /// - width: number - Dialog width in pixels (default 480)
    /// - max_height: number - Maximum dialog height (default 600)
    /// - on_close_command: string - Command to fire when backdrop or close button clicked
    /// - children: array - Content to render inside the modal body
    /// </remarks>
    private Control RenderModal(JsonElement el)
    {
        var title = el.GetStringProp("title") ?? "Dialog";
        var width = el.GetDoubleProp("width", 480);
        var maxHeight = el.GetDoubleProp("max_height", 600);
        var onCloseCommand = el.GetStringProp("on_close_command");

        // Create a panel that will be added to the overlay layer
        var overlayPanel = new Panel();

        // Semi-transparent backdrop that fills the entire area
        var backdrop = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        if (onCloseCommand != null)
        {
            backdrop.PointerPressed += (_, _) =>
            {
                SendCommand(onCloseCommand, "{}");
            };
        }

        overlayPanel.Children.Add(backdrop);

        // Dialog box centered in the overlay
        var dialog = new Border
        {
            Background = SurfaceElevated,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Radius("ThemeRadiusMd"),
            Width = width,
            MaxHeight = maxHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BoxShadow = BoxShadows.Parse("0 8 32 0 #60000000"),
            ClipToBounds = true,
        };

        var dialogContent = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,*"),
        };

        // Header with title and close button
        var hasTitle = !string.IsNullOrEmpty(title);
        var header = new Border
        {
            Padding = hasTitle
                ? new Thickness(Dbl("ThemeSpacingLg", 16), Dbl("ThemeSpacingMd", 12))
                : new Thickness(Dbl("ThemeSpacingSm", 8), 2),
            BorderBrush = hasTitle ? BorderSubtle : Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, hasTitle ? 1 : 0),
        };

        var headerContent = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
        };

        if (hasTitle)
        {
            var titleText = new TextBlock
            {
                Text = title,
                FontSize = FontSize("ThemeFontSizeLg", 18),
                FontWeight = FontWeight.SemiBold,
                Foreground = TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(titleText, 0);
            headerContent.Children.Add(titleText);
        }

        if (onCloseCommand != null)
        {
            var closeButton = new Button
            {
                Content = new TextBlock
                {
                    Text = "\u00D7", // x symbol
                    FontSize = FontSize("ThemeFontSizeXl", 20),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            closeButton.Click += (_, _) =>
            {
                SendCommand(onCloseCommand, "{}");
            };
            Grid.SetColumn(closeButton, 1);
            headerContent.Children.Add(closeButton);
        }

        header.Child = headerContent;
        Grid.SetRow(header, 0);
        dialogContent.Children.Add(header);

        // Body content with padding
        var body = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var bodyPadding = el.GetDoubleProp("padding", Dbl("ThemeSpacingLg", 20));
        if (el.TryGetProperty("children", out var children))
        {
            var panel = new StackPanel
            {
                Spacing = Dbl("ThemeSpacingMd", 12),
                Margin = new Thickness(bodyPadding),
            };
            foreach (var child in children.EnumerateArray())
            {
                panel.Children.Add(RenderComponent(child));
            }
            body.Content = panel;
        }

        Grid.SetRow(body, 1);
        dialogContent.Children.Add(body);

        dialog.Child = dialogContent;
        overlayPanel.Children.Add(dialog);

        // Schedule adding to OverlayLayer after the control is attached
        overlayPanel.AttachedToVisualTree += (sender, _) =>
        {
            if (sender is Panel panel)
            {
                var topLevel = TopLevel.GetTopLevel(panel);
                if (topLevel is Window window)
                {
                    var overlayLayer = OverlayLayer.GetOverlayLayer(topLevel);
                    if (overlayLayer != null)
                    {
                        // Create backdrop that fills the entire overlay
                        var backdropClone = new Border
                        {
                            Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)),
                        };
                        if (onCloseCommand != null)
                        {
                            backdropClone.PointerPressed += (_, _) =>
                            {
                                SendCommand(onCloseCommand, "{}");
                            };
                        }

                        // Use a Panel to host backdrop and centered dialog
                        // Bind size to window client area
                        var modalHost = new Panel();

                        // Set initial size and update on window resize
                        void UpdateSize()
                        {
                            modalHost.Width = window.ClientSize.Width;
                            modalHost.Height = window.ClientSize.Height;
                        }
                        UpdateSize();

                        // Subscribe to size changes
                        EventHandler<WindowResizedEventArgs>? resizeHandler = null;
                        resizeHandler = (_, _) => UpdateSize();
                        window.Resized += resizeHandler;

                        modalHost.Children.Add(backdropClone);

                        // Re-parent the dialog to the overlay - it's already set to center alignment
                        panel.Children.Remove(dialog);
                        modalHost.Children.Add(dialog);

                        overlayLayer.Children.Add(modalHost);

                        // Store reference for cleanup (include resize handler)
                        panel.Tag = (overlayLayer, modalHost, window, resizeHandler);

                        // Hide the placeholder panel
                        panel.IsVisible = false;
                    }
                }
            }
        };

        overlayPanel.DetachedFromVisualTree += (sender, _) =>
        {
            if (sender is Panel panel && panel.Tag is (OverlayLayer layer, Panel host, Window win, EventHandler<WindowResizedEventArgs> handler))
            {
                win.Resized -= handler;
                layer.Children.Remove(host);
            }
        };

        return overlayPanel;
    }

    private Control RenderStatusBar(JsonElement el)
    {
        var statusMessage = el.GetStringProp("status_message");

        var panel = new DockPanel
        {
            Margin = new Thickness(
                Dbl("ThemeSpacingLg", 16),
                Dbl("ThemeSpacingSm", 8)),
        };

        // Status message on the right
        if (!string.IsNullOrEmpty(statusMessage))
        {
            var statusText = new TextBlock
            {
                Text = statusMessage,
                FontSize = FontSize("ThemeFontSizeXsSm", 11),
                Foreground = TextMuted,
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(statusText, Dock.Right);
            panel.Children.Add(statusText);
        }

        // Items on the left
        if (el.TryGetProperty("items", out var items))
        {
            var itemsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Dbl("ThemeSpacingLg", 16),
            };
            foreach (var item in items.EnumerateArray())
            {
                var label = item.GetStringProp("label") ?? "";
                var value = item.GetStringProp("value") ?? "";
                itemsPanel.Children.Add(new TextBlock
                {
                    Text = $"{label}: {value}",
                    FontSize = FontSize("ThemeFontSizeXsSm", 11),
                    Foreground = TextMuted,
                });
            }
            panel.Children.Add(itemsPanel);
        }

        return new Border
        {
            BorderBrush = BorderSubtle,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = Surface,
            Child = panel,
        };
    }

    private Control RenderDetailHeader(JsonElement el)
    {
        var title = el.GetStringProp("title") ?? "";
        var icon = el.GetStringProp("icon") ?? "\uD83D\uDCC4"; // 📄
        var editable = el.GetBoolProp("editable", false);
        var titleCommand = el.GetStringProp("title_command");
        var iconCommand = el.GetStringProp("icon_command");
        var pageId = el.GetStringProp("page_id") ?? "";

        // Capture page state for button group and editor lock enforcement
        _currentPageIsLocked = el.GetBoolProp("is_locked", false);
        _currentPageIsArchived = el.GetBoolProp("is_archived", false);
        _currentPageIsTrashed = el.GetBoolProp("is_trashed", false);

        // Parse child pages for TOC "parent" mode
        _currentChildPages.Clear();
        if (el.TryGetProperty("child_pages", out var cpArr) && cpArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var cp in cpArr.EnumerateArray())
            {
                var cpId = cp.GetStringProp("id") ?? "";
                var cpTitle = cp.GetStringProp("title") ?? "";
                var cpIcon = cp.GetStringProp("icon") ?? "\uD83D\uDCC4";
                _currentChildPages.Add((cpId, cpTitle, cpIcon));
            }
        }

        var panel = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(
                Dbl("ThemeSpacingXl", 24),
                Dbl("ThemeSpacingMd", 12),
                Dbl("ThemeSpacingXl", 24),
                Dbl("ThemeSpacingXs", 4)),
        };

        // Icon + title row
        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Dbl("ThemeSpacingSm", 8),
        };

        // Clickable icon (emoji picker trigger) — hidden when show_icon is false
        var showIcon = el.GetBoolProp("show_icon", true);
        if (showIcon)
        {
            var iconBtn = new Button
            {
                Content = new TextBlock
                {
                    Text = icon,
                    FontSize = FontSize("ThemeFontSize2Xl3Xl", 34),
                },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (editable && iconCommand != null)
            {
                _pageIconBtn = iconBtn;
                _pageIconCommand = iconCommand;
                _pageIconPageId = pageId;
                iconBtn.Click += (_, _) => OpenPageEmojiPicker();
            }
            titleRow.Children.Add(iconBtn);
        }

        if (editable && titleCommand != null)
        {
            // Editable title — transparent TextBox that looks like a heading
            var titleBox = new TextBox
            {
                Text = title,
                FontSize = FontSize("ThemeFontSizeXl2Xl", 28),
                FontWeight = FontWeight.Bold,
                Foreground = TextPrimary,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 6),
                AcceptsReturn = false,
                TextWrapping = TextWrapping.NoWrap,
                Watermark = "Untitled",
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsReadOnly = _currentPageIsLocked,
            };

            // Auto-focus and select all when title is "Untitled" (new page)
            if (title == "Untitled")
            {
                titleBox.AttachedToVisualTree += (_, _) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        titleBox.Focus();
                        titleBox.SelectAll();
                    }, Avalonia.Threading.DispatcherPriority.Background);
                };
            }

            // Debounced title sync — keeps Rust state in sync on every keystroke
            // so that any re-render always has the latest title.
            // Capture page_id at creation time to prevent race conditions during navigation
            var titlePageId = pageId;
            System.Timers.Timer? titleDebounce = null;
            void CommitTitle()
            {
                titleDebounce?.Stop();
                titleDebounce?.Dispose();
                titleDebounce = null;
                // Include page_id so the plugin can verify this update is for the correct page
                // This prevents stale LostFocus events from updating the wrong page after navigation
                SendCommandSilent(titleCommand,
                    JsonSerializer.Serialize(new { title = titleBox.Text ?? "", page_id = titlePageId }));
            }

            var lastKnownTitle = title;
            titleBox.TextChanged += (_, _) =>
            {
                // Skip if the text hasn't actually changed (e.g. visual tree attachment)
                var current = titleBox.Text ?? "";
                if (current == lastKnownTitle) return;
                lastKnownTitle = current;

                // Live-update sidebar label (using titlePageId captured above)
                if (!string.IsNullOrEmpty(titlePageId) && _pageListTitles.TryGetValue(titlePageId, out var label))
                    label.Text = string.IsNullOrEmpty(current) ? "Untitled" : current;

                // Debounce the Rust state update (200ms)
                titleDebounce?.Stop();
                titleDebounce?.Dispose();
                titleDebounce = new System.Timers.Timer(200) { AutoReset = false };
                titleDebounce.Elapsed += (_, _) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => CommitTitle());
                };
                titleDebounce.Start();
            };

            // LostFocus — flush immediately + save
            titleBox.LostFocus += (_, _) =>
            {
                CommitTitle();
                SendCommandSilent("save_page", "{}");
            };

            // Enter commits and unfocuses; Cmd/Ctrl+E opens emoji picker
            titleBox.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter)
                {
                    CommitTitle();
                    SendCommandSilent("save_page", "{}");
                    if (titleBox.Parent is Control p)
                        p.Focus();
                    e.Handled = true;
                }
                else if (e.Key == Avalonia.Input.Key.E &&
                         (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
                {
                    OpenPageEmojiPicker();
                    e.Handled = true;
                }
            };

            titleRow.Children.Add(titleBox);
        }
        else
        {
            titleRow.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = FontSize("ThemeFontSizeXl2Xl", 28),
                FontWeight = FontWeight.Bold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        // Build action buttons panel (right-aligned in title row)
        var actionsHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Dbl("ThemeSpacingSm", 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Render action buttons from JSON (Save/Lock/Archive/Delete)
        if (el.TryGetProperty("actions", out var actions))
        {
            foreach (var action in actions.EnumerateArray())
            {
                actionsHost.Children.Add(RenderComponent(action));
            }
        }

        // Wrap title row in a DockPanel with actions right-aligned
        var titleDock = new DockPanel();
        DockPanel.SetDock(actionsHost, Dock.Right);
        titleDock.Children.Add(actionsHost);
        titleDock.Children.Add(titleRow);
        panel.Children.Add(titleDock);

        // Second row: metadata (left) + view toggle (right)
        var secondRow = new DockPanel
        {
            Margin = new Thickness(0, 2, 0, 0),
        };

        var viewToggle = el.GetBoolProp("view_toggle", false);
        if (viewToggle)
        {
            var toggleHost = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            _headerViewToggleHost = toggleHost;
            DockPanel.SetDock(toggleHost, Dock.Right);
            secondRow.Children.Add(toggleHost);
        }
        else
        {
            _headerViewToggleHost = null;
        }

        if (el.TryGetProperty("metadata", out var metadata))
        {
            var metaPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Dbl("ThemeSpacingMd", 12),
                VerticalAlignment = VerticalAlignment.Center,
            };
            foreach (var item in metadata.EnumerateArray())
            {
                var label = item.GetStringProp("label");
                var value = item.GetStringProp("value") ?? "";
                if (string.IsNullOrEmpty(value)) continue;
                var text = label != null ? $"{label}: {value}" : value;
                metaPanel.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = FontSize("ThemeFontSizeXs", 11),
                    Foreground = TextMuted,
                    FontStyle = FontStyle.Italic,
                });
            }
            secondRow.Children.Add(metaPanel);
        }

        panel.Children.Add(secondRow);

        // Properties row: stats/properties below metadata, separated by a border
        if (el.TryGetProperty("properties", out var properties))
        {
            var propPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Dbl("ThemeSpacingMd", 12),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, Dbl("ThemeSpacingSm", 8), 0, 0),
            };
            foreach (var item in properties.EnumerateArray())
            {
                var label = item.GetStringProp("label");
                var value = item.GetStringProp("value") ?? "";
                if (string.IsNullOrEmpty(value)) continue;
                var text = label != null ? $"{label}: {value}" : value;
                propPanel.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = FontSize("ThemeFontSizeXs", 11),
                    Foreground = TextMuted,
                });
            }

            var propBorder = new Border
            {
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, Dbl("ThemeSpacingSm", 8), 0, 0),
                Child = propPanel,
            };
            panel.Children.Add(propBorder);
        }

        return panel;
    }

    private static Control RenderSectionHeader(JsonElement el)
    {
        var text = el.GetStringProp("text") ?? "";
        var align = el.GetStringProp("align");

        return new TextBlock
        {
            Text = text.ToUpperInvariant(),
            FontSize = FontSize("ThemeFontSizeXs", 10),
            FontWeight = FontWeight.Bold,
            Foreground = TextMuted,
            Margin = new Thickness(
                Dbl("ThemeSpacingMd", 12),  // Align with feed_item/article_item text
                Dbl("ThemeSpacingLg", 16),
                Dbl("ThemeSpacingMd", 12),
                Dbl("ThemeSpacingSm", 8)),
            LetterSpacing = 1.5,
            HorizontalAlignment = align switch
            {
                "right" => HorizontalAlignment.Right,
                "center" => HorizontalAlignment.Center,
                _ => HorizontalAlignment.Left,
            },
        };
    }

    private Control RenderArticleItem(JsonElement el)
    {
        var title = el.GetStringProp("title") ?? "Untitled";
        var date = el.GetStringProp("date");
        var starred = el.GetBoolProp("starred", false);
        var unread = el.GetBoolProp("unread", false);
        var command = el.GetStringProp("command");
        var id = el.GetStringProp("id") ?? "";

        var panel = new StackPanel
        {
            Spacing = Dbl("ThemeSpacingXs", 4),
        };

        // Title row
        var titleRow = new DockPanel();

        if (starred)
        {
            var star = new TextBlock
            {
                Text = "\u2605",
                FontSize = FontSize("ThemeFontSizeSm", 12),
                Foreground = WarningBrush,
                Margin = new Thickness(0, 0, Dbl("ThemeSpacingXs", 4), 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(star, Dock.Left);
            titleRow.Children.Add(star);
        }

        titleRow.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = FontSize("ThemeFontSizeSmMd", 13),
            FontWeight = unread ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = unread ? TextPrimary : TextSecondary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
        });

        panel.Children.Add(titleRow);

        if (!string.IsNullOrEmpty(date))
        {
            panel.Children.Add(new TextBlock
            {
                Text = date,
                FontSize = FontSize("ThemeFontSizeXsSm", 11),
                Foreground = TextMuted,
            });
        }

        var wrapper = new Border
        {
            Child = panel,
            Padding = new Thickness(
                Dbl("ThemeSpacingSm", 8),
                Dbl("ThemeSpacingMd", 12)),
            Margin = new Thickness(
                Dbl("ThemeSpacingXs", 4), 0),  // 4px margin + 8px padding = 12px, aligns with section headers
            CornerRadius = Radius("ThemeRadiusXs"),
        };

        if (command != null)
        {
            wrapper.PointerEntered += (s, _) => { if (s is Border b) b.Background = HoverBrush; };
            wrapper.PointerExited += (s, _) => { if (s is Border b) b.Background = Brushes.Transparent; };
            wrapper.PointerPressed += (_, _) =>
            {
                SendCommand(command, JsonSerializer.Serialize(new { id }));
            };
            wrapper.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        }

        AttachContextMenu(wrapper, el, id);

        return wrapper;
    }

    private Control RenderFeedItem(JsonElement el)
    {
        var title = el.GetStringProp("title") ?? "Untitled";
        var subtitle = el.GetStringProp("subtitle");
        var projectName = el.GetStringProp("project_name");
        var priority = el.GetStringProp("priority");
        var unreadCount = el.GetIntProp("unread_count", 0);
        var isSelected = el.GetBoolProp("is_selected", false);
        var modifiedAt = el.GetInt64Prop("modified_at", 0);
        var command = el.GetStringProp("command");
        var id = el.GetStringProp("id") ?? "";

        // Determine priority border color
        IBrush? priorityBorder = priority switch
        {
            "urgent" => DangerBrush,
            "high" => WarningBrush,
            "medium" => Primary,
            "low" => SuccessBrush,
            _ => null,
        };

        var mainContent = new DockPanel();

        // Right side: due date and unread badge
        var rightPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Dbl("ThemeSpacingSm", 8),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Due date display
        if (modifiedAt > 0)
        {
            var dueDate = DateTimeOffset.FromUnixTimeSeconds(modifiedAt).LocalDateTime;
            var dueDateText = dueDate.ToString("MMM d");
            var isOverdue = dueDate.Date < DateTime.Today;
            rightPanel.Children.Add(new TextBlock
            {
                Text = dueDateText,
                FontSize = FontSize("ThemeFontSizeXsSm", 11),
                Foreground = isOverdue ? DangerBrush : TextMuted,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        // Unread badge
        if (unreadCount > 0)
        {
            rightPanel.Children.Add(new Border
            {
                Background = Primary,
                CornerRadius = Radius("ThemeRadiusFull"),
                Padding = new Thickness(5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = unreadCount.ToString(),
                    FontSize = FontSize("ThemeFontSizeXs", 10),
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.SemiBold,
                },
            });
        }

        if (rightPanel.Children.Count > 0)
        {
            DockPanel.SetDock(rightPanel, Dock.Right);
            mainContent.Children.Add(rightPanel);
        }

        // Center: title and subtitle stack
        var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = FontSize("ThemeFontSizeSmMd", 13),
            Foreground = TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        // Build subtitle line: formatted status + project name
        var subtitleParts = new List<string>();
        if (!string.IsNullOrEmpty(subtitle))
        {
            // Format status: "in_progress" → "In Progress", "todo" → "Todo", etc.
            var formattedStatus = subtitle switch
            {
                "todo" => "Todo",
                "in_progress" => "In Progress",
                "done" => "Done",
                "waiting" => "Waiting",
                "cancelled" => "Cancelled",
                "blocked" => "Blocked",
                "review" => "Review",
                "deferred" => "Deferred",
                _ => subtitle,
            };
            subtitleParts.Add(formattedStatus);
        }
        if (!string.IsNullOrEmpty(projectName))
            subtitleParts.Add(projectName);

        if (subtitleParts.Count > 0)
        {
            textStack.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", subtitleParts),
                FontSize = FontSize("ThemeFontSizeXsSm", 11),
                Foreground = TextMuted,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        mainContent.Children.Add(textStack);

        // Inner wrapper with padding
        var innerWrapper = new Border
        {
            Child = mainContent,
            Padding = new Thickness(
                Dbl("ThemeSpacingSm", 8),
                Dbl("ThemeSpacingSm", 8)),
        };

        // Outer wrapper with priority border and selection highlight
        // 4px margin + 8px padding = 12px total, aligns with section headers
        var wrapper = new Border
        {
            Child = innerWrapper,
            Margin = new Thickness(Dbl("ThemeSpacingXs", 4), 2),
            CornerRadius = Radius("ThemeRadiusXs"),
            BorderThickness = priorityBorder != null
                ? new Thickness(3, 0, 0, 0)
                : new Thickness(0),
            BorderBrush = priorityBorder,
            Background = isSelected ? HoverBrush : Brushes.Transparent,
        };

        if (command != null)
        {
            var capturedIsSelected = isSelected;
            wrapper.PointerEntered += (s, _) => { if (s is Border b) b.Background = HoverBrush; };
            wrapper.PointerExited += (s, _) => { if (s is Border b) b.Background = capturedIsSelected ? HoverBrush : Brushes.Transparent; };
            wrapper.PointerPressed += (_, _) =>
            {
                SendCommand(command, JsonSerializer.Serialize(new { id }));
            };
            wrapper.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        }

        AttachContextMenu(wrapper, el, id);

        return wrapper;
    }

    private static Control RenderSpacer(JsonElement el)
    {
        var height = el.GetDoubleProp("height", Dbl("ThemeSpacingSm", 8));
        return new Border { Height = height };
    }

    // ================================================================
    // File components (Archive plugin)
    // ================================================================

    /// <summary>
    /// Renders a breadcrumb navigation bar for folder hierarchy.
    /// JSON: { "type": "breadcrumbs", "items": [{"label":"...", "args":"..."}], "command": "select_folder" }
    /// </summary>
    private Control RenderBreadcrumbs(JsonElement el)
    {
        var command = el.GetStringProp("command");
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(
                Dbl("ThemeSpacingSm", 8),
                Dbl("ThemeSpacingXs", 4),
                Dbl("ThemeSpacingSm", 8),
                Dbl("ThemeSpacingXs", 4)),
        };

        if (!el.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return panel;

        var crumbs = items.EnumerateArray().ToList();
        for (int i = 0; i < crumbs.Count; i++)
        {
            var crumb = crumbs[i];
            var label = crumb.GetStringProp("label") ?? "";
            var args = crumb.GetStringProp("args") ?? "{}";
            var isLast = i == crumbs.Count - 1;

            var link = new TextBlock
            {
                Text = label,
                FontSize = FontSize("ThemeFontSizeSmMd", 13),
                Foreground = isLast ? TextPrimary : Primary,
                FontFamily = Font("ThemeFontSans"),
                FontWeight = isLast ? FontWeight.SemiBold : FontWeight.Normal,
                Cursor = isLast ? null : new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };

            if (!isLast && command != null)
            {
                link.PointerPressed += (_, _) => SendCommand(command, args);
                link.PointerEntered += (s, _) =>
                {
                    if (s is TextBlock tb) tb.TextDecorations = Avalonia.Media.TextDecorations.Underline;
                };
                link.PointerExited += (s, _) =>
                {
                    if (s is TextBlock tb) tb.TextDecorations = null;
                };
            }

            panel.Children.Add(link);

            if (!isLast)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "›",
                    FontSize = FontSize("ThemeFontSizeSmMd", 13),
                    Foreground = TextMuted,
                    Margin = new Thickness(4, 0),
                });
            }
        }

        return panel;
    }

    /// <summary>
    /// <summary>
    /// Renders a horizontal list of tag chips.
    /// JSON: { "type": "tag_list", "tags": ["tag1", "tag2"] }
    /// </summary>
    private Control RenderTagList(JsonElement el)
    {
        var panel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var maxVisible = el.GetIntProp("max_visible", 0); // 0 = show all
        var removable = el.GetBoolProp("removable", false);
        var removeCommand = el.GetStringProp("remove_command");

        if (el.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            var tagList = tags.EnumerateArray().ToList();
            var totalCount = tagList.Count;
            var visibleCount = maxVisible > 0 ? Math.Min(maxVisible, totalCount) : totalCount;

            for (int i = 0; i < visibleCount; i++)
            {
                var tag = tagList[i];
                var text = tag.ValueKind == JsonValueKind.String ? tag.GetString() ?? "" : tag.ToString();
                if (string.IsNullOrEmpty(text)) continue;

                var tagContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                tagContent.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = FontSize("ThemeFontSizeXsSm", 11),
                    Foreground = TextPrimary,
                    FontFamily = Font("ThemeFontSans"),
                    VerticalAlignment = VerticalAlignment.Center,
                });

                if (removable && removeCommand != null)
                {
                    var tagText = text;
                    var removeBtn = new Button
                    {
                        Content = "\u2715",
                        FontSize = FontSize("ThemeFontSize2Xs", 9),
                        Padding = new Thickness(0),
                        MinWidth = 0, MinHeight = 0,
                        Background = Brushes.Transparent,
                        Foreground = TextMuted,
                        BorderThickness = new Thickness(0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    };
                    removeBtn.Click += (_, _) => SendCommand(removeCommand,
                        System.Text.Json.JsonSerializer.Serialize(new { value = tagText }));
                    tagContent.Children.Add(removeBtn);
                }

                panel.Children.Add(new Border
                {
                    Background = SurfaceElevated,
                    CornerRadius = Radius("ThemeRadiusSm"),
                    Padding = new Thickness(Dbl("ThemeSpacingSm", 8), Dbl("ThemeSpacingXs", 4)),
                    Margin = new Thickness(0, 0, Dbl("ThemeSpacingXs", 4), Dbl("ThemeSpacingXs", 4)),
                    BorderBrush = BorderSubtle,
                    BorderThickness = new Thickness(1),
                    Child = tagContent,
                });
            }

            // "+N more" overflow indicator with hover popup
            var overflow = totalCount - visibleCount;
            if (overflow > 0)
            {
                // Build popup content: a vertical list of removable tag pills
                var popupStack = new StackPanel { Spacing = 4, MaxWidth = 150 };
                for (int j = visibleCount; j < totalCount; j++)
                {
                    var oTag = tagList[j];
                    var oText = oTag.ValueKind == JsonValueKind.String ? oTag.GetString() ?? "" : oTag.ToString();
                    if (string.IsNullOrEmpty(oText)) continue;

                    var pillContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                    pillContent.Children.Add(new TextBlock
                    {
                        Text = oText,
                        FontSize = FontSize("ThemeFontSizeXsSm", 11),
                        Foreground = TextPrimary,
                        FontFamily = Font("ThemeFontSans"),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });

                    if (removable && removeCommand != null)
                    {
                        var capturedText = oText;
                        var pillRemoveBtn = new Button
                        {
                            Content = "\u2715",
                            FontSize = FontSize("ThemeFontSize2Xs", 9),
                            Padding = new Thickness(0),
                            MinWidth = 0, MinHeight = 0,
                            Background = Brushes.Transparent,
                            Foreground = TextMuted,
                            BorderThickness = new Thickness(0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                        };
                        pillRemoveBtn.Click += (_, _) => SendCommand(removeCommand,
                            System.Text.Json.JsonSerializer.Serialize(new { value = capturedText }));
                        pillContent.Children.Add(pillRemoveBtn);
                    }

                    popupStack.Children.Add(new Border
                    {
                        Background = SurfaceElevated,
                        CornerRadius = Radius("ThemeRadiusSm"),
                        Padding = new Thickness(Dbl("ThemeSpacingSm", 8), Dbl("ThemeSpacingXs", 4)),
                        BorderBrush = BorderSubtle,
                        BorderThickness = new Thickness(1),
                        Child = pillContent,
                    });
                }

                var flyoutContent = new Border
                {
                    Background = Surface,
                    CornerRadius = Radius("ThemeRadiusMd"),
                    Padding = new Thickness(8),
                    BorderBrush = BorderSubtle,
                    BorderThickness = new Thickness(1),
                    Child = popupStack,
                };

                var moreBorder = new Border
                {
                    Background = Brushes.Transparent,
                    Padding = new Thickness(Dbl("ThemeSpacingSm", 8), Dbl("ThemeSpacingXs", 4)),
                    Margin = new Thickness(0, 0, Dbl("ThemeSpacingXs", 4), Dbl("ThemeSpacingXs", 4)),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    Child = new TextBlock
                    {
                        Text = $"+{overflow} more",
                        FontSize = FontSize("ThemeFontSizeXsSm", 11),
                        Foreground = TextMuted,
                        FontFamily = Font("ThemeFontSans"),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextDecorations = Avalonia.Media.TextDecorations.Underline,
                    },
                };

                var flyout = new Avalonia.Controls.Flyout
                {
                    Content = flyoutContent,
                    Placement = PlacementMode.Bottom,
                    ShowMode = FlyoutShowMode.TransientWithDismissOnPointerMoveAway,
                };
                moreBorder.PointerEntered += (_, _) => flyout.ShowAt(moreBorder);

                panel.Children.Add(moreBorder);
            }
        }

        return panel;
    }

    private Control RenderDatePicker(JsonElement el)
    {
        var placeholder = el.GetStringProp("placeholder") ?? "Select date";
        var command = el.GetStringProp("command") ?? "";
        var clearCommand = el.GetStringProp("clear_command");
        var allowClear = el.GetBoolProp("allow_clear", false);

        var picker = new CalendarDatePicker
        {
            Watermark = placeholder,
            FontSize = FontSize("ThemeFontSizeSm", 12),
            FontFamily = Font("ThemeFontSans"),
            Foreground = TextPrimary,
            MinWidth = 140,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };

        // Parse unix timestamp value → local date (handle both string and number)
        long ts = 0;
        if (el.TryGetProperty("value", out var valueProp))
        {
            if (valueProp.ValueKind == JsonValueKind.Number)
                ts = valueProp.GetInt64();
            else if (valueProp.ValueKind == JsonValueKind.String)
            {
                var rawValue = valueProp.GetString();
                if (!string.IsNullOrEmpty(rawValue) && rawValue != "null")
                    long.TryParse(rawValue, out ts);
            }
        }
        if (ts != 0)
        {
            var dto = DateTimeOffset.FromUnixTimeSeconds(ts);
            picker.SelectedDate = dto.LocalDateTime;
        }

        picker.SelectedDateChanged += (_, _) =>
        {
            if (picker.SelectedDate is { } selectedDate)
            {
                var utc = selectedDate.ToUniversalTime();
                var utcTs = new DateTimeOffset(utc).ToUnixTimeSeconds();
                SendCommandDeferred(command, JsonSerializer.Serialize(new { value = utcTs.ToString() }));
            }
            else if (allowClear && clearCommand != null)
            {
                SendCommandDeferred(clearCommand, "{}");
            }
        };

        return picker;
    }

    /// <summary>
    /// Renders a month calendar grid with day cells and events.
    /// </summary>
    private Control RenderCalendarGrid(JsonElement el)
    {
        var month = el.GetStringProp("month") ?? "";
        var year = el.GetStringProp("year") ?? "";
        var today = el.GetStringProp("today") ?? "";
        var selectedDate = el.GetStringProp("selected_date") ?? "";
        var onDayClick = el.GetStringProp("on_day_click") ?? "";
        var onDayDoubleClick = el.GetStringProp("on_day_double_click") ?? "";
        var onEventClick = el.GetStringProp("on_event_click") ?? "";
        var onEventDoubleClick = el.GetStringProp("on_event_double_click") ?? "";
        var weekStart = el.GetStringProp("week_start") ?? "sunday";
        var flex = el.GetBoolProp("flex", false);

        // Parse month/year for grid generation (fallback to current month/year)
        var now = DateTime.Now;
        if (!int.TryParse(month, out var monthNum)) monthNum = now.Month;
        if (!int.TryParse(year, out var yearNum)) yearNum = now.Year;

        // Default today to actual today if not provided
        if (string.IsNullOrEmpty(today))
            today = $"{now.Year}-{now.Month:D2}-{now.Day:D2}";

        // Parse days data from template
        var daysData = new List<CalendarDayData>();
        if (el.TryGetProperty("days", out var daysEl) && daysEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var day in daysEl.EnumerateArray())
            {
                var dayData = new CalendarDayData
                {
                    Date = day.GetStringProp("date") ?? "",
                    DayNumber = day.GetIntProp("day", 0),
                    IsCurrentMonth = day.GetBoolProp("is_current_month", true),
                    IsToday = day.GetBoolProp("is_today", false),
                    IsSelected = day.GetBoolProp("is_selected", false),
                    Events = new List<CalendarEventData>()
                };
                if (day.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
                {
                    foreach (var evt in events.EnumerateArray())
                    {
                        dayData.Events.Add(new CalendarEventData
                        {
                            Id = evt.GetStringProp("id") ?? "",
                            Title = evt.GetStringProp("title") ?? "",
                            Color = evt.GetStringProp("color"),
                            IsAllDay = evt.GetBoolProp("is_all_day", false),
                            Start = evt.GetInt64Prop("start", 0),
                            End = evt.GetInt64Prop("end", 0),
                            Description = evt.GetStringProp("description"),
                            Location = evt.GetStringProp("location"),
                            CalendarName = evt.GetStringProp("calendar_name"),
                            Status = evt.GetStringProp("status"),
                            Tags = ParseStringArray(evt, "tags"),
                        });
                    }
                }
                daysData.Add(dayData);
            }
        }

        // If no days data provided, generate a basic month grid
        if (daysData.Count == 0)
        {
            daysData = GenerateMonthDays(yearNum, monthNum, today, selectedDate, weekStart == "monday");
        }

        // Build the grid
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,*,*,*,*,*"),
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*,*"),
        };

        if (flex)
        {
            grid.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            grid.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        }

        // Header row with day names
        var dayNames = weekStart == "monday"
            ? new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }
            : new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

        for (int col = 0; col < 7; col++)
        {
            var header = new TextBlock
            {
                Text = dayNames[col],
                FontSize = FontSize("ThemeFontSizeSm", 12),
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = TextMuted,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Thickness(4, 8),
            };
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, col);
            grid.Children.Add(header);
        }

        // Day cells
        for (int i = 0; i < daysData.Count && i < 42; i++)
        {
            var day = daysData[i];
            int row = (i / 7) + 1;
            int col = i % 7;

            var cell = CreateCalendarDayCell(day, onDayClick, onDayDoubleClick, onEventClick, onEventDoubleClick);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            grid.Children.Add(cell);
        }

        return grid;
    }

    private record CalendarDayData
    {
        public string Date { get; init; } = "";
        public int DayNumber { get; init; }
        public bool IsCurrentMonth { get; init; }
        public bool IsToday { get; init; }
        public bool IsSelected { get; init; }
        public List<CalendarEventData> Events { get; init; } = new();
    }

    private record CalendarEventData
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public string? Color { get; init; }
        public bool IsAllDay { get; init; }
        public long Start { get; init; }
        public long End { get; init; }
        public string? Description { get; init; }
        public string? Location { get; init; }
        public string? CalendarName { get; init; }
        public string? Status { get; init; }
        public string[]? Tags { get; init; }

        /// Formats the event time as a short string like "9am" or "2:30pm"
        public string TimeLabel
        {
            get
            {
                if (IsAllDay || Start == 0) return "";
                var dt = DateTimeOffset.FromUnixTimeSeconds(Start).LocalDateTime;
                var min = dt.Minute;
                var hour = dt.Hour;
                var ampm = hour >= 12 ? "pm" : "am";
                var h12 = hour % 12;
                if (h12 == 0) h12 = 12;
                return min == 0 ? $"{h12}{ampm}" : $"{h12}:{min:D2}{ampm}";
            }
        }

        /// Formats the event time range like "9:00 AM – 10:30 AM"
        public string TimeRangeLabel
        {
            get
            {
                if (IsAllDay || Start == 0) return "All Day";
                var startDt = DateTimeOffset.FromUnixTimeSeconds(Start).LocalDateTime;
                var endDt = DateTimeOffset.FromUnixTimeSeconds(End).LocalDateTime;
                return $"{FormatTime12(startDt)} – {FormatTime12(endDt)}";
            }
        }

        private static string FormatTime12(DateTime dt)
        {
            var h = dt.Hour % 12;
            if (h == 0) h = 12;
            var ampm = dt.Hour >= 12 ? "PM" : "AM";
            return dt.Minute == 0 ? $"{h} {ampm}" : $"{h}:{dt.Minute:D2} {ampm}";
        }
    }

    private List<CalendarDayData> GenerateMonthDays(int year, int month, string today, string selectedDate, bool mondayStart)
    {
        var days = new List<CalendarDayData>();
        var firstOfMonth = new DateTime(year, month, 1);
        var daysInMonth = DateTime.DaysInMonth(year, month);

        // Calculate starting day of week
        int startDow = (int)firstOfMonth.DayOfWeek;
        if (mondayStart)
            startDow = startDow == 0 ? 6 : startDow - 1;

        // Previous month days
        var prevMonth = firstOfMonth.AddMonths(-1);
        var prevMonthDays = DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month);
        for (int i = startDow - 1; i >= 0; i--)
        {
            var d = prevMonthDays - i;
            var date = $"{prevMonth.Year}-{prevMonth.Month:D2}-{d:D2}";
            days.Add(new CalendarDayData
            {
                Date = date,
                DayNumber = d,
                IsCurrentMonth = false,
                IsToday = date == today,
                IsSelected = date == selectedDate
            });
        }

        // Current month days
        for (int d = 1; d <= daysInMonth; d++)
        {
            var date = $"{year}-{month:D2}-{d:D2}";
            days.Add(new CalendarDayData
            {
                Date = date,
                DayNumber = d,
                IsCurrentMonth = true,
                IsToday = date == today,
                IsSelected = date == selectedDate
            });
        }

        // Next month days to fill grid (up to 42 cells)
        var nextMonth = firstOfMonth.AddMonths(1);
        int remaining = 42 - days.Count;
        for (int d = 1; d <= remaining; d++)
        {
            var date = $"{nextMonth.Year}-{nextMonth.Month:D2}-{d:D2}";
            days.Add(new CalendarDayData
            {
                Date = date,
                DayNumber = d,
                IsCurrentMonth = false,
                IsToday = date == today,
                IsSelected = date == selectedDate
            });
        }

        return days;
    }

    private Control CreateCalendarDayCell(CalendarDayData day, string onDayClick, string onDayDoubleClick, string onEventClick, string onEventDoubleClick)
    {
        var cell = new Border
        {
            BorderThickness = new Thickness(0.5),
            BorderBrush = BorderSubtle,
            MinHeight = 80,
            Padding = new Thickness(4),
            Background = day.IsSelected
                ? PrimaryMuted
                : day.IsToday
                    ? HoverBrush
                    : Avalonia.Media.Brushes.Transparent,
        };

        var stack = new StackPanel { Spacing = 2 };

        // Day number
        var dayText = new TextBlock
        {
            Text = day.DayNumber.ToString(),
            FontSize = FontSize("ThemeFontSizeSm", 12),
            FontWeight = day.IsToday ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal,
            Foreground = day.IsCurrentMonth ? TextPrimary : TextMuted,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 4, 4),
        };

        if (day.IsToday)
        {
            // Circle around today's date
            var todayBorder = new Border
            {
                Background = Primary,
                CornerRadius = new CornerRadius(12),
                Width = 24,
                Height = 24,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Child = new TextBlock
                {
                    Text = day.DayNumber.ToString(),
                    FontSize = FontSize("ThemeFontSizeSm", 12),
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    Foreground = Avalonia.Media.Brushes.White,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                }
            };
            stack.Children.Add(todayBorder);
        }
        else
        {
            stack.Children.Add(dayText);
        }

        // Events (show up to 3, then "+N more")
        var eventsToShow = day.Events.Take(3).ToList();
        foreach (var evt in eventsToShow)
        {
            var timeLabel = evt.TimeLabel;
            var displayText = string.IsNullOrEmpty(timeLabel) ? evt.Title : $"{timeLabel} {evt.Title}";

            var pillContent = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 3 };
            if (!string.IsNullOrEmpty(timeLabel))
            {
                pillContent.Children.Add(new TextBlock
                {
                    Text = timeLabel,
                    FontSize = FontSize("ThemeFontSizeXs", 10),
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Avalonia.Media.Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            pillContent.Children.Add(new TextBlock
            {
                Text = evt.Title,
                FontSize = FontSize("ThemeFontSizeXs", 10),
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.White, 0.9),
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var eventPill = new Border
            {
                Background = !string.IsNullOrEmpty(evt.Color)
                    ? TryParseBrush(evt.Color)
                    : Primary,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(0, 1),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                ClipToBounds = true,
                Child = pillContent,
            };

            var capturedEvt = evt;

            // Use a generous custom double-click detector (600ms window) instead of
            // DoubleTapped which relies on the OS threshold and is often too fast.
            eventPill.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(eventPill).Properties.IsLeftButtonPressed) return;
                e.Handled = true;

                var now = DateTime.UtcNow;
                if (_lastEventPillClickId == capturedEvt.Id
                    && (now - _lastEventPillClickTime).TotalMilliseconds < EventDoubleClickMs
                    && !string.IsNullOrEmpty(onEventDoubleClick))
                {
                    // Double-click detected
                    _lastEventPillClickId = null;
                    _lastEventPillClickTime = DateTime.MinValue;
                    CloseActiveEventPopup();
                    SendCommand(onEventDoubleClick, JsonSerializer.Serialize(new { id = capturedEvt.Id }));
                }
                else
                {
                    // First click — show popup
                    _lastEventPillClickId = capturedEvt.Id;
                    _lastEventPillClickTime = now;
                    ShowEventDetailPopup(capturedEvt, eventPill, onEventClick);
                }
            };

            stack.Children.Add(eventPill);
        }

        if (day.Events.Count > 3)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"+{day.Events.Count - 3} more",
                FontSize = FontSize("ThemeFontSize2Xs", 9),
                Foreground = TextMuted,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        cell.Child = stack;

        // Day click handler
        if (!string.IsNullOrEmpty(onDayClick) || !string.IsNullOrEmpty(onDayDoubleClick))
        {
            cell.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            var capturedDate = day.Date;

            if (!string.IsNullOrEmpty(onDayClick))
            {
                cell.PointerPressed += (_, e) =>
                {
                    if (e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed)
                    {
                        SendCommand(onDayClick, JsonSerializer.Serialize(new { date = capturedDate }));
                    }
                };
            }

            if (!string.IsNullOrEmpty(onDayDoubleClick))
            {
                cell.DoubleTapped += (_, _) =>
                {
                    SendCommand(onDayDoubleClick, JsonSerializer.Serialize(new { date = capturedDate }));
                };
            }
        }

        return cell;
    }

    private static string[]? ParseStringArray(JsonElement el, string propName)
    {
        if (!el.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            var s = item.GetString();
            if (!string.IsNullOrEmpty(s)) list.Add(s);
        }
        return list.Count > 0 ? list.ToArray() : null;
    }

    private void CloseActiveEventPopup()
    {
        if (_activeEventPopup != null)
        {
            _activeEventPopup.IsOpen = false;
            _activeEventPopup = null;
        }
    }

    /// Show an event detail popup anchored at the clicked control.
    private void ShowEventDetailPopup(
        string eventId,
        string title,
        string timeRange,
        string? location,
        string? description,
        string? calendarName,
        string? color,
        string? status,
        string[]? tags,
        long startTs,
        long endTs,
        bool isAllDay,
        Control anchor,
        string onEventClick)
    {
        CloseActiveEventPopup();

        // Send select_event so plugin state tracks the selection
        if (!string.IsNullOrEmpty(onEventClick))
            SendCommand(onEventClick, JsonSerializer.Serialize(new { id = eventId }));

        var eventBrush = !string.IsNullOrEmpty(color)
            ? TryParseBrush(color)
            : Primary;
        var eventColor = eventBrush is Avalonia.Media.SolidColorBrush scbPopup
            ? scbPopup.Color
            : Avalonia.Media.Colors.DodgerBlue;

        // --- Build popup content ---
        // Outer horizontal: accent bar | content
        var outerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("4,*"),
            MinWidth = 280,
            MaxWidth = 360,
        };

        // Left color accent bar
        var accentBar = new Border
        {
            Background = eventBrush,
            CornerRadius = new CornerRadius(4, 0, 0, 4),
        };
        Grid.SetColumn(accentBar, 0);
        outerGrid.Children.Add(accentBar);

        // Right content area
        var contentStack = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(Dbl("ThemeSpacingMd", 12)),
        };
        Grid.SetColumn(contentStack, 1);
        outerGrid.Children.Add(contentStack);

        // Top-right action buttons row
        var actionRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 2,
            Margin = new Thickness(0, 0, 0, 4),
        };

        Border MakeIconButton(string icon, string tooltip, Action onClick)
        {
            var btn = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                Background = Avalonia.Media.Brushes.Transparent,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = icon,
                    FontSize = FontSize("ThemeFontSizeSm", 12),
                    Foreground = TextMuted,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
            };
            ToolTip.SetTip(btn, tooltip);
            btn.PointerEntered += (s, _) =>
            {
                if (s is Border b) b.Background = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.Parse("#20808080"));
            };
            btn.PointerExited += (s, _) =>
            {
                if (s is Border b) b.Background = Avalonia.Media.Brushes.Transparent;
            };
            btn.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                onClick();
            };
            return btn;
        }

        actionRow.Children.Add(MakeIconButton("\u270E", "Edit", () =>
        {
            CloseActiveEventPopup();
            SendCommand("open_edit_event_dialog", JsonSerializer.Serialize(new { id = eventId }));
        }));
        actionRow.Children.Add(MakeIconButton("\uD83D\uDDD1", "Delete", () =>
        {
            CloseActiveEventPopup();
            SendCommand("delete_event", JsonSerializer.Serialize(new { id = eventId }));
        }));
        actionRow.Children.Add(MakeIconButton("\u2715", "Close", () =>
        {
            CloseActiveEventPopup();
        }));

        contentStack.Children.Add(actionRow);

        // Title
        contentStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = FontSize("ThemeFontSizeLg", 18),
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimary,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 6),
        });

        // Date + time row with icon
        var dateTimeLabel = FormatEventDateRange(startTs, endTs, isAllDay);
        if (!string.IsNullOrEmpty(dateTimeLabel))
        {
            var dtRow = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 0, 0, 4),
            };
            dtRow.Children.Add(new TextBlock
            {
                Text = "\uD83D\uDD50",
                FontSize = FontSize("ThemeFontSizeSm", 12),
                Foreground = TextMuted,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            dtRow.Children.Add(new TextBlock
            {
                Text = dateTimeLabel,
                FontSize = FontSize("ThemeFontSizeSm", 12),
                Foreground = TextSecondary,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            contentStack.Children.Add(dtRow);
        }

        // Location row with icon
        if (!string.IsNullOrEmpty(location))
        {
            var locRow = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 0, 0, 4),
            };
            locRow.Children.Add(new TextBlock
            {
                Text = "\uD83D\uDCCD",
                FontSize = FontSize("ThemeFontSizeSm", 12),
                Foreground = TextMuted,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            locRow.Children.Add(new TextBlock
            {
                Text = location,
                FontSize = FontSize("ThemeFontSizeSm", 12),
                Foreground = TextSecondary,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            contentStack.Children.Add(locRow);
        }

        // Calendar row with color dot
        if (!string.IsNullOrEmpty(calendarName))
        {
            var calRow = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 0, 0, 4),
            };
            calRow.Children.Add(new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = eventBrush,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(1, 0, 0, 0),
            });
            calRow.Children.Add(new TextBlock
            {
                Text = calendarName,
                FontSize = FontSize("ThemeFontSizeSm", 12),
                Foreground = TextSecondary,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            contentStack.Children.Add(calRow);
        }

        // Status (only if non-default)
        if (!string.IsNullOrEmpty(status) && status != "confirmed")
        {
            var statusRow = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 0, 0, 4),
            };
            statusRow.Children.Add(new TextBlock
            {
                Text = "\u25CF",
                FontSize = FontSize("ThemeFontSizeXs", 10),
                Foreground = TextMuted,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(1, 0, 0, 0),
            });
            statusRow.Children.Add(new TextBlock
            {
                Text = char.ToUpper(status[0]) + status[1..],
                FontSize = FontSize("ThemeFontSizeSm", 12),
                Foreground = TextSecondary,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            contentStack.Children.Add(statusRow);
        }

        // Description (separated)
        if (!string.IsNullOrEmpty(description))
        {
            contentStack.Children.Add(new Border
            {
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 8, 0, 0),
                Margin = new Thickness(0, 6, 0, 0),
                Child = new TextBlock
                {
                    Text = description,
                    FontSize = FontSize("ThemeFontSizeSm", 12),
                    Foreground = TextSecondary,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    MaxLines = 5,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                }
            });
        }

        // Tags
        if (tags is { Length: > 0 })
        {
            var tagRow = new WrapPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 0),
            };
            foreach (var tag in tags)
            {
                tagRow.Children.Add(new Border
                {
                    Background = new Avalonia.Media.SolidColorBrush(eventColor, 0.15),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(8, 2),
                    Margin = new Thickness(0, 0, 4, 2),
                    Child = new TextBlock
                    {
                        Text = tag,
                        FontSize = FontSize("ThemeFontSizeXs", 10),
                        Foreground = TextSecondary,
                    }
                });
            }
            contentStack.Children.Add(tagRow);
        }

        // Popup border
        var popupBorder = new Border
        {
            Child = outerGrid,
            Background = SurfaceElevated,
            BorderBrush = BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 16, OffsetY = 6, Color = Avalonia.Media.Color.Parse("#40000000") }),
        };

        var popup = new Popup
        {
            Child = popupBorder,
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = 0,
            IsLightDismissEnabled = true,
        };

        popup.Closed += (_, _) =>
        {
            if (_activeEventPopup == popup)
                _activeEventPopup = null;
            // Clean up from visual tree
            if (anchor.Parent is Panel pp)
                pp.Children.Remove(popup);
        };

        // Add popup to visual tree
        if (anchor.Parent is Panel parentPanel)
            parentPanel.Children.Add(popup);

        _activeEventPopup = popup;
        popup.IsOpen = true;
    }

    /// Format a readable date range like "Tuesday, Feb 4 · 9:00 AM – 10:30 AM"
    private static string FormatEventDateRange(long startTs, long endTs, bool isAllDay)
    {
        if (startTs == 0) return "";
        var startDt = DateTimeOffset.FromUnixTimeSeconds(startTs).LocalDateTime;
        var endDt = DateTimeOffset.FromUnixTimeSeconds(endTs).LocalDateTime;

        var dayLabel = $"{startDt:dddd}, {startDt:MMM d}";
        if (isAllDay)
        {
            if (startDt.Date == endDt.Date || endTs == 0)
                return dayLabel;
            return $"{dayLabel} – {endDt:dddd}, {endDt:MMM d}";
        }

        string FmtTime(DateTime dt)
        {
            var h = dt.Hour % 12;
            if (h == 0) h = 12;
            var ampm = dt.Hour >= 12 ? "PM" : "AM";
            return dt.Minute == 0 ? $"{h} {ampm}" : $"{h}:{dt.Minute:D2} {ampm}";
        }

        if (startDt.Date == endDt.Date)
            return $"{dayLabel} \u00B7 {FmtTime(startDt)} – {FmtTime(endDt)}";

        return $"{startDt:MMM d}, {FmtTime(startDt)} – {endDt:MMM d}, {FmtTime(endDt)}";
    }

    /// Overload for CalendarEventData (month view pills)
    private void ShowEventDetailPopup(CalendarEventData evt, Control anchor, string onEventClick)
    {
        ShowEventDetailPopup(
            evt.Id, evt.Title, evt.TimeRangeLabel,
            evt.Location, evt.Description, evt.CalendarName,
            evt.Color, evt.Status, evt.Tags,
            evt.Start, evt.End, evt.IsAllDay,
            anchor, onEventClick);
    }

    /// Overload for WeekEventData (week/day view blocks)
    private void ShowEventDetailPopup(WeekEventData evt, Control anchor, string onEventClick)
    {
        var timeRange = evt.IsAllDay ? "All Day" : evt.TimeRangeLabel;
        ShowEventDetailPopup(
            evt.Id, evt.Title, timeRange,
            evt.Location, evt.Description, evt.CalendarName,
            evt.Color, evt.Status, evt.Tags,
            evt.Start, evt.End, evt.IsAllDay,
            anchor, onEventClick);
    }

    private Avalonia.Media.IBrush TryParseBrush(string color)
    {
        try
        {
            if (color.StartsWith("#"))
                return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(color));

            // Named colors — these are explicit plugin-specified palette colors
            return color.ToLowerInvariant() switch
            {
                "red" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#EF4444")),
                "green" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#22C55E")),
                "blue" => Primary,
                "yellow" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#EAB308")),
                "orange" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F97316")),
                "purple" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#A855F7")),
                "pink" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#EC4899")),
                "teal" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#14B8A6")),
                "gray" or "grey" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#6B7280")),
                _ => Primary,
            };
        }
        catch
        {
            return Primary;
        }
    }

    // Event pill double-click detection with generous threshold
    private const int EventDoubleClickMs = 600;
    private DateTime _lastEventPillClickTime = DateTime.MinValue;
    private string? _lastEventPillClickId;

    // Active event detail popup
    private Popup? _activeEventPopup;

    // Week grid drag state
    private bool _weekEventDragging;
    private bool _weekEventResizing;
    private string? _weekDragEventId;
    private string? _weekDragSourceDate;
    private double _weekDragStartHour;
    private double _weekDragDuration;
    private Point _weekDragStartPoint;
    private Border? _weekDragPreview;
    private Canvas? _weekDragCanvas;
    private Grid? _weekDayColumnsGrid;
    private double _weekHourHeight;
    private int _weekStartHour;
    private List<WeekDayData>? _weekDaysData;
    private string _weekOnEventUpdate = "";

    /// <summary>
    /// Renders a week grid with time slots and positioned events.
    /// Supports drag-to-move and drag-to-resize with 15-minute snapping.
    /// </summary>
    private Control RenderWeekGrid(JsonElement el)
    {
        var today = el.GetStringProp("today") ?? "";
        var onDayClick = el.GetStringProp("on_day_click") ?? "";
        var onDayDoubleClick = el.GetStringProp("on_day_double_click") ?? "";
        var onEventClick = el.GetStringProp("on_event_click") ?? "";
        var onEventDoubleClick = el.GetStringProp("on_event_double_click") ?? "";
        var onEventUpdate = el.GetStringProp("on_event_update") ?? "update_event_time";
        var hourHeight = el.GetDoubleProp("hour_height", 60);
        var startHour = el.GetIntProp("start_hour", 0);
        var endHour = el.GetIntProp("end_hour", 24);

        _weekHourHeight = hourHeight;
        _weekStartHour = startHour;
        _weekOnEventUpdate = onEventUpdate;

        // Parse days data
        var daysData = new List<WeekDayData>();
        if (el.TryGetProperty("days", out var daysEl) && daysEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var day in daysEl.EnumerateArray())
            {
                var dayData = new WeekDayData
                {
                    Date = day.GetStringProp("date") ?? "",
                    DayName = day.GetStringProp("day_name") ?? "",
                    Label = day.GetStringProp("label") ?? "",
                    DayNumber = day.GetIntProp("day_number", 0),
                    IsToday = day.GetBoolProp("is_today", false),
                    IsSelected = day.GetBoolProp("is_selected", false),
                    Events = new List<WeekEventData>()
                };
                if (day.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
                {
                    foreach (var evt in events.EnumerateArray())
                    {
                        dayData.Events.Add(new WeekEventData
                        {
                            Id = evt.GetStringProp("id") ?? "",
                            Title = evt.GetStringProp("title") ?? "",
                            Color = evt.GetStringProp("color"),
                            StartHour = evt.GetDoubleProp("start_hour", 0),
                            DurationHours = evt.GetDoubleProp("duration_hours", 1),
                            IsAllDay = evt.GetBoolProp("is_all_day", false),
                            Start = evt.GetInt64Prop("start", 0),
                            End = evt.GetInt64Prop("end", 0),
                            Description = evt.GetStringProp("description"),
                            Location = evt.GetStringProp("location"),
                            CalendarName = evt.GetStringProp("calendar_name"),
                            Status = evt.GetStringProp("status"),
                            Tags = ParseStringArray(evt, "tags"),
                        });
                    }
                }
                daysData.Add(dayData);
            }
        }
        _weekDaysData = daysData;

        var totalHours = endHour - startHour;

        // Main container with drag overlay
        var rootGrid = new Grid();

        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            ColumnDefinitions = new ColumnDefinitions("50,*"),
        };

        // Day headers row
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", daysData.Count))),
            Margin = new Thickness(0, 0, 0, 4),
        };
        Grid.SetRow(headerGrid, 0);
        Grid.SetColumn(headerGrid, 1);

        for (int i = 0; i < daysData.Count; i++)
        {
            var day = daysData[i];
            var headerStack = new StackPanel
            {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };

            headerStack.Children.Add(new TextBlock
            {
                Text = day.DayName,
                FontSize = FontSize("ThemeFontSizeXs", 10),
                Foreground = TextMuted,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            });

            var dayNumText = new TextBlock
            {
                Text = day.DayNumber.ToString(),
                FontSize = FontSize("ThemeFontSizeLg", 18),
                FontWeight = Avalonia.Media.FontWeight.Bold,
                Foreground = day.IsToday ? Avalonia.Media.Brushes.White : TextPrimary,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };

            if (day.IsToday)
            {
                var todayCircle = new Border
                {
                    Background = Primary,
                    CornerRadius = new CornerRadius(16),
                    Width = 32,
                    Height = 32,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Child = dayNumText,
                };
                dayNumText.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
                headerStack.Children.Add(todayCircle);
            }
            else
            {
                headerStack.Children.Add(dayNumText);
            }

            Grid.SetColumn(headerStack, i);
            headerGrid.Children.Add(headerStack);
        }
        mainGrid.Children.Add(headerGrid);

        // All-day events row - sticky header that stays visible when scrolling
        var hasAnyAllDayEvents = daysData.Any(d => d.Events.Any(e => e.IsAllDay));

        var allDayRowGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("50,*"),
            MinHeight = hasAnyAllDayEvents ? 30 : 0,
        };

        // Label
        var allDayLabel = new TextBlock
        {
            Text = hasAnyAllDayEvents ? "all-day" : "",
            FontSize = FontSize("ThemeFontSizeXs", 10),
            Foreground = TextMuted,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(allDayLabel, 0);
        allDayRowGrid.Children.Add(allDayLabel);

        // All-day events grid for each day
        var allDayEventsGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", daysData.Count))),
        };

        for (int i = 0; i < daysData.Count; i++)
        {
            var day = daysData[i];
            var allDayEvents = day.Events.Where(e => e.IsAllDay).ToList();

            if (allDayEvents.Any())
            {
                var dayAllDayPanel = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Vertical,
                    Spacing = 2,
                    Margin = new Thickness(2, 2),
                };

                foreach (var evt in allDayEvents)
                {
                    var capturedEvt = evt;
                    var eventPill = new Border
                    {
                        Background = !string.IsNullOrEmpty(evt.Color)
                            ? TryParseBrush(evt.Color)
                            : Primary,
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(6, 2),
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                        Child = new TextBlock
                        {
                            Text = evt.Title,
                            FontSize = FontSize("ThemeFontSizeXs", 10),
                            Foreground = Avalonia.Media.Brushes.White,
                            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                        }
                    };

                    eventPill.PointerPressed += (_, e) =>
                    {
                        if (!e.GetCurrentPoint(eventPill).Properties.IsLeftButtonPressed) return;
                        e.Handled = true;

                        var now = DateTime.UtcNow;
                        if (_lastEventPillClickId == capturedEvt.Id
                            && (now - _lastEventPillClickTime).TotalMilliseconds < EventDoubleClickMs
                            && !string.IsNullOrEmpty(onEventDoubleClick))
                        {
                            _lastEventPillClickId = null;
                            _lastEventPillClickTime = DateTime.MinValue;
                            CloseActiveEventPopup();
                            SendCommand(onEventDoubleClick, JsonSerializer.Serialize(new { id = capturedEvt.Id }));
                        }
                        else
                        {
                            _lastEventPillClickId = capturedEvt.Id;
                            _lastEventPillClickTime = now;
                            ShowEventDetailPopup(capturedEvt, eventPill, onEventClick);
                        }
                    };

                    dayAllDayPanel.Children.Add(eventPill);
                }

                Grid.SetColumn(dayAllDayPanel, i);
                allDayEventsGrid.Children.Add(dayAllDayPanel);
            }
        }

        Grid.SetColumn(allDayEventsGrid, 1);
        allDayRowGrid.Children.Add(allDayEventsGrid);

        var allDayRowBorder = new Border
        {
            BorderBrush = BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = allDayRowGrid,
        };
        Grid.SetRow(allDayRowBorder, 1);
        Grid.SetColumnSpan(allDayRowBorder, 2);
        mainGrid.Children.Add(allDayRowBorder);

        // Scrollable time grid
        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scrollViewer, 2);
        Grid.SetColumnSpan(scrollViewer, 2);

        var timeGridOuter = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("50,*"),
        };

        // Time labels column
        var timeLabelsPanel = new StackPanel();
        for (int hour = startHour; hour < endHour; hour++)
        {
            var label = hour == 0 ? "12 AM" :
                        hour < 12 ? $"{hour} AM" :
                        hour == 12 ? "12 PM" :
                        $"{hour - 12} PM";

            timeLabelsPanel.Children.Add(new Border
            {
                Height = hourHeight,
                Child = new TextBlock
                {
                    Text = label,
                    FontSize = FontSize("ThemeFontSizeXs", 10),
                    Foreground = TextMuted,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Margin = new Thickness(0, -6, 8, 0),
                }
            });
        }
        Grid.SetColumn(timeLabelsPanel, 0);
        timeGridOuter.Children.Add(timeLabelsPanel);

        // Day columns with events
        var dayColumnsGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", daysData.Count))),
        };
        _weekDayColumnsGrid = dayColumnsGrid;
        Grid.SetColumn(dayColumnsGrid, 1);

        for (int i = 0; i < daysData.Count; i++)
        {
            var day = daysData[i];
            var dayColumn = CreateWeekDayColumn(day, i, hourHeight, startHour, endHour, onDayClick, onDayDoubleClick, onEventClick, onEventDoubleClick);
            Grid.SetColumn(dayColumn, i);
            dayColumnsGrid.Children.Add(dayColumn);
        }

        timeGridOuter.Children.Add(dayColumnsGrid);
        scrollViewer.Content = timeGridOuter;
        mainGrid.Children.Add(scrollViewer);

        rootGrid.Children.Add(mainGrid);

        // Drag preview overlay canvas - must fill the entire root grid
        var dragCanvas = new Canvas
        {
            IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };
        _weekDragCanvas = dragCanvas;
        rootGrid.Children.Add(dragCanvas);

        return rootGrid;
    }

    private record WeekDayData
    {
        public string Date { get; init; } = "";
        public string DayName { get; init; } = "";
        public string Label { get; init; } = "";
        public int DayNumber { get; init; }
        public bool IsToday { get; init; }
        public bool IsSelected { get; init; }
        public List<WeekEventData> Events { get; init; } = new();
    }

    private record WeekEventData
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public string? Color { get; init; }
        public double StartHour { get; init; }
        public double DurationHours { get; init; }
        public bool IsAllDay { get; init; }
        public long Start { get; init; }
        public long End { get; init; }
        public string? Description { get; init; }
        public string? Location { get; init; }
        public string? CalendarName { get; init; }
        public string? Status { get; init; }
        public string[]? Tags { get; init; }

        public string TimeRangeLabel
        {
            get
            {
                if (IsAllDay || Start == 0) return "";
                var startDt = DateTimeOffset.FromUnixTimeSeconds(Start).LocalDateTime;
                var endDt = DateTimeOffset.FromUnixTimeSeconds(End).LocalDateTime;
                return $"{FormatTime12(startDt)} – {FormatTime12(endDt)}";
            }
        }

        private static string FormatTime12(DateTime dt)
        {
            var h = dt.Hour % 12;
            if (h == 0) h = 12;
            var ampm = dt.Hour >= 12 ? "pm" : "am";
            return dt.Minute == 0 ? $"{h}{ampm}" : $"{h}:{dt.Minute:D2}{ampm}";
        }
    }

    private Control CreateWeekDayColumn(WeekDayData day, int dayIndex, double hourHeight, int startHour, int endHour, string onDayClick, string onDayDoubleClick, string onEventClick, string onEventDoubleClick)
    {
        // Container with hour grid lines and events overlay
        var container = new Grid
        {
            Background = day.IsToday
                ? HoverBrush
                : Avalonia.Media.Brushes.Transparent,
        };

        // Hour grid lines with 15-minute subdivisions
        var linesPanel = new StackPanel();
        for (int hour = startHour; hour < endHour; hour++)
        {
            var hourBorder = new Border
            {
                Height = hourHeight,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1, 0, 0, 1),
            };

            // Add 15-minute grid lines (lighter)
            var quarterGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("*,*,*,*"),
            };
            for (int q = 1; q < 4; q++)
            {
                var quarterLine = new Border
                {
                    BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#20808080")),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                };
                Grid.SetRow(quarterLine, q - 1);
                quarterGrid.Children.Add(quarterLine);
            }
            hourBorder.Child = quarterGrid;

            linesPanel.Children.Add(hourBorder);
        }
        container.Children.Add(linesPanel);

        // Events overlay using Canvas for absolute positioning
        var eventsCanvas = new Canvas
        {
            ClipToBounds = true,
        };

        // Compute overlap layout so concurrent events show side-by-side
        var timedEvents = day.Events.Where(e => !e.IsAllDay).OrderBy(e => e.StartHour).ToList();
        var eventColumns = new List<(WeekEventData Evt, int Column, int TotalColumns)>();

        if (timedEvents.Count > 0)
        {
            // Assign columns using a greedy approach
            var columns = new List<List<WeekEventData>>(); // each column is a list of non-overlapping events
            var evtColIndex = new Dictionary<string, int>();

            foreach (var evt in timedEvents)
            {
                int placed = -1;
                for (int c = 0; c < columns.Count; c++)
                {
                    var lastInCol = columns[c].Last();
                    if (evt.StartHour >= lastInCol.StartHour + lastInCol.DurationHours)
                    {
                        placed = c;
                        break;
                    }
                }
                if (placed < 0)
                {
                    placed = columns.Count;
                    columns.Add(new List<WeekEventData>());
                }
                columns[placed].Add(evt);
                evtColIndex[evt.Id] = placed;
            }

            // For each event, determine max concurrent overlap group size
            foreach (var evt in timedEvents)
            {
                int col = evtColIndex[evt.Id];
                // Count how many columns have events that overlap with this one
                int concurrent = 0;
                for (int c = 0; c < columns.Count; c++)
                {
                    if (columns[c].Any(other =>
                        other.StartHour < evt.StartHour + evt.DurationHours &&
                        other.StartHour + other.DurationHours > evt.StartHour))
                    {
                        concurrent++;
                    }
                }
                eventColumns.Add((evt, col, Math.Max(concurrent, 1)));
            }
        }

        foreach (var (evt, col, totalCols) in eventColumns)
        {
            var eventElement = CreateDraggableWeekEvent(evt, day.Date, dayIndex, hourHeight, startHour, onEventClick, onEventDoubleClick);
            eventsCanvas.Children.Add(eventElement);

            var capturedElement = eventElement;
            var capturedCol = col;
            var capturedTotal = totalCols;
            eventsCanvas.SizeChanged += (_, args) =>
            {
                var colWidth = Math.Max(0, (args.NewSize.Width - 4) / capturedTotal);
                capturedElement.Width = colWidth;
                Canvas.SetLeft(capturedElement, 2 + capturedCol * colWidth);
            };
        }

        container.Children.Add(eventsCanvas);

        // Day click/double-click handlers on background
        var capturedDate = day.Date;
        container.PointerPressed += (sender, e) =>
        {
            if (e.Source == container || e.Source == linesPanel || (e.Source is Border b && b.Parent == linesPanel))
            {
                if (e.ClickCount == 2 && !string.IsNullOrEmpty(onDayDoubleClick))
                {
                    // Calculate time from click position
                    var pos = e.GetPosition(container);
                    var clickHour = SnapToQuarterHour(pos.Y / hourHeight + startHour);
                    SendCommand(onDayDoubleClick, JsonSerializer.Serialize(new { date = capturedDate, hour = clickHour }));
                }
            }
        };

        return container;
    }

    private Border CreateDraggableWeekEvent(WeekEventData evt, string date, int dayIndex, double hourHeight, int startHour, string onEventClick, string onEventDoubleClick = "")
    {
        var top = (evt.StartHour - startHour) * hourHeight;
        var height = Math.Max(evt.DurationHours * hourHeight, 15); // Minimum height

        var eventGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
        };

        // Event content - title + time range
        var contentPanel = new StackPanel
        {
            Margin = new Thickness(5, 3, 4, 2),
            Spacing = 0,
        };

        contentPanel.Children.Add(new TextBlock
        {
            Text = evt.Title,
            FontSize = FontSize("ThemeFontSizeXs", 10),
            FontWeight = FontWeight.SemiBold,
            Foreground = Avalonia.Media.Brushes.White,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        });

        var timeRange = evt.TimeRangeLabel;
        if (!string.IsNullOrEmpty(timeRange))
        {
            contentPanel.Children.Add(new TextBlock
            {
                Text = timeRange,
                FontSize = FontSize("ThemeFontSizeXxs", 9),
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.White, 0.75),
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }

        var contentBorder = new Border { Child = contentPanel };
        Grid.SetRow(contentBorder, 0);
        eventGrid.Children.Add(contentBorder);

        // Resize handle at bottom
        var resizeHandle = new Border
        {
            Height = 6,
            Background = Avalonia.Media.Brushes.Transparent,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeNorthSouth),
        };
        Grid.SetRow(resizeHandle, 1);
        eventGrid.Children.Add(resizeHandle);

        var eventBrush = !string.IsNullOrEmpty(evt.Color)
            ? TryParseBrush(evt.Color)
            : Primary;
        // Darker left accent border for polished look
        var eventColor = eventBrush is Avalonia.Media.SolidColorBrush scb
            ? scb.Color
            : Avalonia.Media.Colors.DodgerBlue;
        var accentColor = Avalonia.Media.Color.FromArgb(
            255,
            (byte)Math.Max(0, eventColor.R - 40),
            (byte)Math.Max(0, eventColor.G - 40),
            (byte)Math.Max(0, eventColor.B - 40));

        var eventBorder = new Border
        {
            Background = eventBrush,
            CornerRadius = new CornerRadius(4),
            BorderBrush = new Avalonia.Media.SolidColorBrush(accentColor),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Margin = new Thickness(2, 1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child = eventGrid,
            Tag = new WeekEventDragData
            {
                EventId = evt.Id,
                Date = date,
                DayIndex = dayIndex,
                StartHour = evt.StartHour,
                DurationHours = evt.DurationHours,
                Color = evt.Color
            }
        };

        Canvas.SetTop(eventBorder, top);
        eventBorder.Height = height;

        // Drag to move
        var capturedEvt = evt;
        var capturedId = evt.Id;
        eventBorder.PointerPressed += (sender, e) =>
        {
            if (e.Source == resizeHandle)
            {
                // Start resize
                StartWeekEventResize(eventBorder, e);
            }
            else
            {
                // Start drag or click
                var props = e.GetCurrentPoint(eventBorder).Properties;
                if (props.IsLeftButtonPressed)
                {
                    StartWeekEventDrag(eventBorder, e);
                }
            }
            e.Handled = true;
        };

        eventBorder.PointerMoved += (sender, e) =>
        {
            if (_weekEventDragging || _weekEventResizing)
            {
                UpdateWeekEventDrag(e);
                e.Handled = true;
            }
        };

        eventBorder.PointerReleased += (sender, e) =>
        {
            if (_weekEventDragging || _weekEventResizing)
            {
                EndWeekEventDrag(e);
                e.Handled = true;
            }
            else
            {
                // Single-click — show popup
                ShowEventDetailPopup(capturedEvt, eventBorder, onEventClick);
            }
        };

        // Handle pointer capture lost
        eventBorder.PointerCaptureLost += (_, _) =>
        {
            CancelWeekEventDrag();
        };

        // Double-click to edit event (generous 600ms threshold)
        eventBorder.DoubleTapped += (_, e) =>
        {
            if (!string.IsNullOrEmpty(onEventDoubleClick))
            {
                CancelWeekEventDrag();
                CloseActiveEventPopup();
                SendCommand(onEventDoubleClick, JsonSerializer.Serialize(new { id = capturedId }));
                e.Handled = true;
            }
        };
        // Also support custom timer-based double-click for consistency
        eventBorder.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(eventBorder).Properties.IsLeftButtonPressed) return;
            var now = DateTime.UtcNow;
            if (_lastEventPillClickId == capturedId
                && (now - _lastEventPillClickTime).TotalMilliseconds < EventDoubleClickMs
                && !string.IsNullOrEmpty(onEventDoubleClick))
            {
                _lastEventPillClickId = null;
                _lastEventPillClickTime = DateTime.MinValue;
                CancelWeekEventDrag();
                CloseActiveEventPopup();
                SendCommand(onEventDoubleClick, JsonSerializer.Serialize(new { id = capturedId }));
                e.Handled = true;
            }
            else
            {
                _lastEventPillClickId = capturedId;
                _lastEventPillClickTime = now;
            }
        };

        return eventBorder;
    }

    private record WeekEventDragData
    {
        public string EventId { get; init; } = "";
        public string Date { get; init; } = "";
        public int DayIndex { get; init; }
        public double StartHour { get; init; }
        public double DurationHours { get; init; }
        public string? Color { get; init; }
    }

    private void StartWeekEventDrag(Border eventBorder, PointerPressedEventArgs e)
    {
        if (eventBorder.Tag is not WeekEventDragData data) return;
        if (_weekDragCanvas == null) return;

        _weekEventDragging = true;
        _weekEventResizing = false;
        _weekDragEventId = data.EventId;
        _weekDragSourceDate = data.Date;
        _weekDragStartHour = data.StartHour;
        _weekDragDuration = data.DurationHours;

        // Use a valid reference control for position (day_grid has dummy _weekDayColumnsGrid)
        Control referenceControl = _weekDayColumnsGrid?.Bounds.Width > 0 ? _weekDayColumnsGrid : _weekDragCanvas;
        _weekDragStartPoint = e.GetPosition(referenceControl);

        e.Pointer.Capture(eventBorder);

        // Get actual visual position of event relative to drag canvas
        var eventVisualPos = eventBorder.TranslatePoint(new Point(0, 0), _weekDragCanvas);

        // Create drag preview at actual visual position
        CreateWeekDragPreview(data, eventBorder.Height, eventVisualPos);
    }

    private void StartWeekEventResize(Border eventBorder, PointerPressedEventArgs e)
    {
        if (eventBorder.Tag is not WeekEventDragData data) return;
        if (_weekDragCanvas == null) return;

        _weekEventDragging = false;
        _weekEventResizing = true;
        _weekDragEventId = data.EventId;
        _weekDragSourceDate = data.Date;
        _weekDragStartHour = data.StartHour;
        _weekDragDuration = data.DurationHours;

        // Use a valid reference control for position (day_grid has dummy _weekDayColumnsGrid)
        Control referenceControl = _weekDayColumnsGrid?.Bounds.Width > 0 ? _weekDayColumnsGrid : _weekDragCanvas;
        _weekDragStartPoint = e.GetPosition(referenceControl);

        e.Pointer.Capture(eventBorder);

        // Get actual visual position of event relative to drag canvas
        var eventVisualPos = eventBorder.TranslatePoint(new Point(0, 0), _weekDragCanvas);

        // Create resize preview at actual visual position
        CreateWeekDragPreview(data, eventBorder.Height, eventVisualPos);
    }

    private void CreateWeekDragPreview(WeekEventDragData data, double height, Point? visualPos)
    {
        _weekDragPreview = new Border
        {
            Background = !string.IsNullOrEmpty(data.Color)
                ? TryParseBrush(data.Color)
                : Primary,
            Opacity = 0.7,
            CornerRadius = new CornerRadius(4),
            BorderBrush = Primary,
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
            Height = height,
        };

        if (_weekDragCanvas != null)
        {
            // Calculate column width - use day columns grid if valid, otherwise use drag canvas
            var gridWidth = _weekDayColumnsGrid?.Bounds.Width ?? 0;
            if (gridWidth <= 0) gridWidth = _weekDragCanvas.Bounds.Width;
            if (gridWidth <= 0) gridWidth = 200; // Fallback

            var columnWidth = gridWidth / (_weekDaysData?.Count ?? 1);
            _weekDragPreview.Width = Math.Max(50, columnWidth - 4); // Minimum width of 50

            // Use actual visual position if available, otherwise calculate from data
            if (visualPos.HasValue)
            {
                Canvas.SetTop(_weekDragPreview, visualPos.Value.Y);
                Canvas.SetLeft(_weekDragPreview, visualPos.Value.X);
            }
            else if (_weekDayColumnsGrid != null)
            {
                // Fallback: calculate from data with offset
                var dayColumnsOffset = _weekDayColumnsGrid.TranslatePoint(new Point(0, 0), _weekDragCanvas) ?? new Point(0, 0);
                var top = (_weekDragStartHour - _weekStartHour) * _weekHourHeight;
                var left = data.DayIndex * columnWidth + 2;

                Canvas.SetTop(_weekDragPreview, top + dayColumnsOffset.Y);
                Canvas.SetLeft(_weekDragPreview, left + dayColumnsOffset.X);
            }

            _weekDragCanvas.Children.Add(_weekDragPreview);
        }
    }

    private void UpdateWeekEventDrag(PointerEventArgs e)
    {
        if (_weekDragPreview == null || _weekDaysData == null || _weekDragCanvas == null)
        {
            _log.Debug("UpdateWeekEventDrag: Early return - preview={HasPreview} daysData={HasDays} canvas={HasCanvas}",
                _weekDragPreview != null, _weekDaysData != null, _weekDragCanvas != null);
            return;
        }

        // Get position - use the control that has valid bounds
        // For day_grid, _weekDayColumnsGrid is a dummy so use _weekDragCanvas
        Control referenceControl = _weekDayColumnsGrid?.Bounds.Width > 0 ? _weekDayColumnsGrid : _weekDragCanvas;
        var currentPos = e.GetPosition(referenceControl);

        // Calculate column width - for day view (1 day), use the full canvas width
        var columnWidth = _weekDaysData.Count == 1
            ? _weekDragCanvas.Bounds.Width
            : (_weekDayColumnsGrid?.Bounds.Width ?? _weekDragCanvas.Bounds.Width) / _weekDaysData.Count;

        // Guard against invalid column width
        if (columnWidth <= 0) columnWidth = 100; // Fallback

        // Get the actual offset for positioning
        var dayColumnsOffset = _weekDayColumnsGrid?.Bounds.Width > 0
            ? _weekDayColumnsGrid.TranslatePoint(new Point(0, 0), _weekDragCanvas) ?? new Point(0, 0)
            : new Point(0, 0);

        if (_weekEventDragging)
        {
            // Calculate new day and time
            var deltaY = currentPos.Y - _weekDragStartPoint.Y;
            var deltaHours = deltaY / _weekHourHeight;
            var newStartHour = SnapToQuarterHour(_weekDragStartHour + deltaHours);
            newStartHour = Math.Max(_weekStartHour, Math.Min(24 - _weekDragDuration, newStartHour));

            // Calculate new day index
            var newDayIndex = columnWidth > 0 ? (int)(currentPos.X / columnWidth) : 0;
            newDayIndex = Math.Max(0, Math.Min(_weekDaysData.Count - 1, newDayIndex));

            var top = (newStartHour - _weekStartHour) * _weekHourHeight;
            var left = newDayIndex * columnWidth + 2;

            Canvas.SetTop(_weekDragPreview, top + dayColumnsOffset.Y);
            Canvas.SetLeft(_weekDragPreview, left + dayColumnsOffset.X);

            // Store for drop
            _weekDragPreview.Tag = new { DayIndex = newDayIndex, StartHour = newStartHour };
            _log.Debug("UpdateWeekEventDrag: Set tag - DayIndex={DayIndex} StartHour={StartHour}", newDayIndex, newStartHour);
        }
        else if (_weekEventResizing)
        {
            // Calculate new duration
            var deltaY = currentPos.Y - _weekDragStartPoint.Y;
            var deltaHours = deltaY / _weekHourHeight;
            var newDuration = SnapToQuarterHour(_weekDragDuration + deltaHours);
            newDuration = Math.Max(0.25, Math.Min(24 - _weekDragStartHour, newDuration)); // Min 15 min

            _weekDragPreview.Height = newDuration * _weekHourHeight;

            // Store for drop
            _weekDragPreview.Tag = new { Duration = newDuration };
            _log.Debug("UpdateWeekEventDrag: Set resize tag - Duration={Duration}", newDuration);
        }
    }

    private void EndWeekEventDrag(PointerReleasedEventArgs e)
    {
        _log.Debug("EndWeekEventDrag: dragging={Dragging} resizing={Resizing} previewTag={HasTag} daysData={HasDays}",
            _weekEventDragging, _weekEventResizing, _weekDragPreview?.Tag != null, _weekDaysData != null);

        if (_weekDragPreview?.Tag == null || _weekDaysData == null)
        {
            _log.Debug("EndWeekEventDrag: Cancelled - preview tag or days data is null");
            CancelWeekEventDrag();
            return;
        }

        // Save state BEFORE releasing capture (PointerCaptureLost will reset these)
        var wasDragging = _weekEventDragging;
        var wasResizing = _weekEventResizing;
        var eventId = _weekDragEventId;
        var sourceDate = _weekDragSourceDate;
        var startHour = _weekDragStartHour;
        var duration = _weekDragDuration;
        var previewTag = _weekDragPreview.Tag;
        var daysData = _weekDaysData;
        var onEventUpdate = _weekOnEventUpdate;

        e.Pointer.Capture(null);

        try
        {
            if (wasDragging)
            {
                // Get new position from preview tag
                var tagType = previewTag.GetType();
                var dayIndexProp = tagType.GetProperty("DayIndex");
                var startHourProp = tagType.GetProperty("StartHour");

                _log.Debug("EndWeekEventDrag: Drag mode - dayIndexProp={HasDay} startHourProp={HasHour}",
                    dayIndexProp != null, startHourProp != null);

                if (dayIndexProp != null && startHourProp != null)
                {
                    var newDayIndex = (int)dayIndexProp.GetValue(previewTag)!;
                    var newStartHour = (double)startHourProp.GetValue(previewTag)!;
                    var newDate = daysData[newDayIndex].Date;

                    var argsJson = JsonSerializer.Serialize(new
                    {
                        id = eventId,
                        date = newDate,
                        start_hour = newStartHour,
                        duration_hours = duration
                    });

                    _log.Debug("EndWeekEventDrag: Sending command '{Command}' with args: {Args}",
                        onEventUpdate, argsJson);

                    // Send update command
                    SendCommand(onEventUpdate, argsJson);
                }
            }
            else if (wasResizing)
            {
                var tagType = previewTag.GetType();
                var durationProp = tagType.GetProperty("Duration");

                _log.Debug("EndWeekEventDrag: Resize mode - durationProp={HasDuration}", durationProp != null);

                if (durationProp != null)
                {
                    var newDuration = (double)durationProp.GetValue(previewTag)!;

                    var argsJson = JsonSerializer.Serialize(new
                    {
                        id = eventId,
                        date = sourceDate,
                        start_hour = startHour,
                        duration_hours = newDuration
                    });

                    _log.Debug("EndWeekEventDrag: Sending command '{Command}' with args: {Args}",
                        onEventUpdate, argsJson);

                    // Send update command
                    SendCommand(onEventUpdate, argsJson);
                }
            }
        }
        finally
        {
            CancelWeekEventDrag();
        }
    }

    private void CancelWeekEventDrag()
    {
        _weekEventDragging = false;
        _weekEventResizing = false;
        _weekDragEventId = null;

        if (_weekDragPreview != null && _weekDragCanvas != null)
        {
            _weekDragCanvas.Children.Remove(_weekDragPreview);
            _weekDragPreview = null;
        }
    }

    private static double SnapToQuarterHour(double hour)
    {
        // Snap to nearest 15 minutes (0.25 hour)
        return Math.Round(hour * 4) / 4.0;
    }

    // ================================================================
    // Day Grid (single day time-based view with drag-and-drop)
    // ================================================================

    /// <summary>
    /// Renders a single-day time grid with hourly slots and positioned events.
    /// Similar to week_grid but for a single day.
    /// </summary>
    private Control RenderDayGrid(JsonElement el)
    {
        var date = el.GetStringProp("date") ?? "";
        var label = el.GetStringProp("label") ?? "";
        var todayDate = el.GetStringProp("today") ?? "";
        var onEventClick = el.GetStringProp("on_event_click") ?? "";
        var onEventDoubleClick = el.GetStringProp("on_event_double_click") ?? "";
        var onEventUpdate = el.GetStringProp("on_event_update") ?? "update_event_time";
        var onDayClick = el.GetStringProp("on_day_click") ?? "";
        var onDayDoubleClick = el.GetStringProp("on_day_double_click") ?? "";
        var hourHeight = el.GetDoubleProp("hour_height", 60);
        var startHour = el.GetIntProp("start_hour", 0);
        var endHour = el.GetIntProp("end_hour", 24);

        _weekHourHeight = hourHeight;
        _weekStartHour = startHour;
        _weekOnEventUpdate = onEventUpdate;

        // Parse events data
        var events = new List<WeekEventData>();
        if (el.TryGetProperty("events", out var eventsEl) && eventsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var evt in eventsEl.EnumerateArray())
            {
                events.Add(new WeekEventData
                {
                    Id = evt.GetStringProp("id") ?? "",
                    Title = evt.GetStringProp("title") ?? "",
                    Color = evt.GetStringProp("color") ?? "#4A90D9",
                    IsAllDay = evt.GetBoolProp("is_all_day", false),
                    StartHour = evt.GetDoubleProp("start_hour", 0),
                    DurationHours = evt.GetDoubleProp("duration_hours", 1),
                    Start = evt.GetInt64Prop("start", 0),
                    End = evt.GetInt64Prop("end", 0),
                    Description = evt.GetStringProp("description"),
                    Location = evt.GetStringProp("location"),
                    CalendarName = evt.GetStringProp("calendar_name"),
                    Status = evt.GetStringProp("status"),
                    Tags = ParseStringArray(evt, "tags"),
                });
            }
        }

        // Store for drag-drop (use single-day wrapper)
        _weekDaysData = new List<WeekDayData>
        {
            new WeekDayData
            {
                Date = date,
                Label = label,
                IsToday = date == todayDate,
                IsSelected = true,
                Events = events
            }
        };

        var totalHours = endHour - startHour;
        var allDayEvents = events.Where(e => e.IsAllDay).ToList();
        var timedEvents = events.Where(e => !e.IsAllDay).ToList();

        // Root grid: all-day row (sticky) + scrollable time grid
        var rootGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
        };

        // All-day events row (sticky, outside scroll viewer)
        if (allDayEvents.Any())
        {
            var allDayRowGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("50,*"),
                MinHeight = 30,
            };

            var allDayLabel = new TextBlock
            {
                Text = "all-day",
                FontSize = FontSize("ThemeFontSizeXs", 10),
                Foreground = TextMuted,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 8, 0),
            };
            Grid.SetColumn(allDayLabel, 0);
            allDayRowGrid.Children.Add(allDayLabel);

            var allDayEventsPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(4, 2),
            };

            foreach (var evt in allDayEvents)
            {
                var capturedEvt = evt;
                var eventPill = new Border
                {
                    Background = !string.IsNullOrEmpty(evt.Color)
                        ? TryParseBrush(evt.Color)
                        : Primary,
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(8, 4),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    Child = new TextBlock
                    {
                        Text = evt.Title,
                        FontSize = FontSize("ThemeFontSizeSm", 12),
                        Foreground = Avalonia.Media.Brushes.White,
                        TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                    }
                };

                eventPill.PointerPressed += (_, e) =>
                {
                    if (!e.GetCurrentPoint(eventPill).Properties.IsLeftButtonPressed) return;
                    e.Handled = true;

                    var now = DateTime.UtcNow;
                    if (_lastEventPillClickId == capturedEvt.Id
                        && (now - _lastEventPillClickTime).TotalMilliseconds < EventDoubleClickMs
                        && !string.IsNullOrEmpty(onEventDoubleClick))
                    {
                        _lastEventPillClickId = null;
                        _lastEventPillClickTime = DateTime.MinValue;
                        CloseActiveEventPopup();
                        SendCommand(onEventDoubleClick, JsonSerializer.Serialize(new { id = capturedEvt.Id }));
                    }
                    else
                    {
                        _lastEventPillClickId = capturedEvt.Id;
                        _lastEventPillClickTime = now;
                        ShowEventDetailPopup(capturedEvt, eventPill, onEventClick);
                    }
                };

                allDayEventsPanel.Children.Add(eventPill);
            }

            Grid.SetColumn(allDayEventsPanel, 1);
            allDayRowGrid.Children.Add(allDayEventsPanel);

            var allDayBorder = new Border
            {
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = allDayRowGrid,
            };
            Grid.SetRow(allDayBorder, 0);
            rootGrid.Children.Add(allDayBorder);
        }

        // Scrollable time grid
        var mainGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("50,*"),
        };

        // Time labels column
        var timeLabels = new StackPanel { Orientation = Orientation.Vertical };
        for (int h = startHour; h < endHour; h++)
        {
            var hourText = h == 0 ? "12 AM" :
                          h < 12 ? $"{h} AM" :
                          h == 12 ? "12 PM" :
                          $"{h - 12} PM";

            timeLabels.Children.Add(new TextBlock
            {
                Text = hourText,
                FontSize = FontSize("ThemeFontSizeXs", 10),
                Foreground = TextMuted,
                Height = hourHeight,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Padding = new Thickness(0, 2, 8, 0),
                TextAlignment = Avalonia.Media.TextAlignment.Right,
            });
        }

        var timeLabelsContainer = new ScrollViewer
        {
            Content = timeLabels,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        Grid.SetColumn(timeLabelsContainer, 0);
        mainGrid.Children.Add(timeLabelsContainer);

        // Day column with time grid lines and timed events only
        var dayColumn = CreateDayGridColumn(date, timedEvents, hourHeight, startHour, endHour, onEventClick, onEventDoubleClick, onDayClick, onDayDoubleClick, date == todayDate);
        Grid.SetColumn(dayColumn, 1);
        mainGrid.Children.Add(dayColumn);

        // Wrap time grid in scroll viewer
        var scrollViewer = new ScrollViewer
        {
            Content = mainGrid,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        // Sync scroll between time labels and day column
        scrollViewer.ScrollChanged += (_, _) =>
        {
            timeLabelsContainer.Offset = new Vector(0, scrollViewer.Offset.Y);
        };

        Grid.SetRow(scrollViewer, 1);
        rootGrid.Children.Add(scrollViewer);

        return rootGrid;
    }

    private Control CreateDayGridColumn(
        string date,
        List<WeekEventData> events,
        double hourHeight,
        int startHour,
        int endHour,
        string onEventClick,
        string onEventDoubleClick,
        string onDayClick,
        string onDayDoubleClick,
        bool isToday)
    {
        var totalHours = endHour - startHour;
        var totalHeight = totalHours * hourHeight;

        // Container with fixed height
        var container = new Grid
        {
            Height = totalHeight,
            Background = isToday ? HoverBrush : Brushes.Transparent,
        };

        // Hour lines canvas
        var linesCanvas = new Canvas
        {
            Width = double.NaN,
            Height = totalHeight,
        };

        for (int h = 0; h <= totalHours; h++)
        {
            var line = new Border
            {
                Height = 1,
                Background = BorderSubtle,
                Width = 10000, // Will be clipped
            };
            Canvas.SetLeft(line, 0);
            Canvas.SetTop(line, h * hourHeight);
            linesCanvas.Children.Add(line);
        }
        container.Children.Add(linesCanvas);

        // Events canvas
        var eventsCanvas = new Canvas
        {
            Width = double.NaN,
            Height = totalHeight,
            ClipToBounds = true,
        };

        // Drag overlay canvas
        var dragCanvas = new Canvas
        {
            Width = double.NaN,
            Height = totalHeight,
            ClipToBounds = true,
            IsHitTestVisible = false,
        };
        _weekDragCanvas = dragCanvas;

        // Store grid for drag calculations (single column)
        _weekDayColumnsGrid = new Grid();
        _weekDayColumnsGrid.Tag = container;

        // Handle click/double-click on background
        container.PointerPressed += (s, e) =>
        {
            if (e.GetCurrentPoint(container).Properties.IsLeftButtonPressed)
            {
                var pos = e.GetPosition(container);
                var clickedHour = startHour + (pos.Y / hourHeight);

                if (!string.IsNullOrEmpty(onDayClick))
                {
                    SendCommand(onDayClick, JsonSerializer.Serialize(new { date, hour = clickedHour }));
                }
            }
        };

        container.DoubleTapped += (s, e) =>
        {
            if (!string.IsNullOrEmpty(onDayDoubleClick))
            {
                var pos = e.GetPosition(container);
                var clickedHour = startHour + (pos.Y / hourHeight);
                var snappedHour = SnapToQuarterHour(clickedHour);
                SendCommand(onDayDoubleClick, JsonSerializer.Serialize(new { date, hour = snappedHour }));
            }
        };

        // Compute overlap layout for day view events
        var timedEvents = events.Where(e => !e.IsAllDay).OrderBy(e => e.StartHour).ToList();
        var dayEventColumns = new List<(WeekEventData Evt, int Column, int TotalColumns)>();

        if (timedEvents.Count > 0)
        {
            var columns = new List<List<WeekEventData>>();
            var evtColIndex = new Dictionary<string, int>();

            foreach (var evt in timedEvents)
            {
                int placed = -1;
                for (int c = 0; c < columns.Count; c++)
                {
                    var lastInCol = columns[c].Last();
                    if (evt.StartHour >= lastInCol.StartHour + lastInCol.DurationHours)
                    {
                        placed = c;
                        break;
                    }
                }
                if (placed < 0)
                {
                    placed = columns.Count;
                    columns.Add(new List<WeekEventData>());
                }
                columns[placed].Add(evt);
                evtColIndex[evt.Id] = placed;
            }

            foreach (var evt in timedEvents)
            {
                int col = evtColIndex[evt.Id];
                int concurrent = 0;
                for (int c = 0; c < columns.Count; c++)
                {
                    if (columns[c].Any(other =>
                        other.StartHour < evt.StartHour + evt.DurationHours &&
                        other.StartHour + other.DurationHours > evt.StartHour))
                    {
                        concurrent++;
                    }
                }
                dayEventColumns.Add((evt, col, Math.Max(concurrent, 1)));
            }
        }

        foreach (var (evt, col, totalCols) in dayEventColumns)
        {
            var eventBorder = CreateDraggableWeekEvent(evt, date, 0, hourHeight, startHour, onEventClick, onEventDoubleClick);
            eventsCanvas.Children.Add(eventBorder);

            var capturedBorder = eventBorder;
            var capturedCol = col;
            var capturedTotal = totalCols;
            eventsCanvas.SizeChanged += (_, _) =>
            {
                var canvasWidth = eventsCanvas.Bounds.Width;
                if (canvasWidth <= 0) return;
                var colWidth = Math.Max(0, (canvasWidth - 8) / capturedTotal);
                capturedBorder.Width = colWidth;
                Canvas.SetLeft(capturedBorder, 4 + capturedCol * colWidth);
            };
        }

        container.Children.Add(eventsCanvas);
        container.Children.Add(dragCanvas);

        return container;
    }

    private Control RenderComboInput(JsonElement el)
    {
        var placeholder = el.GetStringProp("placeholder") ?? "";
        var onSubmitCommand = el.GetStringProp("on_submit_command") ?? "";

        var autoComplete = new AutoCompleteBox
        {
            Watermark = placeholder,
            FontSize = FontSize("ThemeFontSizeSm", 12),
            FontFamily = Font("ThemeFontSans"),
            Foreground = TextPrimary,
            FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
            MinimumPrefixLength = 0,
            MinWidth = 140,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, Dbl("ThemeSpacingXs", 4), 0, 0),
        };

        // Populate items from "items" array (strings) or "items_from" data binding
        if (el.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in items.EnumerateArray())
            {
                var s = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.ToString();
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
            autoComplete.ItemsSource = list;
        }

        void DoSubmit(string? text)
        {
            text = text?.Trim();
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(onSubmitCommand)) return;
            SendCommandDeferred(onSubmitCommand, JsonSerializer.Serialize(new { value = text }));
            autoComplete.Text = "";
            autoComplete.SelectedItem = null;
        }

        autoComplete.KeyDown += (_, args) =>
        {
            if (args.Key == Avalonia.Input.Key.Enter)
            {
                // Use selected item if dropdown selection is active, otherwise use typed text
                var textToSubmit = autoComplete.SelectedItem as string ?? autoComplete.Text;
                DoSubmit(textToSubmit);
                args.Handled = true;
            }
        };

        // Don't auto-submit on selection change - only submit on Enter
        // This allows users to type freely without accidental submissions

        return autoComplete;
    }

    /// Renders a segmented progress bar.
    /// JSON: { "type": "progress_bar", "value": 0.6, "active_value": 0.2, "height": 6 }
    /// value = green (complete) fraction 0-1, active_value = yellow (in progress) fraction 0-1.
    /// Background is red/danger, fills yellow then green from left to right.
    private Control RenderProgressBar(JsonElement el)
    {
        var doneVal = el.GetDoubleProp("value", 0);
        var activeVal = el.GetDoubleProp("active_value", 0);
        var height = el.GetDoubleProp("height", 6);
        var total = doneVal + activeVal;
        if (total > 1.0) { activeVal = 1.0 - doneVal; total = 1.0; }

        var grid = new Grid
        {
            Height = height,
            ClipToBounds = true,
            MinWidth = 40,
        };

        // Background (grey when nothing done, red when items remain incomplete)
        var hasAnyProgress = doneVal > 0 || activeVal > 0;
        grid.Children.Add(new Border
        {
            Background = hasAnyProgress ? DangerBrush : BorderSubtle,
            CornerRadius = new CornerRadius(height / 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });

        // Yellow (in-progress) segment — covers done + active width
        if (total > 0)
        {
            var yellowBar = new Border
            {
                Background = WarningBrush,
                CornerRadius = new CornerRadius(height / 2),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            yellowBar.Bind(Border.WidthProperty, new Avalonia.Data.Binding("Bounds.Width")
            {
                Source = grid,
                Converter = new FractionWidthConverter(total),
            });
            grid.Children.Add(yellowBar);
        }

        // Green (done) segment
        if (doneVal > 0)
        {
            var greenBar = new Border
            {
                Background = SuccessBrush,
                CornerRadius = new CornerRadius(height / 2),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            greenBar.Bind(Border.WidthProperty, new Avalonia.Data.Binding("Bounds.Width")
            {
                Source = grid,
                Converter = new FractionWidthConverter(doneVal),
            });
            grid.Children.Add(greenBar);
        }

        return grid;
    }

    /// Converter that multiplies a Rect's Width by a fraction.
    private class FractionWidthConverter : Avalonia.Data.Converters.IValueConverter
    {
        private readonly double _fraction;
        public FractionWidthConverter(double fraction) => _fraction = fraction;
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is Avalonia.Rect rect) return rect.Width * _fraction;
            return 0.0;
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// Renders files as a responsive grid of cards with icon, name, size, and context menu.
    /// JSON: { "type": "file_grid", "items": "{{filtered_files}}", "command": "select_file", "context_menu": [...] }
    /// </summary>
    private Control RenderFileGrid(JsonElement el)
    {
        var command = el.GetStringProp("command");
        var folderCommand = el.GetStringProp("folder_command");
        var wrapPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(Dbl("ThemeSpacingSm", 8)),
        };

        // Render folders first
        if (el.TryGetProperty("folders", out var folders) && folders.ValueKind == JsonValueKind.Array)
        {
            foreach (var folder in folders.EnumerateArray())
            {
                var folderId = folder.GetStringProp("id") ?? "";
                var folderName = folder.GetStringProp("name") ?? "Untitled";

                var folderContent = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "📁",
                            FontSize = FontSize("ThemeFontSize2Xl", 28),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(0, 0, 0, Dbl("ThemeSpacingXs", 4)),
                        },
                        new TextBlock
                        {
                            Text = folderName,
                            FontSize = FontSize("ThemeFontSizeSmMd", 13),
                            Foreground = TextPrimary,
                            FontFamily = Font("ThemeFontSans"),
                            FontWeight = FontWeight.SemiBold,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            MaxWidth = 120,
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                    },
                };

                var folderCard = new Border
                {
                    Child = folderContent,
                    Width = 140,
                    Height = 130,
                    Background = SurfaceElevated,
                    CornerRadius = Radius("ThemeRadiusSm"),
                    Padding = new Thickness(Dbl("ThemeSpacingSm", 8)),
                    Margin = new Thickness(Dbl("ThemeSpacingSm", 8)),
                    BorderBrush = BorderSubtle,
                    BorderThickness = new Thickness(1),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                };

                folderCard.Tag = $"folder:{folderId}"; // Tag so drag-select skips it
                folderCard.PointerEntered += (s, _) => { if (s is Border b) b.Background = HoverBrush; };
                folderCard.PointerExited += (s, _) => { if (s is Border b) b.Background = SurfaceElevated; };

                if (folderCommand != null)
                {
                    folderCard.PointerPressed += (_, _) =>
                        SendCommand(folderCommand, JsonSerializer.Serialize(new { folder_id = folderId }));
                }

                wrapPanel.Children.Add(folderCard);
            }
        }

        if (!el.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return wrapPanel;

        foreach (var item in items.EnumerateArray())
        {
            var id = item.GetStringProp("id") ?? "";
            var name = item.GetStringProp("display_name") ?? item.GetStringProp("name") ?? "Untitled";
            var icon = item.GetStringProp("icon") ?? "📄";
            var sizeDisplay = item.GetStringProp("size_display") ?? "";
            var isFavorite = item.GetBoolProp("is_favorite", false);
            var isImage = item.GetBoolProp("is_image", false);

            // Try to load thumbnail for image files
            Control visualBlock;
            Avalonia.Media.Imaging.Bitmap? thumb = isImage ? LoadArchiveThumbnail(id) : null;

            if (thumb != null)
            {
                visualBlock = new Border
                {
                    Child = new Avalonia.Controls.Image
                    {
                        Source = thumb,
                        Stretch = Avalonia.Media.Stretch.UniformToFill,
                        Width = 124,
                        Height = 80,
                    },
                    CornerRadius = Radius("ThemeRadiusXs"),
                    ClipToBounds = true,
                    Margin = new Thickness(0, 0, 0, Dbl("ThemeSpacingXs", 4)),
                };
            }
            else
            {
                visualBlock = new TextBlock
                {
                    Text = icon,
                    FontSize = FontSize("ThemeFontSize2Xl", 28),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, Dbl("ThemeSpacingXs", 4)),
                };
            }

            var nameBlock = new TextBlock
            {
                Text = name,
                FontSize = FontSize("ThemeFontSizeSmMd", 13),
                Foreground = TextPrimary,
                FontFamily = Font("ThemeFontSans"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 120,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var sizeBlock = new TextBlock
            {
                Text = sizeDisplay,
                FontSize = FontSize("ThemeFontSizeXs", 10),
                Foreground = TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var cardContent = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 2,
                Children = { visualBlock, nameBlock, sizeBlock },
            };

            if (isFavorite)
            {
                var starBadge = new TextBlock
                {
                    Text = "★",
                    FontSize = FontSize("ThemeFontSizeXs", 10),
                    Foreground = WarningBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                cardContent.Children.Add(starBadge);
            }

            var card = new Border
            {
                Child = cardContent,
                Width = 140,
                Height = thumb != null ? 140 : 130,
                Background = SurfaceElevated,
                CornerRadius = Radius("ThemeRadiusSm"),
                Padding = new Thickness(Dbl("ThemeSpacingSm", 8)),
                Margin = new Thickness(Dbl("ThemeSpacingSm", 8)),
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
            };

            card.PointerEntered += (s, _) => { if (s is Border b) b.Background = HoverBrush; };
            card.PointerExited += (s, _) => { if (s is Border b) b.Background = SurfaceElevated; };

            if (command != null)
            {
                card.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                card.PointerPressed += (_, _) =>
                    SendCommand(command, JsonSerializer.Serialize(new { id }));
            }

            AttachContextMenu(card, el, id);
            card.Tag = id; // Store file ID for drag-select lookup
            wrapPanel.Children.Add(card);
        }

        // Wrap in a drag-select container
        return WrapWithDragSelect(wrapPanel, command);
    }

    /// <summary>
    /// Wraps a WrapPanel in a Panel with drag-select (marquee/lasso) behavior.
    /// Draws a translucent selection rectangle and highlights intersecting cards.
    /// </summary>
    private Control WrapWithDragSelect(WrapPanel content, string? selectCommand)
    {
        var overlay = new Panel();
        overlay.Children.Add(content);

        var selectionRect = new Border
        {
            Background = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromArgb(40, 100, 150, 255)),
            BorderBrush = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromArgb(120, 100, 150, 255)),
            BorderThickness = new Thickness(1),
            IsVisible = false,
            IsHitTestVisible = false,
        };
        overlay.Children.Add(selectionRect);

        Avalonia.Point? dragStart = null;
        var selectedBorders = new HashSet<Border>();

        overlay.PointerPressed += (_, e) =>
        {
            // Only start drag on the background (not on cards)
            if (e.Source is Border b && b.Tag is string)
                return; // Click on a card — let the card handle it
            // Check if the source is inside a card
            if (e.Source is Avalonia.Visual v)
            {
                var parent = v;
                while (parent != null)
                {
                    if (parent is Border pb && pb.Tag is string)
                        return; // Inside a card
                    parent = parent.GetVisualParent() as Avalonia.Visual;
                }
            }

            dragStart = e.GetPosition(overlay);
            e.Pointer.Capture(overlay);

            // Clear previous selection highlight
            foreach (var prev in selectedBorders)
                prev.BorderBrush = BorderSubtle;
            selectedBorders.Clear();
        };

        overlay.PointerMoved += (_, e) =>
        {
            if (dragStart is null) return;
            var current = e.GetPosition(overlay);
            var x = Math.Min(dragStart.Value.X, current.X);
            var y = Math.Min(dragStart.Value.Y, current.Y);
            var w = Math.Abs(current.X - dragStart.Value.X);
            var h = Math.Abs(current.Y - dragStart.Value.Y);

            Canvas.SetLeft(selectionRect, x);
            Canvas.SetTop(selectionRect, y);
            selectionRect.Width = w;
            selectionRect.Height = h;
            selectionRect.Margin = new Thickness(x, y, 0, 0);
            selectionRect.HorizontalAlignment = HorizontalAlignment.Left;
            selectionRect.VerticalAlignment = VerticalAlignment.Top;
            selectionRect.IsVisible = w > 4 || h > 4;

            // Highlight cards that intersect the rectangle
            var selRect = new Avalonia.Rect(x, y, w, h);
            foreach (var child in content.Children)
            {
                if (child is Border card && card.Tag is string)
                {
                    var cardBounds = card.Bounds;
                    var cardTopLeft = card.TranslatePoint(new Avalonia.Point(0, 0), overlay);
                    if (cardTopLeft.HasValue)
                    {
                        var cardRect = new Avalonia.Rect(cardTopLeft.Value, new Avalonia.Size(cardBounds.Width, cardBounds.Height));
                        if (selRect.Intersects(cardRect))
                        {
                            card.BorderBrush = Primary;
                            selectedBorders.Add(card);
                        }
                        else
                        {
                            card.BorderBrush = BorderSubtle;
                            selectedBorders.Remove(card);
                        }
                    }
                }
            }
        };

        overlay.PointerReleased += (_, e) =>
        {
            if (dragStart is null) return;
            dragStart = null;
            e.Pointer.Capture(null);
            selectionRect.IsVisible = false;

            // Select the highlighted items
            if (selectCommand != null && selectedBorders.Count > 0)
            {
                foreach (var card in selectedBorders)
                {
                    if (card.Tag is string id)
                        SendCommand(selectCommand, JsonSerializer.Serialize(new { id }));
                }
            }
        };

        return overlay;
    }

    /// <summary>
    /// Renders files as a table/list with columns: icon, name, favorite, size, modified date.
    /// JSON: { "type": "file_list", "columns": [...], "items": "{{filtered_files}}", "command": "select_file", "context_menu": [...] }
    /// </summary>
    private Control RenderFileList(JsonElement el)
    {
        var command = el.GetStringProp("command");
        var folderCommand = el.GetStringProp("folder_command");
        var panel = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(Dbl("ThemeSpacingSm", 8)),
        };

        // Column header row
        if (el.TryGetProperty("columns", out var columns) && columns.ValueKind == JsonValueKind.Array)
        {
            var headerRow = new DockPanel
            {
                Margin = new Thickness(
                    Dbl("ThemeSpacingSm", 8), 0,
                    Dbl("ThemeSpacingSm", 8), Dbl("ThemeSpacingXs", 4)),
            };

            foreach (var col in columns.EnumerateArray())
            {
                var label = col.GetStringProp("label");
                if (string.IsNullOrEmpty(label)) continue;

                var width = col.GetDoubleProp("width", 0);
                var flex = col.GetBoolProp("flex", false);
                var align = col.GetStringProp("align");

                var colHeader = new TextBlock
                {
                    Text = label,
                    FontSize = FontSize("ThemeFontSizeXsSm", 11),
                    Foreground = TextMuted,
                    FontWeight = FontWeight.SemiBold,
                    FontFamily = Font("ThemeFontSans"),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = align == "right" ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                };

                if (flex)
                {
                    // Flex column takes remaining space
                    headerRow.Children.Add(colHeader);
                }
                else if (width > 0)
                {
                    var wrapper = new Border { Width = width, Child = colHeader };
                    DockPanel.SetDock(wrapper, Dock.Right);
                    headerRow.Children.Add(wrapper);
                }
            }

            panel.Children.Add(headerRow);
            panel.Children.Add(new Separator { Background = BorderSubtle, Margin = new Thickness(0, 2) });
        }

        // Render folder rows before files
        if (el.TryGetProperty("folders", out var listFolders) && listFolders.ValueKind == JsonValueKind.Array)
        {
            foreach (var folder in listFolders.EnumerateArray())
            {
                var folderId = folder.GetStringProp("id") ?? "";
                var folderName = folder.GetStringProp("name") ?? "Untitled";

                var folderRow = new DockPanel { LastChildFill = true };

                var folderIcon = new TextBlock
                {
                    Text = "📁",
                    FontSize = FontSize("ThemeFontSizeMd", 14),
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 32,
                    TextAlignment = TextAlignment.Center,
                };
                DockPanel.SetDock(folderIcon, Dock.Left);
                folderRow.Children.Add(folderIcon);

                folderRow.Children.Add(new TextBlock
                {
                    Text = folderName,
                    FontSize = FontSize("ThemeFontSizeSmMd", 13),
                    Foreground = TextPrimary,
                    FontFamily = Font("ThemeFontSans"),
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

                var folderWrapper = new Border
                {
                    Child = folderRow,
                    Padding = new Thickness(
                        Dbl("ThemeSpacingSm", 8),
                        Dbl("ThemeSpacingXs", 4)),
                    Margin = new Thickness(0, 1),
                    CornerRadius = Radius("ThemeRadiusXs"),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                };

                folderWrapper.PointerEntered += (s, _) => { if (s is Border b) b.Background = HoverBrush; };
                folderWrapper.PointerExited += (s, _) => { if (s is Border b) b.Background = Brushes.Transparent; };

                folderWrapper.Tag = $"folder:{folderId}";
                if (folderCommand != null)
                {
                    folderWrapper.PointerPressed += (_, e) =>
                    {
                        e.Handled = true;
                        SendCommand(folderCommand, JsonSerializer.Serialize(new { folder_id = folderId }));
                    };
                }

                panel.Children.Add(folderWrapper);
            }
        }

        if (!el.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return panel;

        foreach (var item in items.EnumerateArray())
        {
            var id = item.GetStringProp("id") ?? "";
            var name = item.GetStringProp("display_name") ?? item.GetStringProp("name") ?? "Untitled";
            var icon = item.GetStringProp("icon") ?? "📄";
            var favoriteIcon = item.GetStringProp("favorite_icon") ?? "";
            var sizeDisplay = item.GetStringProp("size_display") ?? "";
            var modifiedDisplay = item.GetStringProp("modified_display") ?? "";
            var isImage = item.GetBoolProp("is_image", false);

            var row = new DockPanel
            {
                LastChildFill = true,
            };

            // Icon or mini thumbnail (left)
            Control iconControl;
            var listThumb = isImage ? LoadArchiveThumbnail(id) : null;
            if (listThumb != null)
            {
                iconControl = new Border
                {
                    Child = new Avalonia.Controls.Image
                    {
                        Source = listThumb,
                        Stretch = Avalonia.Media.Stretch.UniformToFill,
                        Width = 24,
                        Height = 24,
                    },
                    CornerRadius = Radius("ThemeRadiusXs"),
                    ClipToBounds = true,
                    Width = 32,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            else
            {
                iconControl = new TextBlock
                {
                    Text = icon,
                    FontSize = FontSize("ThemeFontSizeMd", 14),
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 32,
                    TextAlignment = TextAlignment.Center,
                };
            }
            DockPanel.SetDock(iconControl, Dock.Left);
            row.Children.Add(iconControl);

            // Modified date (right)
            var modifiedBlock = new TextBlock
            {
                Text = modifiedDisplay,
                FontSize = FontSize("ThemeFontSizeXsSm", 11),
                Foreground = TextMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 120,
                TextAlignment = TextAlignment.Right,
            };
            DockPanel.SetDock(modifiedBlock, Dock.Right);
            row.Children.Add(modifiedBlock);

            // Size (right)
            var sizeBlock = new TextBlock
            {
                Text = sizeDisplay,
                FontSize = FontSize("ThemeFontSizeXsSm", 11),
                Foreground = TextMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 80,
                TextAlignment = TextAlignment.Right,
            };
            DockPanel.SetDock(sizeBlock, Dock.Right);
            row.Children.Add(sizeBlock);

            // Favorite icon (right)
            var favBlock = new TextBlock
            {
                Text = favoriteIcon,
                FontSize = FontSize("ThemeFontSizeXsSm", 11),
                Foreground = WarningBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 24,
                TextAlignment = TextAlignment.Center,
            };
            DockPanel.SetDock(favBlock, Dock.Right);
            row.Children.Add(favBlock);

            // Name (fill remaining)
            var nameBlock = new TextBlock
            {
                Text = name,
                FontSize = FontSize("ThemeFontSizeSmMd", 13),
                Foreground = TextPrimary,
                FontFamily = Font("ThemeFontSans"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            row.Children.Add(nameBlock);

            var wrapper = new Border
            {
                Child = row,
                Padding = new Thickness(
                    Dbl("ThemeSpacingSm", 8),
                    Dbl("ThemeSpacingXs", 4)),
                Margin = new Thickness(0, 1),
                CornerRadius = Radius("ThemeRadiusXs"),
            };

            wrapper.PointerEntered += (s, _) => { if (s is Border b) b.Background = HoverBrush; };
            wrapper.PointerExited += (s, _) => { if (s is Border b) b.Background = Brushes.Transparent; };

            if (command != null)
            {
                wrapper.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                wrapper.PointerPressed += (_, _) =>
                    SendCommand(command, JsonSerializer.Serialize(new { id }));
            }

            AttachContextMenu(wrapper, el, id);
            panel.Children.Add(wrapper);
        }

        return panel;
    }

    /// <summary>
    /// Renders a file preview placeholder for the detail pane.
    /// JSON: { "type": "file_preview", "file_id": "...", "mime_type": "image/jpeg" }
    /// </summary>
    private Control RenderFilePreview(JsonElement el)
    {
        var mimeType = el.GetStringProp("mime_type") ?? "";
        var fileId = el.GetStringProp("file_id") ?? "";

        var container = new StackPanel
        {
            Spacing = Dbl("ThemeSpacingSm", 8),
            Margin = new Thickness(Dbl("ThemeSpacingSm", 8)),
        };

        // Image preview: load original from archive
        if (mimeType.StartsWith("image/"))
        {
            var original = LoadArchiveOriginal(fileId);
            if (original != null)
            {
                var (bitmap, filePath) = original.Value;
                var imgControl = new Avalonia.Controls.Image
                {
                    Source = bitmap,
                    Stretch = Avalonia.Media.Stretch.Uniform,
                    MaxHeight = 300,
                };

                container.Children.Add(new Border
                {
                    Child = imgControl,
                    CornerRadius = Radius("ThemeRadiusSm"),
                    ClipToBounds = true,
                });

                // Image-specific metadata
                var metaPanel = new StackPanel { Spacing = 2 };
                var px = bitmap.PixelSize;
                if (px.Width > 0 && px.Height > 0)
                {
                    metaPanel.Children.Add(CreateMetadataRow("Dimensions", $"{px.Width} × {px.Height} px"));
                }
                try
                {
                    var fi = new System.IO.FileInfo(filePath);
                    metaPanel.Children.Add(CreateMetadataRow("File Size", FormatFileSize((ulong)fi.Length)));
                }
                catch { /* ignore */ }

                if (metaPanel.Children.Count > 0)
                {
                    container.Children.Add(new Border
                    {
                        Background = SurfaceElevated,
                        CornerRadius = Radius("ThemeRadiusXs"),
                        Padding = new Thickness(Dbl("ThemeSpacingSm", 8)),
                        Child = metaPanel,
                    });
                }

                return container;
            }
        }

        // Non-image or missing: icon placeholder
        var previewIcon = mimeType switch
        {
            var m when m.StartsWith("image/") => "🖼",
            var m when m.StartsWith("video/") => "🎬",
            var m when m.StartsWith("audio/") => "🎵",
            var m when m.Contains("pdf") => "📕",
            _ => "📄",
        };

        container.Children.Add(new Border
        {
            Background = SurfaceElevated,
            CornerRadius = Radius("ThemeRadiusSm"),
            Padding = new Thickness(Dbl("ThemeSpacingXl", 24)),
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = Dbl("ThemeSpacingSm", 8),
                Children =
                {
                    new TextBlock
                    {
                        Text = previewIcon,
                        FontSize = FontSize("ThemeFontSize4Xl", 48),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = mimeType,
                        FontSize = FontSize("ThemeFontSizeXsSm", 11),
                        Foreground = TextMuted,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
        });

        return container;
    }

    /// <summary>
    /// Creates a label/value row for file metadata in the preview pane.
    /// </summary>
    private Control CreateMetadataRow(string label, string value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 1) };
        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = FontSize("ThemeFontSizeXsSm", 11),
            Foreground = TextMuted,
            FontFamily = Font("ThemeFontSans"),
            Width = 80,
        };
        DockPanel.SetDock(labelBlock, Dock.Left);
        row.Children.Add(labelBlock);
        row.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = FontSize("ThemeFontSizeXsSm", 11),
            Foreground = TextPrimary,
            FontFamily = Font("ThemeFontSans"),
        });
        return row;
    }

    /// <summary>
    /// Formats a byte count into human-readable string (same logic as Rust side).
    /// </summary>
    private static string FormatFileSize(ulong bytes)
    {
        const ulong KB = 1024;
        const ulong MB = KB * 1024;
        const ulong GB = MB * 1024;
        return bytes switch
        {
            >= GB => $"{bytes / (double)GB:F2} GB",
            >= MB => $"{bytes / (double)MB:F2} MB",
            >= KB => $"{bytes / (double)KB:F2} KB",
            _ => $"{bytes} B",
        };
    }

    // ================================================================
    // Feedback components
    // ================================================================

    private static Control RenderLoading(JsonElement el)
    {
        var message = el.GetStringProp("message") ?? "Loading...";

        return new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = Dbl("ThemeSpacingSm", 8),
            Children =
            {
                new ProgressBar
                {
                    IsIndeterminate = true,
                    Width = 200,
                },
                new TextBlock
                {
                    Text = message,
                    FontSize = FontSize("ThemeFontSizeSmMd", 13),
                    Foreground = TextMuted,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            },
        };
    }

    private Control RenderEmptyState(JsonElement el)
    {
        var message = el.GetStringProp("message") ?? "Nothing here yet";
        var actionLabel = el.GetStringProp("action_label");
        var actionCommand = el.GetStringProp("action_command");

        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = Dbl("ThemeSpacingMd", 12),
            Margin = new Thickness(Dbl("ThemeSpacingXl", 24)),
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    FontSize = FontSize("ThemeFontSizeLg", 16),
                    Foreground = TextMuted,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        if (actionLabel != null && actionCommand != null)
        {
            var button = new Button
            {
                Content = actionLabel,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            button.Click += (_, _) => SendCommand(actionCommand, "{}");
            panel.Children.Add(button);
        }

        return panel;
    }

    private static Control RenderErrorComponent(JsonElement el)
    {
        var message = el.GetStringProp("message") ?? "An error occurred";
        return CreateErrorState(message);
    }

    private Control RenderHtmlContent(JsonElement el)
    {
        var html = el.GetStringProp("html") ?? el.GetStringProp("value") ?? "";
        if (string.IsNullOrWhiteSpace(html))
            return new Border();

        var panel = new StackPanel { Spacing = 8 };
        var parser = new HtmlContentParser(
            (key, _) => Brush(key, Brushes.White),
            FontSize,
            Font,
            NetworkFetcher);
        parser.Parse(html, panel);

        return new Border
        {
            Padding = new Thickness(Dbl("ThemeSpacingLg", 16)),
            Child = panel,
        };
    }

    // ================================================================
    // Context menu builder
    // ================================================================

    /// Builds and attaches a ContextMenu from the "context_menu" array on any component.
    /// Each menu item routes its command through SendCommand with the parent's id as args.
    private void AttachContextMenu(Control control, JsonElement el, string? itemId)
    {
        if (!el.TryGetProperty("context_menu", out var menuEl) || menuEl.ValueKind != JsonValueKind.Array)
            return;

        // Read page state for dynamic context menu labels
        var ctxArchived = el.GetBoolProp("is_archived", false);
        var ctxTrashed = el.GetBoolProp("is_trashed", false);
        var ctxLocked = el.GetBoolProp("is_locked", false);

        var menu = new ContextMenu();
        foreach (var item in menuEl.EnumerateArray())
        {
            if (item.GetStringProp("type") == "separator")
            {
                menu.Items.Add(new Separator());
                continue;
            }

            var label = item.GetStringProp("label") ?? "";
            var command = item.GetStringProp("command");
            var icon = item.GetStringProp("icon");
            var variant = item.GetStringProp("variant");

            // Dynamic label overrides based on page state
            if (command == "toggle_archive")
                label = ctxArchived ? "Unarchive" : "Archive";
            else if (command == "toggle_trash")
                label = ctxTrashed ? "Restore" : "Trash";
            else if (command == "toggle_lock")
                label = ctxLocked ? "Unlock" : "Lock";
            else if (command == "delete_page" && !ctxTrashed)
                continue; // Only show permanent delete for trashed pages

            var header = icon != null ? $"{icon}  {label}" : label;
            var menuItem = new MenuItem
            {
                Header = header,
                Foreground = variant == "danger" ? DangerBrush : TextPrimary,
                FontSize = FontSize("ThemeFontSizeSm", 12),
                FontFamily = Font("ThemeFontSans"),
            };

            if (command != null)
            {
                // Merge explicit template args with the item id
                var explicitArgs = item.GetStringProp("args");
                string args;
                if (explicitArgs != null && itemId != null)
                {
                    // Parse explicit args and inject id
                    try
                    {
                        using var doc = JsonDocument.Parse(explicitArgs);
                        var dict = new Dictionary<string, object?> { ["id"] = itemId };
                        foreach (var prop in doc.RootElement.EnumerateObject())
                            dict[prop.Name] = prop.Value.ValueKind switch
                            {
                                JsonValueKind.String => prop.Value.GetString(),
                                JsonValueKind.Number => prop.Value.GetRawText(),
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                _ => prop.Value.GetRawText(),
                            };
                        args = JsonSerializer.Serialize(dict);
                    }
                    catch
                    {
                        args = explicitArgs;
                    }
                }
                else if (explicitArgs != null)
                {
                    args = explicitArgs;
                }
                else if (itemId != null)
                {
                    args = JsonSerializer.Serialize(new { id = itemId });
                }
                else
                {
                    args = "{}";
                }
                menuItem.Click += (_, _) => SendCommand(command, args);
            }

            menu.Items.Add(menuItem);
        }

        if (menu.Items.Count > 0)
            control.ContextMenu = menu;
    }

    // ================================================================
    // Notes plugin components
    // ================================================================

    private Control RenderPageItem(JsonElement el)
    {
        var title = el.GetStringProp("title") ?? "Untitled";
        var preview = el.GetStringProp("preview");
        var icon = el.GetStringProp("icon") ?? "\uD83D\uDCC4";
        var isArchived = el.GetBoolProp("is_archived", false);
        var isTrashed = el.GetBoolProp("is_trashed", false);
        var isLocked = el.GetBoolProp("is_locked", false);
        var command = el.GetStringProp("command");
        var args = el.GetStringProp("args") ?? "{}";
        var id = el.GetStringProp("id") ?? "";
        var depth = el.GetIntProp("depth", 0);
        var hasChildren = el.GetBoolProp("has_children", false);
        var isExpanded = el.GetBoolProp("is_expanded", false);

        var panel = new DockPanel();

        // Expand/collapse chevron or spacer for alignment
        if (hasChildren)
        {
            var chevron = new TextBlock
            {
                Text = isExpanded ? "\u25BC" : "\u25B6",
                FontSize = FontSize("ThemeFontSizeXs", 10),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TextMuted,
                Margin = new Thickness(0, 0, Dbl("ThemeSpacingXs", 4), 0),
                MinWidth = 14,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            chevron.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                SendCommand("toggle_page_expand", $"{{\"id\":\"{id}\"}}");
            };
            DockPanel.SetDock(chevron, Dock.Left);
            panel.Children.Add(chevron);
        }
        else if (depth > 0)
        {
            var spacer = new Border { MinWidth = 14, Margin = new Thickness(0, 0, Dbl("ThemeSpacingXs", 4), 0) };
            DockPanel.SetDock(spacer, Dock.Left);
            panel.Children.Add(spacer);
        }

        // Icon on the left
        var iconText = new TextBlock
        {
            Text = icon,
            FontSize = FontSize("ThemeFontSizeLg", 16),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, Dbl("ThemeSpacingSm", 8), 0),
            MinWidth = 22,
        };
        if (!string.IsNullOrEmpty(id))
            _pageListIcons[id] = iconText;
        DockPanel.SetDock(iconText, Dock.Left);
        panel.Children.Add(iconText);

        // Badges on the right
        if (isArchived || isTrashed || isLocked)
        {
            var badgePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Dbl("ThemeSpacingXs", 4),
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (isArchived)
            {
                badgePanel.Children.Add(new Border
                {
                    Background = PrimaryMuted,
                    CornerRadius = Radius("ThemeRadiusFull"),
                    Padding = new Thickness(4, 1),
                    Child = new TextBlock
                    {
                        Text = "Archived",
                        FontSize = FontSize("ThemeFontSizeXs", 10),
                        Foreground = Brushes.White,
                    },
                });
            }

            if (isTrashed)
            {
                badgePanel.Children.Add(new Border
                {
                    Background = DangerBrush,
                    CornerRadius = Radius("ThemeRadiusFull"),
                    Padding = new Thickness(4, 1),
                    Child = new TextBlock
                    {
                        Text = "Trashed",
                        FontSize = FontSize("ThemeFontSizeXs", 10),
                        Foreground = Brushes.White,
                    },
                });
            }

            if (isLocked && !isArchived && !isTrashed)
            {
                badgePanel.Children.Add(new TextBlock
                {
                    Text = "\uD83D\uDD12",
                    FontSize = FontSize("ThemeFontSizeXs", 10),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            DockPanel.SetDock(badgePanel, Dock.Right);
            panel.Children.Add(badgePanel);
        }

        // Content in the middle
        var content = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var titleLabel = new TextBlock
        {
            Text = title,
            FontSize = FontSize("ThemeFontSizeSmMd", 13),
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };
        if (!string.IsNullOrEmpty(id))
            _pageListTitles[id] = titleLabel;
        content.Children.Add(titleLabel);

        // Subtitle/preview intentionally omitted — page list shows title only

        panel.Children.Add(content);

        // Indentation based on depth
        var indentLeft = depth * 20;

        var wrapper = new Border
        {
            Tag = id,
            Child = panel,
            // Transparent background required for full-area hit-testing (drag + hover)
            Background = Brushes.Transparent,
            Padding = new Thickness(
                Dbl("ThemeSpacingLg", 16) + indentLeft,
                Dbl("ThemeSpacingSm", 8),
                Dbl("ThemeSpacingLg", 16),
                Dbl("ThemeSpacingSm", 8)),
            Margin = new Thickness(
                Dbl("ThemeSpacingSm", 8), 0),
            CornerRadius = Radius("ThemeRadiusXs"),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        wrapper.PointerEntered += (s, _) =>
        {
            if (s is Border b && !_isPageDragging) b.Background = HoverBrush;
            // Prefetch page data on hover for faster navigation
            if (!string.IsNullOrEmpty(id) && PrefetchRequested != null)
                PrefetchRequested.Invoke("note", id);
        };
        wrapper.PointerExited += (s, _) =>
        {
            if (s is Border b && !_isPageDragging) b.Background = Brushes.Transparent;
            // Cancel prefetch if mouse leaves before debounce completes
            if (!string.IsNullOrEmpty(id) && PrefetchCancelled != null)
                PrefetchCancelled.Invoke("note", id);
        };

        // Page drag/drop: press starts tracking, move initiates drag after threshold,
        // release either ends drag or fires click.
        Point? pageDragStart = null;
        wrapper.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(wrapper).Properties.IsLeftButtonPressed)
            {
                pageDragStart = e.GetPosition(wrapper);
            }
        };
        wrapper.PointerMoved += (_, e) =>
        {
            if (_isPageDragging)
            {
                UpdatePageDrag(e);
                return;
            }
            if (pageDragStart.HasValue)
            {
                var pos = e.GetPosition(wrapper);
                var delta = pos - pageDragStart.Value;
                if (Math.Abs(delta.X) > 5 || Math.Abs(delta.Y) > 5)
                {
                    pageDragStart = null;
                    StartPageDrag(wrapper, id, e);
                }
            }
        };
        wrapper.PointerReleased += (_, e) =>
        {
            pageDragStart = null;
            if (_isPageDragging)
                EndPageDrag(e);
            else if (command != null)
                SendCommand(command, args);
        };
        // Also handle pointer capture lost (e.g. window deactivation during drag)
        wrapper.PointerCaptureLost += (_, _) =>
        {
            pageDragStart = null;
            if (_isPageDragging)
                CleanUpPageDrag();
        };

        AttachContextMenu(wrapper, el, id);

        return wrapper;
    }

    /// <summary>
    /// Renders a group of buttons as a connected segmented control (shared borders).
    /// Each button is forced to the same width so the group looks uniform.
    /// </summary>
    private Control RenderButtonGroup(JsonElement el)
    {
        if (!el.TryGetProperty("buttons", out var buttons) || buttons.ValueKind != JsonValueKind.Array)
            return new Border();

        var buttonList = buttons.EnumerateArray().ToList();
        if (buttonList.Count == 0) return new Border();

        var r = Radius("ThemeRadiusSm").TopLeft;
        // Use Star columns so every button gets equal width
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(
                string.Join(",", Enumerable.Repeat("*", buttonList.Count))),
        };

        for (var i = 0; i < buttonList.Count; i++)
        {
            var btn = buttonList[i];
            var icon = btn.GetStringProp("icon") ?? "";
            var label = btn.GetStringProp("label") ?? "";
            var command = btn.GetStringProp("command");
            var args = btn.GetStringProp("args") ?? "{}";
            var variant = btn.GetStringProp("variant") ?? "default";

            // State-aware button overrides
            var isActive = false;
            if (command == "toggle_lock")
            {
                icon = _currentPageIsLocked ? "\uD83D\uDD12" : "\uD83D\uDD13"; // 🔒 / 🔓
                label = _currentPageIsLocked ? "Locked" : "Lock";
                isActive = _currentPageIsLocked;
            }
            else if (command == "toggle_archive")
            {
                icon = _currentPageIsArchived ? "\uD83D\uDCE6" : "\uD83D\uDCC2"; // 📦 / 📂
                label = _currentPageIsArchived ? "Archived" : "Archive";
                isActive = _currentPageIsArchived;
            }
            else if (command == "toggle_trash")
            {
                icon = _currentPageIsTrashed ? "\u267B\uFE0F" : "\uD83D\uDDD1"; // ♻️ / 🗑
                label = _currentPageIsTrashed ? "Restore" : "Trash";
                variant = _currentPageIsTrashed ? "default" : variant;
            }
            var isSaveDisabled = command == "save_page" && _currentPageIsLocked;

            var cornerRadius = i == 0
                ? new CornerRadius(r, 0, 0, r)
                : i == buttonList.Count - 1
                    ? new CornerRadius(0, r, r, 0)
                    : new CornerRadius(0);

            var contentPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Dbl("ThemeSpacingXs", 4),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            if (!string.IsNullOrEmpty(icon))
            {
                contentPanel.Children.Add(new TextBlock
                {
                    Text = icon,
                    FontSize = FontSize("ThemeFontSizeMd", 14),
                    MinWidth = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            contentPanel.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = FontSize("ThemeFontSizeSm", 12),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = isSaveDisabled ? TextMuted
                    : variant == "danger" ? DangerBrush : TextPrimary,
            });

            var button = new Button
            {
                Content = contentPanel,
                CornerRadius = cornerRadius,
                Padding = Thick("ThemeButtonPaddingSm"),
                Background = isActive ? PrimarySubtle : Brushes.Transparent,
                BorderBrush = BorderBrush_,
                BorderThickness = new Thickness(
                    i == 0 ? 1 : 0, 1,
                    1, 1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                IsEnabled = !isSaveDisabled,
                Opacity = isSaveDisabled ? 0.5 : 1.0,
            };

            if (command != null && !isSaveDisabled)
            {
                var cmd = command;
                var a = args;
                button.Click += (_, _) => SendCommand(cmd, a);
            }

            Grid.SetColumn(button, i);
            grid.Children.Add(button);
        }

        return grid;
    }

    private Control RenderBlockEditor(JsonElement el)
    {
        var hasPluginBlocks = el.TryGetProperty("blocks", out var pluginBlocks)
                              && pluginBlocks.ValueKind == JsonValueKind.Array
                              && pluginBlocks.GetArrayLength() > 0;

        // Detect current page ID from the view context
        var pageId = el.GetStringProp("page_id") ?? _shadowState.PageId ?? "__default__";

        _log.Debug("Shadow: RenderBlockEditor — pageId={PageId} hasPluginBlocks={HasPlugin} shadowPageId={ShadowPage} shadowHasBlocks={ShadowHas} shadowIsDirty={Dirty} shadowBlockCount={Count}",
            pageId, hasPluginBlocks, _shadowState.PageId, _shadowState.HasBlocks, _shadowState.IsDirty, _shadowState.Blocks.Count);

        // Load shadow state from plugin JSON on first render or page change
        if (_shadowState.PageId != pageId)
        {
            _log.Debug("Shadow: Page changed from {Old} to {New} — flushing and reloading",
                _shadowState.PageId, pageId);
            // Flush + save outgoing page before switching
            if (_shadowState.IsDirty)
            {
                _shadowState.FlushSync(SendCommandSilent);
                SendCommandSilent("save_page", "{}");
            }
            _shadowState.Clear();
            if (hasPluginBlocks)
                _shadowState.LoadFromPluginJson(pageId, pluginBlocks);
        }

        // Determine effective block source: shadow state wins if populated
        var hasShadowBlocks = _shadowState.PageId == pageId && _shadowState.HasBlocks;
        var hasBlocks = hasShadowBlocks || hasPluginBlocks;

        // If both shadow and plugin have blocks, merge any new plugin blocks
        // into shadow (e.g. block added via palette command)
        if (hasShadowBlocks && hasPluginBlocks)
        {
            var newIds = _shadowState.MergeNewBlocksFromPlugin(pluginBlocks);
            if (newIds.Count > 0)
                _pendingFocusBlockId = newIds[^1]; // focus the last newly added block
        }

        _log.Debug("Shadow: Source decision — hasShadowBlocks={Shadow} hasPluginBlocks={Plugin} → rendering from {Source} (count={Count})",
            hasShadowBlocks, hasPluginBlocks, hasShadowBlocks ? "SHADOW" : hasPluginBlocks ? "PLUGIN" : "EMPTY",
            hasShadowBlocks ? _shadowState.Blocks.Count : 0);

        // If shadow has blocks, serialize to JSON for RenderBlock compatibility
        JsonElement blocks = default;
        if (hasShadowBlocks)
        {
            var shadowJson = _shadowState.SerializeBlocksJson();
            _log.Debug("Shadow: Serialized {Count} shadow blocks, JSON length={Len}",
                _shadowState.Blocks.Count, shadowJson.Length);
            using var shadowDoc = JsonDocument.Parse(shadowJson);
            blocks = shadowDoc.RootElement.Clone();
        }
        else if (hasPluginBlocks)
        {
            blocks = pluginBlocks;
            if (!_shadowState.HasBlocks)
                _shadowState.LoadFromPluginJson(pageId, pluginBlocks);
        }

        // -- Block view (rich editor) --
        var blockPanel = new StackPanel { Spacing = Dbl("ThemeSpacingMd", 12), Margin = new Thickness(0, 0, 0, 120) };
        _blockPanel = blockPanel;

        // Wire PointerMoved on panel so dragging into empty space below last block still works
        blockPanel.PointerMoved += (_, e) => UpdateBlockDrag(e);
        blockPanel.PointerReleased += (_, e) =>
        {
            if (_isDragging) EndBlockDrag(e);
        };

        if (!hasBlocks)
        {
            var placeholder = new Controls.RichTextEditor.RichTextEditor
            {
                Markdown = "",
                BlockId = "__empty__",
                FontSize = FontSize("ThemeFontSizeMd", 14),
                MinHeight = FontSize("ThemeFontSizeMd", 14) * 1.6,
                Opacity = 0.5,
            };
            var fired = false;
            placeholder.TextChanged += (_, markdown) =>
            {
                if (fired) return;
                fired = true;
                SendCommandSilent("add_block", JsonSerializer.Serialize(new { text = markdown }));
            };
            placeholder.GotFocus += (_, _) => placeholder.Opacity = 1.0;
            placeholder.LostFocus += (_, _) => placeholder.Opacity = 0.5;
            blockPanel.Children.Add(placeholder);
        }
        else
        {
            var blockArray = new List<JsonElement>();
            foreach (var b in blocks.EnumerateArray()) blockArray.Add(b);

            // Check if we should use lazy rendering (large document, no side-by-side blocks)
            var blockCount = blockArray.Count;
            var hasSideBySide = blockArray.Any(b => b.GetStringProp("layout") == "side_by_side");
            var useLazyRendering = blockCount >= 30 && !hasSideBySide;

            if (useLazyRendering)
            {
                // Large document - use lazy/virtualized rendering
                _lazyBlockRenderer?.Dispose();
                _lazyBlockRenderer = new Controls.LazyBlockRenderer(blockPanel);
                _lazyBlockRenderer.Initialize(
                    blocks,
                    renderBlock: metadata => RenderBlock(metadata.Data, metadata.Type),
                    wireEvents: null // Events are wired inside RenderBlock
                );
                _log.Debug("BlockEditor: Using lazy rendering for {Count} blocks", blockCount);
            }
            else
            {
                // Small document or has side-by-side - render all immediately
                for (var bi = 0; bi < blockArray.Count; bi++)
                {
                    var block = blockArray[bi];
                    var blockType = block.GetStringProp("type") ?? "paragraph";
                    var pairId = block.GetStringProp("pair_id");
                    var layout = block.GetStringProp("layout");

                    // Side-by-side detection: check if next block has same pair_id
                    if (layout == "side_by_side" && pairId is not null && bi + 1 < blockArray.Count)
                    {
                        var nextBlock = blockArray[bi + 1];
                        var nextPairId = nextBlock.GetStringProp("pair_id");

                        if (nextPairId == pairId)
                        {
                            var nextBlockType = nextBlock.GetStringProp("type") ?? "paragraph";
                            var leftInner = RenderBlock(block, blockType);
                            var rightInner = RenderBlock(nextBlock, nextBlockType);

                            // Default to top-aligned; read persisted alignment from block data
                            var pairAlign = block.GetStringProp("pair_valign") ?? "top";
                            var valign = pairAlign switch
                            {
                                "center" => VerticalAlignment.Center,
                                "bottom" => VerticalAlignment.Bottom,
                                _ => VerticalAlignment.Top,
                            };

                            leftInner.VerticalAlignment = valign;
                            rightInner.VerticalAlignment = valign;

                            var pairGrid = new Grid
                            {
                                ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(Dbl("ThemeSpacingSm", 8), GridUnitType.Pixel), new ColumnDefinition(1, GridUnitType.Star) },
                            };
                            pairGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                            Grid.SetColumn(leftInner, 0);
                            Grid.SetColumn(rightInner, 2);
                            pairGrid.Children.Add(leftInner);
                            pairGrid.Children.Add(rightInner);
                            blockPanel.Children.Add(pairGrid);

                            bi++; // skip the next block since we already rendered it
                            continue;
                        }
                    }

                    var rendered = RenderBlock(block, blockType);
                    blockPanel.Children.Add(rendered);
                }
            }
        }

        // Lock enforcement: make all editors read-only and hide drag handles
        if (_currentPageIsLocked)
        {
            ApplyLockToBlockPanel(blockPanel);
        }

        // Focus newly added block after render
        if (_pendingFocusBlockId is not null)
        {
            var focusId = _pendingFocusBlockId;
            _pendingFocusBlockId = null;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var editor = FindEditorInBlock(blockPanel, focusId);
                if (editor is Controls.RichTextEditor.RichTextEditor rte)
                {
                    rte.BringIntoView();
                    rte.SetCaretToStart();
                }
                else if (editor is not null)
                {
                    editor.BringIntoView();
                    editor.Focus();
                }
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }

        // Start 300ms sync timer to drain pending commands to plugin
        _syncTimer?.Stop();
        _syncTimer?.Dispose();
        _syncTimer = new System.Timers.Timer(300) { AutoReset = true };
        _syncTimer.Elapsed += (_, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_shadowState.IsDirty)
                    _log.Debug("Shadow: Timer drain — pending commands queued");
                if (_shadowState.DrainPendingCommands(SendCommandSilent))
                {
                    // Commands were sent — persist to disk
                    SendCommandSilent("save_page", "{}");
                    if (_saveStatusText != null)
                        _saveStatusText.Text = "Saved";
                }
            });
        };
        _syncTimer.Start();

        // -- Markdown view (raw text editor) --
        var fullMarkdown = hasBlocks ? BuildFullMarkdown(blocks) : "";
        var markdownEditor = new Controls.CodeBlockEditor
        {
            BlockId = "__markdown_view__",
            IsVisible = false,
            ShowToolbar = false,
            IsWordWrapEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 400,
        };
        markdownEditor.Code = fullMarkdown;
        markdownEditor.Language = "Markdown";

        // Eagerly compute markdown (the JsonElement may be disposed after render)
        var cachedMarkdown = fullMarkdown;

        // -- Segmented view mode selector (Rich Editor | Markdown) --
        var r = Radius("ThemeRadiusSm").TopLeft;
        var modeLabels = new[] { "\u270F\uFE0F Rich Editor", "\uD83D\uDCDD Markdown" };
        var modeButtons = new Button[2];
        var modeGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            Margin = new Thickness(0, 0, 0, Dbl("ThemeSpacingSm", 8)),
            MaxWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        // Wrap blockPanel in a Grid so the drag overlay Canvas can layer on top
        var blockPanelHost = new Grid();
        blockPanelHost.Children.Add(blockPanel);

        // Current mode: 0=Rich, 1=Markdown
        var currentMode = 0;
        var container = new DockPanel { LastChildFill = true, HorizontalAlignment = HorizontalAlignment.Stretch, Focusable = true };

        // Markdown→blocks sync state (declared early so button handlers can reference)
        System.Timers.Timer? mdSyncTimer = null;
        var mdDirty = false;

        void ApplyMode(int mode)
        {
            currentMode = mode;
            for (var i = 0; i < 2; i++)
            {
                modeButtons[i].Background = i == mode ? Primary : Brushes.Transparent;
                modeButtons[i].Foreground = i == mode ? Brushes.White : TextMuted;
            }

            switch (mode)
            {
                case 0: // Rich Editor
                    blockPanelHost.IsVisible = true;
                    markdownEditor.IsVisible = false;
                    break;
                case 1: // Markdown
                    markdownEditor.Code = cachedMarkdown;
                    blockPanelHost.IsVisible = false;
                    markdownEditor.IsVisible = true;
                    break;
            }
        }

        for (var i = 0; i < 2; i++)
        {
            var idx = i;
            var cornerRadius = i == 0
                ? new CornerRadius(r, 0, 0, r)
                : new CornerRadius(0, r, r, 0);

            modeButtons[i] = new Button
            {
                Content = modeLabels[i],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = Thick("ThemeButtonPaddingSm"),
                FontSize = FontSize("ThemeFontSizeSm", 12),
                CornerRadius = cornerRadius,
                Background = i == 0 ? Primary : Brushes.Transparent,
                Foreground = i == 0 ? Brushes.White : TextMuted,
            };
            modeButtons[i].Click += (_, _) =>
            {
                var wasMarkdownMode = currentMode == 1;

                // Capture markdown from the markdown editor
                if (currentMode == 1)
                    cachedMarkdown = markdownEditor.Code;

                // If markdown was edited, flush to backend and refresh blocks
                if (wasMarkdownMode && mdDirty && idx == 0)
                {
                    FlushMdSync();
                    ApplyMode(idx);
                    if (PluginId is not null)
                        SettingsWriter?.Invoke(PluginId, "block_editor_view_mode", idx.ToString());
                    ViewStateRefreshRequested?.Invoke();
                    return;
                }

                ApplyMode(idx);
                if (PluginId is not null)
                    SettingsWriter?.Invoke(PluginId, "block_editor_view_mode", idx.ToString());
            };
            Grid.SetColumn(modeButtons[i], i);
            modeGrid.Children.Add(modeButtons[i]);
        }

        // Place view toggle in header if host exists, otherwise inline
        if (_headerViewToggleHost != null)
        {
            _headerViewToggleHost.Children.Add(modeGrid);
        }
        else
        {
            DockPanel.SetDock(modeGrid, Dock.Top);
            container.Children.Add(modeGrid);
        }
        // Both editors overlay each other (only one visible at a time)
        var editorHost = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        editorHost.Children.Add(blockPanelHost);
        editorHost.Children.Add(markdownEditor);
        container.Children.Add(editorHost);

        // Restore persisted view mode
        if (PluginId is not null)
        {
            var saved = SettingsReader?.Invoke(PluginId, "block_editor_view_mode");
            if (int.TryParse(saved, out var savedMode) && savedMode is >= 0 and <= 1)
                ApplyMode(savedMode);
        }

        // Ctrl+S / Cmd+S saves immediately; Ctrl+E / Cmd+E opens emoji picker
        container.KeyDown += (_, e) =>
        {
            var cmdOrCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            if (e.Key == Avalonia.Input.Key.S && cmdOrCtrl)
            {
                _shadowState.FlushSync(SendCommandSilent);
                SendCommandSilent("save_page", "{}");
                e.Handled = true;
            }
            else if (e.Key == Avalonia.Input.Key.E && cmdOrCtrl)
            {
                OpenPageEmojiPicker();
                e.Handled = true;
            }
        };

        // Block editor LostFocus → auto-save
        blockPanel.LostFocus += (_, e) =>
        {
            // Only save when focus leaves the block panel entirely
            if (e.Source is Control source && source.FindAncestorOfType<StackPanel>() == blockPanel)
                return;
            _shadowState.FlushSync(SendCommandSilent);
            SendCommandSilent("save_page", "{}");
        };

        // Live-sync: rebuild markdown from live block content
        _onBlockContentChanged = () =>
        {
            var live = _shadowState.HasBlocks ? _shadowState.BuildMarkdown() : BuildMarkdownFromLiveBlocks();
            cachedMarkdown = live;
            if (_saveStatusText != null)
                _saveStatusText.Text = "Unsaved changes";
            // Rebuild any TOC panels so they reflect current headings
            foreach (var (_, tocPanel) in _tocPanels)
                RebuildTocEntries(tocPanel);
        };

        // Debounce markdown→blocks sync: persist to backend, refresh on mode switch
        void DebounceMdSync(string md)
        {
            cachedMarkdown = md;
            mdDirty = true;
            mdSyncTimer?.Stop();
            mdSyncTimer?.Dispose();
            mdSyncTimer = new System.Timers.Timer(500) { AutoReset = false };
            mdSyncTimer.Elapsed += (_, _) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    SendCommandSilent("set_content_from_markdown",
                        JsonSerializer.Serialize(new { markdown = md }));
                });
            };
            mdSyncTimer.Start();
        }

        void FlushMdSync()
        {
            if (!mdDirty) return;
            mdSyncTimer?.Stop();
            mdSyncTimer?.Dispose();
            mdSyncTimer = null;
            SendCommandSilent("set_content_from_markdown",
                JsonSerializer.Serialize(new { markdown = cachedMarkdown }));
            mdDirty = false;
        }

        markdownEditor.CodeChanged += (_, _) =>
        {
            if (currentMode == 1)
                DebounceMdSync(markdownEditor.Code);
        };

        return container;
    }

    /// <summary>
    /// Builds a full markdown string from the block JSON array for the raw view.
    /// </summary>
    private static string BuildFullMarkdown(JsonElement blocks)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var block in blocks.EnumerateArray())
        {
            var type = block.GetStringProp("type") ?? "paragraph";
            switch (type)
            {
                case "heading":
                    var level = block.GetIntProp("level", 1);
                    sb.Append(new string('#', level));
                    sb.Append(' ');
                    sb.AppendLine(block.GetStringProp("text") ?? "");
                    break;
                case "code_block":
                    var lang = block.GetStringProp("language") ?? "";
                    sb.AppendLine($"```{lang}");
                    sb.AppendLine(block.GetStringProp("code") ?? block.GetStringProp("text") ?? "");
                    sb.AppendLine("```");
                    break;
                case "blockquote":
                    foreach (var line in (block.GetStringProp("text") ?? "").Split('\n'))
                        sb.AppendLine($"> {line}");
                    break;
                case "callout":
                    var icon = block.GetStringProp("icon") ?? "ℹ️";
                    sb.AppendLine($"> {icon} {block.GetStringProp("text") ?? ""}");
                    break;
                case "bullet_list":
                    if (block.TryGetProperty("items", out var bItems))
                        AppendListItems(sb, bItems, "- ", 0);
                    break;
                case "numbered_list":
                    if (block.TryGetProperty("items", out var nItems))
                        AppendNumberedListItems(sb, nItems, 0);
                    break;
                case "task_list":
                    if (block.TryGetProperty("items", out var tItems))
                        AppendTaskListItems(sb, tItems, 0);
                    break;
                case "horizontal_rule":
                    sb.AppendLine("---");
                    break;
                case "image":
                    var alt = block.GetStringProp("alt") ?? "";
                    var url = block.GetStringProp("url") ?? "";
                    sb.AppendLine($"![{alt}]({url})");
                    break;
                default:
                    sb.AppendLine(block.GetStringProp("text") ?? "");
                    break;
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static void AppendListItems(System.Text.StringBuilder sb, JsonElement items, string prefix, int depth)
    {
        var indent = new string(' ', depth * 2);
        foreach (var item in items.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? ""
                : item.GetStringProp("text") ?? "";
            sb.AppendLine($"{indent}{prefix}{text}");
            if (item.TryGetProperty("children", out var children) && children.GetArrayLength() > 0)
                AppendListItems(sb, children, prefix, depth + 1);
        }
    }

    private static void AppendNumberedListItems(System.Text.StringBuilder sb, JsonElement items, int depth)
    {
        var indent = new string(' ', depth * 2);
        var idx = 0;
        foreach (var item in items.EnumerateArray())
        {
            idx++;
            var text = item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? ""
                : item.GetStringProp("text") ?? "";
            sb.AppendLine($"{indent}{idx}. {text}");
            if (item.TryGetProperty("children", out var children) && children.GetArrayLength() > 0)
                AppendNumberedListItems(sb, children, depth + 1);
        }
    }

    private static void AppendTaskListItems(System.Text.StringBuilder sb, JsonElement items, int depth)
    {
        var indent = new string(' ', depth * 2);
        foreach (var item in items.EnumerateArray())
        {
            var text = item.GetStringProp("text") ?? "";
            var isChecked = item.GetBoolProp("checked", false) || item.GetBoolProp("is_checked", false);
            var check = isChecked ? "x" : " ";
            sb.AppendLine($"{indent}- [{check}] {text}");
            if (item.TryGetProperty("children", out var children) && children.GetArrayLength() > 0)
                AppendTaskListItems(sb, children, depth + 1);
        }
    }

    /// <summary>
    /// Walks a control tree to find the first RichTextEditor (used for merge operations).
    /// </summary>
    private static Controls.RichTextEditor.RichTextEditor? FindRichTextEditor(Control control)
    {
        if (control is Controls.RichTextEditor.RichTextEditor rte) return rte;
        if (control is Border b && b.Child is Control bc) return FindRichTextEditor(bc);
        if (control is Decorator d && d.Child is Control dc) return FindRichTextEditor(dc);
        if (control is Panel p)
        {
            foreach (var c in p.Children)
                if (c is Control cc)
                {
                    var found = FindRichTextEditor(cc);
                    if (found != null) return found;
                }
        }
        return null;
    }

    /// <summary>
    /// Recursively collects all RichTextEditors inside a control tree (in visual order).
    /// </summary>
    private static void CollectEditors(Control control, List<Controls.RichTextEditor.RichTextEditor> result)
    {
        if (control is Controls.RichTextEditor.RichTextEditor rte) { result.Add(rte); return; }
        if (control is Border b && b.Child is Control bc) { CollectEditors(bc, result); return; }
        if (control is Decorator d && d.Child is Control dc) { CollectEditors(dc, result); return; }
        if (control is Panel p)
            foreach (var c in p.Children)
                if (c is Control cc)
                    CollectEditors(cc, result);
    }

    /// <summary>
    /// Optimistically re-renders a single block in place from shadow state.
    /// </summary>
    private void ReplaceBlockInPlace(string blockId)
    {
        if (_blockPanel == null) return;

        var shadowBlock = _shadowState.GetBlock(blockId);
        if (shadowBlock == null) return;
        var blockJson = _shadowState.SerializeSingleBlockJson(blockId);
        if (blockJson == null) return;

        using var doc = JsonDocument.Parse(blockJson);
        var rendered = RenderBlock(doc.RootElement.Clone(), shadowBlock.Type);

        // Search top-level borders
        for (var i = 0; i < _blockPanel.Children.Count; i++)
        {
            if (_blockPanel.Children[i] is Border b && b.Tag is string bid && bid == blockId)
            {
                _blockPanel.Children[i] = rendered;
                _onBlockContentChanged?.Invoke();
                return;
            }
            // Also search inside pair Grids
            if (_blockPanel.Children[i] is Grid pairGrid)
            {
                for (var j = 0; j < pairGrid.Children.Count; j++)
                {
                    if (pairGrid.Children[j] is Border bw && bw.Tag is string bwId && bwId == blockId)
                    {
                        Grid.SetColumn(rendered, Grid.GetColumn(bw));
                        pairGrid.Children[j] = rendered;
                        _onBlockContentChanged?.Invoke();
                        return;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Recursively walks the block panel to set all editors to read-only and hide
    /// drag handles / delete buttons when a page is locked.
    /// </summary>
    private static void ApplyLockToBlockPanel(Control root)
    {
        // Editors become read-only (text selection + link clicks still work).
        // Block wrapper chrome (drag handle, delete, toolbar, hover/focus highlight)
        // is hidden so the page looks static.
        if (root is StackPanel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is Border wrapper && wrapper.Tag is string)
                {
                    LockBlockWrapper(wrapper);
                }
                else if (child is Grid pairGrid)
                {
                    // Side-by-side pair grid — lock each block wrapper inside
                    foreach (var gc in pairGrid.Children)
                    {
                        if (gc is Border bw && bw.Tag is string)
                            LockBlockWrapper(bw);
                    }
                }
            }
        }
    }

    private static void LockBlockWrapper(Border wrapper)
    {
        // The wrapper's child is a Panel overlay containing:
        //   [0] Grid with columns: dragHandle | content | gap | actionsPanel
        //   [1] toolbar
        if (wrapper.Child is Panel overlay)
        {
            // Hide toolbar
            if (overlay.Children.Count > 1)
            {
                overlay.Children[1].IsVisible = false;
                overlay.Children[1].IsHitTestVisible = false;
            }

            // Hide drag handle (col 0) and actions panel (col 3)
            if (overlay.Children[0] is Grid grid)
            {
                foreach (var gc in grid.Children)
                {
                    var col = Grid.GetColumn(gc);
                    if (col == 0 || col == 3)
                    {
                        gc.IsVisible = false;
                        gc.IsHitTestVisible = false;
                    }
                }
            }
        }

        wrapper.Classes.Add("locked");
        ApplyLockToEditors(wrapper);
    }

    private static void ApplyLockToEditors(Control control)
    {
        if (control is Controls.RichTextEditor.RichTextEditor rte)
            rte.IsReadOnly = true;
        else if (control is Controls.CodeBlockEditor cbe)
            cbe.IsReadOnly = true;

        if (control is Panel p)
            foreach (var child in p.Children)
                ApplyLockToEditors(child);
        else if (control is Decorator d && d.Child is Control dc)
            ApplyLockToEditors(dc);
        else if (control is ContentControl cc && cc.Content is Control content)
            ApplyLockToEditors(content);
    }

    // ---- Live markdown sync for split view ----
    private Action? _onBlockContentChanged;
    private readonly record struct BlockMeta(string Type, int Level, string Language, string Icon, string Variant, bool Ordered, bool ShowHeader = true, bool AlternatingRows = false);
    private readonly Dictionary<string, BlockMeta> _blockMeta = new();
    // Per-table filter state: blockId → (filterText, rebuildAction)
    private readonly Dictionary<string, (string Filter, Action<string> Rebuild)> _tableFilterState = new();


    /// <summary>
    /// Rebuilds markdown from the live block panel by walking each wrapper,
    /// extracting current text from RichTextEditors and CodeBlockEditors.
    /// </summary>
    private string BuildMarkdownFromLiveBlocks()
    {
        if (_blockPanel is null) return "";
        var sb = new System.Text.StringBuilder();

        foreach (var child in _blockPanel.Children)
        {
            // Each child is a Border wrapper with Tag = blockId
            if (child is not Border { Child: Grid grid, Tag: string blockId }) continue;
            if (!_blockMeta.TryGetValue(blockId, out var meta)) continue;

            // Column 1 of the grid is the inner control
            var inner = grid.Children.Count > 1 ? grid.Children[1] : null;
            if (inner is null) continue;

            switch (meta.Type)
            {
                case "paragraph":
                    sb.AppendLine(ExtractRichText(inner));
                    break;
                case "heading":
                    sb.Append(new string('#', meta.Level));
                    sb.Append(' ');
                    sb.AppendLine(ExtractRichText(inner));
                    break;
                case "code_block":
                    sb.AppendLine($"```{meta.Language}");
                    sb.AppendLine(ExtractCodeText(inner));
                    sb.AppendLine("```");
                    break;
                case "blockquote":
                    foreach (var line in ExtractRichText(inner).Split('\n'))
                        sb.AppendLine($"> {line}");
                    break;
                case "callout":
                    sb.AppendLine($"> {meta.Icon} {ExtractRichText(inner)}");
                    break;
                case "bullet_list":
                    AppendLiveListItems(sb, inner, ordered: false);
                    break;
                case "numbered_list":
                    AppendLiveListItems(sb, inner, ordered: true);
                    break;
                case "task_list":
                    AppendLiveTaskListItems(sb, inner);
                    break;
                case "horizontal_rule":
                    sb.AppendLine("---");
                    break;
                case "image":
                    sb.AppendLine("![image]()");
                    break;
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string ExtractRichText(Control control)
    {
        // Direct RichTextEditor
        if (control is Controls.RichTextEditor.RichTextEditor rte)
            return rte.Markdown;
        // Wrapped in a Border (blockquote, callout)
        if (control is Border { Child: Control c })
            return ExtractRichText(c);
        // Callout row: StackPanel with [icon, editor]
        if (control is StackPanel { Orientation: Orientation.Horizontal } sp)
        {
            foreach (var c2 in sp.Children)
                if (c2 is Controls.RichTextEditor.RichTextEditor rte2)
                    return rte2.Markdown;
        }
        return "";
    }

    private static string ExtractCodeText(Control control)
    {
        if (control is Controls.CodeBlockEditor cbe) return cbe.Code;
        return "";
    }

    private static void AppendLiveListItems(System.Text.StringBuilder sb, Control control, bool ordered)
    {
        if (control is not StackPanel panel) return;
        var idx = 0;
        foreach (var row in panel.Children)
        {
            idx++;
            if (row is not StackPanel { Orientation: Orientation.Horizontal } rowPanel) continue;
            var prefix = ordered ? $"{idx}. " : "- ";
            foreach (var c in rowPanel.Children)
            {
                if (c is Controls.RichTextEditor.RichTextEditor rte)
                {
                    sb.AppendLine($"{prefix}{rte.Markdown}");
                    break;
                }
            }
        }
    }

    private static void AppendLiveTaskListItems(System.Text.StringBuilder sb, Control control)
    {
        if (control is not StackPanel panel) return;
        foreach (var row in panel.Children)
        {
            if (row is not StackPanel { Orientation: Orientation.Horizontal } rowPanel) continue;
            var isChecked = false;
            string text = "";
            foreach (var c in rowPanel.Children)
            {
                if (c is CheckBox cb) isChecked = cb.IsChecked == true;
                if (c is Controls.RichTextEditor.RichTextEditor rte) text = rte.Markdown;
            }
            var check = isChecked ? "x" : " ";
            sb.AppendLine($"- [{check}] {text}");
        }
    }

    /// <summary>
    /// Rebuilds the block panel from fresh backend data without a full view re-render.
    /// Used by split view to sync markdown edits → blocks in real time.
    /// </summary>
    private void RefreshBlockPanel()
    {
        if (_blockPanel is null || ViewStateProvider is null) return;

        var json = ViewStateProvider();
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            // Walk the rendered JSON to find the block_editor's blocks array
            var blocksEl = FindBlocksInJson(doc.RootElement);
            if (blocksEl is null) return;

            _blockMeta.Clear();
            _blockPanel.Children.Clear();

            foreach (var block in blocksEl.Value.EnumerateArray())
            {
                var blockType = block.GetStringProp("type") ?? "paragraph";
                _blockPanel.Children.Add(RenderBlock(block, blockType));
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to refresh block panel from backend");
        }
    }

    /// Recursively search the rendered JSON tree for a "blocks" array inside a block_editor.
    private static JsonElement? FindBlocksInJson(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("type", out var t) && t.GetString() == "block_editor"
                && el.TryGetProperty("blocks", out var b) && b.ValueKind == JsonValueKind.Array)
                return b;

            foreach (var prop in el.EnumerateObject())
            {
                var found = FindBlocksInJson(prop.Value);
                if (found is not null) return found;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var found = FindBlocksInJson(item);
                if (found is not null) return found;
            }
        }
        return null;
    }

    // ---- Page list title/icon labels keyed by page ID for live updates ----
    private readonly Dictionary<string, TextBlock> _pageListTitles = new();
    private readonly Dictionary<string, TextBlock> _pageListIcons = new();

    // ---- TOC panels that need rebuilding when headings change ----
    private readonly List<(string BlockId, StackPanel Panel)> _tocPanels = [];

    // ---- Current page state (set by RenderDetailHeader) ----
    private bool _currentPageIsLocked;
    private bool _currentPageIsArchived;
    private bool _currentPageIsTrashed;
    private List<(string Id, string Title, string Icon)> _currentChildPages = [];

    // ---- View toggle host (set by RenderDetailHeader when view_toggle: true) ----
    private Panel? _headerViewToggleHost;

    // ---- Emoji picker shortcut state (set by RenderDetailHeader) ----
    private Button? _pageIconBtn;
    private string? _pageIconCommand;
    private string? _pageIconPageId;

    // ---- Shadow state: single source of truth for block content ----
    private readonly BlockShadowState _shadowState = new();
    private System.Timers.Timer? _syncTimer;
    private TextBlock? _saveStatusText;
    private NeuronGraphControl? _activeGraphControl;
    private DeferredGraphData? _pendingGraphHydration;
    private string? _pendingFocusBlockId;
    private string? _focusedBlockId;

    /// <summary>The block ID currently focused by the user, or null if none.</summary>
    public string? FocusedBlockId => _focusedBlockId;

    // ---- Page drag state ----
    private Panel? _pageListPanel; // Grid or StackPanel containing page item Borders
    private bool _isPageDragging;
    private string? _pageDragSourceId;
    private Border? _pageDragSourceWrapper;
    private Point _pageDragPointerOffset;
    private Border? _pageDragGhost;
    private Canvas? _pageDragOverlay;
    private Border? _pageDropIndicator;
    private Border? _pageDragTargetWrapper;
    private string? _pageDragTargetPosition; // "before", "after", "child"
    private Avalonia.Threading.DispatcherTimer? _pageDragScrollTimer;
    private double _pageDragScrollSpeed;
    private Avalonia.Threading.DispatcherTimer? _pageAutoExpandTimer;
    private string? _pageAutoExpandTargetId;

    // ---- Custom block drag state ----
    private StackPanel? _blockPanel;
    private Controls.LazyBlockRenderer? _lazyBlockRenderer;
    private Border? _dropIndicator;
    private Border? _dragGhost;
    private Canvas? _dragOverlay;
    private bool _isDragging;
    private bool _isResizingImage;
    private string? _dragSourceBlockId;
    private Border? _dragSourceWrapper;
    private Point _dragPointerOffset;

    // Target tracking during drag
    private Border? _dragTargetWrapper;
    private bool _dragTargetBelow;
    private bool _dragTargetPair; // true when dragging to side-by-side position
    private Border? _pairDropIndicator; // vertical indicator for side-by-side
    private Avalonia.Threading.DispatcherTimer? _dragScrollTimer;
    private double _dragScrollSpeed; // pixels per tick, negative=up, positive=down

    private Control RenderBlock(JsonElement block, string blockType)
    {
        var blockId = block.GetStringProp("id") ?? "";
        _blockMeta[blockId] = new BlockMeta(
            Type: blockType,
            Level: block.GetIntProp("level", 1),
            Language: block.GetStringProp("language") ?? "",
            Icon: block.GetStringProp("icon") ?? "",
            Variant: block.GetStringProp("variant") ?? "",
            Ordered: blockType == "numbered_list",
            ShowHeader: !block.TryGetProperty("show_header", out var shMeta) || shMeta.GetBoolean(),
            AlternatingRows: block.TryGetProperty("alternating_rows", out var arMeta) && arMeta.GetBoolean());

        var inner = blockType switch
        {
            "paragraph" => RenderBlockParagraph(block),
            "heading" => RenderBlockHeading(block),
            "code_block" => RenderBlockCodeBlock(block),
            "blockquote" => RenderBlockQuote(block),
            "callout" => RenderBlockCallout(block),
            "bullet_list" => RenderBlockList(block, ordered: false),
            "numbered_list" => RenderBlockList(block, ordered: true),
            "task_list" => RenderBlockTaskList(block),
            "image" => RenderBlockImage(block),
            "footnote" => RenderBlockFootnote(block),
            "definition_list" => RenderBlockDefinitionList(block),
            "table" => RenderBlockTable(block),
            "table_of_contents" => RenderBlockTableOfContents(block),
            "horizontal_rule" => new Separator
            {
                Margin = new Thickness(0, Dbl("ThemeSpacingSm", 8)),
                Background = TextPrimary,
            },
            _ => RenderBlockParagraph(block),
        };

        return WrapBlockWithControls(inner, blockId);
    }

    // ---- Page drag methods ----

    private void EnsurePageDragOverlay()
    {
        if (_pageDragOverlay != null) return;

        _pageDropIndicator = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = Primary,
            IsVisible = false,
            IsHitTestVisible = false,
        };

        _pageDragOverlay = new Canvas
        {
            IsHitTestVisible = false,
            ClipToBounds = false,
        };
    }

    /// <summary>
    /// Collect all page-item Borders from the panel.
    /// Works for both Grid (vertical stack) and StackPanel ($for array).
    /// </summary>
    private List<Border> GetPageItemBorders()
    {
        if (_pageListPanel == null) return [];
        return _pageListPanel.Children
            .OfType<Border>()
            .Where(b => b.Tag is string s && !string.IsNullOrEmpty(s))
            .ToList();
    }

    private void StartPageDrag(Border wrapper, string pageId, PointerEventArgs e)
    {
        // Discover the page list panel from the wrapper's parent (Grid or StackPanel)
        if (_pageListPanel == null || !_pageListPanel.Children.Contains(wrapper))
            _pageListPanel = wrapper.Parent as Panel;

        if (_isPageDragging || _pageListPanel == null) return;
        _isPageDragging = true;
        _pageDragSourceId = pageId;
        _pageDragSourceWrapper = wrapper;
        _pageDragTargetWrapper = null;

        EnsurePageDragOverlay();

        // Create ghost: bitmap snapshot of the page item
        var bounds = wrapper.Bounds;
        var ghost = new Border
        {
            Width = bounds.Width,
            Height = bounds.Height,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = PrimarySubtle,
            Background = SurfaceElevated,
            Opacity = 0.85,
            IsHitTestVisible = false,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0, OffsetY = 4, Blur = 12,
                Color = Color.FromArgb(80, 0, 0, 0),
            }),
        };

        try
        {
            var renderTarget = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new PixelSize((int)bounds.Width, (int)bounds.Height),
                new Vector(96, 96));
            renderTarget.Render(wrapper);
            ghost.Child = new Avalonia.Controls.Image
            {
                Source = renderTarget,
                Width = bounds.Width,
                Height = bounds.Height,
                Stretch = Stretch.None,
            };
        }
        catch
        {
            ghost.Child = new TextBlock
            {
                Text = "Moving page...",
                Foreground = TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        _pageDragGhost = ghost;

        // Position ghost at current item location
        var posInPanel = e.GetPosition(_pageListPanel);
        _pageDragPointerOffset = new Point(
            posInPanel.X - bounds.X,
            posInPanel.Y - bounds.Y);

        Canvas.SetLeft(ghost, bounds.X);
        Canvas.SetTop(ghost, bounds.Y);

        // Add overlay into the page list panel itself (works for Grid or StackPanel)
        if (_pageDragOverlay!.Parent is Panel ovParent)
            ovParent.Children.Remove(_pageDragOverlay);

        if (!_pageListPanel.Children.Contains(_pageDragOverlay))
            _pageListPanel.Children.Add(_pageDragOverlay);

        _pageDragOverlay.Children.Clear();
        _pageDragOverlay.Children.Add(ghost);

        // Fade source
        wrapper.Opacity = 0.25;

        // Capture pointer on wrapper to track movement
        e.Pointer.Capture(wrapper);
        e.Handled = true;
    }

    private void UpdatePageDrag(PointerEventArgs e)
    {
        if (!_isPageDragging || _pageDragGhost == null || _pageListPanel == null) return;

        var posInPanel = e.GetPosition(_pageListPanel);

        // Move ghost
        Canvas.SetLeft(_pageDragGhost, posInPanel.X - _pageDragPointerOffset.X);
        Canvas.SetTop(_pageDragGhost, posInPanel.Y - _pageDragPointerOffset.Y);

        // Reset previous target highlight
        if (_pageDragTargetWrapper != null && _pageDragTargetWrapper != _pageDragSourceWrapper)
            _pageDragTargetWrapper.Background = Brushes.Transparent;

        // Find which page item wrapper is under the pointer (by Y position)
        Border? closestWrapper = null;
        var dropPosition = "after";
        var borders = GetPageItemBorders();

        foreach (var bw in borders)
        {
            if (bw == _pageDragSourceWrapper) continue;

            var childBounds = bw.Bounds;

            if (posInPanel.Y < childBounds.Y + childBounds.Height)
            {
                closestWrapper = bw;
                var relY = posInPanel.Y - childBounds.Y;
                var quarter = childBounds.Height * 0.25;

                if (relY < quarter)
                    dropPosition = "before";
                else if (relY > childBounds.Height - quarter)
                    dropPosition = "after";
                else
                    dropPosition = "child";
                break;
            }
            // Track last valid item as fallback (pointer below all items)
            closestWrapper = bw;
            dropPosition = "after";
        }

        if (closestWrapper != null)
        {
            _pageDragTargetWrapper = closestWrapper;
            _pageDragTargetPosition = dropPosition;

            if (dropPosition == "child")
            {
                // Highlight the entire target item with semi-transparent primary bg
                HidePageDropIndicator();
                closestWrapper.Background = new SolidColorBrush(
                    ((ISolidColorBrush)Primary).Color, 0.15);
            }
            else
            {
                // Reset any child highlight on target
                closestWrapper.Background = Brushes.Transparent;
                // Show horizontal blue line indicator (positioned in overlay canvas)
                ShowPageDropIndicator(closestWrapper, dropPosition == "after");
            }

            // Auto-expand on hover for collapsed parent in "child" zone
            var targetId = closestWrapper.Tag as string;
            if (dropPosition == "child" && targetId != _pageAutoExpandTargetId)
            {
                _pageAutoExpandTimer?.Stop();
                _pageAutoExpandTargetId = targetId;
                _pageAutoExpandTimer = new Avalonia.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                _pageAutoExpandTimer.Tick += (_, _) =>
                {
                    _pageAutoExpandTimer?.Stop();
                    if (_isPageDragging && _pageAutoExpandTargetId != null)
                        SendCommand("toggle_page_expand", $"{{\"id\":\"{_pageAutoExpandTargetId}\"}}");
                };
                _pageAutoExpandTimer.Start();
            }
            else if (dropPosition != "child")
            {
                _pageAutoExpandTimer?.Stop();
                _pageAutoExpandTargetId = null;
            }
        }

        // Auto-scroll when pointer is near top/bottom edge of the ScrollViewer
        UpdatePageDragAutoScroll(e);
    }

    /// <summary>
    /// Position the drop indicator line in the overlay canvas at the target's edge.
    /// </summary>
    private void ShowPageDropIndicator(Border target, bool below)
    {
        if (_pageDropIndicator == null || _pageDragOverlay == null) return;

        // Ensure indicator is in the overlay canvas
        if (_pageDropIndicator.Parent is Panel oldParent)
            oldParent.Children.Remove(_pageDropIndicator);
        if (!_pageDragOverlay.Children.Contains(_pageDropIndicator))
            _pageDragOverlay.Children.Add(_pageDropIndicator);

        var bounds = target.Bounds;
        _pageDropIndicator.Width = bounds.Width;
        var y = below ? bounds.Y + bounds.Height - 1.5 : bounds.Y - 1.5;
        Canvas.SetLeft(_pageDropIndicator, bounds.X);
        Canvas.SetTop(_pageDropIndicator, y);
        _pageDropIndicator.IsVisible = true;
    }

    private void HidePageDropIndicator()
    {
        if (_pageDropIndicator != null)
            _pageDropIndicator.IsVisible = false;
    }

    private void UpdatePageDragAutoScroll(PointerEventArgs e)
    {
        const double edgeZone = 40;
        const double scrollSpeed = 8;

        var sv = FindParentScrollViewer(_pageListPanel);
        if (sv == null)
        {
            StopPageDragAutoScroll();
            return;
        }

        var posInScroll = e.GetPosition(sv);
        if (posInScroll.Y < edgeZone)
            _pageDragScrollSpeed = -scrollSpeed * (1.0 - posInScroll.Y / edgeZone);
        else if (posInScroll.Y > sv.Bounds.Height - edgeZone)
            _pageDragScrollSpeed = scrollSpeed * (1.0 - (sv.Bounds.Height - posInScroll.Y) / edgeZone);
        else
        {
            StopPageDragAutoScroll();
            return;
        }

        if (_pageDragScrollTimer == null)
        {
            _pageDragScrollTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _pageDragScrollTimer.Tick += (_, _) =>
            {
                var scrollVw = FindParentScrollViewer(_pageListPanel);
                if (scrollVw == null || !_isPageDragging) { StopPageDragAutoScroll(); return; }
                var newOffset = scrollVw.Offset.Y + _pageDragScrollSpeed;
                newOffset = Math.Max(0, Math.Min(newOffset, scrollVw.Extent.Height - scrollVw.Viewport.Height));
                scrollVw.Offset = new Avalonia.Vector(scrollVw.Offset.X, newOffset);
            };
            _pageDragScrollTimer.Start();
        }
    }

    private void StopPageDragAutoScroll()
    {
        _pageDragScrollTimer?.Stop();
        _pageDragScrollTimer = null;
        _pageDragScrollSpeed = 0;
    }

    private void EndPageDrag(PointerReleasedEventArgs e)
    {
        if (!_isPageDragging) return;

        // Read target fields BEFORE releasing capture, because Capture(null)
        // fires PointerCaptureLost which would call CleanUpPageDrag and null these.
        var sourceId = _pageDragSourceId;
        var targetId = _pageDragTargetWrapper?.Tag as string;
        var position = _pageDragTargetPosition;
        var panel = _pageListPanel;
        var sourceWrapper = _pageDragSourceWrapper;
        var targetWrapper = _pageDragTargetWrapper;

        _isPageDragging = false; // Prevent PointerCaptureLost from cleaning up
        e.Pointer.Capture(null);

        CleanUpPageDrag();

        if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(targetId)
            || string.IsNullOrEmpty(position) || sourceId == targetId)
            return;

        // Send silently to avoid a full re-render flash, then rearrange visually
        SendCommandSilent("move_page",
            $"{{\"id\":\"{sourceId}\",\"target_id\":\"{targetId}\",\"position\":\"{position}\"}}");

        // Visually rearrange the page item in the Grid
        if (panel != null && sourceWrapper != null && targetWrapper != null)
            RearrangePageItemVisual(panel, sourceWrapper, targetWrapper, position);
    }

    private void CleanUpPageDrag()
    {
        _isPageDragging = false;
        StopPageDragAutoScroll();
        _pageAutoExpandTimer?.Stop();
        _pageAutoExpandTargetId = null;

        // Cleanup overlay
        if (_pageDragOverlay != null)
        {
            _pageDragOverlay.Children.Clear();
            if (_pageDragOverlay.Parent is Panel ovParent)
                ovParent.Children.Remove(_pageDragOverlay);
        }
        _pageDragGhost = null;

        // Hide drop indicator
        HidePageDropIndicator();

        // Reset source opacity and target highlight
        if (_pageDragSourceWrapper != null)
            _pageDragSourceWrapper.Opacity = 1.0;
        if (_pageDragTargetWrapper != null)
            _pageDragTargetWrapper.Background = Brushes.Transparent;

        _pageDragSourceWrapper = null;
        _pageDragTargetWrapper = null;
    }

    /// <summary>
    /// Visually rearrange a page item in the Grid after a silent move_page command.
    /// Moves the source Border to the correct position relative to target, and
    /// reassigns Grid.Row on all children so the layout matches the new order.
    /// </summary>
    private void RearrangePageItemVisual(Panel panel, Border source, Border target, string position)
    {
        if (panel is not Grid grid) return;

        // Collect ordered children by current Grid.Row
        var children = grid.Children.OrderBy(c => Grid.GetRow(c)).ToList();

        int srcIdx = children.IndexOf(source);
        int tgtIdx = children.IndexOf(target);
        if (srcIdx < 0 || tgtIdx < 0) return;

        // Remove source from list
        children.RemoveAt(srcIdx);

        // Recalculate target index after removal
        tgtIdx = children.IndexOf(target);

        // Insert at new position
        int insertIdx = position switch
        {
            "before" => tgtIdx,
            "after" => tgtIdx + 1,
            "child" => tgtIdx + 1, // Insert right after the target (parent)
            _ => tgtIdx + 1
        };
        children.Insert(Math.Min(insertIdx, children.Count), source);

        // Update indentation for "child" nesting
        if (position == "child")
        {
            var targetPadding = target.Padding;
            // Source gets one level deeper indent (20px more than target's left padding)
            source.Padding = new Thickness(
                targetPadding.Left + 20,
                source.Padding.Top,
                source.Padding.Right,
                source.Padding.Bottom);
        }

        // Reassign Grid.Row for all children
        for (int i = 0; i < children.Count; i++)
            Grid.SetRow(children[i], i);
    }

    /// <summary>
    /// Ensures the drag overlay canvas and drop indicator exist.
    /// The overlay sits on top of the block panel in a shared Grid.
    /// </summary>
    private void EnsureDragOverlay()
    {
        if (_dragOverlay != null) return;

        _dropIndicator = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = Primary,
            IsVisible = false,
            IsHitTestVisible = false,
        };

        _pairDropIndicator = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = Primary,
            IsVisible = false,
            IsHitTestVisible = false,
        };

        _dragOverlay = new Canvas
        {
            IsHitTestVisible = false,
            ClipToBounds = false,
        };
    }

    private void ShowDropIndicator(Border target, bool below)
    {
        if (_dropIndicator == null || _blockPanel == null) return;

        // Remove from current parent
        if (_dropIndicator.Parent is Panel oldParent)
            oldParent.Children.Remove(_dropIndicator);

        var idx = _blockPanel.Children.IndexOf(target);
        if (idx < 0) return;

        // Don't insert adjacent to the drop indicator itself
        var insertIdx = below ? idx + 1 : idx;
        if (insertIdx > _blockPanel.Children.Count)
            insertIdx = _blockPanel.Children.Count;

        _blockPanel.Children.Insert(insertIdx, _dropIndicator);
        _dropIndicator.IsVisible = true;
    }

    private void HideDropIndicator()
    {
        if (_dropIndicator is { Parent: Panel parent })
            parent.Children.Remove(_dropIndicator);
        if (_dropIndicator != null)
            _dropIndicator.IsVisible = false;
    }

    private void ShowPairDropIndicator(Border target, bool leftSide)
    {
        if (_pairDropIndicator == null || _blockPanel == null) return;

        // Remove from current parent
        if (_pairDropIndicator.Parent is Panel oldP)
            oldP.Children.Remove(_pairDropIndicator);

        var bounds = target.Bounds;
        _pairDropIndicator.Height = bounds.Height;

        // Place in the drag overlay canvas at the correct position
        if (_dragOverlay != null)
        {
            if (!_dragOverlay.Children.Contains(_pairDropIndicator))
                _dragOverlay.Children.Add(_pairDropIndicator);

            var x = leftSide ? bounds.X : bounds.X + bounds.Width - 3;
            Canvas.SetLeft(_pairDropIndicator, x);
            Canvas.SetTop(_pairDropIndicator, bounds.Y);
            _pairDropIndicator.IsVisible = true;
        }
    }

    private void HidePairDropIndicator()
    {
        if (_pairDropIndicator != null)
            _pairDropIndicator.IsVisible = false;
    }

    /// <summary>
    /// Start a custom drag: snapshot the source block into a ghost, capture pointer.
    /// </summary>
    private void StartBlockDrag(Border wrapper, string blockId, PointerPressedEventArgs e)
    {
        if (_isDragging || _blockPanel == null) return;
        _isDragging = true;
        _dragSourceBlockId = blockId;
        _dragSourceWrapper = wrapper;
        _dragTargetWrapper = null;

        EnsureDragOverlay();

        // Create ghost: render the wrapper to a bitmap
        var bounds = wrapper.Bounds;
        var ghost = new Border
        {
            Width = bounds.Width,
            Height = bounds.Height,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = PrimarySubtle,
            Background = SurfaceElevated,
            Opacity = 0.85,
            IsHitTestVisible = false,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0, OffsetY = 4, Blur = 12,
                Color = Color.FromArgb(80, 0, 0, 0),
            }),
        };

        // Render a bitmap snapshot of the block
        try
        {
            var renderTarget = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new PixelSize((int)bounds.Width, (int)bounds.Height),
                new Vector(96, 96));
            renderTarget.Render(wrapper);
            ghost.Child = new Avalonia.Controls.Image
            {
                Source = renderTarget,
                Width = bounds.Width,
                Height = bounds.Height,
                Stretch = Stretch.None,
            };
        }
        catch
        {
            // Fallback if render fails: show a placeholder
            ghost.Child = new TextBlock
            {
                Text = "Moving block...",
                Foreground = TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        _dragGhost = ghost;

        // Position ghost at pointer
        var posInPanel = e.GetPosition(_blockPanel);
        _dragPointerOffset = new Point(
            posInPanel.X - bounds.X,
            posInPanel.Y - bounds.Y);

        Canvas.SetLeft(ghost, bounds.X);
        Canvas.SetTop(ghost, bounds.Y);

        // Add overlay to the drag overlay canvas
        if (_dragOverlay!.Parent is Panel ovParent)
            ovParent.Children.Remove(_dragOverlay);

        // The overlay needs to be in a Grid that also contains the block panel.
        // Find the parent of blockPanel and add overlay there.
        if (_blockPanel.Parent is Grid parentGrid)
        {
            if (!parentGrid.Children.Contains(_dragOverlay))
                parentGrid.Children.Add(_dragOverlay);
        }
        else if (_blockPanel.Parent is Panel parentPanel)
        {
            if (!parentPanel.Children.Contains(_dragOverlay))
                parentPanel.Children.Add(_dragOverlay);
        }

        _dragOverlay.Children.Clear();
        _dragOverlay.Children.Add(ghost);

        // Fade source
        wrapper.Opacity = 0.25;

        // Capture pointer on the drag handle to track movement
        e.Pointer.Capture(wrapper);
        e.Handled = true;
    }

    private void UpdateBlockDrag(PointerEventArgs e)
    {
        if (!_isDragging || _dragGhost == null || _blockPanel == null) return;

        var posInPanel = e.GetPosition(_blockPanel);

        // Move ghost
        Canvas.SetLeft(_dragGhost, posInPanel.X - _dragPointerOffset.X);
        Canvas.SetTop(_dragGhost, posInPanel.Y - _dragPointerOffset.Y);

        // Find which block wrapper is under the pointer (by Y position)
        Border? closestWrapper = null;
        var insertBelow = false;

        foreach (var child in _blockPanel.Children)
        {
            Border? candidate = null;
            if (child is Border bw && bw.Tag is string && bw != _dropIndicator && bw != _dragSourceWrapper)
                candidate = bw;
            else if (child is Grid pg)
            {
                // Use last non-source border inside the pair grid as target
                foreach (var gc in pg.Children)
                {
                    if (gc is Border gbw && gbw.Tag is string && gbw != _dragSourceWrapper)
                        candidate = gbw;
                }
            }
            if (candidate == null) continue;

            var childBounds = child.Bounds;
            var childMidY = childBounds.Y + childBounds.Height / 2;

            if (posInPanel.Y < childBounds.Y + childBounds.Height)
            {
                closestWrapper = candidate;
                insertBelow = posInPanel.Y > childMidY;
                break;
            }
            // Track last valid block as fallback (insert after last)
            closestWrapper = candidate;
            insertBelow = true;
        }

        if (closestWrapper != null)
        {
            _dragTargetWrapper = closestWrapper;
            _dragTargetBelow = insertBelow;

            // Detect side-by-side: pointer in left/right 20% of target
            var targetTag = closestWrapper.Tag as string;
            var targetBounds = closestWrapper.Bounds;
            var relX = posInPanel.X - targetBounds.X;
            var edgeZone = targetBounds.Width * 0.2;
            var excluded = targetTag != null && _blockMeta.TryGetValue(targetTag, out var tm)
                && tm.Type is "table" or "horizontal_rule" or "footnote";
            var srcExcluded = _dragSourceBlockId != null && _blockMeta.TryGetValue(_dragSourceBlockId, out var sm)
                && sm.Type is "table" or "horizontal_rule" or "footnote";

            if (!excluded && !srcExcluded && (relX < edgeZone || relX > targetBounds.Width - edgeZone))
            {
                _dragTargetPair = true;
                HideDropIndicator();
                ShowPairDropIndicator(closestWrapper, relX < edgeZone);
            }
            else
            {
                _dragTargetPair = false;
                HidePairDropIndicator();
                ShowDropIndicator(closestWrapper, insertBelow);
            }
        }

        // Auto-scroll when pointer is near top/bottom edge of the ScrollViewer
        UpdateDragAutoScroll(e);
    }

    private ScrollViewer? FindParentScrollViewer(Control? control)
    {
        while (control != null)
        {
            if (control is ScrollViewer sv) return sv;
            control = control.Parent as Control;
        }
        return null;
    }

    private void UpdateDragAutoScroll(PointerEventArgs e)
    {
        const double edgeZone = 40;
        const double scrollSpeed = 8;

        var sv = FindParentScrollViewer(_blockPanel);
        if (sv == null)
        {
            StopDragAutoScroll();
            return;
        }

        var posInScroll = e.GetPosition(sv);
        if (posInScroll.Y < edgeZone)
            _dragScrollSpeed = -scrollSpeed * (1.0 - posInScroll.Y / edgeZone);
        else if (posInScroll.Y > sv.Bounds.Height - edgeZone)
            _dragScrollSpeed = scrollSpeed * (1.0 - (sv.Bounds.Height - posInScroll.Y) / edgeZone);
        else
        {
            StopDragAutoScroll();
            return;
        }

        if (_dragScrollTimer == null)
        {
            _dragScrollTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _dragScrollTimer.Tick += (_, _) =>
            {
                var scrollVw = FindParentScrollViewer(_blockPanel);
                if (scrollVw == null || !_isDragging) { StopDragAutoScroll(); return; }
                var newOffset = scrollVw.Offset.Y + _dragScrollSpeed;
                newOffset = Math.Max(0, Math.Min(newOffset, scrollVw.Extent.Height - scrollVw.Viewport.Height));
                scrollVw.Offset = new Avalonia.Vector(scrollVw.Offset.X, newOffset);
            };
            _dragScrollTimer.Start();
        }
    }

    private void StopDragAutoScroll()
    {
        _dragScrollTimer?.Stop();
        _dragScrollTimer = null;
        _dragScrollSpeed = 0;
    }

    private void EndBlockDrag(PointerReleasedEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;

        e.Pointer.Capture(null);

        var sourceId = _dragSourceBlockId;
        var targetBlockId = _dragTargetWrapper?.Tag as string;
        var position = _dragTargetBelow ? "after" : "before";

        if (_dragSourceWrapper == null || _dragTargetWrapper == null || _blockPanel == null
            || sourceId == null || targetBlockId == null || sourceId == targetBlockId)
        {
            CleanUpDrag();
            return;
        }

        var panel = _blockPanel;
        var src = _dragSourceWrapper;
        var tgt = _dragTargetWrapper;

        // --- Helper: detach a wrapper from wherever it lives (panel or pair Grid).
        //     If inside a pair Grid, the orphaned sibling is promoted to standalone.
        //     Returns true if it was inside a pair Grid. ---
        bool DetachFromParent(Border wrapper)
        {
            if (panel.Children.Contains(wrapper))
            {
                panel.Children.Remove(wrapper);
                return false;
            }
            for (var i = 0; i < panel.Children.Count; i++)
            {
                if (panel.Children[i] is not Grid pg || !pg.Children.Contains(wrapper)) continue;

                pg.Children.Remove(wrapper);
                Grid.SetColumn(wrapper, 0);
                wrapper.VerticalAlignment = VerticalAlignment.Stretch;

                var orphans = pg.Children.OfType<Border>().Where(b => b.Tag is string).ToList();
                var gridIdx = panel.Children.IndexOf(pg);
                panel.Children.RemoveAt(gridIdx);
                foreach (var orphan in orphans)
                {
                    pg.Children.Remove(orphan);
                    Grid.SetColumn(orphan, 0);
                    orphan.VerticalAlignment = VerticalAlignment.Stretch;
                    panel.Children.Insert(gridIdx++, orphan);
                }
                return true;
            }
            return false;
        }

        // Check if source and target are in the same pair Grid (swap)
        Grid? sharedPairGrid = null;
        for (var i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is Grid pg && pg.Children.Contains(src) && pg.Children.Contains(tgt))
            { sharedPairGrid = pg; break; }
        }

        if (sharedPairGrid != null && !_dragTargetPair)
        {
            // --- Swap within same pair: just swap column assignments ---
            var srcCol = Grid.GetColumn(src);
            var tgtCol = Grid.GetColumn(tgt);
            Grid.SetColumn(src, tgtCol);
            Grid.SetColumn(tgt, srcCol);

            CleanUpDrag();

            // Plugin: unpair → re-pair with swapped order (pair_blocks handles adjacency)
            var srcSb = _shadowState.GetBlock(sourceId);
            var pId = srcSb?.PairId;
            if (pId != null) _shadowState.UnpairBlocks(pId);
            // Determine new left/right based on column assignments
            var leftId = srcCol > tgtCol ? targetBlockId : sourceId;
            var rightId = srcCol > tgtCol ? sourceId : targetBlockId;
            _shadowState.PairBlocks(leftId, rightId);
            _onBlockContentChanged?.Invoke();
        }
        else if (_dragTargetPair)
        {
            // --- Pair drop ---
            // Capture target's position BEFORE detaching anything
            var tgtIdx = panel.Children.IndexOf(tgt);
            if (tgtIdx < 0)
            {
                for (var i = 0; i < panel.Children.Count; i++)
                {
                    if (panel.Children[i] is Grid pg && pg.Children.Contains(tgt))
                    { tgtIdx = i; break; }
                }
            }
            if (tgtIdx < 0) tgtIdx = panel.Children.Count;

            // Detach both from their current parents (pair grids or panel)
            DetachFromParent(src);
            // Unpair source in shadow if needed
            var srcSb = _shadowState.GetBlock(sourceId);
            if (srcSb is { Layout: "side_by_side", PairId: not null })
                _shadowState.UnpairBlocks(srcSb.PairId);

            DetachFromParent(tgt);
            // Unpair target in shadow if needed
            var tgtSb = _shadowState.GetBlock(targetBlockId);
            if (tgtSb is { Layout: "side_by_side", PairId: not null })
                _shadowState.UnpairBlocks(tgtSb.PairId);

            var pairGrid = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(8, GridUnitType.Pixel), new ColumnDefinition(1, GridUnitType.Star) },
            };
            pairGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(tgt, 0);
            Grid.SetColumn(src, 2);
            pairGrid.Children.Add(tgt);
            pairGrid.Children.Add(src);
            // Clamp index — detaching may have reduced panel child count
            var insertIdx = Math.Min(tgtIdx, panel.Children.Count);
            panel.Children.Insert(insertIdx, pairGrid);

            CleanUpDrag();

            _shadowState.PairBlocks(targetBlockId, sourceId);
            _onBlockContentChanged?.Invoke();
        }
        else
        {
            // --- Normal reorder ---
            var wasInPair = DetachFromParent(src);
            // Unpair source in shadow if needed
            var srcSb = _shadowState.GetBlock(sourceId);
            if (srcSb is { Layout: "side_by_side", PairId: not null })
                _shadowState.UnpairBlocks(srcSb.PairId);

            // Find target index (may be top-level or inside a pair Grid)
            var tgtIdx = panel.Children.IndexOf(tgt);
            if (tgtIdx < 0)
            {
                for (var i = 0; i < panel.Children.Count; i++)
                {
                    if (panel.Children[i] is Grid pg && pg.Children.Contains(tgt))
                    { tgtIdx = i; break; }
                }
            }

            if (tgtIdx >= 0)
            {
                var insertIdx = _dragTargetBelow ? tgtIdx + 1 : tgtIdx;
                panel.Children.Insert(Math.Min(insertIdx, panel.Children.Count), src);
            }
            else
            {
                panel.Children.Add(src);
            }

            CleanUpDrag();

            _shadowState.ReorderBlock(sourceId, targetBlockId, position);
            _onBlockContentChanged?.Invoke();
        }
    }

    private void CleanUpDrag()
    {
        HideDropIndicator();
        HidePairDropIndicator();
        StopDragAutoScroll();

        if (_dragSourceWrapper != null)
            _dragSourceWrapper.Opacity = 1.0;

        _dragOverlay?.Children.Clear();
        if (_dragOverlay?.Parent is Panel p)
            p.Children.Remove(_dragOverlay);

        _dragGhost = null;
        _dragSourceWrapper = null;
        _dragSourceBlockId = null;
        _dragTargetWrapper = null;
        _dragTargetPair = false;
    }

    /// <summary>
    /// Walks the visual tree to find the first editable control inside the block
    /// wrapper that has the given blockId as its Tag.
    /// </summary>
    private static Control? FindEditorInBlock(Control root, string blockId)
    {
        // Find the wrapper Border with matching Tag
        if (root is Avalonia.Controls.Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is Border b && b.Tag is string tag && tag == blockId)
                    return FindFirstEditor(b);
                var found = FindEditorInBlock(child, blockId);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private static Control? FindFirstEditor(Control root)
    {
        if (root is Controls.RichTextEditor.RichTextEditor rte)
            return rte;
        if (root is Controls.CodeBlockEditor cbe)
            return cbe;
        if (root is Avalonia.Controls.Panel panel)
        {
            foreach (var child in panel.Children)
            {
                var found = FindFirstEditor(child);
                if (found is not null) return found;
            }
        }
        else if (root is Avalonia.Controls.Decorator dec && dec.Child is { } decChild)
        {
            return FindFirstEditor(decChild);
        }
        else if (root is ContentControl cc && cc.Content is Control ccChild)
        {
            return FindFirstEditor(ccChild);
        }
        return null;
    }

    /// <summary>
    /// Creates a formatting toolbar with B/I/U/S/Code toggle buttons.
    /// Wires to the RichTextEditor inside the block (if any).
    /// </summary>
    private Border CreateBlockFormattingToolbar(Control inner, string blockId)
    {
        // Find the RichTextEditor inside this block
        Controls.RichTextEditor.RichTextEditor? editor = null;
        if (inner is Controls.RichTextEditor.RichTextEditor rte)
            editor = rte;
        else
            editor = FindFirstEditor(inner) as Controls.RichTextEditor.RichTextEditor;

        var toolbarInner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
        };

        var toolbarPanel = new Border
        {
            Child = toolbarInner,
            Background = SurfaceElevated,
            BorderBrush = BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4, 2),
            Margin = new Thickness(24, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0, // hidden until focused; use Opacity instead of IsVisible to avoid layout shifts
            IsHitTestVisible = false,
            ZIndex = 10,
            // Float above the block content using a negative translate
            RenderTransform = new Avalonia.Media.TranslateTransform(0, -36),
        };

        if (editor is null)
            return toolbarPanel; // no editor to wire — return empty hidden panel

        var buttons = new (string Label, string Tooltip, InlineStyle Flag)[]
        {
            ("B", "Bold (Ctrl+B)", InlineStyle.Bold),
            ("I", "Italic (Ctrl+I)", InlineStyle.Italic),
            ("U", "Underline (Ctrl+U)", InlineStyle.Underline),
            ("S", "Strikethrough (Ctrl+Shift+S)", InlineStyle.Strikethrough),
            ("</>", "Code (Ctrl+`)", InlineStyle.Code),
            ("x\u00B2", "Superscript (Ctrl+Shift+.)", InlineStyle.Superscript),
        };

        var toggleButtons = new List<(Border Btn, InlineStyle Flag)>();

        var inactiveBg = Brushes.Transparent;
        var activeBg = PrimarySubtle;
        var hoverBg = HoverSubtle;

        foreach (var (label, tooltip, flag) in buttons)
        {
            var isCode = flag == InlineStyle.Code;
            var text = new TextBlock
            {
                Text = label,
                FontSize = FontSize("ThemeFontSizeSm", 12),
                FontWeight = flag == InlineStyle.Bold ? FontWeight.Bold : FontWeight.Normal,
                FontStyle = flag == InlineStyle.Italic ? Avalonia.Media.FontStyle.Italic : Avalonia.Media.FontStyle.Normal,
                Foreground = TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (flag == InlineStyle.Underline)
                text.TextDecorations = TextDecorations.Underline;
            if (flag == InlineStyle.Strikethrough)
                text.TextDecorations = TextDecorations.Strikethrough;
            if (isCode)
            {
                text.FontSize = FontSize("ThemeFontSizeXs", 10);
                text.FontFamily = Font("ThemeFontMono");
            }

            Border btn;
            if (isCode)
            {
                // Pill styling for code button
                btn = new Border
                {
                    Child = text,
                    Background = SurfaceElevated,
                    BorderBrush = BorderSubtle,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(10, 2),
                    MinWidth = 28,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
            }
            else
            {
                btn = new Border
                {
                    Child = text,
                    Background = inactiveBg,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2),
                    MinWidth = 28,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
            }
            ToolTip.SetTip(btn, tooltip);

            var capturedFlag = flag;
            var capturedBtn = btn;
            var capturedIsCode = isCode;
            var codeInactiveBg = SurfaceElevated;

            btn.PointerEntered += (_, _) =>
            {
                if (capturedBtn.Background != activeBg)
                    capturedBtn.Background = hoverBg;
            };
            btn.PointerExited += (_, _) =>
            {
                var isActive = editor.ActiveStyle.HasFlag(capturedFlag);
                capturedBtn.Background = isActive ? activeBg : (capturedIsCode ? codeInactiveBg : inactiveBg);
            };
            btn.PointerReleased += (_, e) =>
            {
                editor.ToggleStyle(capturedFlag);
                editor.Focus();
                e.Handled = true;
            };

            toggleButtons.Add((btn, flag));
            toolbarInner.Children.Add(btn);
        }

        // Pipe separator between style buttons and color buttons
        toolbarInner.Children.Add(new Border
        {
            Width = 1,
            Height = 16,
            Background = BorderSubtle,
            Margin = new Thickness(4, 0),
        });

        // Font color dropdown ("A" with colored underline)
        AddColorDropdown(toolbarInner, editor, "A", "Font Color", isForeground: true);

        // Background color dropdown (highlighter icon)
        AddColorDropdown(toolbarInner, editor, "\uD83D\uDD8C", "Highlight Color", isForeground: false);

        // Pipe separator before link button
        toolbarInner.Children.Add(new Border
        {
            Width = 1, Height = 16, Background = BorderSubtle, Margin = new Thickness(4, 0),
        });

        // Link / URL button
        {
            var linkBtn = new Border
            {
                Child = new TextBlock
                {
                    Text = "\uD83D\uDD17", // 🔗
                    FontSize = FontSize("ThemeFontSizeSm", 12),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                Background = inactiveBg,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2),
                MinWidth = 28,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(linkBtn, "Insert Link");

            linkBtn.PointerEntered += (_, _) => linkBtn.Background = hoverBg;
            linkBtn.PointerExited += (_, _) => linkBtn.Background = inactiveBg;
            linkBtn.PointerReleased += (_, e) =>
            {
                e.Handled = true;
                ShowLinkPopup(editor, linkBtn);
            };
            toolbarInner.Children.Add(linkBtn);
        }

        // Update toggle button states when active style changes
        editor.ActiveStyleChanged += style =>
        {
            foreach (var (btn, flag) in toggleButtons)
            {
                var isCode = flag == InlineStyle.Code;
                btn.Background = style.HasFlag(flag) ? activeBg : (isCode ? SurfaceElevated : inactiveBg);
            }
        };

        // Also set initial state when toolbar becomes visible
        toolbarPanel.PropertyChanged += (_, args) =>
        {
            if (args.Property == Visual.OpacityProperty && toolbarPanel.Opacity > 0)
            {
                var style = editor.ActiveStyle;
                foreach (var (btn, flag) in toggleButtons)
                {
                    var isCode = flag == InlineStyle.Code;
                    btn.Background = style.HasFlag(flag) ? activeBg : (isCode ? SurfaceElevated : inactiveBg);
                }
            }
        };

        // Block type conversion dropdown
        if (_blockMeta.TryGetValue(blockId, out var meta))
        {
            var isTextBlock = meta.Type is "paragraph" or "heading" or "blockquote" or "callout";
            var isListBlock = meta.Type is "bullet_list" or "numbered_list" or "task_list";

            if (isTextBlock || isListBlock)
            {
                // Pipe separator
                toolbarInner.Children.Add(new Border
                {
                    Width = 1, Height = 16,
                    Background = BorderSubtle,
                    Margin = new Thickness(4, 0),
                });

                var convertOptions = isTextBlock
                    ? new (string Type, string Label, int Level)[]
                    {
                        ("paragraph", "¶ Paragraph", 0),
                        ("heading", "H1", 1), ("heading", "H2", 2), ("heading", "H3", 3),
                        ("heading", "H4", 4), ("heading", "H5", 5), ("heading", "H6", 6),
                        ("blockquote", "❝ Quote", 0),
                        ("callout", "📢 Callout", 0),
                    }
                    : new (string Type, string Label, int Level)[]
                    {
                        ("bullet_list", "• Bullet", 0),
                        ("numbered_list", "1. Numbered", 0),
                        ("task_list", "☐ Task", 0),
                    };

                // Current type label
                var currentLabel = meta.Type switch
                {
                    "paragraph" => "¶",
                    "heading" => $"H{meta.Level}",
                    "blockquote" => "❝",
                    "callout" => "📢",
                    "bullet_list" => "•",
                    "numbered_list" => "1.",
                    "task_list" => "☐",
                    _ => "¶",
                };

                var convertGrid = new StackPanel { Spacing = 2 };
                var convertPopup = new Popup
                {
                    Placement = PlacementMode.Bottom,
                    IsLightDismissEnabled = true,
                    Child = new Border
                    {
                        Background = SurfaceElevated,
                        BorderBrush = BorderSubtle,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(4),
                        Child = convertGrid,
                    },
                };

                foreach (var (type, label, level) in convertOptions)
                {
                    var isCurrent = type == meta.Type && (type != "heading" || level == meta.Level);
                    var optBtn = new Border
                    {
                        Child = new TextBlock
                        {
                            Text = label,
                            FontSize = FontSize("ThemeFontSizeSm", 12),
                            Foreground = isCurrent ? Primary : TextPrimary,
                            FontWeight = isCurrent ? FontWeight.Bold : FontWeight.Normal,
                            Padding = new Thickness(8, 4),
                        },
                        Background = Brushes.Transparent,
                        CornerRadius = new CornerRadius(4),
                        Cursor = new Cursor(StandardCursorType.Hand),
                    };
                    var capturedType = type;
                    var capturedLevel = level;
                    optBtn.PointerEntered += (_, _) => optBtn.Background = HoverSubtle;
                    optBtn.PointerExited += (_, _) => optBtn.Background = Brushes.Transparent;
                    optBtn.PointerReleased += (_, e) =>
                    {
                        convertPopup.IsOpen = false;
                        var lvl = capturedLevel > 0 ? capturedLevel : 1;
                        _shadowState.ConvertBlock(blockId, capturedType, lvl);

                        // Optimistic UI: replace the block control in-place
                        if (_blockPanel != null)
                        {
                            var idx = -1;
                            for (var i = 0; i < _blockPanel.Children.Count; i++)
                            {
                                if (_blockPanel.Children[i] is Border b && b.Tag is string bid && bid == blockId)
                                { idx = i; break; }
                            }

                            if (idx >= 0)
                            {
                                // Re-serialize this single block from shadow state
                                var shadowBlock = _shadowState.GetBlock(blockId);
                                if (shadowBlock != null)
                                {
                                    var blockJson = _shadowState.SerializeSingleBlockJson(blockId);
                                    if (blockJson != null)
                                    {
                                        using var doc = JsonDocument.Parse(blockJson);
                                        var rendered = RenderBlock(doc.RootElement.Clone(), capturedType);
                                        _blockPanel.Children[idx] = rendered;

                                        // Focus the new block
                                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                        {
                                            var newEditor = FindRichTextEditor(rendered);
                                            if (newEditor != null)
                                            {
                                                newEditor.BringIntoView();
                                                newEditor.Focus();
                                            }
                                            else
                                            {
                                                var ctrl = FindFirstEditor(rendered);
                                                ctrl?.BringIntoView();
                                                ctrl?.Focus();
                                            }
                                        }, Avalonia.Threading.DispatcherPriority.Loaded);
                                    }
                                }
                            }
                        }

                        _onBlockContentChanged?.Invoke();
                        e.Handled = true;
                    };
                    convertGrid.Children.Add(optBtn);
                }

                var convertBtn = new Border
                {
                    Child = new TextBlock
                    {
                        Text = currentLabel,
                        FontSize = FontSize("ThemeFontSizeSm", 12),
                        Foreground = TextPrimary,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    Background = Brushes.Transparent,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2),
                    MinWidth = 28,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                ToolTip.SetTip(convertBtn, "Convert block type");
                convertBtn.PointerEntered += (_, _) => convertBtn.Background = HoverSubtle;
                convertBtn.PointerExited += (_, _) => convertBtn.Background = Brushes.Transparent;
                convertBtn.PointerReleased += (_, e) =>
                {
                    convertPopup.PlacementTarget = convertBtn;
                    convertPopup.IsOpen = !convertPopup.IsOpen;
                    e.Handled = true;
                };

                toolbarInner.Children.Add(convertBtn);
                toolbarInner.Children.Add(convertPopup);
            }

            // Table-specific toolbar buttons
            if (meta.Type == "table")
            {
                // Pipe separator
                toolbarInner.Children.Add(new Border
                {
                    Width = 1, Height = 16,
                    Background = BorderSubtle,
                    Margin = new Thickness(4, 0),
                });

                Border MakeTableToggle(string label, string tooltip, bool active, Action onClick)
                {
                    var btn = new Border
                    {
                        Child = new TextBlock
                        {
                            Text = label,
                            FontSize = FontSize("ThemeFontSizeSm", 12),
                            Foreground = active ? Primary : TextMuted,
                            FontWeight = active ? FontWeight.Bold : FontWeight.Normal,
                        },
                        Background = active ? HoverSubtle : Brushes.Transparent,
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2),
                        Cursor = new Cursor(StandardCursorType.Hand),
                    };
                    ToolTip.SetTip(btn, tooltip);
                    btn.PointerEntered += (_, _) => btn.Background = HoverBrush;
                    btn.PointerExited += (_, _) => btn.Background = active ? HoverSubtle : Brushes.Transparent;
                    btn.PointerReleased += (_, e) => { onClick(); e.Handled = true; };
                    return btn;
                }

                Border MakeTableAction(string label, string tooltip, Action onClick)
                {
                    var btn = new Border
                    {
                        Child = new TextBlock
                        {
                            Text = label,
                            FontSize = FontSize("ThemeFontSizeSm", 12),
                            Foreground = TextMuted,
                        },
                        Background = Brushes.Transparent,
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2),
                        Cursor = new Cursor(StandardCursorType.Hand),
                    };
                    ToolTip.SetTip(btn, tooltip);
                    btn.PointerEntered += (_, _) => btn.Background = HoverBrush;
                    btn.PointerExited += (_, _) => btn.Background = Brushes.Transparent;
                    btn.PointerReleased += (_, e) => { onClick(); e.Handled = true; };
                    return btn;
                }

                toolbarInner.Children.Add(MakeTableToggle("⊞ Header", "Toggle header row", meta.ShowHeader, () =>
                {
                    _shadowState.ToggleTableHeader(blockId);
                    ReplaceBlockInPlace(blockId);
                }));
                toolbarInner.Children.Add(MakeTableToggle("≡ Striped", "Toggle alternating row colors", meta.AlternatingRows, () =>
                {
                    _shadowState.ToggleTableAlternatingRows(blockId);
                    ReplaceBlockInPlace(blockId);
                }));

                // Separator
                toolbarInner.Children.Add(new Border
                {
                    Width = 1, Height = 16,
                    Background = BorderSubtle,
                    Margin = new Thickness(4, 0),
                });

                toolbarInner.Children.Add(MakeTableAction("+ Row", "Add row", () =>
                {
                    SendCommandSilent("add_table_row", JsonSerializer.Serialize(new { id = blockId }));
                    // Deferred re-render: plugin adds IDs we don't have yet
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => ViewStateRefreshRequested?.Invoke(),
                        Avalonia.Threading.DispatcherPriority.Background);
                }));
                toolbarInner.Children.Add(MakeTableAction("− Row", "Remove last row", () =>
                {
                    SendCommandSilent("remove_table_row", JsonSerializer.Serialize(new { id = blockId }));
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => ViewStateRefreshRequested?.Invoke(),
                        Avalonia.Threading.DispatcherPriority.Background);
                }));
                toolbarInner.Children.Add(MakeTableAction("+ Col", "Add column", () =>
                {
                    SendCommandSilent("add_table_column", JsonSerializer.Serialize(new { id = blockId }));
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => ViewStateRefreshRequested?.Invoke(),
                        Avalonia.Threading.DispatcherPriority.Background);
                }));
                toolbarInner.Children.Add(MakeTableAction("− Col", "Remove last column", () =>
                {
                    var colIdx = (_shadowState.GetBlock(blockId)?.Content is TableContent tc2) ? tc2.Columns.Count - 1 : 0;
                    SendCommandSilent("remove_table_column", JsonSerializer.Serialize(new { id = blockId, col_index = colIdx }));
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => ViewStateRefreshRequested?.Invoke(),
                        Avalonia.Threading.DispatcherPriority.Background);
                }));

                // Separator
                toolbarInner.Children.Add(new Border
                {
                    Width = 1, Height = 16,
                    Background = BorderSubtle,
                    Margin = new Thickness(4, 0),
                });

                // Filter textbox
                var tableFilterBox = new TextBox
                {
                    Watermark = "🔍 Filter...",
                    FontSize = FontSize("ThemeFontSizeSm", 12),
                    Padding = new Thickness(6, 2),
                    MinWidth = 120,
                    MaxWidth = 200,
                    BorderThickness = new Thickness(1),
                    BorderBrush = BorderSubtle,
                    CornerRadius = new CornerRadius(4),
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = _tableFilterState.TryGetValue(blockId, out var existingFilter) ? existingFilter.Filter : "",
                };
                tableFilterBox.PropertyChanged += (_, args) =>
                {
                    if (args.Property == TextBox.TextProperty)
                    {
                        var newFilter = tableFilterBox.Text ?? "";
                        if (_tableFilterState.TryGetValue(blockId, out var state))
                            state.Rebuild(newFilter);
                    }
                };
                toolbarInner.Children.Add(tableFilterBox);
            }

            // Side-by-side alignment buttons
            var shadowBlock = _shadowState.GetBlock(blockId);
            if (shadowBlock is { Layout: "side_by_side", PairId: not null })
            {
                toolbarInner.Children.Add(new Border
                {
                    Width = 1, Height = 16, Background = BorderSubtle, Margin = new Thickness(4, 0),
                });

                // Pipe separator
                toolbarInner.Children.Add(new Border
                {
                    Width = 1, Height = 16, Background = BorderSubtle, Margin = new Thickness(4, 0),
                });

                var currentValign = shadowBlock.PairValign;
                var capturedPairId = shadowBlock.PairId;
                foreach (var (val, tip, vAlign) in new[] { ("top", "Align Top", VerticalAlignment.Top), ("center", "Align Center", VerticalAlignment.Center), ("bottom", "Align Bottom", VerticalAlignment.Bottom) })
                {
                    var isActive = currentValign == val;
                    var capturedVal = val;
                    var lineColor = isActive ? Primary : TextMuted;
                    // 3 horizontal lines with varying lengths to indicate alignment
                    var linesPanel = new StackPanel
                    {
                        Spacing = 2,
                        VerticalAlignment = vAlign,
                        Width = 14,
                        Height = 14,
                    };
                    var widths = val switch
                    {
                        "top" => new[] { 14.0, 10.0, 6.0 },
                        "center" => new[] { 10.0, 14.0, 10.0 },
                        _ => new[] { 6.0, 10.0, 14.0 }, // bottom
                    };
                    foreach (var w in widths)
                    {
                        linesPanel.Children.Add(new Border
                        {
                            Width = w, Height = 2,
                            Background = lineColor,
                            CornerRadius = new CornerRadius(1),
                            HorizontalAlignment = HorizontalAlignment.Left,
                        });
                    }
                    var alignBtn = new Border
                    {
                        Child = linesPanel,
                        Background = isActive ? HoverSubtle : Brushes.Transparent,
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2),
                        MinWidth = 28,
                        Cursor = new Cursor(StandardCursorType.Hand),
                    };
                    ToolTip.SetTip(alignBtn, tip);
                    alignBtn.PointerEntered += (_, _) => alignBtn.Background = HoverBrush;
                    alignBtn.PointerExited += (_, _) => alignBtn.Background = isActive ? HoverSubtle : Brushes.Transparent;
                    alignBtn.PointerReleased += (_, e) =>
                    {
                        _shadowState.UpdatePairValign(capturedPairId, capturedVal);
                        // Optimistic: update vertical alignment on the pair Grid
                        if (_blockPanel != null)
                        {
                            var newValign = capturedVal switch
                            {
                                "center" => VerticalAlignment.Center,
                                "bottom" => VerticalAlignment.Bottom,
                                _ => VerticalAlignment.Top,
                            };
                            foreach (var child in _blockPanel.Children)
                            {
                                if (child is Grid pairGrid)
                                {
                                    foreach (var gc in pairGrid.Children)
                                    {
                                        if (gc is Border bw && bw.Tag is string bid)
                                        {
                                            var sb2 = _shadowState.GetBlock(bid);
                                            if (sb2?.PairId == capturedPairId)
                                            {
                                                gc.VerticalAlignment = newValign;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        // Re-render toolbar to update active state
                        ReplaceBlockInPlace(blockId);
                        e.Handled = true;
                    };
                    toolbarInner.Children.Add(alignBtn);
                }
            }
        }

        return toolbarPanel;
    }

    private static readonly (TextColor Color, string Hex)[] ColorPalette =
    [
        (TextColor.Default, "#FFFFFF"),
        (TextColor.Gray, "#9B9A97"),
        (TextColor.Brown, "#64473A"),
        (TextColor.Orange, "#D9730D"),
        (TextColor.Yellow, "#DFAB01"),
        (TextColor.Green, "#0F7B6C"),
        (TextColor.Blue, "#0B6E99"),
        (TextColor.Purple, "#6940A5"),
        (TextColor.Pink, "#AD1A72"),
        (TextColor.Red, "#E03E3E"),
    ];

    private static readonly (TextColor Color, string Hex)[] BgColorPalette =
    [
        (TextColor.Default, "#00000000"),
        (TextColor.Gray, "#3C9B9A97"),
        (TextColor.Brown, "#3C64473A"),
        (TextColor.Orange, "#3CD9730D"),
        (TextColor.Yellow, "#3CDFAB01"),
        (TextColor.Green, "#3C0F7B6C"),
        (TextColor.Blue, "#3C0B6E99"),
        (TextColor.Purple, "#3C6940A5"),
        (TextColor.Pink, "#3CAD1A72"),
        (TextColor.Red, "#3CE03E3E"),
    ];

    private void ShowLinkPopup(Controls.RichTextEditor.RichTextEditor editor, Border anchor)
    {
        var hasSelection = editor.HasSelection;

        var popupPanel = new StackPanel
        {
            Spacing = Dbl("ThemeSpacingSm", 8),
            Margin = new Thickness(Dbl("ThemeSpacingSm", 8)),
        };

        TextBox? titleBox = null;
        if (!hasSelection)
        {
            titleBox = CreatePopupTextBox("Link title (optional)");
            popupPanel.Children.Add(WrapPopupTextBox(titleBox));
        }

        var urlBox = CreatePopupTextBox("https://example.com");
        popupPanel.Children.Add(WrapPopupTextBox(urlBox));

        var addBtn = new Button
        {
            Content = new TextBlock
            {
                Text = "Add",
                FontSize = FontSize("ThemeFontSizeSm", 12),
            },
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = Primary,
            Foreground = Brushes.White,
            Padding = new Thickness(Dbl("ThemeSpacingMd", 12), Dbl("ThemeSpacingXs", 4)),
            CornerRadius = new CornerRadius(4),
        };
        popupPanel.Children.Add(addBtn);

        var popupBorder = new Border
        {
            Child = popupPanel,
            Background = SurfaceElevated,
            BorderBrush = BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(Dbl("ThemeSpacingSm", 8)),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 8, OffsetY = 4, Color = Color.Parse("#40000000") }),
            MinWidth = 280,
        };

        var popup = new Popup
        {
            Child = popupBorder,
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -100,
            IsLightDismissEnabled = true,
        };

        // Popup must be in the visual tree to position correctly
        if (anchor.Parent is Panel parentPanel)
            parentPanel.Children.Add(popup);

        addBtn.Click += (_, _) =>
        {
            var url = urlBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(url))
            {
                popup.Close();
                return;
            }

            if (hasSelection)
            {
                // Convert selected text to a link
                var (selStart, selEnd) = editor.SelectionRange;
                editor.Document.PushUndo();
                editor.Document.SetLinkUrl(selStart, selEnd - selStart, url);
                editor.OnContentChanged();
            }
            else
            {
                var title = titleBox?.Text?.Trim() ?? "";
                var linkText = string.IsNullOrEmpty(title) ? url : title;
                editor.InsertLink(linkText, url);
            }

            popup.Close();
            editor.Focus();
        };

        popup.Closed += (_, _) =>
        {
            // Clean up popup from visual tree
            if (anchor.Parent is Panel pp)
                pp.Children.Remove(popup);
        };

        popup.IsOpen = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => urlBox.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Creates a TextBox styled for use inside popups — borderless with transparent background.
    /// Pair with <see cref="WrapPopupTextBox"/> which provides the visible border.
    /// </summary>
    private TextBox CreatePopupTextBox(string watermark)
    {
        return new TextBox
        {
            Watermark = watermark,
            FontSize = FontSize("ThemeFontSizeSm", 12),
            MinWidth = 200,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = TextPrimary,
            Padding = new Thickness(6, 4),
            Margin = new Thickness(0),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
    }

    /// <summary>
    /// Wraps a borderless TextBox in a themed Border that provides visible outline and padding.
    /// </summary>
    private Border WrapPopupTextBox(TextBox box)
    {
        return new Border
        {
            Child = box,
            Background = Surface,
            BorderBrush = BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6),
        };
    }

    private void AddColorDropdown(StackPanel toolbar,
        Controls.RichTextEditor.RichTextEditor editor,
        string label, string tooltip, bool isForeground)
    {
        var palette = isForeground ? ColorPalette : BgColorPalette;

        // Build popup content: 5x2 grid of color swatches
        var grid = new Avalonia.Controls.Primitives.UniformGrid
        {
            Columns = 5,
            Rows = 2,
        };

        var popup = new Avalonia.Controls.Primitives.Popup
        {
            Placement = PlacementMode.Bottom,
            IsLightDismissEnabled = true,
            Child = new Border
            {
                Background = SurfaceElevated,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6),
                Child = grid,
            },
        };

        foreach (var (color, hex) in palette)
        {
            var swatchColor = Color.Parse(hex);
            var swatch = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(swatchColor),
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            var capturedColor = color;
            swatch.PointerReleased += (_, e) =>
            {
                if (isForeground)
                    editor.SetFgColor(capturedColor);
                else
                    editor.SetBgColor(capturedColor);
                popup.IsOpen = false;
                editor.Focus();
                e.Handled = true;
            };
            swatch.PointerEntered += (_, _) => swatch.Opacity = 0.7;
            swatch.PointerExited += (_, _) => swatch.Opacity = 1.0;
            grid.Children.Add(swatch);
        }

        // The button itself
        var underlineColor = isForeground ? Color.Parse("#0B6E99") : Color.Parse("#DFAB01");
        var btnContent = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        btnContent.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = FontSize("ThemeFontSizeSm", 12),
            Foreground = TextPrimary,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        btnContent.Children.Add(new Border
        {
            Height = 2,
            Width = 14,
            Background = new SolidColorBrush(underlineColor),
            Margin = new Thickness(0, -1, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var btn = new Border
        {
            Child = btnContent,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2),
            MinWidth = 28,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(btn, tooltip);

        btn.PointerEntered += (_, _) => btn.Background = HoverSubtle;
        btn.PointerExited += (_, _) => btn.Background = Brushes.Transparent;
        btn.PointerReleased += (_, e) =>
        {
            popup.PlacementTarget = btn;
            popup.IsOpen = !popup.IsOpen;
            e.Handled = true;
        };

        toolbar.Children.Add(btn);
        toolbar.Children.Add(popup);
    }

    /// <summary>
    /// Wraps a block control with a drag handle on the left, action buttons
    /// (move up, move down, delete) on the right, and hover/focus highlighting.
    /// Drag uses a custom pointer-based system with ghost rendering and animation.
    /// </summary>
    private Control WrapBlockWithControls(Control inner, string blockId)
    {
        // -- Left: drag handle (⋮⋮ two vertical ellipsis for 2-column dot grid) --
        var dragHandle = new Border
        {
            Child = new TextBlock
            {
                Text = "\u22EE\u22EE",  // ⋮⋮
                FontSize = FontSize("ThemeFontSizeMd", 14),
                Foreground = TextMuted,
                LetterSpacing = -2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2),
            Opacity = 0,
            IsHitTestVisible = true,
        };
        dragHandle.PointerEntered += (_, _) => dragHandle.Background = HoverSubtle;
        dragHandle.PointerExited += (_, _) => dragHandle.Background = Brushes.Transparent;

        // -- Right: circular delete button (dark blue circle, white ✕) --
        var deleteBtnText = new TextBlock
        {
            Text = "\u2715",  // ✕
            FontSize = FontSize("ThemeFontSizeXsSm", 11),
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var deleteBtnDefaultBg = new SolidColorBrush(Color.FromArgb(160, 60, 90, 140));
        var deleteBtnHoverBg = new SolidColorBrush(Color.FromArgb(220, 80, 120, 180));
        var deleteBtn = new Border
        {
            Child = deleteBtnText,
            Background = deleteBtnDefaultBg,
            CornerRadius = new CornerRadius(10),
            Width = 20,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(deleteBtn, "Delete");
        deleteBtn.PointerEntered += (_, _) => deleteBtn.Background = deleteBtnHoverBg;
        deleteBtn.PointerExited += (_, _) => deleteBtn.Background = deleteBtnDefaultBg;

        var actionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Opacity = 0,
        };
        actionsPanel.Children.Add(deleteBtn);

        // -- Formatting toolbar (floats above the block) --
        var toolbar = CreateBlockFormattingToolbar(inner, blockId);

        // -- Layout: [drag gutter] [content] [actions gutter] --
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,*,8,24"),
        };
        Grid.SetColumn(dragHandle, 0);
        Grid.SetColumn(inner, 1);
        Grid.SetColumn(actionsPanel, 3);
        grid.Children.Add(dragHandle);
        grid.Children.Add(inner);
        grid.Children.Add(actionsPanel);

        // Use a Panel for overlapping layout — toolbar floats on top
        var overlayPanel = new Panel();
        overlayPanel.Children.Add(grid);
        overlayPanel.Children.Add(toolbar);

        var wrapper = new Border
        {
            Child = overlayPanel,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0, 4, 0, 4),
            Margin = new Thickness(0),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Tag = blockId,  // Store blockId for drag target lookup
        };

        // -- Wire action button events (deferred so wrapper is in scope) --
        deleteBtn.PointerReleased += (_, e) =>
        {
            _blockPanel?.Children.Remove(wrapper);
            _shadowState.DeleteBlock(blockId);
            _onBlockContentChanged?.Invoke();
            e.Handled = true;
        };

        // Show/hide controls and highlight on hover/focus
        wrapper.PointerEntered += (_, _) =>
        {
            if (wrapper.Classes.Contains("locked")) return;
            dragHandle.Opacity = 1;
            actionsPanel.Opacity = 1;
            if (!wrapper.IsKeyboardFocusWithin)
                wrapper.Background = HoverSubtle;
        };
        wrapper.PointerExited += (_, _) =>
        {
            if (wrapper.Classes.Contains("locked")) return;
            if (!wrapper.IsKeyboardFocusWithin)
            {
                dragHandle.Opacity = 0;
                actionsPanel.Opacity = 0;
                wrapper.Background = Brushes.Transparent;
                toolbar.Opacity = 0;
                toolbar.IsHitTestVisible = false;
            }
        };
        wrapper.GotFocus += (_, _) =>
        {
            _focusedBlockId = blockId;
            if (wrapper.Classes.Contains("locked")) return;
            wrapper.BorderBrush = PrimarySubtle;
            wrapper.Background = HoverSubtle;
            dragHandle.Opacity = 1;
            actionsPanel.Opacity = 1;
            toolbar.Opacity = 1;
            toolbar.IsHitTestVisible = true;
        };
        wrapper.LostFocus += async (_, _) =>
        {
            await System.Threading.Tasks.Task.Delay(50);
            if (wrapper.IsKeyboardFocusWithin)
                return;
            if (wrapper.Classes.Contains("locked")) return;
            wrapper.BorderBrush = Brushes.Transparent;
            wrapper.Background = wrapper.IsPointerOver ? HoverSubtle : Brushes.Transparent;
            dragHandle.Opacity = wrapper.IsPointerOver ? 1 : 0;
            actionsPanel.Opacity = wrapper.IsPointerOver ? 1 : 0;
            toolbar.Opacity = 0;
            toolbar.IsHitTestVisible = false;
        };

        // -- Custom pointer-based drag --
        dragHandle.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(dragHandle).Properties.IsLeftButtonPressed)
                StartBlockDrag(wrapper, blockId, e);
        };

        wrapper.PointerMoved += (_, e) => UpdateBlockDrag(e);
        wrapper.PointerReleased += (_, e) =>
        {
            if (_isDragging)
                EndBlockDrag(e);
        };

        // Context menu for unpairing side-by-side blocks
        if (!_currentPageIsLocked)
        {
            var shadowBlock = _shadowState.GetBlock(blockId);
            if (shadowBlock is { Layout: "side_by_side", PairId: not null })
            {
                var ctxMenu = new ContextMenu();
                var unpairItem = new MenuItem { Header = "Unpair blocks" };
                var capturedPairId = shadowBlock.PairId;
                unpairItem.Click += (_, _) =>
                {
                    _log.Debug("Unpair: starting for pairId={PairId} blockId={BlockId}", capturedPairId, blockId);
                    _shadowState.UnpairBlocks(capturedPairId);

                    // Optimistic: find the pair Grid containing this block and split it
                    if (_blockPanel != null)
                    {
                        for (var i = 0; i < _blockPanel.Children.Count; i++)
                        {
                            if (_blockPanel.Children[i] is not Grid pg) continue;

                            // Check if this grid contains the block we're unpairing
                            var containsBlock = false;
                            var blockIds = new List<string>();
                            foreach (var gc in pg.Children)
                            {
                                if (gc is Border bw && bw.Tag is string bid)
                                {
                                    blockIds.Add(bid);
                                    if (bid == blockId) containsBlock = true;
                                }
                            }
                            if (!containsBlock) continue;

                            _log.Debug("Unpair: found pair Grid at index {Idx} with blocks [{Ids}]",
                                i, string.Join(", ", blockIds));

                            _blockPanel.Children.RemoveAt(i);
                            var insertAt = i;
                            foreach (var bid in blockIds)
                            {
                                var sb = _shadowState.GetBlock(bid);
                                if (sb == null) continue;
                                var json = _shadowState.SerializeSingleBlockJson(bid);
                                if (json == null) continue;
                                using var d = JsonDocument.Parse(json);
                                var rendered = RenderBlock(d.RootElement.Clone(), sb.Type);
                                _blockPanel.Children.Insert(insertAt++, rendered);
                            }
                            break;
                        }
                    }
                    _onBlockContentChanged?.Invoke();
                };
                ctxMenu.Items.Add(unpairItem);
                wrapper.ContextMenu = ctxMenu;
            }
        }

        return wrapper;
    }

    private Control RenderBlockParagraph(JsonElement block)
    {
        var text = block.GetStringProp("text") ?? "";
        var blockId = block.GetStringProp("id") ?? "";
        var editor = new Controls.RichTextEditor.RichTextEditor
        {
            Markdown = text,
            BlockId = blockId,
            FontSize = FontSize("ThemeFontSizeMd", 14),
            Placeholder = "Type something...",
        };
        WireBlockEditorEvents(editor, blockId);
        return editor;
    }

    private Control RenderBlockHeading(JsonElement block)
    {
        var text = block.GetStringProp("text") ?? "";
        var blockId = block.GetStringProp("id") ?? "";
        var level = block.GetIntProp("level", 1);

        var baseFontSize = FontSize("ThemeFontSizeMd", 14);
        var scale = level switch
        {
            1 => 2.0,
            2 => 1.8,
            3 => 1.6,
            4 => 1.4,
            5 => 1.2,
            _ => 1.0,
        };
        var size = baseFontSize * scale;
        var weight = FontWeight.Bold;

        var editor = new Controls.RichTextEditor.RichTextEditor
        {
            Markdown = text,
            BlockId = blockId,
            FontSize = size,
            BaseFontWeight = weight,
            Margin = new Thickness(0, Dbl("ThemeSpacingSm", 8), 0, Dbl("ThemeSpacingXs", 4)),
            Placeholder = $"Heading {level}",
        };
        WireBlockEditorEvents(editor, blockId);
        return editor;
    }

    private void WireBlockEditorEvents(Controls.RichTextEditor.RichTextEditor editor, string blockId)
    {
        editor.TextChanged += (id, markdown) =>
        {
            _log.Debug("Shadow: TextChanged fired — blockId={BlockId} markdownLen={Len}", id, markdown.Length);
            _shadowState.UpdateBlockText(id, markdown);
            _onBlockContentChanged?.Invoke();
        };

        editor.SplitRequested += (id, afterText) =>
        {
            var newBlockId = Guid.NewGuid().ToString("N")[..12];

            // Update shadow state (handles both data model + enqueuing command)
            _shadowState.SplitBlock(id, afterText, newBlockId);

            // Optimistic UI: insert a new paragraph block after this one
            if (_blockPanel != null)
            {
                Border? currentWrapper = null;
                var idx = -1;
                for (var i = 0; i < _blockPanel.Children.Count; i++)
                {
                    if (_blockPanel.Children[i] is Border b && b.Tag is string bid && bid == id)
                    {
                        currentWrapper = b;
                        idx = i;
                        break;
                    }
                }

                if (currentWrapper != null && idx >= 0)
                {
                    var newEditor = new Controls.RichTextEditor.RichTextEditor
                    {
                        Markdown = afterText,
                        BlockId = newBlockId,
                        FontSize = FontSize("ThemeFontSizeMd", 14),
                        Placeholder = "Type something...",
                    };
                    WireBlockEditorEvents(newEditor, newBlockId);
                    _blockMeta[newBlockId] = new BlockMeta("paragraph", 1, "", "", "", false);
                    var wrapped = WrapBlockWithControls(newEditor, newBlockId);
                    _blockPanel.Children.Insert(idx + 1, wrapped);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        newEditor.BringIntoView();
                        newEditor.SetCaretToStart();
                    }, Avalonia.Threading.DispatcherPriority.Loaded);
                }
            }
            _onBlockContentChanged?.Invoke();
        };

        editor.MergeWithPreviousRequested += id =>
        {
            // Update shadow state
            _shadowState.MergeBlockWithPrevious(id);

            // Optimistic UI: merge this block's text into the previous and remove
            if (_blockPanel != null)
            {
                var idx = -1;
                for (var i = 0; i < _blockPanel.Children.Count; i++)
                {
                    if (_blockPanel.Children[i] is Border b && b.Tag is string bid && bid == id)
                    { idx = i; break; }
                }

                if (idx > 0)
                {
                    var prevWrapper = _blockPanel.Children[idx - 1];
                    var currWrapper = _blockPanel.Children[idx];
                    var prevEditor = FindRichTextEditor(prevWrapper);
                    var currEditor = FindRichTextEditor(currWrapper);
                    if (prevEditor != null && currEditor != null)
                    {
                        var prevLen = prevEditor.Markdown.Length;
                        prevEditor.Markdown += currEditor.Markdown;
                        _blockPanel.Children.RemoveAt(idx);
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            prevEditor.SetCaretToPosition(prevLen);
                        });
                    }
                }
            }
            _onBlockContentChanged?.Invoke();
        };

        editor.EmojiPickerRequested += _ =>
        {
            ShowEmojiPicker(editor, selectedEmoji => editor.InsertEmoji(selectedEmoji));
        };

        editor.LinkPickerRequested += _ =>
        {
            ShowLinkPicker(editor);
        };

        editor.InternalLinkActivated += (linkType, itemId) =>
        {
            InternalLinkActivated?.Invoke(linkType, itemId);
        };

        // Prefetch on internal link hover for faster navigation
        editor.InternalLinkHovered += (linkType, itemId) =>
        {
            PrefetchRequested?.Invoke(linkType, itemId);
        };

        editor.InternalLinkUnhovered += (linkType, itemId) =>
        {
            PrefetchCancelled?.Invoke(linkType, itemId);
        };

        editor.InternalLinkRemoved += linkUrl =>
        {
            // Flush text so shadow state is current before the re-render
            editor.FlushTextChange();
            NotifyLinkRemoved(linkUrl);
        };

        WireEditorFocusNavigation(editor);
    }

    /// <summary>
    /// Wires up/down arrow navigation for any RichTextEditor — navigates between
    /// editors within the same block wrapper, then to adjacent blocks.
    /// </summary>
    private void WireEditorFocusNavigation(Controls.RichTextEditor.RichTextEditor editor)
    {
        editor.FocusAdjacentRequested += (_, direction) =>
        {
            if (_blockPanel is null) return;

            // Walk up the visual tree from this editor to find the wrapper Border with a Tag
            Border? currentWrapper = null;
            Control? walk = editor;
            while (walk != null)
            {
                if (walk is Border bw && bw.Tag is string && _blockPanel.Children.Contains(bw))
                {
                    currentWrapper = bw;
                    break;
                }
                walk = walk.Parent as Control;
            }
            if (currentWrapper is null) return;

            // Collect all editors inside this block wrapper to allow intra-block navigation
            var editorsInBlock = new List<Controls.RichTextEditor.RichTextEditor>();
            CollectEditors(currentWrapper, editorsInBlock);

            if (editorsInBlock.Count > 1)
            {
                var myIdx = editorsInBlock.IndexOf(editor);
                if (myIdx >= 0)
                {
                    var nextIdx = myIdx + direction;
                    if (nextIdx >= 0 && nextIdx < editorsInBlock.Count)
                    {
                        if (direction < 0) editorsInBlock[nextIdx].SetCaretToEnd();
                        else editorsInBlock[nextIdx].SetCaretToStart();
                        return;
                    }
                }
            }

            // Move to adjacent block, skipping non-editable blocks (e.g. dividers)
            var idx = _blockPanel.Children.IndexOf(currentWrapper);
            for (var targetIdx = idx + direction;
                 targetIdx >= 0 && targetIdx < _blockPanel.Children.Count;
                 targetIdx += direction)
            {
                if (_blockPanel.Children[targetIdx] is not Border targetWrapper) continue;
                var allEditors = new List<Controls.RichTextEditor.RichTextEditor>();
                CollectEditors(targetWrapper, allEditors);
                if (allEditors.Count > 0)
                {
                    var target = direction < 0 ? allEditors[^1] : allEditors[0];
                    if (direction < 0) target.SetCaretToEnd();
                    else target.SetCaretToStart();
                    return;
                }
            }
        };

        editor.LinkEditRequested += (ed, start, length, linkText, linkUrl) =>
        {
            ShowLinkEditPopup(ed, start, length, linkText, linkUrl);
        };
    }

    private void ShowLinkEditPopup(Controls.RichTextEditor.RichTextEditor editor, int start, int length, string linkText, string linkUrl)
    {
        var popupPanel = new StackPanel
        {
            Spacing = Dbl("ThemeSpacingSm", 8),
            Margin = new Thickness(Dbl("ThemeSpacingSm", 8)),
        };

        var titleBox = CreatePopupTextBox("Link text");
        titleBox.Text = linkText;
        popupPanel.Children.Add(WrapPopupTextBox(titleBox));

        var urlBox = CreatePopupTextBox("https://example.com");
        urlBox.Text = linkUrl;
        popupPanel.Children.Add(WrapPopupTextBox(urlBox));

        var btnRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = Dbl("ThemeSpacingSm", 8), HorizontalAlignment = HorizontalAlignment.Right };

        var removeBtn = new Button
        {
            Content = new TextBlock { Text = "Remove", FontSize = FontSize("ThemeFontSizeSm", 12) },
            Foreground = TextMuted,
            Padding = new Thickness(Dbl("ThemeSpacingMd", 12), Dbl("ThemeSpacingXs", 4)),
            CornerRadius = new CornerRadius(4),
        };

        var updateBtn = new Button
        {
            Content = new TextBlock { Text = "Update", FontSize = FontSize("ThemeFontSizeSm", 12) },
            Background = Primary,
            Foreground = Brushes.White,
            Padding = new Thickness(Dbl("ThemeSpacingMd", 12), Dbl("ThemeSpacingXs", 4)),
            CornerRadius = new CornerRadius(4),
        };

        btnRow.Children.Add(removeBtn);
        btnRow.Children.Add(updateBtn);
        popupPanel.Children.Add(btnRow);

        var popup = new Popup
        {
            Child = new Border
            {
                Child = popupPanel,
                Background = SurfaceElevated,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(Dbl("ThemeSpacingSm", 8)),
                BoxShadow = new BoxShadows(new BoxShadow { Blur = 8, OffsetY = 4, Color = Color.Parse("#40000000") }),
                MinWidth = 280,
            },
            PlacementTarget = editor,
            Placement = PlacementMode.Pointer,
            IsLightDismissEnabled = true,
        };

        if (editor.Parent is Panel parentPanel)
            parentPanel.Children.Add(popup);

        updateBtn.Click += (_, _) =>
        {
            var newUrl = urlBox.Text?.Trim() ?? "";
            var newText = titleBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(newUrl))
            {
                // Treat empty URL as remove
                _doc_RemoveLink(editor, start, length);
                NotifyLinkRemoved(linkUrl);
            }
            else
            {
                editor.Document.PushUndo();
                // Replace text if changed
                if (newText != linkText && !string.IsNullOrEmpty(newText))
                {
                    editor.Document.Delete(start, length);
                    editor.Document.Insert(start, newText, InlineStyle.Link, linkUrl: newUrl);
                }
                else
                {
                    editor.Document.SetLinkUrl(start, length, newUrl);
                }
                editor.OnContentChanged();
            }
            popup.Close();
            editor.Focus();
        };

        removeBtn.Click += (_, _) =>
        {
            _doc_RemoveLink(editor, start, length);
            NotifyLinkRemoved(linkUrl);
            popup.Close();
            editor.Focus();
        };

        popup.Closed += (_, _) =>
        {
            if (editor.Parent is Panel pp)
                pp.Children.Remove(popup);
        };

        popup.IsOpen = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => urlBox.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private static void _doc_RemoveLink(Controls.RichTextEditor.RichTextEditor editor, int start, int length)
    {
        editor.Document.PushUndo();
        editor.Document.SetLinkUrl(start, length, null);
        editor.OnContentChanged();
    }

    /// <summary>
    /// If the removed URL is a privstack:// internal link, notify the plugin
    /// so it can clean up the entity store link relationship.
    /// </summary>
    private void NotifyLinkRemoved(string? linkUrl)
    {
        if (string.IsNullOrEmpty(linkUrl) || !linkUrl.StartsWith("privstack://", StringComparison.OrdinalIgnoreCase))
            return;

        var path = linkUrl["privstack://".Length..];
        var slash = path.IndexOf('/');
        if (slash <= 0) return;

        var targetId = path[(slash + 1)..];
        if (!string.IsNullOrEmpty(targetId))
        {
            var args = JsonSerializer.Serialize(new { target_id = targetId });
            SendCommand("remove_link", args);
        }
    }

    private Control RenderBlockCodeBlock(JsonElement block)
    {
        var code = block.GetStringProp("code")
                   ?? block.GetStringProp("text")
                   ?? "";
        var blockId = block.GetStringProp("id") ?? "";
        var language = block.GetStringProp("language") ?? "Plain Text";

        var editor = new Controls.CodeBlockEditor
        {
            BlockId = blockId,
        };
        editor.Code = code;
        editor.Language = language;

        editor.CodeChanged += (id, newCode) =>
        {
            _shadowState.UpdateBlockCode(id, newCode);
            _onBlockContentChanged?.Invoke();
        };
        editor.LanguageChanged += (id, lang) =>
        {
            _shadowState.UpdateBlockLanguage(id, lang);
        };

        return editor;
    }

    private Control RenderBlockQuote(JsonElement block)
    {
        var text = block.GetStringProp("text") ?? "";
        var blockId = block.GetStringProp("id") ?? "";
        var editor = new Controls.RichTextEditor.RichTextEditor
        {
            Markdown = text,
            BlockId = blockId,
            FontSize = FontSize("ThemeFontSizeMd", 14),
            BaseFontStyle = Avalonia.Media.FontStyle.Italic,
            Placeholder = "Quote...",
        };
        WireBlockEditorEvents(editor, blockId);

        return new Border
        {
            BorderBrush = Primary,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(Dbl("ThemeSpacingMd", 12), Dbl("ThemeSpacingSm", 8)),
            Margin = new Thickness(0, Dbl("ThemeSpacingXs", 4)),
            Child = editor,
        };
    }

    private Control RenderBlockCallout(JsonElement block)
    {
        var text = block.GetStringProp("text") ?? "";
        var blockId = block.GetStringProp("id") ?? "";
        var icon = block.GetStringProp("icon") ?? "\uD83D\uDCE2";
        var variant = block.GetStringProp("variant") ?? "info";

        var borderBrush = variant switch
        {
            "warning" => WarningBrush,
            "danger" or "error" => DangerBrush,
            "success" => SuccessBrush,
            _ => Primary,
        };

        var editor = new Controls.RichTextEditor.RichTextEditor
        {
            Markdown = text,
            BlockId = blockId,
            FontSize = FontSize("ThemeFontSizeMd", 14),
            BaseFontWeight = Avalonia.Media.FontWeight.Bold,
            Placeholder = "Callout text...",
        };
        WireBlockEditorEvents(editor, blockId);

        var iconBlock = new TextBlock
        {
            Text = icon,
            FontSize = FontSize("ThemeFontSizeLg", 18),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 0, 4, 0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        iconBlock.PointerReleased += (_, e) =>
        {
            ShowEmojiPicker(iconBlock, selectedEmoji =>
            {
                iconBlock.Text = selectedEmoji;
                SendCommandSilent("update_block",
                    JsonSerializer.Serialize(new { id = blockId, icon = selectedEmoji }));
            });
            e.Handled = true;
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Dbl("ThemeSpacingSm", 8),
        };
        row.Children.Add(iconBlock);
        row.Children.Add(editor);

        return new Border
        {
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Radius("ThemeRadiusSm"),
            Padding = new Thickness(Dbl("ThemeSpacingMd", 12)),
            Margin = new Thickness(0, Dbl("ThemeSpacingXs", 4)),
            Child = row,
        };
    }

    private Control RenderBlockList(JsonElement block, bool ordered)
    {
        var blockId = block.GetStringProp("id") ?? "";
        var panel = new StackPanel
        {
            Spacing = Dbl("ThemeSpacingXs", 4),
            Margin = new Thickness(Dbl("ThemeSpacingLg", 16), 0, 0, 0),
        };

        if (block.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            RenderListItems(panel, blockId, items, ordered, 0);

        return panel;
    }

    private void RenderListItems(StackPanel panel, string blockId, JsonElement items, bool ordered, int depth)
    {
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            index++;
            var text = item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? ""
                : item.GetStringProp("text") ?? "";
            var itemId = item.GetStringProp("id") ?? "";
            var row = CreateListItemRow(panel, blockId, itemId, text, ordered, index, depth);
            row.Margin = new Thickness(depth * Dbl("ThemeSpacingLg", 16), 0, 0, 0);
            panel.Children.Add(row);

            // Render nested children recursively
            if (item.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array && children.GetArrayLength() > 0)
                RenderListItems(panel, blockId, children, ordered, depth + 1);
        }
    }

    /// <summary>
    /// Returns the appropriate bullet character for an unordered list at the given depth.
    /// Depth 0 = disc (•), depth 1 = circle (◦), depth 2 = square (▪), then repeats.
    /// </summary>
    private static string UnorderedBullet(int depth) => (depth % 3) switch
    {
        0 => "\u2022",  // • disc
        1 => "\u25E6",  // ◦ circle
        _ => "\u25AA",  // ▪ square
    };

    /// <summary>
    /// Formats an ordered list prefix at the given depth.
    /// Depth 0 = "1." numbers, depth 1 = "a." letters, depth 2 = "i." roman, then repeats.
    /// </summary>
    private static string OrderedPrefix(int depth, int index)
    {
        return (depth % 3) switch
        {
            0 => $"{index}.",
            1 => $"{ToLetter(index)}.",
            _ => $"{ToRoman(index)}.",
        };

        static string ToLetter(int n)
        {
            // 1→a, 2→b, ... 26→z, 27→aa, etc.
            var sb = new System.Text.StringBuilder();
            while (n > 0) { n--; sb.Insert(0, (char)('a' + n % 26)); n /= 26; }
            return sb.ToString();
        }

        static string ToRoman(int n)
        {
            if (n <= 0 || n > 3999) return n.ToString();
            ReadOnlySpan<(int val, string sym)> table =
            [
                (1000, "m"), (900, "cm"), (500, "d"), (400, "cd"),
                (100, "c"), (90, "xc"), (50, "l"), (40, "xl"),
                (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i")
            ];
            var sb = new System.Text.StringBuilder();
            foreach (var (val, sym) in table)
                while (n >= val) { sb.Append(sym); n -= val; }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Creates a fully-wired list item row (bullet or numbered) that can be used
    /// both during initial render and for optimistic inserts.
    /// </summary>
    private StackPanel CreateListItemRow(StackPanel panel, string blockId, string itemId, string text, bool ordered, int index, int depth = 0)
    {
        var prefix = ordered ? $"{OrderedPrefix(depth, index)} " : $"{UnorderedBullet(depth)} ";
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Dbl("ThemeSpacingXs", 4),
        };
        row.Children.Add(new TextBlock
        {
            Text = prefix,
            Foreground = TextMuted,
            FontSize = FontSize("ThemeFontSizeMd", 14),
            MinWidth = ordered ? 24 : 12,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var editor = new Controls.RichTextEditor.RichTextEditor
        {
            Markdown = text,
            BlockId = itemId,
            FontSize = FontSize("ThemeFontSizeMd", 14),
            VerticalAlignment = VerticalAlignment.Center,
            Placeholder = "List item...",
        };
        var capturedItemId = itemId;
        editor.TextChanged += (_, markdown) =>
        {
            _shadowState.UpdateListItemText(blockId, capturedItemId, markdown);
            _onBlockContentChanged?.Invoke();
        };
        editor.SplitRequested += (_, afterText) =>
        {
            var newItemId = Guid.NewGuid().ToString("N")[..12];
            _shadowState.AddListItem(blockId, capturedItemId, newItemId, afterText);
            var rowIdx = panel.Children.IndexOf(row);
            var currentDepth = (int)(row.Margin.Left / Dbl("ThemeSpacingLg", 16));
            var newRow = CreateListItemRow(panel, blockId, newItemId, afterText, ordered, rowIdx + 2, currentDepth);
            newRow.Margin = row.Margin; // Inherit indent level
            panel.Children.Insert(rowIdx + 1, newRow);
            if (ordered) RenumberListItems(panel);
            if (newRow.Children.Count > 1 && newRow.Children[1] is Controls.RichTextEditor.RichTextEditor newEd)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => newEd.SetCaretToStart(), Avalonia.Threading.DispatcherPriority.Loaded);
            _onBlockContentChanged?.Invoke();
        };
        editor.IndentRequested += _ =>
        {
            _shadowState.IndentListItem(blockId, capturedItemId);
            var step = Dbl("ThemeSpacingLg", 16);
            row.Margin = new Thickness(row.Margin.Left + step, 0, 0, 0);
            if (ordered)
                RenumberListItems(panel);
            else if (row.Children[0] is TextBlock tb)
                tb.Text = $"{UnorderedBullet((int)(row.Margin.Left / step))} ";
        };
        editor.OutdentRequested += _ =>
        {
            _shadowState.OutdentListItem(blockId, capturedItemId);
            var step = Dbl("ThemeSpacingLg", 16);
            row.Margin = new Thickness(Math.Max(0, row.Margin.Left - step), 0, 0, 0);
            if (ordered)
                RenumberListItems(panel);
            else if (row.Children[0] is TextBlock tb)
                tb.Text = $"{UnorderedBullet((int)(row.Margin.Left / step))} ";
        };
        WireEditorFocusNavigation(editor);
        row.Children.Add(editor);
        return row;
    }

    private Control RenderBlockTaskList(JsonElement block)
    {
        var blockId = block.GetStringProp("id") ?? "";
        var panel = new StackPanel
        {
            Spacing = Dbl("ThemeSpacingXs", 4),
            Margin = new Thickness(Dbl("ThemeSpacingLg", 16), 0, 0, 0),
        };

        if (block.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            RenderTaskListItems(panel, blockId, items, 0);

        return panel;
    }

    private void RenderTaskListItems(StackPanel panel, string blockId, JsonElement items, int depth)
    {
        foreach (var item in items.EnumerateArray())
        {
            var text = item.GetStringProp("text") ?? "";
            var isChecked = item.GetBoolProp("checked", false)
                            || item.GetBoolProp("is_checked", false);
            var itemId = item.GetStringProp("id") ?? "";
            var row = CreateTaskItemRow(panel, blockId, itemId, text, isChecked);
            row.Margin = new Thickness(depth * Dbl("ThemeSpacingLg", 16), 0, 0, 0);
            panel.Children.Add(row);

            if (item.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array && children.GetArrayLength() > 0)
                RenderTaskListItems(panel, blockId, children, depth + 1);
        }
    }

    /// <summary>
    /// Creates a fully-wired task list item row (checkbox + editor) that can be used
    /// both during initial render and for optimistic inserts.
    /// </summary>
    private StackPanel CreateTaskItemRow(StackPanel panel, string blockId, string itemId, string text, bool isChecked)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Dbl("ThemeSpacingXs", 4),
        };

        var capturedItemId = itemId;
        var cb = new CheckBox
        {
            IsChecked = isChecked,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 0,
            Padding = new Thickness(0),
        };
        cb.IsCheckedChanged += (_, _) =>
        {
            _shadowState.ToggleTaskItem(blockId, capturedItemId);
        };

        var editor = new Controls.RichTextEditor.RichTextEditor
        {
            Markdown = text,
            BlockId = capturedItemId,
            FontSize = FontSize("ThemeFontSizeMd", 14),
            VerticalAlignment = VerticalAlignment.Center,
            Placeholder = "Task...",
        };
        editor.TextChanged += (_, markdown) =>
        {
            _shadowState.UpdateListItemText(blockId, capturedItemId, markdown);
            _onBlockContentChanged?.Invoke();
        };
        editor.SplitRequested += (_, afterText) =>
        {
            var newItemId = Guid.NewGuid().ToString("N")[..12];
            _shadowState.AddListItem(blockId, capturedItemId, newItemId, afterText);
            var rowIdx = panel.Children.IndexOf(row);
            var newRow = CreateTaskItemRow(panel, blockId, newItemId, afterText, false);
            panel.Children.Insert(rowIdx + 1, newRow);
            if (newRow.Children.Count > 1 && newRow.Children[1] is Controls.RichTextEditor.RichTextEditor newEd)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => newEd.SetCaretToStart(), Avalonia.Threading.DispatcherPriority.Loaded);
            _onBlockContentChanged?.Invoke();
        };
        editor.IndentRequested += _ =>
        {
            _shadowState.IndentListItem(blockId, capturedItemId);
            var step = Dbl("ThemeSpacingLg", 16);
            row.Margin = new Thickness(row.Margin.Left + step, 0, 0, 0);
        };
        editor.OutdentRequested += _ =>
        {
            _shadowState.OutdentListItem(blockId, capturedItemId);
            var step = Dbl("ThemeSpacingLg", 16);
            row.Margin = new Thickness(Math.Max(0, row.Margin.Left - step), 0, 0, 0);
        };
        WireEditorFocusNavigation(editor);

        row.Children.Add(cb);
        row.Children.Add(editor);
        return row;
    }

    /// <summary>
    /// Renumbers the prefix TextBlocks in an ordered list panel (1. 2. 3. ...).
    /// </summary>
    private void RenumberListItems(StackPanel panel)
    {
        // Track a counter per depth level; reset when depth decreases
        var counterByDepth = new Dictionary<int, int>();
        var step = Dbl("ThemeSpacingLg", 16);

        foreach (var child in panel.Children)
        {
            if (child is StackPanel { Orientation: Orientation.Horizontal } rowPanel
                && rowPanel.Children.Count > 0
                && rowPanel.Children[0] is TextBlock tb)
            {
                var depth = step > 0 ? (int)(rowPanel.Margin.Left / step) : 0;

                // Reset counters for depths deeper than current (they ended)
                foreach (var key in counterByDepth.Keys.Where(k => k > depth).ToList())
                    counterByDepth.Remove(key);

                counterByDepth.TryGetValue(depth, out var count);
                count++;
                counterByDepth[depth] = count;

                tb.Text = $"{OrderedPrefix(depth, count)} ";
            }
        }
    }

    private Control RenderBlockImage(JsonElement block)
    {
        var blockId = block.GetStringProp("id") ?? "";
        var url = block.GetStringProp("url") ?? "";
        var alt = block.GetStringProp("alt") ?? "";
        var title = block.GetStringProp("title") ?? "";
        var align = block.GetStringProp("align") ?? "left";
        var widthVal = block.TryGetProperty("width", out var wProp) && wProp.ValueKind == JsonValueKind.Number
            ? (double?)wProp.GetDouble() : null;

        var hAlign = align switch
        {
            "center" => HorizontalAlignment.Center,
            "right" => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left,
        };

        var outerContainer = new StackPanel { Spacing = Dbl("ThemeSpacingSm", 8) };

        if (string.IsNullOrWhiteSpace(url))
        {
            outerContainer.Children.Add(CreateImagePlaceholder(blockId));
        }
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var statusText = new TextBlock
            {
                Text = "Downloading image\u2026",
                Foreground = TextMuted,
                FontSize = FontSize("ThemeFontSizeSm", 12),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            outerContainer.Children.Add(new Border
            {
                Background = SurfaceElevated,
                CornerRadius = Radius("ThemeRadiusSm"),
                Padding = new Thickness(Dbl("ThemeSpacingLg", 16)),
                Child = statusText,
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    var bytes = NetworkFetcher is not null ? await NetworkFetcher(url) : null;
                    if (bytes is null || bytes.Length == 0)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            statusText.Text = "Could not download image. Check network permissions.");
                        return;
                    }

                    var ext = ".png";
                    try
                    {
                        var uriPath = new Uri(url).AbsolutePath;
                        var uriExt = System.IO.Path.GetExtension(uriPath);
                        if (!string.IsNullOrEmpty(uriExt) && uriExt.Length <= 5)
                            ext = uriExt;
                    }
                    catch { /* default to .png */ }

                    var storagePath = ImageStoragePath;
                    if (string.IsNullOrEmpty(storagePath))
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            statusText.Text = "Image storage path not configured.");
                        return;
                    }

                    Directory.CreateDirectory(storagePath);
                    var fileName = $"{Guid.NewGuid():N}{ext}";
                    var localPath = System.IO.Path.Combine(storagePath, fileName);
                    await System.IO.File.WriteAllBytesAsync(localPath, bytes);

                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _shadowState.UpdateImageUrl(blockId, localPath);
                        _onBlockContentChanged?.Invoke();
                        ReplaceBlockInPlace(blockId);
                    });
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Image download failed for blockId={BlockId} url={Url}", blockId, url);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        statusText.Text = "Failed to download image.");
                }
            });
        }
        else
        {
            // Local path (or file://) — load bitmap, wrap in resizable container
            var localPath = url;
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try { localPath = new Uri(url).LocalPath; }
                catch { /* use as-is */ }
            }

            if (System.IO.File.Exists(localPath))
            {
                try
                {
                    var bitmap = new Avalonia.Media.Imaging.Bitmap(localPath);
                    var imageControl = new Avalonia.Controls.Image
                    {
                        Stretch = Avalonia.Media.Stretch.Uniform,
                        Source = bitmap,
                    };

                    // Resizable wrapper with grip handles on left and right edges
                    var imageWrapper = new Border
                    {
                        Child = imageControl,
                        ClipToBounds = true,
                    };
                    // Container that holds grips + image, aligned as a unit
                    var resizeContainer = new Grid
                    {
                        HorizontalAlignment = hAlign,
                        MaxWidth = 1200,
                        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                    };
                    if (widthVal.HasValue && widthVal.Value > 20)
                        resizeContainer.Width = widthVal.Value;
                    else
                        resizeContainer.MaxWidth = 800;

                    Grid.SetColumn(imageWrapper, 1);
                    resizeContainer.Children.Add(imageWrapper);

                    if (!_currentPageIsLocked)
                    {
                        CreateImageResizeGrips(blockId, resizeContainer);
                        outerContainer.Children.Add(resizeContainer);
                    }
                    else
                    {
                        outerContainer.Children.Add(resizeContainer);
                    }

                    // Alignment toolbar (below image, only when not locked)
                    if (!_currentPageIsLocked)
                        outerContainer.Children.Add(CreateImageAlignmentBar(blockId, align));
                }
                catch
                {
                    outerContainer.Children.Add(CreateImageErrorPlaceholder("Failed to load image."));
                }
            }
            else
            {
                outerContainer.Children.Add(CreateImageErrorPlaceholder("Image not found."));
            }
        }

        // Caption / alt text input — always centered under the image
        var altBox = new TextBox
        {
            Text = alt,
            Watermark = "Alt text...",
            FontSize = FontSize("ThemeFontSizeSm", 12),
            Foreground = TextMuted,
            IsReadOnly = _currentPageIsLocked,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            MaxWidth = widthVal.HasValue ? widthVal.Value : 800,
        };
        altBox.LostFocus += (_, _) =>
        {
            _shadowState.UpdateImageAlt(blockId, altBox.Text ?? "");
            _onBlockContentChanged?.Invoke();
        };
        outerContainer.Children.Add(altBox);

        return outerContainer;
    }

    private Control CreateImagePlaceholder(string blockId)
    {
        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = Dbl("ThemeSpacingSm", 8),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // "Choose File" button
        var chooseBtn = new Button
        {
            Content = "Choose File",
            FontSize = FontSize("ThemeFontSizeSm", 12),
            Padding = new Thickness(Dbl("ThemeSpacingSm", 8), Dbl("ThemeSpacingXs", 4)),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        chooseBtn.Click += async (_, _) =>
        {
            if (ImageFilePicker is null) return;
            var selected = await ImageFilePicker();
            if (string.IsNullOrEmpty(selected)) return;

            var localPath = CopyImageToStorage(selected);
            if (localPath is null) return;

            _shadowState.UpdateImageUrl(blockId, localPath);
            _onBlockContentChanged?.Invoke();
            ReplaceBlockInPlace(blockId);
        };
        buttonPanel.Children.Add(chooseBtn);

        // "Paste URL" button — toggles a TextBox
        var urlBox = new TextBox
        {
            Watermark = "Paste image URL and press Enter\u2026",
            FontSize = FontSize("ThemeFontSizeSm", 12),
            IsReadOnly = _currentPageIsLocked,
            IsVisible = false,
            MinWidth = 300,
        };
        var pasteUrlBtn = new Button
        {
            Content = "Paste URL",
            FontSize = FontSize("ThemeFontSizeSm", 12),
            Padding = new Thickness(Dbl("ThemeSpacingSm", 8), Dbl("ThemeSpacingXs", 4)),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        pasteUrlBtn.Click += (_, _) =>
        {
            urlBox.IsVisible = !urlBox.IsVisible;
            if (urlBox.IsVisible)
                urlBox.Focus();
        };
        urlBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && !string.IsNullOrWhiteSpace(urlBox.Text))
            {
                _shadowState.UpdateImageUrl(blockId, urlBox.Text!);
                _onBlockContentChanged?.Invoke();
                ReplaceBlockInPlace(blockId);
            }
        };
        buttonPanel.Children.Add(pasteUrlBtn);

        // "Paste from Clipboard" button
        var clipboardBtn = new Button
        {
            Content = "Paste from Clipboard",
            FontSize = FontSize("ThemeFontSizeSm", 12),
            Padding = new Thickness(Dbl("ThemeSpacingSm", 8), Dbl("ThemeSpacingXs", 4)),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        clipboardBtn.Click += async (_, _) =>
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                var clipboard = topLevel?.Clipboard;
                if (clipboard is null) return;

                // Try to read text (URL) from clipboard first
#pragma warning disable CS0618 // IClipboard methods are obsolete but replacements require IAsyncDataTransfer
                var text = await clipboard.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text) &&
                    (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    _shadowState.UpdateImageUrl(blockId, text);
                    _onBlockContentChanged?.Invoke();
                    ReplaceBlockInPlace(blockId);
                    return;
                }

                // Try to read image data from clipboard
                var formats = await clipboard.GetFormatsAsync();
                if (formats.Contains("image/png") || formats.Contains("public.png"))
                {
                    var data = await clipboard.GetDataAsync("image/png")
                            ?? await clipboard.GetDataAsync("public.png");
                    if (data is byte[] imgBytes && imgBytes.Length > 0)
                    {
                        var storagePath = ImageStoragePath;
                        if (string.IsNullOrEmpty(storagePath)) return;
                        Directory.CreateDirectory(storagePath);
                        var fileName = $"{Guid.NewGuid():N}.png";
                        var localPath = System.IO.Path.Combine(storagePath, fileName);
                        await System.IO.File.WriteAllBytesAsync(localPath, imgBytes);
                        _shadowState.UpdateImageUrl(blockId, localPath);
                        _onBlockContentChanged?.Invoke();
                        ReplaceBlockInPlace(blockId);
                    }
                }
#pragma warning restore CS0618
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to paste image from clipboard for blockId={BlockId}", blockId);
            }
        };
        buttonPanel.Children.Add(clipboardBtn);

        var placeholder = new Border
        {
            Background = SurfaceElevated,
            CornerRadius = Radius("ThemeRadiusSm"),
            Padding = new Thickness(Dbl("ThemeSpacingLg", 24)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new StackPanel
            {
                Spacing = Dbl("ThemeSpacingSm", 8),
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "\uD83D\uDDBC Add an image",
                        Foreground = TextMuted,
                        FontSize = FontSize("ThemeFontSizeMd", 14),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    buttonPanel,
                    urlBox,
                },
            },
        };
        return placeholder;
    }

    private Control CreateImageErrorPlaceholder(string message)
    {
        return new Border
        {
            Background = SurfaceElevated,
            CornerRadius = Radius("ThemeRadiusSm"),
            Padding = new Thickness(Dbl("ThemeSpacingLg", 16)),
            Child = new TextBlock
            {
                Text = message,
                Foreground = TextMuted,
                FontSize = FontSize("ThemeFontSizeSm", 12),
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
    }

    private Control CreateImageAlignmentBar(string blockId, string currentAlign)
    {
        var bar = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        };

        foreach (var (val, tip, iconLines) in new[]
        {
            ("left", "Align left", new[] { 14.0, 10.0, 14.0 }),
            ("center", "Align center", new[] { 10.0, 14.0, 10.0 }),
            ("right", "Align right", new[] { 14.0, 10.0, 14.0 }),
        })
        {
            var isActive = currentAlign == val;
            var capturedVal = val;
            var lineColor = isActive ? Primary : TextMuted;

            var linesPanel = new StackPanel { Spacing = 2, Width = 14, Height = 14 };
            var hAlignLine = val switch
            {
                "right" => HorizontalAlignment.Right,
                "center" => HorizontalAlignment.Center,
                _ => HorizontalAlignment.Left,
            };
            foreach (var w in iconLines)
            {
                linesPanel.Children.Add(new Border
                {
                    Width = w, Height = 2,
                    Background = lineColor,
                    CornerRadius = new CornerRadius(1),
                    HorizontalAlignment = hAlignLine,
                });
            }

            var alignBtn = new Border
            {
                Child = linesPanel,
                Background = isActive ? HoverSubtle : Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2),
                MinWidth = 28,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            ToolTip.SetTip(alignBtn, tip);
            alignBtn.PointerEntered += (_, _) => alignBtn.Background = HoverBrush;
            alignBtn.PointerExited += (_, _) => alignBtn.Background = isActive ? HoverSubtle : Brushes.Transparent;
            alignBtn.PointerReleased += (_, e) =>
            {
                _shadowState.UpdateImageAlign(blockId, capturedVal);
                _onBlockContentChanged?.Invoke();
                ReplaceBlockInPlace(blockId);
                e.Handled = true;
            };
            bar.Children.Add(alignBtn);
        }

        // Reset width button
        var resetBtn = new Border
        {
            Child = new TextBlock
            {
                Text = "Reset size",
                FontSize = FontSize("ThemeFontSizeXs", 10),
                Foreground = TextMuted,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        ToolTip.SetTip(resetBtn, "Reset to original size");
        resetBtn.PointerEntered += (_, _) => resetBtn.Background = HoverBrush;
        resetBtn.PointerExited += (_, _) => resetBtn.Background = Brushes.Transparent;
        resetBtn.PointerReleased += (_, e) =>
        {
            _shadowState.UpdateImageWidth(blockId, null);
            _onBlockContentChanged?.Invoke();
            ReplaceBlockInPlace(blockId);
            e.Handled = true;
        };
        bar.Children.Add(resetBtn);

        return bar;
    }

    /// <summary>
    /// Adds left and right edge grip bars to a resizeContainer Grid for image resizing.
    /// The grips are vertical bars that appear on hover.
    /// </summary>
    private void CreateImageResizeGrips(string blockId, Grid resizeContainer)
    {
        // Shared state for the drag
        double startX = 0;
        double startWidth = 0;
        int dragSide = 0; // -1 = left grip, +1 = right grip

        Border MakeGrip(bool isRight)
        {
            var gripBar = new Border
            {
                Width = 6,
                Background = Brushes.Transparent,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                HorizontalAlignment = isRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };

            // Inner visible pill — only shows on hover
            var pill = new Border
            {
                Width = 4,
                Height = 36,
                Background = Primary,
                CornerRadius = new CornerRadius(2),
                Opacity = 0,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            gripBar.Child = pill;

            gripBar.PointerEntered += (_, _) => pill.Opacity = 0.6;
            gripBar.PointerExited += (_, _) => { if (!_isResizingImage) pill.Opacity = 0; };

            gripBar.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(gripBar).Properties.IsLeftButtonPressed) return;
                _isResizingImage = true;
                dragSide = isRight ? 1 : -1;
                startX = e.GetPosition(this).X;
                startWidth = !double.IsNaN(resizeContainer.Width)
                    ? resizeContainer.Width
                    : resizeContainer.Bounds.Width;
                if (startWidth <= 0) startWidth = 400;
                pill.Opacity = 1;
                e.Pointer.Capture(gripBar);
                e.Handled = true;
            };

            gripBar.PointerMoved += (_, e) =>
            {
                if (!_isResizingImage) return;
                var currentX = e.GetPosition(this).X;
                var delta = (currentX - startX) * dragSide;
                var newWidth = Math.Clamp(startWidth + delta, 50, 1200);
                resizeContainer.Width = newWidth;
                resizeContainer.MaxWidth = double.PositiveInfinity;
                e.Handled = true;
            };

            gripBar.PointerReleased += (_, e) =>
            {
                if (!_isResizingImage) return;
                _isResizingImage = false;
                e.Pointer.Capture(null);
                pill.Opacity = 0;
                var finalWidth = !double.IsNaN(resizeContainer.Width)
                    ? resizeContainer.Width
                    : resizeContainer.Bounds.Width;
                _shadowState.UpdateImageWidth(blockId, finalWidth);
                _onBlockContentChanged?.Invoke();
                e.Handled = true;
            };

            return gripBar;
        }

        var leftGrip = MakeGrip(false);
        var rightGrip = MakeGrip(true);
        Grid.SetColumn(leftGrip, 0);
        Grid.SetColumn(rightGrip, 2);
        resizeContainer.Children.Add(leftGrip);
        resizeContainer.Children.Add(rightGrip);

        // Also show grips when hovering the image area
        resizeContainer.PointerEntered += (_, _) =>
        {
            if (!_isResizingImage)
            {
                ((Border)leftGrip.Child!).Opacity = 0.4;
                ((Border)rightGrip.Child!).Opacity = 0.4;
            }
        };
        resizeContainer.PointerExited += (_, _) =>
        {
            if (!_isResizingImage)
            {
                ((Border)leftGrip.Child!).Opacity = 0;
                ((Border)rightGrip.Child!).Opacity = 0;
            }
        };
    }

    /// <summary>
    /// Copies an image file to the ImageStoragePath if not already there.
    /// Returns the local path inside storage, or null on failure.
    /// </summary>
    private string? CopyImageToStorage(string sourcePath)
    {
        var storagePath = ImageStoragePath;
        if (string.IsNullOrEmpty(storagePath)) return null;

        Directory.CreateDirectory(storagePath);

        // If already inside storage dir, use directly
        var fullSource = System.IO.Path.GetFullPath(sourcePath);
        var fullStorage = System.IO.Path.GetFullPath(storagePath);
        if (fullSource.StartsWith(fullStorage, StringComparison.OrdinalIgnoreCase))
            return fullSource;

        var ext = System.IO.Path.GetExtension(sourcePath);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        var fileName = $"{Guid.NewGuid():N}{ext}";
        var destPath = System.IO.Path.Combine(storagePath, fileName);

        try
        {
            System.IO.File.Copy(sourcePath, destPath, overwrite: false);
            return destPath;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to copy image to storage: {Source} → {Dest}", sourcePath, destPath);
            return null;
        }
    }

    private Control RenderBlockFootnote(JsonElement block)
    {
        var blockId = block.GetStringProp("id") ?? "";
        var label = block.GetStringProp("label") ?? "1";
        var content = block.GetStringProp("content") ?? block.GetStringProp("text") ?? "";

        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = Dbl("ThemeSpacingSm", 8) };

        var labelBox = new TextBox
        {
            Text = label,
            Watermark = "#",
            Width = 40,
            FontSize = FontSize("ThemeFontSizeSm", 12),
            FontWeight = FontWeight.Bold,
            FontFamily = new Avalonia.Media.FontFamily("Cascadia Code, Consolas, monospace"),
            IsReadOnly = _currentPageIsLocked,
        };
        labelBox.LostFocus += (_, _) =>
        {
            _shadowState.UpdateFootnoteLabel(blockId, labelBox.Text ?? "1");
            _onBlockContentChanged?.Invoke();
        };

        var editor = new Controls.RichTextEditor.RichTextEditor
        {
            Markdown = content,
            BlockId = blockId,
            FontSize = FontSize("ThemeFontSizeMd", 14),
            Placeholder = "Footnote content...",
        };
        WireBlockEditorEvents(editor, blockId);

        var prefix = new TextBlock
        {
            Text = $"[^",
            FontSize = FontSize("ThemeFontSizeSm", 12),
            Foreground = TextMuted,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var suffix = new TextBlock
        {
            Text = $"]:",
            FontSize = FontSize("ThemeFontSizeSm", 12),
            Foreground = TextMuted,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        row.Children.Add(prefix);
        row.Children.Add(labelBox);
        row.Children.Add(suffix);
        row.Children.Add(editor);

        return row;
    }

    private Control RenderBlockDefinitionList(JsonElement block)
    {
        var blockId = block.GetStringProp("id") ?? "";
        var panel = new StackPanel { Spacing = Dbl("ThemeSpacingMd", 12) };

        if (!block.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return panel;

        foreach (var item in items.EnumerateArray())
        {
            var itemId = item.GetStringProp("id") ?? "";
            var term = item.GetStringProp("term") ?? "";

            var itemPanel = new StackPanel { Spacing = Dbl("ThemeSpacingXs", 4) };

            // Term editor (bold)
            var termEditor = new Controls.RichTextEditor.RichTextEditor
            {
                Markdown = term,
                BlockId = $"{blockId}__term__{itemId}",
                FontSize = FontSize("ThemeFontSizeMd", 14),
                BaseFontWeight = FontWeight.Bold,
                Placeholder = "Term...",
            };
            var capturedItemId = itemId;
            termEditor.TextChanged += (_, markdown) =>
            {
                _shadowState.UpdateDefinitionTerm(blockId, capturedItemId, markdown);
                _onBlockContentChanged?.Invoke();
            };
            WireEditorFocusNavigation(termEditor);
            itemPanel.Children.Add(termEditor);

            // Definitions
            if (item.TryGetProperty("definitions", out var defs) && defs.ValueKind == JsonValueKind.Array)
            {
                foreach (var def in defs.EnumerateArray())
                {
                    var defId = def.GetStringProp("id") ?? "";
                    var defText = def.GetStringProp("text") ?? "";

                    var defRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = Dbl("ThemeSpacingSm", 8) };
                    defRow.Margin = new Thickness(Dbl("ThemeSpacingLg", 24), 0, 0, 0);

                    var colonLabel = new TextBlock
                    {
                        Text = ":",
                        FontSize = FontSize("ThemeFontSizeMd", 14),
                        Foreground = TextMuted,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    };

                    var defEditor = new Controls.RichTextEditor.RichTextEditor
                    {
                        Markdown = defText,
                        BlockId = $"{blockId}__def__{defId}",
                        FontSize = FontSize("ThemeFontSizeMd", 14),
                        Placeholder = "Definition...",
                    };
                    var capturedDefId = defId;
                    defEditor.TextChanged += (_, markdown) =>
                    {
                        _shadowState.UpdateDefinitionText(blockId, capturedItemId, capturedDefId, markdown);
                        _onBlockContentChanged?.Invoke();
                    };
                    WireEditorFocusNavigation(defEditor);

                    defRow.Children.Add(colonLabel);
                    defRow.Children.Add(defEditor);

                    // Remove definition button (if more than 1)
                    if (defs.GetArrayLength() > 1 && !_currentPageIsLocked)
                    {
                        var removeDefBtn = new Button
                        {
                            Content = "−",
                            FontSize = FontSize("ThemeFontSizeSm", 12),
                            Padding = new Thickness(4, 0),
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        };
                        var rmDefId = defId;
                        removeDefBtn.Click += (_, _) =>
                        {
                            _shadowState.RemoveDefinition(blockId, capturedItemId, rmDefId);
                            ReplaceBlockInPlace(blockId);
                        };
                        defRow.Children.Add(removeDefBtn);
                    }

                    itemPanel.Children.Add(defRow);
                }
            }

            // Add definition button
            if (!_currentPageIsLocked)
            {
                var addDefBtn = new Button
                {
                    Content = "+ Definition",
                    FontSize = FontSize("ThemeFontSizeSm", 12),
                    Padding = new Thickness(Dbl("ThemeSpacingSm", 8), Dbl("ThemeSpacingXs", 4)),
                    Margin = new Thickness(Dbl("ThemeSpacingLg", 24), Dbl("ThemeSpacingXs", 4), 0, 0),
                    Foreground = TextMuted,
                };
                var addItemIdCapture = itemId;
                addDefBtn.Click += (_, _) =>
                {
                    _shadowState.AddDefinition(blockId, addItemIdCapture);
                    ReplaceBlockInPlace(blockId);
                };
                itemPanel.Children.Add(addDefBtn);
            }

            panel.Children.Add(itemPanel);

            // Remove item button (if more than 1 item)
            if (items.GetArrayLength() > 1 && !_currentPageIsLocked)
            {
                var removeItemBtn = new Button
                {
                    Content = "− Remove term",
                    FontSize = FontSize("ThemeFontSizeSm", 12),
                    Padding = new Thickness(Dbl("ThemeSpacingSm", 8), Dbl("ThemeSpacingXs", 4)),
                    Foreground = TextMuted,
                };
                var rmItemId = itemId;
                removeItemBtn.Click += (_, _) =>
                {
                    _shadowState.RemoveDefinitionItem(blockId, rmItemId);
                    ReplaceBlockInPlace(blockId);
                };
                panel.Children.Add(removeItemBtn);
            }
        }

        // Add new term button
        if (!_currentPageIsLocked)
        {
            var addTermBtn = new Button
            {
                Content = "+ Add Term",
                FontSize = FontSize("ThemeFontSizeSm", 12),
                Padding = new Thickness(Dbl("ThemeSpacingSm", 8), Dbl("ThemeSpacingXs", 4)),
                Foreground = TextMuted,
                Margin = new Thickness(0, Dbl("ThemeSpacingSm", 8), 0, 0),
            };
            addTermBtn.Click += (_, _) =>
            {
                _shadowState.AddDefinitionItem(blockId);
                ReplaceBlockInPlace(blockId);
            };
            panel.Children.Add(addTermBtn);
        }

        return panel;
    }

    private Control RenderBlockTable(JsonElement block)
    {
        var blockId = block.GetStringProp("id") ?? "";
        var container = new StackPanel { Spacing = 0 };

        // Parse display properties
        var showHeader = !block.TryGetProperty("show_header", out var shEl) || shEl.GetBoolean();
        var alternatingRows = block.TryGetProperty("alternating_rows", out var arEl) && arEl.GetBoolean();

        // Parse columns
        var columns = new List<string>();
        if (block.TryGetProperty("columns", out var colsEl) && colsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in colsEl.EnumerateArray())
                columns.Add(c.GetString() ?? "left");
        }
        if (columns.Count == 0) columns.AddRange(["left", "left", "left"]);

        var colCount = columns.Count;

        // Parse stored column width proportions (Star values)
        var colWidths = new List<double>();
        if (block.TryGetProperty("column_widths", out var cwEl) && cwEl.ValueKind == JsonValueKind.Array)
            foreach (var v in cwEl.EnumerateArray())
                colWidths.Add(v.GetDouble());
        // Pad to colCount with 1.0 if not enough entries
        while (colWidths.Count < colCount) colWidths.Add(1.0);

        // Collect all data rows for filter
        var allRows = new List<JsonElement>();
        if (block.TryGetProperty("rows", out var rowsEl) && rowsEl.ValueKind == JsonValueKind.Array)
            foreach (var row in rowsEl.EnumerateArray())
                allRows.Add(row);

        // Filter state (driven from toolbar)
        var filterText = _tableFilterState.TryGetValue(blockId, out var fs) ? fs.Filter : "";
        var gridContainer = new StackPanel { Spacing = 0 };

        void RebuildTableGrid()
        {
            gridContainer.Children.Clear();
            var grid = new Grid();

            // Star-sized columns with splitter columns between them
            // Layout: [col0] [splitter] [col1] [splitter] [col2] ...
            // Grid column index for data col i = i * 2
            var totalGridCols = colCount * 2 - 1;
            for (var c = 0; c < totalGridCols; c++)
            {
                if (c % 2 == 0)
                {
                    var dataIdx = c / 2;
                    var starVal = dataIdx < colWidths.Count ? colWidths[dataIdx] : 1.0;
                    grid.ColumnDefinitions.Add(new ColumnDefinition(starVal, GridUnitType.Star) { MinWidth = 50 });
                }
                else
                    grid.ColumnDefinitions.Add(new ColumnDefinition(4, GridUnitType.Pixel));
            }

            int DataCol(int ci) => ci * 2;

            var rowIndex = 0;

            // Header row
            if (showHeader && block.TryGetProperty("header_row", out var headerEl2) && headerEl2.ValueKind == JsonValueKind.Object)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var rowId = headerEl2.GetStringProp("id") ?? "";
                if (headerEl2.TryGetProperty("cells", out var headerCells) && headerCells.ValueKind == JsonValueKind.Array)
                {
                    var ci = 0;
                    foreach (var cell in headerCells.EnumerateArray())
                    {
                        if (ci >= colCount) break;
                        var capturedCi = ci;
                        var cellCtrl = CreateTableCell(blockId, rowId, cell, isHeader: true);
                        var headerBg = new Border
                        {
                            Background = HoverBrush,
                            Child = cellCtrl,
                            Padding = new Thickness(0),
                        };

                        if (!_currentPageIsLocked)
                        {
                            var sortPanel = new StackPanel
                            {
                                Orientation = Avalonia.Layout.Orientation.Horizontal,
                                HorizontalAlignment = HorizontalAlignment.Right,
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(0, 0, 4, 0),
                            };
                            var sortAsc = new Button
                            {
                                Content = new TextBlock { Text = "▲", FontSize = FontSize("ThemeFontSize2Xs", 9), Foreground = TextMuted },
                                Background = Brushes.Transparent,
                                BorderThickness = new Thickness(0),
                                Padding = new Thickness(2, 0),
                                MinWidth = 0, MinHeight = 0,
                                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                            };
                            sortAsc.Click += (_, _) =>
                            {
                                _shadowState.SortTableColumn(blockId, capturedCi, "asc");
                                ReplaceBlockInPlace(blockId);
                            };
                            var sortDesc = new Button
                            {
                                Content = new TextBlock { Text = "▼", FontSize = FontSize("ThemeFontSize2Xs", 9), Foreground = TextMuted },
                                Background = Brushes.Transparent,
                                BorderThickness = new Thickness(0),
                                Padding = new Thickness(2, 0),
                                MinWidth = 0, MinHeight = 0,
                                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                            };
                            sortDesc.Click += (_, _) =>
                            {
                                _shadowState.SortTableColumn(blockId, capturedCi, "desc");
                                ReplaceBlockInPlace(blockId);
                            };
                            sortPanel.Children.Add(sortAsc);
                            sortPanel.Children.Add(sortDesc);

                            var cellWrapper = new Grid();
                            cellWrapper.Children.Add(headerBg);
                            cellWrapper.Children.Add(sortPanel);
                            Grid.SetRow(cellWrapper, rowIndex);
                            Grid.SetColumn(cellWrapper, DataCol(ci));
                            grid.Children.Add(cellWrapper);
                        }
                        else
                        {
                            Grid.SetRow(headerBg, rowIndex);
                            Grid.SetColumn(headerBg, DataCol(ci));
                            grid.Children.Add(headerBg);
                        }
                        ci++;
                    }
                }
                rowIndex++;

                // Separator line below header
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var sepBorder = new Border { Background = Primary, Height = 2 };
                Grid.SetRow(sepBorder, rowIndex);
                Grid.SetColumn(sepBorder, 0);
                Grid.SetColumnSpan(sepBorder, totalGridCols);
                grid.Children.Add(sepBorder);
                rowIndex++;
            }

            // GridSplitters span all rows + persist widths on drag
            if (!_currentPageIsLocked)
            {
                for (var s = 1; s < totalGridCols; s += 2)
                {
                    var splitter = new GridSplitter
                    {
                        Width = 4,
                        Background = Brushes.Transparent,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Stretch,
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast),
                    };
                    Grid.SetColumn(splitter, s);
                    Grid.SetRow(splitter, 0);
                    Grid.SetRowSpan(splitter, 100);
                    var capturedGrid = grid;
                    splitter.DragCompleted += (_, _) =>
                    {
                        // Read back the actual Star values from column definitions after drag
                        var newWidths = new List<double>();
                        for (var gc = 0; gc < capturedGrid.ColumnDefinitions.Count; gc++)
                        {
                            if (gc % 2 == 0)
                                newWidths.Add(Math.Round(capturedGrid.ColumnDefinitions[gc].Width.Value, 2));
                        }
                        _shadowState.SetColumnWidths(blockId, newWidths);
                        _onBlockContentChanged?.Invoke();
                    };
                    grid.Children.Add(splitter);
                }
            }

            // Data rows (filtered)
            var filter = filterText.Trim().ToLowerInvariant();
            var dataRowIdx = 0;
            foreach (var row in allRows)
            {
                if (!string.IsNullOrEmpty(filter))
                {
                    var matches = false;
                    if (row.TryGetProperty("cells", out var fCells) && fCells.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var fc in fCells.EnumerateArray())
                        {
                            var ct = fc.GetStringProp("text") ?? "";
                            if (ct.Contains(filter, StringComparison.OrdinalIgnoreCase))
                            { matches = true; break; }
                        }
                    }
                    if (!matches) continue;
                }

                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var rowId = row.GetStringProp("id") ?? "";
                if (row.TryGetProperty("cells", out var cells) && cells.ValueKind == JsonValueKind.Array)
                {
                    var ci = 0;
                    foreach (var cell in cells.EnumerateArray())
                    {
                        if (ci >= colCount) break;
                        var cellCtrl = CreateTableCell(blockId, rowId, cell, isHeader: false);
                        if (alternatingRows && dataRowIdx % 2 == 1)
                        {
                            var stripeBg = new Border { Background = HoverSubtle, Child = cellCtrl };
                            Grid.SetRow(stripeBg, rowIndex);
                            Grid.SetColumn(stripeBg, DataCol(ci));
                            grid.Children.Add(stripeBg);
                        }
                        else
                        {
                            Grid.SetRow(cellCtrl, rowIndex);
                            Grid.SetColumn(cellCtrl, DataCol(ci));
                            grid.Children.Add(cellCtrl);
                        }
                        ci++;
                    }
                }
                dataRowIdx++;
                rowIndex++;
            }

            gridContainer.Children.Add(new Border
            {
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = Radius("ThemeRadiusSm"),
                Padding = new Thickness(0),
                ClipToBounds = true,
                Child = grid,
            });
        }

        // Register rebuild callback for toolbar filter
        _tableFilterState[blockId] = (filterText, newFilter =>
        {
            filterText = newFilter;
            _tableFilterState[blockId] = (filterText, _tableFilterState[blockId].Rebuild);
            RebuildTableGrid();
        });

        // Initial build
        RebuildTableGrid();

        container.Children.Add(gridContainer);

        return container;
    }

    private Control CreateTableCell(string blockId, string rowId, JsonElement cell, bool isHeader)
    {
        var cellId = cell.GetStringProp("id") ?? "";
        var text = cell.GetStringProp("text") ?? "";

        var editor = new Controls.RichTextEditor.RichTextEditor
        {
            Markdown = text,
            BlockId = $"{blockId}__cell__{cellId}",
            FontSize = FontSize("ThemeFontSizeMd", 14),
            BaseFontWeight = isHeader ? FontWeight.Bold : FontWeight.Normal,
            Margin = new Thickness(8, 6),
            IsReadOnly = _currentPageIsLocked,
            Placeholder = isHeader ? "Header..." : "Cell...",
        };

        var capturedRowId = rowId;
        var capturedCellId = cellId;
        editor.TextChanged += (_, markdown) =>
        {
            _shadowState.UpdateTableCell(blockId, capturedRowId, capturedCellId, markdown);
            _onBlockContentChanged?.Invoke();
        };
        WireEditorFocusNavigation(editor);

        return editor;
    }

    private Control RenderBlockTableOfContents(JsonElement block)
    {
        var blockId = block.GetStringProp("id") ?? "";
        var mode = block.GetStringProp("mode") ?? "document";

        var container = new StackPanel { Spacing = Dbl("ThemeSpacingSm", 8) };

        // Header with mode toggle
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Dbl("ThemeSpacingSm", 8) };
        var titleLabel = new TextBlock
        {
            Text = "Table of Contents",
            FontSize = FontSize("ThemeFontSizeMd", 14),
            FontWeight = FontWeight.Bold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerRow.Children.Add(titleLabel);

        // Mode toggle buttons
        if (!_currentPageIsLocked)
        {
            var docBtn = new Border
            {
                Child = new TextBlock
                {
                    Text = "Document",
                    FontSize = FontSize("ThemeFontSizeSm", 12),
                    Foreground = mode == "document" ? TextPrimary : TextMuted,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                Background = mode == "document" ? PrimarySubtle : Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(Dbl("ThemeSpacingSm", 8), Dbl("ThemeSpacingXs", 4)),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            docBtn.PointerReleased += (_, _) =>
            {
                SendCommandSilent("update_block", JsonSerializer.Serialize(new { id = blockId, mode = "document" }));
            };

            var parentBtn = new Border
            {
                Child = new TextBlock
                {
                    Text = "Child Pages",
                    FontSize = FontSize("ThemeFontSizeSm", 12),
                    Foreground = mode == "parent" ? TextPrimary : TextMuted,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                Background = mode == "parent" ? PrimarySubtle : Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(Dbl("ThemeSpacingSm", 8), Dbl("ThemeSpacingXs", 4)),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            parentBtn.PointerReleased += (_, _) =>
            {
                SendCommandSilent("update_block", JsonSerializer.Serialize(new { id = blockId, mode = "parent" }));
            };

            headerRow.Children.Add(new Border { Width = 1, Height = 16, Background = BorderSubtle, Margin = new Thickness(4, 0) });
            headerRow.Children.Add(docBtn);
            headerRow.Children.Add(parentBtn);
        }

        container.Children.Add(headerRow);

        // Separator
        container.Children.Add(new Separator { Background = BorderSubtle, Height = 1 });

        // Content based on mode
        var entriesPanel = new StackPanel { Spacing = Dbl("ThemeSpacingXs", 4) };

        if (mode == "document")
        {
            RebuildTocEntries(entriesPanel);
            _tocPanels.Add((blockId, entriesPanel));
        }
        else // "parent" mode
        {
            if (_currentChildPages.Count == 0)
            {
                entriesPanel.Children.Add(new TextBlock
                {
                    Text = "No child pages.",
                    FontSize = FontSize("ThemeFontSizeSm", 12),
                    Foreground = TextMuted,
                    FontStyle = Avalonia.Media.FontStyle.Italic,
                });
            }
            else
            {
                foreach (var (cpId, cpTitle, cpIcon) in _currentChildPages)
                {
                    var capturedId = cpId;
                    var entryRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Dbl("ThemeSpacingSm", 8) };

                    entryRow.Children.Add(new TextBlock
                    {
                        Text = cpIcon,
                        FontSize = FontSize("ThemeFontSizeSm", 12),
                        VerticalAlignment = VerticalAlignment.Center,
                    });

                    var link = new TextBlock
                    {
                        Text = string.IsNullOrEmpty(cpTitle) ? "Untitled" : cpTitle,
                        FontSize = FontSize("ThemeFontSizeSm", 12),
                        Foreground = Primary,
                        TextDecorations = TextDecorations.Underline,
                        VerticalAlignment = VerticalAlignment.Center,
                    };

                    var entryBorder = new Border
                    {
                        Child = entryRow,
                        Padding = new Thickness(Dbl("ThemeSpacingXs", 4), 2),
                        Cursor = new Cursor(StandardCursorType.Hand),
                    };
                    entryBorder.PointerReleased += (_, _) =>
                    {
                        SendCommand("select_page", JsonSerializer.Serialize(new { id = capturedId }));
                    };

                    entryRow.Children.Add(link);
                    entriesPanel.Children.Add(entryBorder);
                }
            }
        }

        container.Children.Add(entriesPanel);
        return container;
    }

    private void RebuildTocEntries(StackPanel panel)
    {
        panel.Children.Clear();
        foreach (var sb in _shadowState.Blocks)
        {
            if (sb.Content is HeadingContent hc)
            {
                var level = hc.Level;
                var indent = (level - 1) * Dbl("ThemeSpacingLg", 24);
                var capturedBlockId = sb.Id;

                var entry = new Border
                {
                    Child = new TextBlock
                    {
                        Text = string.IsNullOrEmpty(hc.Text) ? $"Heading {level}" : hc.Text,
                        FontSize = FontSize("ThemeFontSizeSm", 12),
                        Foreground = Primary,
                        TextDecorations = TextDecorations.Underline,
                    },
                    Margin = new Thickness(indent, 0, 0, 0),
                    Padding = new Thickness(Dbl("ThemeSpacingXs", 4), 2),
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                entry.PointerReleased += (_, _) =>
                {
                    if (_blockPanel is not null)
                    {
                        foreach (var child in _blockPanel.Children)
                        {
                            if (child is Border b && b.Tag is string bid && bid == capturedBlockId)
                            {
                                b.BringIntoView();
                                var editor = FindRichTextEditor(b);
                                editor?.Focus();
                                break;
                            }
                        }
                    }
                };
                panel.Children.Add(entry);
            }
        }

        if (panel.Children.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No headings in this document.",
                FontSize = FontSize("ThemeFontSizeSm", 12),
                Foreground = TextMuted,
                FontStyle = Avalonia.Media.FontStyle.Italic,
            });
        }
    }

    private Control RenderGraphView(JsonElement el)
    {
        var hasSettings = el.TryGetProperty("settings", out var _settingsCheck);
        _log.Debug("RenderGraphView: hasSettings={HasSettings}, settingsKind={Kind}",
            hasSettings, hasSettings ? _settingsCheck.ValueKind.ToString() : "N/A");

        var centerId = el.GetStringProp("center_id");
        var onNodeClick = el.GetStringProp("on_node_click");

        var nodeElements = new List<JsonElement>();
        var edgeElements = new List<JsonElement>();

        if (el.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in nodes.EnumerateArray())
                nodeElements.Add(n);
        }

        if (el.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in edges.EnumerateArray())
                edgeElements.Add(e);
        }

        _log.Debug("RenderGraphView: centerId={CenterId}, nodeCount={NodeCount}, edgeCount={EdgeCount}, onNodeClick={OnNodeClick}",
            centerId ?? "(null)", nodeElements.Count, edgeElements.Count, onNodeClick ?? "(null)");

        // Log detailed node/edge info at verbose level for link debugging
        foreach (var n in nodeElements)
        {
            var nid = n.GetStringProp("id");
            var ntitle = n.GetStringProp("title");
            var ntype = n.GetStringProp("node_type");
            var nlinkType = n.GetStringProp("link_type");
            var ndepth = n.TryGetProperty("depth", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : -1;
            _log.Verbose("  GraphView node: Id={Id}, Title={Title}, NodeType={NodeType}, LinkType={LinkType}, Depth={Depth}",
                nid, ntitle, ntype, nlinkType, ndepth);
        }
        foreach (var e in edgeElements)
        {
            var src = e.GetStringProp("source");
            var tgt = e.GetStringProp("target");
            var etype = e.GetStringProp("edge_type");
            _log.Verbose("  GraphView edge: {Source} -> {Target}, EdgeType={EdgeType}", src, tgt, etype);
        }

        // Overlay container: graph (or empty state) + settings gear at bottom-left
        var overlay = new Panel { MinHeight = 150 };

        if (nodeElements.Count == 0)
        {
            overlay.Children.Add(new TextBlock
            {
                Text = "No graph data",
                Foreground = TextMuted,
                FontSize = FontSize("ThemeFontSizeSm", 12),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else
        {
            var physics = new PhysicsParameters();
            if (el.TryGetProperty("repulsion", out var rep) && rep.ValueKind == JsonValueKind.Number)
                physics.RepulsionStrength = rep.GetDouble();
            if (el.TryGetProperty("link_distance", out var ld) && ld.ValueKind == JsonValueKind.Number)
                physics.LinkDistance = ld.GetDouble();
            if (el.TryGetProperty("link_strength", out var ls) && ls.ValueKind == JsonValueKind.Number)
                physics.LinkStrength = ls.GetDouble();
            if (el.TryGetProperty("collision_strength", out var cs) && cs.ValueKind == JsonValueKind.Number)
                physics.CollisionStrength = cs.GetDouble();
            if (el.TryGetProperty("center_strength", out var cstr) && cstr.ValueKind == JsonValueKind.Number)
                physics.CenterStrength = cstr.GetDouble();
            if (el.TryGetProperty("velocity_decay", out var vd) && vd.ValueKind == JsonValueKind.Number)
                physics.VelocityDecay = vd.GetDouble();

            var physicsEnabled = el.GetBoolProp("physics_enabled", true);

            // Create empty graph control as placeholder - hydration is deferred
            var canvas = new NeuronGraphControl
            {
                Nodes = [],  // Empty initially
                Edges = [],
                CenterId = centerId,
                Physics = physicsEnabled ? physics : null,
                MinHeight = 150,
            };

            // Snapshot node id→link_type into a plain dictionary so the closure
            // doesn't hold references to JsonElements (which become invalid when
            // the backing JsonDocument is disposed on re-render).
            string? hostLinkType = null;
            var nodeLinkTypes = new Dictionary<string, string>();
            foreach (var n in nodeElements)
            {
                var nid = n.GetStringProp("id");
                var nlt = n.GetStringProp("link_type");
                if (nid != null && nlt != null)
                    nodeLinkTypes[nid] = nlt;

                // Center node (depth 0) defines the host plugin's link type
                if (hostLinkType == null
                    && n.TryGetProperty("depth", out var d)
                    && d.ValueKind == JsonValueKind.Number
                    && d.GetInt32() == 0
                    && nlt != null)
                {
                    hostLinkType = nlt;
                }
            }

            if (onNodeClick != null)
            {
                canvas.NodeClicked += nodeId =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var hasLinkType = nodeLinkTypes.TryGetValue(nodeId, out var nodeLinkType);
                    _log.Debug("GraphView.NodeClicked: [T+{T}ms] nodeId={NodeId}, hasLinkType={HasLinkType}, linkType={LinkType}, hasInternalLinkHandler={HasHandler}, nodeLinkTypesCount={Count}",
                        sw.ElapsedMilliseconds, nodeId, hasLinkType, nodeLinkType ?? "(none)", InternalLinkActivated != null, nodeLinkTypes.Count);

                    // Log all available link types for debugging
                    if (!hasLinkType)
                    {
                        _log.Warning("GraphView.NodeClicked: Node {NodeId} has no link_type! Available link types: {LinkTypes}",
                            nodeId, string.Join(", ", nodeLinkTypes.Select(kvp => $"{kvp.Key}={kvp.Value}")));
                    }

                    // Route all graph node clicks through InternalLinkActivated
                    // so cross-plugin navigation works automatically
                    if (InternalLinkActivated != null && hasLinkType)
                    {
                        _log.Debug("GraphView.NodeClicked: [T+{T}ms] Routing to InternalLinkActivated linkType={LinkType}, nodeId={NodeId}",
                            sw.ElapsedMilliseconds, nodeLinkType, nodeId);
                        InternalLinkActivated.Invoke(nodeLinkType!, nodeId);
                        _log.Debug("GraphView.NodeClicked: [T+{T}ms] InternalLinkActivated returned", sw.ElapsedMilliseconds);
                        return;
                    }

                    if (onNodeClick != null)
                    {
                        _log.Debug("GraphView.NodeClicked: [T+{T}ms] Sending command={Command}, nodeId={NodeId}", sw.ElapsedMilliseconds, onNodeClick, nodeId);
                        SendCommand(onNodeClick, JsonSerializer.Serialize(new { id = nodeId }));
                        _log.Debug("GraphView.NodeClicked: [T+{T}ms] SendCommand returned", sw.ElapsedMilliseconds);
                    }
                };

                // Prefetch on hover for graph nodes
                canvas.NodeHovered += nodeId =>
                {
                    if (PrefetchRequested != null
                        && nodeLinkTypes.TryGetValue(nodeId, out var nodeLinkType))
                    {
                        _log.Verbose("GraphView.NodeHovered: Requesting prefetch linkType={LinkType}, nodeId={NodeId}",
                            nodeLinkType, nodeId);
                        PrefetchRequested.Invoke(nodeLinkType, nodeId);
                    }
                };

                canvas.NodeUnhovered += nodeId =>
                {
                    if (PrefetchCancelled != null
                        && nodeLinkTypes.TryGetValue(nodeId, out var nodeLinkType))
                    {
                        _log.Verbose("GraphView.NodeUnhovered: Cancelling prefetch linkType={LinkType}, nodeId={NodeId}",
                            nodeLinkType, nodeId);
                        PrefetchCancelled.Invoke(nodeLinkType, nodeId);
                    }
                };
            }

            _activeGraphControl = canvas;
            overlay.Children.Add(canvas);

            // Pre-parse graph data now (while JsonDocument is still valid)
            // to avoid ObjectDisposedException in deferred hydration
            var graphData = Models.GraphData.FromJson(nodeElements, edgeElements);

            // Store deferred hydration data - graph will be populated after main content renders
            _pendingGraphHydration = new DeferredGraphData(
                canvas,
                graphData,
                physicsEnabled ? physics : null,
                centerId);
        }

        // Settings flyout button pinned to bottom-left of graph
        if (el.TryGetProperty("settings", out var settingsEl))
        {
            _log.Debug("RenderGraphView: Creating settings flyout button, settingsEl type={Type}",
                settingsEl.GetStringProp("type") ?? "unknown");
            var settingsContent = RenderComponent(settingsEl);
            var flyout = new Flyout
            {
                Content = new Border
                {
                    Background = SurfaceElevated,
                    BorderBrush = BorderBrush_,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12),
                    Width = 240,
                    Child = settingsContent,
                },
                Placement = PlacementMode.Left,
            };

            var gearBtn = new Button
            {
                Content = new TextBlock
                {
                    Text = "\u2699",
                    FontSize = FontSize("ThemeFontSizeLg", 18),
                    Foreground = TextPrimary,
                },
                Background = SurfaceElevated,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(6),
                Opacity = 0.8,
                Flyout = flyout,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            gearBtn.PointerEntered += (s, _) => { if (s is Button b) b.Opacity = 1.0; };
            gearBtn.PointerExited += (s, _) => { if (s is Button b) b.Opacity = 0.8; };
            overlay.Children.Add(gearBtn);
        }
        else
        {
            _log.Debug("RenderGraphView: No 'settings' property found on graph_view element");
        }

        return new Border
        {
            Child = overlay,
            BorderBrush = BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = Radius("ThemeRadiusSm"),
            Margin = new Thickness(
                Dbl("ThemeSpacingLg", 16),
                Dbl("ThemeSpacingSm", 8)),
        };
    }

    private Control RenderBacklinksList(JsonElement el)
    {
        var command = el.GetStringProp("command");
        var panel = new StackPanel
        {
            Spacing = Dbl("ThemeSpacingXs", 4),
            Margin = new Thickness(Dbl("ThemeSpacingSm", 12), 0, 0, 0),
        };

        if (el.TryGetProperty("backlinks", out var backlinks) && backlinks.ValueKind == JsonValueKind.Array)
        {
            foreach (var bl in backlinks.EnumerateArray())
            {
                var id = bl.GetStringProp("id") ?? "";
                var title = bl.GetStringProp("title") ?? "Untitled";
                var linkType = bl.GetStringProp("link_type") ?? "";
                var icon = bl.GetStringProp("icon") ?? "\uD83D\uDD17";

                var row = new DockPanel();

                // Link type badge on the right
                if (!string.IsNullOrEmpty(linkType))
                {
                    var badge = new Border
                    {
                        Background = SurfaceElevated,
                        CornerRadius = Radius("ThemeRadiusFull"),
                        Padding = new Thickness(4, 1),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = linkType,
                            FontSize = FontSize("ThemeFontSizeXs", 10),
                            Foreground = TextMuted,
                        },
                    };
                    DockPanel.SetDock(badge, Dock.Right);
                    row.Children.Add(badge);
                }

                // Icon + title
                var titleRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = Dbl("ThemeSpacingSm", 8),
                };
                titleRow.Children.Add(new TextBlock
                {
                    Text = icon,
                    FontSize = FontSize("ThemeFontSizeSmMd", 13),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                titleRow.Children.Add(new TextBlock
                {
                    Text = title,
                    FontSize = FontSize("ThemeFontSizeSmMd", 13),
                    Foreground = TextPrimary,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                row.Children.Add(titleRow);

                var wrapper = new Border
                {
                    Child = row,
                    Padding = new Thickness(
                        Dbl("ThemeSpacingLg", 16),
                        Dbl("ThemeSpacingSm", 8)),
                    Margin = new Thickness(
                        Dbl("ThemeSpacingSm", 8), 0),
                    CornerRadius = Radius("ThemeRadiusXs"),
                };

                {
                    var capturedId = id;
                    var capturedLinkType = linkType;
                    wrapper.PointerEntered += (s, _) =>
                    {
                        if (s is Border b) b.Background = HoverBrush;
                        // Prefetch view state on hover
                        PrefetchRequested?.Invoke(capturedLinkType, capturedId);
                    };
                    wrapper.PointerExited += (s, _) =>
                    {
                        if (s is Border b) b.Background = Brushes.Transparent;
                        // Cancel prefetch if mouse leaves before click
                        PrefetchCancelled?.Invoke(capturedLinkType, capturedId);
                    };
                    wrapper.PointerPressed += (_, _) =>
                    {
                        // Route all backlink clicks through InternalLinkActivated
                        // so cross-plugin navigation works automatically
                        if (InternalLinkActivated != null)
                        {
                            InternalLinkActivated.Invoke(capturedLinkType, capturedId);
                        }
                        else if (command != null)
                        {
                            SendCommand(command, JsonSerializer.Serialize(new { id = capturedId }));
                        }
                    };
                    wrapper.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                }

                panel.Children.Add(wrapper);
            }
        }

        return panel;
    }

    // ================================================================
    // Helpers
    // ================================================================

    /// Emoji keyword lookup for search. Maps emoji char → space-separated keywords.
    private static readonly Dictionary<string, string> EmojiKeywords = new()
    {
        // ---- Smileys ----
        ["😀"] = "grin smile happy", ["😃"] = "smile happy open", ["😄"] = "smile happy grin",
        ["😁"] = "grin teeth beam", ["😆"] = "laugh squint haha", ["😅"] = "sweat smile nervous",
        ["😂"] = "joy laugh cry tears", ["🤣"] = "rofl rolling laugh", ["😊"] = "blush smile warm",
        ["😇"] = "angel halo innocent", ["🙂"] = "slight smile", ["🙃"] = "upside down ironic",
        ["😉"] = "wink", ["😍"] = "love heart eyes", ["🤩"] = "star struck excited",
        ["😘"] = "kiss blow love", ["😗"] = "kiss", ["😙"] = "kiss smile", ["😚"] = "kiss blush",
        ["😋"] = "yum tongue delicious taste", ["🤔"] = "think hmm ponder", ["🤨"] = "raised eyebrow skeptic",
        ["😐"] = "neutral blank", ["😑"] = "expressionless", ["😶"] = "silent mute no mouth",
        ["🙄"] = "eyeroll annoyed", ["😬"] = "grimace awkward cringe", ["😮"] = "open mouth wow",
        ["😲"] = "astonished shocked wow", ["🤯"] = "mind blown explode head",
        ["😢"] = "cry sad tear", ["😭"] = "sob crying loud wail", ["😤"] = "angry huff steam triumph",
        ["😡"] = "angry rage mad red", ["🤬"] = "swear curse angry symbols",
        ["😱"] = "scream fear horror", ["😨"] = "fear scared pale", ["😰"] = "anxious worried sweat",
        ["🥵"] = "hot heat sweat", ["🥶"] = "cold freeze blue",
        ["🥱"] = "yawn tired sleepy bored", ["😴"] = "sleep zzz snore",
        ["😎"] = "cool sunglasses", ["🤓"] = "nerd glasses geek", ["🥸"] = "disguise glasses nose",
        ["🤠"] = "cowboy hat", ["🤑"] = "money rich dollar tongue",
        ["🤮"] = "vomit sick puke throw up", ["🤧"] = "sneeze sick tissue cold",
        ["😷"] = "mask sick medical face", ["🤒"] = "thermometer sick fever ill",
        ["🤕"] = "bandage hurt injured head", ["😵"] = "dizzy spiral crossed eyes",
        ["🥴"] = "woozy drunk tipsy", ["🤥"] = "lie pinocchio nose",
        ["😈"] = "devil evil smirk horns", ["👿"] = "devil angry evil imp",
        ["💀"] = "skull dead death skeleton", ["☠️"] = "skull crossbones death poison",
        ["💩"] = "poop poo pile", ["🤡"] = "clown circus", ["👻"] = "ghost boo halloween",
        ["👽"] = "alien ufo space", ["🤖"] = "robot bot machine", ["😺"] = "cat smile happy grin",
        // ---- People ----
        ["👍"] = "thumbs up like good yes agree ok", ["👎"] = "thumbs down dislike bad no disagree",
        ["👏"] = "clap applause bravo", ["🙏"] = "pray please hope thank hands",
        ["👋"] = "wave hello hi bye hand", ["✌️"] = "peace victory two fingers",
        ["🤞"] = "fingers crossed luck hope", ["🤟"] = "love you sign hand",
        ["🤘"] = "rock horn metal sign", ["👌"] = "ok perfect fine hand",
        ["✍️"] = "writing hand write", ["💪"] = "muscle strong flex bicep arm",
        ["🧠"] = "brain smart think mind", ["👀"] = "eyes look see watch stare",
        ["👁️"] = "eye look see single",
        ["👊"] = "fist bump punch", ["🤚"] = "raised back hand stop",
        ["🤛"] = "left fist bump", ["🤜"] = "right fist bump",
        ["🖐️"] = "hand raised fingers spread", ["☝️"] = "point up index one finger",
        ["👆"] = "point up backhand", ["👇"] = "point down backhand",
        ["👈"] = "point left backhand", ["👉"] = "point right backhand",
        ["🫵"] = "point index you", ["🫶"] = "heart hands love",
        ["🫱"] = "rightward hand", ["🫲"] = "leftward hand",
        ["🫳"] = "palm down hand", ["🫴"] = "palm up hand",
        ["🤲"] = "palms up together open hands", ["🤝"] = "handshake deal agree",
        ["🙌"] = "raise hands celebrate hooray", ["🙋"] = "raise hand volunteer hi",
        ["🙅"] = "no gesture stop cross", ["🙆"] = "ok gesture yes",
        ["🙇"] = "bow respect sorry", ["🤦"] = "facepalm doh",
        ["🤷"] = "shrug idk whatever dunno",
        ["👨"] = "man male guy", ["👩"] = "woman female lady",
        ["👧"] = "girl child", ["👦"] = "boy child", ["👶"] = "baby infant",
        ["🧑"] = "person adult", ["👴"] = "old man grandpa elder",
        ["👵"] = "old woman grandma elder", ["👱"] = "blond person hair",
        ["🧔"] = "beard man facial hair",
        // ---- Documents ----
        ["📄"] = "document page file paper", ["📝"] = "memo note write pad",
        ["📓"] = "notebook journal", ["📖"] = "book open read pages",
        ["📘"] = "blue book", ["📚"] = "books library study stack",
        ["📕"] = "red book closed", ["📗"] = "green book", ["📙"] = "orange book",
        ["📜"] = "scroll parchment old", ["📁"] = "folder directory file",
        ["📂"] = "folder open file", ["📋"] = "clipboard list paste",
        ["📌"] = "pin pushpin tack", ["📎"] = "paperclip attach clip",
        ["🔖"] = "bookmark tag label", ["📦"] = "package box ship delivery",
        ["📰"] = "newspaper news press", ["✏️"] = "pencil edit write draw",
        ["🖊️"] = "pen write ink", ["🖍️"] = "crayon draw color",
        ["🖌️"] = "paintbrush art draw paint", ["📲"] = "mobile phone arrow",
        ["🖨️"] = "printer print paper", ["💾"] = "floppy save disk retro",
        ["💿"] = "cd disc optical", ["📀"] = "dvd disc blu-ray",
        ["📼"] = "vhs tape cassette video retro", ["📹"] = "video camera record camcorder",
        ["📺"] = "tv television screen watch",
        // ---- Communication ----
        ["💬"] = "speech bubble chat message talk text", ["📧"] = "email mail envelope at",
        ["📨"] = "incoming mail envelope", ["📩"] = "envelope arrow down mail",
        ["📬"] = "mailbox full flag", ["📮"] = "postbox mail send",
        ["🔔"] = "bell notification alert ring", ["📱"] = "phone mobile cell smartphone",
        ["💻"] = "laptop computer mac pc", ["🔗"] = "link chain url hyperlink",
        ["📭"] = "mailbox empty", ["📪"] = "mailbox closed empty",
        ["📫"] = "mailbox closed flag", ["📤"] = "outbox tray send upload",
        ["📥"] = "inbox tray receive download", ["📯"] = "postal horn",
        ["📣"] = "megaphone announce shout", ["📢"] = "loudspeaker announce public",
        ["🔕"] = "bell mute silent no notification", ["📞"] = "telephone receiver call phone",
        ["📟"] = "pager beeper", ["📠"] = "fax machine",
        ["📻"] = "radio broadcast fm am", ["🌐"] = "globe web internet world",
        ["🔊"] = "speaker loud volume sound max", ["🔈"] = "speaker low volume quiet",
        ["🔉"] = "speaker medium volume", ["🔇"] = "mute silent speaker off",
        ["🗣️"] = "speaking head talk voice", ["💭"] = "thought bubble think cloud",
        // ---- Work ----
        ["💼"] = "briefcase business work office bag", ["🛠️"] = "tools hammer wrench fix build",
        ["⚙️"] = "gear settings cog config mechanical", ["🔍"] = "search magnify find lens left",
        ["🔎"] = "search magnify find lens right", ["📈"] = "chart up growth trend increase",
        ["📉"] = "chart down decline decrease drop", ["📊"] = "bar chart graph stats data",
        ["📅"] = "calendar date schedule day", ["📆"] = "calendar tear off date",
        ["⏰"] = "alarm clock time wake morning", ["⏳"] = "hourglass timer wait sand flowing",
        ["🕒"] = "clock three time", ["💰"] = "money bag dollar rich wealth",
        ["💳"] = "credit card payment swipe", ["💵"] = "dollar bill money cash us",
        ["💴"] = "yen money japan", ["💶"] = "euro money europe", ["💷"] = "pound money uk sterling",
        ["💸"] = "money fly wings spend waste",
        ["🔧"] = "wrench tool fix repair spanner", ["🔨"] = "hammer tool build hit nail",
        ["🔩"] = "nut bolt screw hardware", ["🛡️"] = "shield security protect defense",
        ["🔬"] = "microscope science lab zoom", ["🔭"] = "telescope space astronomy look",
        ["💽"] = "minidisc disk", ["🖥️"] = "desktop computer screen monitor",
        ["⌨️"] = "keyboard type input keys", ["🖱️"] = "mouse click cursor pointer",
        ["🖲️"] = "trackball input device", ["🔌"] = "plug electric power outlet",
        ["🔋"] = "battery power charge energy", ["🪜"] = "ladder climb steps",
        ["🧲"] = "magnet attract pull",
        ["🏦"] = "bank money building finance", ["🏨"] = "hotel building travel sleep",
        ["🏪"] = "store shop convenience", ["🏬"] = "department store mall shop",
        ["🏩"] = "love hotel heart building",
        // ---- Symbols ----
        ["💡"] = "bulb idea light lamp bright", ["🔥"] = "fire hot flame lit burn",
        ["⭐"] = "star favorite yellow", ["🎯"] = "target goal bullseye aim dart",
        ["🚀"] = "rocket launch ship fast space", ["⚡"] = "lightning zap electric bolt power",
        ["🌟"] = "glowing star shine sparkle", ["✨"] = "sparkles magic twinkle shine",
        ["💫"] = "dizzy star shooting", ["💥"] = "boom explosion bang collision",
        ["✅"] = "check yes done complete green", ["❌"] = "cross no wrong cancel delete red",
        ["⚠️"] = "warning caution alert triangle", ["🚧"] = "construction wip barrier",
        ["🔒"] = "lock locked secure closed", ["🔓"] = "unlock unlocked open",
        ["🔴"] = "red circle dot", ["🔵"] = "blue circle dot",
        ["🟢"] = "green circle dot", ["🟡"] = "yellow circle dot",
        ["🟠"] = "orange circle dot", ["🟣"] = "purple circle dot",
        ["⬛"] = "black square large", ["⬜"] = "white square large",
        ["ℹ️"] = "info information letter", ["❓"] = "question mark red",
        ["❗"] = "exclamation important red bang", ["♻️"] = "recycle green environment",
        ["🚫"] = "prohibited forbidden ban no stop", ["🔄"] = "refresh reload sync arrows rotate",
        ["🏳️"] = "white flag surrender", ["🏴"] = "black flag pirate",
        ["🏳️‍🌈"] = "rainbow flag pride lgbtq", ["🚩"] = "red flag warning triangular",
        ["♠️"] = "spade card suit", ["♥️"] = "heart card suit red",
        ["♦️"] = "diamond card suit red", ["♣️"] = "club card suit",
        ["♟️"] = "chess pawn piece", ["♾️"] = "infinity forever loop",
        ["☢️"] = "radioactive nuclear hazard", ["☣️"] = "biohazard toxic danger",
        ["⚛️"] = "atom science physics", ["☯️"] = "yin yang balance harmony",
        ["☮️"] = "peace symbol sign", ["✝️"] = "cross christian latin",
        ["✡️"] = "star david jewish", ["☸️"] = "wheel dharma buddhist",
        ["⚜️"] = "fleur de lis scout", ["🔱"] = "trident emblem sea",
        ["©️"] = "copyright symbol c", ["®️"] = "registered trademark r",
        ["™️"] = "trademark symbol tm", ["➕"] = "plus add more positive",
        ["➖"] = "minus subtract less negative", ["➗"] = "divide division math",
        ["✖️"] = "multiply times cross math", ["〰️"] = "wavy dash squiggle",
        ["➰"] = "curly loop", ["➿"] = "double curly loop",
        ["✔️"] = "check mark done heavy", ["☑️"] = "checkbox checked ballot",
        ["▪️"] = "black small square", ["▫️"] = "white small square",
        ["◾"] = "black medium small square", ["◽"] = "white medium small square",
        ["◼️"] = "black medium square", ["◻️"] = "white medium square",
        ["⭕"] = "circle red hollow", ["🔘"] = "radio button circle",
        // ---- Plants & Nature ----
        ["🌞"] = "sun face bright warm day", ["🌙"] = "moon crescent night",
        ["☁️"] = "cloud weather overcast sky", ["⛈️"] = "thunder storm cloud rain lightning",
        ["🌈"] = "rainbow arc colors sky", ["🌿"] = "herb plant leaf green nature",
        ["🌱"] = "seedling sprout grow new plant", ["🌲"] = "evergreen tree pine conifer",
        ["🌺"] = "hibiscus flower tropical", ["🌻"] = "sunflower flower yellow",
        ["🌼"] = "blossom flower daisy", ["🌷"] = "tulip flower spring",
        ["🌸"] = "cherry blossom flower pink sakura", ["🌹"] = "rose flower red love",
        ["🍁"] = "maple leaf fall autumn canada", ["🍃"] = "leaf wind blow flutter",
        ["🌵"] = "cactus desert prickly", ["🌴"] = "palm tree tropical island",
        ["🌳"] = "tree deciduous green round", ["🐝"] = "bee honey buzz insect",
        ["🍀"] = "four leaf clover luck lucky irish", ["🌾"] = "rice plant grain harvest wheat",
        ["🌽"] = "corn maize cob ear", ["🍄"] = "mushroom fungus toadstool",
        ["🪴"] = "potted plant houseplant indoor", ["🪷"] = "lotus flower water",
        ["🪹"] = "nest empty bird", ["🪵"] = "wood log timber",
        ["🌶️"] = "hot pepper chili spicy red", ["🥀"] = "wilted flower rose dead",
        ["❄️"] = "snowflake cold winter ice frozen", ["🌪️"] = "tornado twister cyclone storm",
        ["🌊"] = "wave ocean water sea surf", ["🌌"] = "milky way galaxy space night stars",
        ["☄️"] = "comet meteor shooting star space",
        ["🌕"] = "full moon bright night", ["🌑"] = "new moon dark night",
        ["🌜"] = "moon face last quarter", ["🌛"] = "moon face first quarter",
        ["⛅"] = "sun cloud partly cloudy",
        // ---- Animals ----
        ["🐶"] = "dog puppy woof pet", ["🐱"] = "cat kitten meow pet",
        ["🦊"] = "fox red cunning", ["🐻"] = "bear brown grizzly",
        ["🐼"] = "panda bear bamboo black white", ["🦁"] = "lion king mane",
        ["🐯"] = "tiger stripes cat", ["🦄"] = "unicorn magic horse horn",
        ["🐢"] = "turtle tortoise slow shell", ["🦉"] = "owl wise night hoot bird",
        ["🦋"] = "butterfly insect wings pretty", ["🐛"] = "bug caterpillar insect worm",
        ["🐠"] = "fish tropical colorful", ["🐳"] = "whale spouting ocean sea",
        ["🐧"] = "penguin bird cold arctic", ["🐵"] = "monkey face primate",
        ["🐒"] = "monkey primate ape climb", ["🐷"] = "pig face oink pink",
        ["🐸"] = "frog toad ribbit green", ["🐹"] = "hamster face pet rodent",
        ["🐺"] = "wolf face howl", ["🐭"] = "mouse face small rodent",
        ["🐮"] = "cow face moo bovine", ["🐴"] = "horse face equine neigh",
        ["🐔"] = "chicken hen rooster bird", ["🦆"] = "duck bird quack water",
        ["🦅"] = "eagle bird raptor freedom", ["🦜"] = "parrot bird colorful talk",
        ["🐦"] = "bird tweet chirp", ["🐥"] = "chick baby bird hatched",
        ["🐍"] = "snake reptile slither", ["🐊"] = "crocodile alligator reptile",
        ["🦈"] = "shark fish jaws ocean", ["🐙"] = "octopus tentacle sea",
        ["🦑"] = "squid tentacle sea", ["🐚"] = "shell spiral sea beach",
        ["🐜"] = "ant insect colony small", ["🦞"] = "lobster claw seafood",
        ["🦀"] = "crab pinch seafood", ["🐞"] = "ladybug beetle insect spots",
        ["🦚"] = "peacock bird feathers beautiful", ["🦛"] = "hippo hippopotamus",
        ["🦒"] = "giraffe tall spots neck", ["🦘"] = "kangaroo hop pouch australia",
        ["🦥"] = "sloth slow lazy tree", ["🦦"] = "otter swim playful",
        ["🦨"] = "skunk smell stink spray", ["🦭"] = "seal sea lion",
        ["🦮"] = "guide dog service", ["🪲"] = "beetle bug insect",
        // ---- Food ----
        ["☕"] = "coffee hot drink cafe cup", ["🍵"] = "tea hot drink green cup",
        ["🍺"] = "beer mug drink ale pint", ["🍷"] = "wine glass drink red",
        ["🥤"] = "cup straw drink soda juice",
        ["🍕"] = "pizza slice food cheese", ["🍔"] = "burger hamburger food beef",
        ["🍪"] = "cookie biscuit sweet treat", ["🎂"] = "birthday cake celebration party candles",
        ["🍰"] = "shortcake cake dessert slice",
        ["🍎"] = "apple red fruit", ["🍓"] = "strawberry fruit berry red",
        ["🍉"] = "watermelon fruit summer slice", ["🥑"] = "avocado fruit guacamole green",
        ["🍋"] = "lemon citrus sour yellow", ["🍍"] = "pineapple fruit tropical",
        ["🍌"] = "banana fruit yellow", ["🍐"] = "pear fruit green",
        ["🍑"] = "peach fruit fuzzy", ["🍒"] = "cherry cherries fruit red",
        ["🥝"] = "kiwi fruit green", ["🥥"] = "coconut tropical palm",
        ["🍇"] = "grapes fruit vine purple", ["🍈"] = "melon fruit cantaloupe",
        ["🫐"] = "blueberry berry fruit blue",
        ["🍞"] = "bread loaf wheat", ["🥐"] = "croissant french pastry",
        ["🥖"] = "baguette french bread long", ["🫓"] = "flatbread pita naan",
        ["🥨"] = "pretzel twisted snack",
        ["🍳"] = "egg frying cooking pan", ["🥚"] = "egg chicken oval",
        ["🧀"] = "cheese wedge cheddar", ["🍖"] = "meat bone drumstick",
        ["🍗"] = "poultry leg chicken turkey drumstick",
        ["🌭"] = "hot dog sausage frank", ["🌮"] = "taco mexican food shell",
        ["🌯"] = "burrito wrap mexican", ["🥗"] = "salad green healthy bowl",
        ["🍝"] = "spaghetti pasta noodle italian",
        ["🍜"] = "ramen noodle soup bowl", ["🍣"] = "sushi fish japanese rice",
        ["🍛"] = "curry rice indian food", ["🍚"] = "rice bowl white cooked",
        ["🍙"] = "rice ball onigiri japanese",
        ["🍸"] = "cocktail martini drink", ["🍹"] = "tropical drink umbrella",
        ["🍻"] = "beer mugs cheers toast clink", ["🍼"] = "baby bottle milk",
        ["🧃"] = "juice box drink",
        ["🍧"] = "shaved ice dessert", ["🍨"] = "ice cream sundae dessert",
        ["🍩"] = "donut doughnut sweet", ["🍦"] = "ice cream cone soft serve",
        ["🧁"] = "cupcake muffin sweet frosting",
        // ---- Travel ----
        ["🏠"] = "house home building", ["🏢"] = "office building work",
        ["🏫"] = "school building education", ["🏥"] = "hospital building medical health",
        ["🏭"] = "factory building industrial",
        ["🌍"] = "globe earth europe africa world", ["🌎"] = "globe earth americas world",
        ["🌏"] = "globe earth asia australia world", ["✈️"] = "airplane plane fly travel flight",
        ["🚗"] = "car automobile vehicle drive red",
        ["🚂"] = "train locomotive steam railway", ["🚢"] = "ship cruise boat ocean liner",
        ["🏔️"] = "mountain snow peak", ["🏖️"] = "beach umbrella sand vacation",
        ["🏙️"] = "city skyline buildings urban",
        ["🚕"] = "taxi cab yellow car ride", ["🚌"] = "bus public transport",
        ["🚎"] = "trolleybus electric", ["🚐"] = "minibus van",
        ["🚑"] = "ambulance medical emergency", ["🚒"] = "fire truck engine red",
        ["🚓"] = "police car cop", ["🚔"] = "police car oncoming",
        ["🚖"] = "taxi oncoming", ["🚙"] = "suv sport car blue",
        ["🛵"] = "scooter motor vespa", ["🛶"] = "canoe kayak paddle boat",
        ["🚲"] = "bicycle bike cycle ride pedal", ["🛴"] = "kick scooter",
        ["🛹"] = "skateboard skate", ["🚁"] = "helicopter chopper fly",
        ["🚀"] = "rocket launch ship fast space", ["🛸"] = "ufo flying saucer alien",
        ["⛵"] = "sailboat boat wind sail yacht", ["🚤"] = "speedboat motorboat fast boat",
        ["🏗️"] = "construction crane building", ["🏚️"] = "derelict house abandoned",
        ["🏛️"] = "classical building columns roman", ["⛺"] = "tent camping outdoor",
        ["🏝️"] = "desert island tropical palm", ["🗼"] = "tokyo tower landmark",
        ["🗽"] = "statue liberty freedom new york", ["🗾"] = "japan map silhouette",
        ["🏯"] = "japanese castle", ["🏰"] = "castle european medieval",
        // ---- Sports ----
        ["⚽"] = "soccer football ball sport kick", ["🏀"] = "basketball ball sport hoop",
        ["🏈"] = "football american ball sport", ["⚾"] = "baseball ball sport",
        ["🥎"] = "softball ball sport", ["🎾"] = "tennis ball racket sport",
        ["🏐"] = "volleyball ball sport", ["🏉"] = "rugby ball sport",
        ["🥏"] = "frisbee disc flying", ["🎱"] = "billiards pool eight ball",
        ["🏓"] = "ping pong table tennis paddle", ["🏸"] = "badminton shuttlecock racket",
        ["🏒"] = "ice hockey stick puck", ["🏑"] = "field hockey stick",
        ["🥍"] = "lacrosse stick ball", ["⛳"] = "golf flag hole green",
        ["🏹"] = "bow arrow archery", ["🎣"] = "fishing pole rod hook",
        ["🤿"] = "diving mask snorkel scuba", ["🥊"] = "boxing glove fight punch",
        ["🥋"] = "martial arts karate judo", ["🏿"] = "skin tone dark",
        ["⛷️"] = "ski skiing snow winter", ["🏂"] = "snowboard winter sport",
        ["🧷"] = "safety pin", ["🏄"] = "surf surfing wave board",
        ["🏊"] = "swim swimming pool water", ["🚴"] = "bike cycling bicycle ride",
        ["🚵"] = "mountain bike cycling", ["🤸"] = "cartwheel gymnastics",
        ["🤺"] = "fencing sword", ["🤼"] = "wrestling sport",
        ["🤽"] = "water polo sport", ["🤾"] = "handball sport throw",
        ["🏋️"] = "weight lifting gym strong", ["🧘"] = "yoga meditation lotus zen",
        ["🧗"] = "climbing rock wall", ["🏇"] = "horse racing jockey",
        // ---- Fun ----
        ["🎉"] = "party confetti celebration tada", ["🎊"] = "confetti ball celebration",
        ["🏆"] = "trophy winner cup champion gold", ["🎖️"] = "medal military honor",
        ["🥇"] = "gold medal first place winner", ["🥈"] = "silver medal second place",
        ["🥉"] = "bronze medal third place", ["🎮"] = "game controller video gaming",
        ["🎲"] = "dice game roll chance random", ["🎭"] = "theater drama mask comedy tragedy",
        ["🎨"] = "art palette paint color draw", ["🎵"] = "music note sound",
        ["🎶"] = "music notes sound melody song", ["🎬"] = "movie film clapper cinema",
        ["📷"] = "camera photo picture", ["📸"] = "camera flash photo snapshot",
        ["🎥"] = "movie camera film cinema", ["🎤"] = "microphone sing karaoke voice",
        ["🎹"] = "piano keyboard music keys", ["🎸"] = "guitar rock music string",
        ["🎺"] = "trumpet brass music horn", ["🎻"] = "violin fiddle music string",
        ["🪕"] = "banjo string music", ["🪘"] = "drum percussion beat",
        ["🎼"] = "music score sheet notes", ["🎠"] = "carousel horse merry go round",
        ["🎡"] = "ferris wheel carnival ride", ["🎢"] = "roller coaster ride thrill",
        ["🎪"] = "circus tent carnival", ["🎫"] = "ticket admit entry",
        ["🎀"] = "ribbon bow gift pink", ["🎁"] = "gift present wrapped box birthday",
        ["🎃"] = "jack o lantern pumpkin halloween", ["🎄"] = "christmas tree holiday",
        ["🎆"] = "fireworks celebration night", ["🎇"] = "sparkler firework celebrate",
        ["🎈"] = "balloon party celebrate", ["🧨"] = "firecracker dynamite explosive",
        ["🪅"] = "pinata party candy", ["🎐"] = "wind chime bell japanese",
        // ---- Hearts ----
        ["❤️"] = "red heart love", ["🩷"] = "pink heart love light",
        ["💜"] = "purple heart love", ["💙"] = "blue heart love",
        ["💚"] = "green heart love eco", ["🧡"] = "orange heart love",
        ["💛"] = "yellow heart love bright", ["🤍"] = "white heart love pure",
        ["🖤"] = "black heart love dark", ["🤎"] = "brown heart love",
        ["💖"] = "sparkling heart love pink shine", ["💗"] = "growing heart love pink pulse",
        ["💓"] = "beating heart love pulse alive", ["💞"] = "revolving hearts love orbit",
        ["💕"] = "two hearts love couple", ["❣️"] = "heart exclamation love",
        ["💝"] = "heart ribbon gift love", ["💘"] = "heart arrow cupid love",
        ["💔"] = "broken heart sad love", ["❤️‍🔥"] = "heart fire love passion burn",
        ["❤️‍🩹"] = "mending heart heal love", ["🩹"] = "bandage adhesive heal fix",
        ["💋"] = "kiss lips love smooch", ["💌"] = "love letter envelope heart",
        ["💍"] = "ring diamond engagement wedding", ["💎"] = "gem diamond jewel precious",
        ["💐"] = "bouquet flowers gift", ["🌹"] = "rose flower love red",
        ["🌺"] = "hibiscus flower tropical",
    };

    // -1 = show all categories, >= 0 = filter to that category index
    private static readonly (string Icon, string Name, string[] Emojis)[] EmojiCategories =
    [
        ("\uD83D\uDE00", "Smileys", [
            "\uD83D\uDE00", "\uD83D\uDE03", "\uD83D\uDE04", "\uD83D\uDE01", "\uD83D\uDE06", // 😀😃😄😁😆
            "\uD83D\uDE05", "\uD83D\uDE02", "\uD83E\uDD23", "\uD83D\uDE0A", "\uD83D\uDE07", // 😅😂🤣😊😇
            "\uD83D\uDE42", "\uD83D\uDE43", "\uD83D\uDE09", "\uD83D\uDE0D", "\uD83E\uDD29", // 🙂🙃😉😍🤩
            "\uD83D\uDE18", "\uD83D\uDE17", "\uD83D\uDE19", "\uD83D\uDE1A", "\uD83D\uDE0B", // 😘😗😙😚😋
            "\uD83E\uDD14", "\uD83E\uDD28", "\uD83D\uDE10", "\uD83D\uDE11", "\uD83D\uDE36", // 🤔🤨😐😑😶
            "\uD83D\uDE44", "\uD83D\uDE2C", "\uD83D\uDE2E", "\uD83D\uDE32", "\uD83E\uDD2F", // 🙄😬😮😲🤯
            "\uD83D\uDE22", "\uD83D\uDE2D", "\uD83D\uDE24", "\uD83D\uDE21", "\uD83E\uDD2C", // 😢😭😤😡🤬
            "\uD83D\uDE31", "\uD83D\uDE28", "\uD83D\uDE30", "\uD83E\uDD75", "\uD83E\uDD76", // 😱😨😰🥵🥶
            "\uD83E\uDD71", "\uD83D\uDE34", "\uD83D\uDE0E", "\uD83E\uDD13", "\uD83E\uDD78", // 🥱😴😎🤓🥸
            "\uD83E\uDD20", "\uD83E\uDD11", "\uD83E\uDD2E", "\uD83E\uDD27", "\uD83D\uDE37", // 🤠🤑🤮🤧😷
            "\uD83E\uDD12", "\uD83E\uDD15", "\uD83D\uDE35", "\uD83E\uDD74", "\uD83E\uDD25", // 🤒🤕😵🥴🤥
            "\uD83D\uDE08", "\uD83D\uDC7F", "\uD83D\uDC80", "\u2620\uFE0F", "\uD83D\uDCA9", // 😈👿💀☠️💩
            "\uD83E\uDD21", "\uD83D\uDC7B", "\uD83D\uDC7D", "\uD83E\uDD16", "\uD83D\uDE3A", // 🤡👻👽🤖😺
        ]),
        ("\uD83D\uDC4B", "People", [
            "\uD83D\uDC4D", "\uD83D\uDC4E", "\uD83D\uDC4F", "\uD83D\uDE4F", "\uD83D\uDC4B", // 👍👎👏🙏👋
            "\u270C\uFE0F", "\uD83E\uDD1E", "\uD83E\uDD1F", "\uD83E\uDD18", "\uD83D\uDC4C", // ✌️🤞🤟🤘👌
            "\u270D\uFE0F", "\uD83D\uDCAA", "\uD83E\uDDE0", "\uD83D\uDC40", "\uD83D\uDC41\uFE0F", // ✍️💪🧠👀👁️
            "\uD83D\uDC4A", "\uD83E\uDD1A", "\uD83E\uDD1B", "\uD83E\uDD1C", "\uD83D\uDD90\uFE0F", // 👊🤚🤛🤜🖐️
            "\u261D\uFE0F", "\uD83D\uDC46", "\uD83D\uDC47", "\uD83D\uDC48", "\uD83D\uDC49", // ☝️👆👇👈👉
            "\uD83E\uDEF5", "\uD83E\uDEF6", "\uD83E\uDEF1", "\uD83E\uDEF2", "\uD83E\uDEF3", // 🫵🫶🫱🫲🫳
            "\uD83E\uDEF4", "\uD83E\uDD32", "\uD83E\uDD1D", "\uD83D\uDE4C", "\uD83D\uDE4B", // 🫴🤲🤝🙌🙋
            "\uD83D\uDE45", "\uD83D\uDE46", "\uD83D\uDE47", "\uD83E\uDD26", "\uD83E\uDD37", // 🙅🙆🙇🤦🤷
            "\uD83D\uDC68", "\uD83D\uDC69", "\uD83D\uDC67", "\uD83D\uDC66", "\uD83D\uDC76", // 👨👩👧👦👶
            "\uD83E\uDDD1", "\uD83D\uDC74", "\uD83D\uDC75", "\uD83D\uDC71", "\uD83E\uDDD4", // 🧑👴👵👱🧔
        ]),
        ("\uD83D\uDCC4", "Documents", [
            "\uD83D\uDCC4", "\uD83D\uDCDD", "\uD83D\uDCD3", "\uD83D\uDCD6", "\uD83D\uDCD8", // 📄📝📓📖📘
            "\uD83D\uDCDA", "\uD83D\uDCD5", "\uD83D\uDCD7", "\uD83D\uDCD9", "\uD83D\uDCDC", // 📚📕📗📙📜
            "\uD83D\uDCC1", "\uD83D\uDCC2", "\uD83D\uDCCB", "\uD83D\uDCCC", "\uD83D\uDCCE", // 📁📂📋📌📎
            "\uD83D\uDD16", "\uD83D\uDCE6", "\uD83D\uDCF0", "\u270F\uFE0F", "\uD83D\uDD8A\uFE0F", // 🔖📦📰✏️🖊️
            "\uD83D\uDD8D\uFE0F", "\uD83D\uDD8C\uFE0F", "\uD83D\uDCF2", "\uD83D\uDDA8\uFE0F", "\uD83D\uDCBE", // 🖍️🖌️📲🖨️💾
            "\uD83D\uDCBF", "\uD83D\uDCC0", "\uD83D\uDCFC", "\uD83D\uDCF9", "\uD83D\uDCFA", // 💿📀📼📹📺
        ]),
        ("\uD83D\uDCAC", "Communication", [
            "\uD83D\uDCAC", "\uD83D\uDCE7", "\uD83D\uDCE8", "\uD83D\uDCE9", "\uD83D\uDCEC", // 💬📧📨📩📬
            "\uD83D\uDCEE", "\uD83D\uDD14", "\uD83D\uDCF1", "\uD83D\uDCBB", "\uD83D\uDD17", // 📮🔔📱💻🔗
            "\uD83D\uDCED", "\uD83D\uDCEA", "\uD83D\uDCEB", "\uD83D\uDCE4", "\uD83D\uDCE5", // 📭📪📫📤📥
            "\uD83D\uDCEF", "\uD83D\uDCE3", "\uD83D\uDCE2", "\uD83D\uDD15", "\uD83D\uDCDE", // 📯📣📢🔕📞
            "\uD83D\uDCDF", "\uD83D\uDCE0", "\uD83D\uDCFB", "\uD83C\uDF10", "\uD83D\uDD0A", // 📟📠📻🌐🔊
            "\uD83D\uDD08", "\uD83D\uDD09", "\uD83D\uDD07", "\uD83D\uDDE3\uFE0F", "\uD83D\uDCAD", // 🔈🔉🔇🗣️💭
        ]),
        ("\uD83D\uDCBC", "Work", [
            "\uD83D\uDCBC", "\uD83D\uDEE0\uFE0F", "\u2699\uFE0F", "\uD83D\uDD0D", "\uD83D\uDD0E", // 💼🛠️⚙️🔍🔎
            "\uD83D\uDCC8", "\uD83D\uDCC9", "\uD83D\uDCCA", "\uD83D\uDCC5", "\uD83D\uDCC6", // 📈📉📊📅📆
            "\u23F0", "\u23F3", "\uD83D\uDD52", "\uD83D\uDCB0", "\uD83D\uDCB3",              // ⏰⏳🕒💰💳
            "\uD83D\uDCB5", "\uD83D\uDCB4", "\uD83D\uDCB6", "\uD83D\uDCB7", "\uD83D\uDCB8", // 💵💴💶💷💸
            "\uD83D\uDD27", "\uD83D\uDD28", "\uD83D\uDD29", "\uD83D\uDEE1\uFE0F", "\uD83D\uDD2C", // 🔧🔨🔩🛡️🔬
            "\uD83D\uDD2D", "\uD83D\uDCBD", "\uD83D\uDDA5\uFE0F", "\u2328\uFE0F", "\uD83D\uDDB1\uFE0F", // 🔭💽🖥️⌨️🖱️
            "\uD83D\uDDB2\uFE0F", "\uD83D\uDD0C", "\uD83D\uDD0B", "\uD83E\uDE9C", "\uD83E\uDDF2", // 🖲️🔌🔋🪜🧲
            "\uD83C\uDFE6", "\uD83C\uDFE8", "\uD83C\uDFEA", "\uD83C\uDFEC", "\uD83C\uDFE9", // 🏦🏨🏪🏬🏩
        ]),
        ("\uD83D\uDCA1", "Symbols", [
            "\uD83D\uDCA1", "\uD83D\uDD25", "\u2B50", "\uD83C\uDFAF", "\uD83D\uDE80",       // 💡🔥⭐🎯🚀
            "\u26A1", "\uD83C\uDF1F", "\u2728", "\uD83D\uDCAB", "\uD83D\uDCA5",              // ⚡🌟✨💫💥
            "\u2705", "\u274C", "\u26A0\uFE0F", "\uD83D\uDEA7", "\uD83D\uDD12",              // ✅❌⚠️🚧🔒
            "\uD83D\uDD13", "\uD83D\uDD34", "\uD83D\uDD35", "\uD83D\uDFE2", "\uD83D\uDFE1", // 🔓🔴🔵🟢🟡
            "\uD83D\uDFE0", "\uD83D\uDFE3", "\u2B1B", "\u2B1C", "\u2139\uFE0F",              // 🟠🟣⬛⬜ℹ️
            "\u2753", "\u2757", "\u267B\uFE0F", "\uD83D\uDEAB", "\uD83D\uDD04",              // ❓❗♻️🚫🔄
            "\uD83C\uDFF3\uFE0F", "\uD83C\uDFF4", "\uD83C\uDFF3\uFE0F\u200D\uD83C\uDF08", "\uD83D\uDEA9", "\u2660\uFE0F", // 🏳️🏴🏳️‍🌈🚩♠️
            "\u2665\uFE0F", "\u2666\uFE0F", "\u2663\uFE0F", "\u265F\uFE0F", "\u267E\uFE0F", // ♥️♦️♣️♟️♾️
            "\u2622\uFE0F", "\u2623\uFE0F", "\u269B\uFE0F", "\u262F\uFE0F", "\u262E\uFE0F", // ☢️☣️⚛️☯️☮️
            "\u271D\uFE0F", "\u2721\uFE0F", "\u2638\uFE0F", "\u269C\uFE0F", "\uD83D\uDD31", // ✝️✡️☸️⚜️🔱
            "\u00A9\uFE0F", "\u00AE\uFE0F", "\u2122\uFE0F", "\u2795", "\u2796",              // ©️®️™️➕➖
            "\u2797", "\u2716\uFE0F", "\u3030\uFE0F", "\u27B0", "\u27BF",                     // ➗✖️〰️➰➿
            "\u2714\uFE0F", "\u2611\uFE0F", "\u25AA\uFE0F", "\u25AB\uFE0F", "\u25FE",        // ✔️☑️▪️▫️◾
            "\u25FD", "\u25FC\uFE0F", "\u25FB\uFE0F", "\u2B55", "\uD83D\uDD18",              // ◽◼️◻️⭕🔘
        ]),
        ("\uD83C\uDF3F", "Plants", [
            "\uD83C\uDF1E", "\uD83C\uDF19", "\u2601\uFE0F", "\u26C8\uFE0F", "\uD83C\uDF08", // 🌞🌙☁️⛈️🌈
            "\uD83C\uDF3F", "\uD83C\uDF31", "\uD83C\uDF32", "\uD83C\uDF3A", "\uD83C\uDF3B", // 🌿🌱🌲🌺🌻
            "\uD83C\uDF3C", "\uD83C\uDF37", "\uD83C\uDF38", "\uD83C\uDF39", "\uD83C\uDF41", // 🌼🌷🌸🌹🍁
            "\uD83C\uDF43", "\uD83C\uDF35", "\uD83C\uDF34", "\uD83C\uDF33", "\uD83D\uDC1D", // 🍃🌵🌴🌳🐝
            "\uD83C\uDF40", "\uD83C\uDF3E", "\uD83C\uDF3D", "\uD83C\uDF44", "\uD83E\uDEB4", // 🍀🌾🌽🍄🪴
            "\uD83E\uDEB7", "\uD83E\uDEB9", "\uD83E\uDEB5", "\uD83C\uDF36\uFE0F", "\uD83E\uDD40", // 🪷🪹🪵🌶️🥀
            "\u2744\uFE0F", "\uD83C\uDF2A\uFE0F", "\uD83C\uDF0A", "\uD83C\uDF0C", "\u2604\uFE0F", // ❄️🌪️🌊🌌☄️
            "\uD83C\uDF15", "\uD83C\uDF11", "\uD83C\uDF1C", "\uD83C\uDF1B", "\u26C5",        // 🌕🌑🌜🌛⛅
        ]),
        ("\uD83D\uDC36", "Animals", [
            "\uD83D\uDC36", "\uD83D\uDC31", "\uD83E\uDD8A", "\uD83D\uDC3B", "\uD83D\uDC3C", // 🐶🐱🦊🐻🐼
            "\uD83E\uDD81", "\uD83D\uDC2F", "\uD83E\uDD84", "\uD83D\uDC22", "\uD83E\uDD89", // 🦁🐯🦄🐢🦉
            "\uD83E\uDD8B", "\uD83D\uDC1B", "\uD83D\uDC20", "\uD83D\uDC33", "\uD83D\uDC27", // 🦋🐛🐠🐳🐧
            "\uD83D\uDC35", "\uD83D\uDC12", "\uD83D\uDC37", "\uD83D\uDC38", "\uD83D\uDC39", // 🐵🐒🐷🐸🐹
            "\uD83D\uDC3A", "\uD83D\uDC2D", "\uD83D\uDC2E", "\uD83D\uDC34", "\uD83D\uDC14", // 🐺🐭🐮🐴🐔
            "\uD83E\uDD86", "\uD83E\uDD85", "\uD83E\uDD9C", "\uD83D\uDC26", "\uD83D\uDC25", // 🦆🦅🦜🐦🐥
            "\uD83D\uDC0D", "\uD83D\uDC0A", "\uD83E\uDD88", "\uD83D\uDC19", "\uD83E\uDD91", // 🐍🐊🦈🐙🦑
            "\uD83D\uDC1A", "\uD83D\uDC1C", "\uD83E\uDD9E", "\uD83E\uDD80", "\uD83D\uDC1E", // 🐚🐜🦞🦀🐞
            "\uD83E\uDD9A", "\uD83E\uDD9B", "\uD83E\uDD92", "\uD83E\uDD98", "\uD83E\uDDA5", // 🦚🦛🦒🦘🦥
            "\uD83E\uDDA6", "\uD83E\uDDA8", "\uD83E\uDDAD", "\uD83E\uDDAE", "\uD83E\uDEB2", // 🦦🦨🦭🦮🪲
        ]),
        ("\uD83C\uDF55", "Food", [
            "\u2615", "\uD83C\uDF75", "\uD83C\uDF7A", "\uD83C\uDF77", "\uD83E\uDD64",        // ☕🍵🍺🍷🥤
            "\uD83C\uDF55", "\uD83C\uDF54", "\uD83C\uDF6A", "\uD83C\uDF82", "\uD83C\uDF70", // 🍕🍔🍪🎂🍰
            "\uD83C\uDF4E", "\uD83C\uDF53", "\uD83C\uDF49", "\uD83E\uDD51", "\uD83C\uDF4B", // 🍎🍓🍉🥑🍋
            "\uD83C\uDF4D", "\uD83C\uDF4C", "\uD83C\uDF50", "\uD83C\uDF51", "\uD83C\uDF52", // 🍍🍌🍐🍑🍒
            "\uD83E\uDD5D", "\uD83E\uDD65", "\uD83C\uDF47", "\uD83C\uDF48", "\uD83E\uDED0", // 🥝🥥🍇🍈🫐
            "\uD83C\uDF5E", "\uD83E\uDD50", "\uD83E\uDD56", "\uD83E\uDED3", "\uD83E\uDD68", // 🍞🥐🥖🫓🥨
            "\uD83C\uDF73", "\uD83E\uDD5A", "\uD83E\uDDC0", "\uD83C\uDF56", "\uD83C\uDF57", // 🍳🥚🧀🍖🍗
            "\uD83C\uDF2D", "\uD83C\uDF2E", "\uD83C\uDF2F", "\uD83E\uDD57", "\uD83C\uDF5D", // 🌭🌮🌯🥗🍝
            "\uD83C\uDF5C", "\uD83C\uDF63", "\uD83C\uDF5B", "\uD83C\uDF5A", "\uD83C\uDF59", // 🍜🍣🍛🍚🍙
            "\uD83C\uDF78", "\uD83C\uDF79", "\uD83C\uDF7B", "\uD83C\uDF7C", "\uD83E\uDDC3", // 🍸🍹🍻🍼🧃
            "\uD83C\uDF67", "\uD83C\uDF68", "\uD83C\uDF69", "\uD83C\uDF66", "\uD83E\uDDC1", // 🍧🍨🍩🍦🧁
        ]),
        ("\uD83C\uDFE0", "Travel", [
            "\uD83C\uDFE0", "\uD83C\uDFE2", "\uD83C\uDFEB", "\uD83C\uDFE5", "\uD83C\uDFED", // 🏠🏢🏫🏥🏭
            "\uD83C\uDF0D", "\uD83C\uDF0E", "\uD83C\uDF0F", "\u2708\uFE0F", "\uD83D\uDE97", // 🌍🌎🌏✈️🚗
            "\uD83D\uDE82", "\uD83D\uDEA2", "\uD83C\uDFD4\uFE0F", "\uD83C\uDFD6\uFE0F", "\uD83C\uDFD9\uFE0F", // 🚂🚢🏔️🏖️🏙️
            "\uD83D\uDE95", "\uD83D\uDE8C", "\uD83D\uDE8E", "\uD83D\uDE90", "\uD83D\uDE91", // 🚕🚌🚎🚐🚑
            "\uD83D\uDE92", "\uD83D\uDE93", "\uD83D\uDE94", "\uD83D\uDE96", "\uD83D\uDE99", // 🚒🚓🚔🚖🚙
            "\uD83D\uDEF5", "\uD83D\uDEF6", "\uD83D\uDEB2", "\uD83D\uDEF4", "\uD83D\uDEF9", // 🛵🛶🚲🛴🛹
            "\uD83D\uDE81", "\uD83D\uDE80", "\uD83D\uDEF8", "\u26F5", "\uD83D\uDEA4",        // 🚁🚀🛸⛵🚤
            "\uD83C\uDFD7\uFE0F", "\uD83C\uDFDA\uFE0F", "\uD83C\uDFDB\uFE0F", "\u26FA", "\uD83C\uDFDD\uFE0F", // 🏗️🏚️🏛️⛺🏝️
            "\uD83D\uDDFC", "\uD83D\uDDFD", "\uD83D\uDDFE", "\uD83C\uDFEF", "\uD83C\uDFF0", // 🗼🗽🗾🏯🏰
        ]),
        ("\uD83C\uDFC5", "Sports", [
            "\u26BD", "\uD83C\uDFC0", "\uD83C\uDFC8", "\u26BE", "\uD83E\uDD4E",              // ⚽🏀🏈⚾🥎
            "\uD83C\uDFBE", "\uD83C\uDFD0", "\uD83C\uDFC9", "\uD83E\uDD4F", "\uD83C\uDFB1", // 🎾🏐🏉🥏🎱
            "\uD83C\uDFD3", "\uD83C\uDFF8", "\uD83C\uDFD2", "\uD83C\uDFD1", "\uD83E\uDD4D", // 🏓🏸🏒🏑🥍
            "\u26F3", "\uD83C\uDFF9", "\uD83C\uDFA3", "\uD83E\uDD3F", "\uD83E\uDD4A",        // ⛳🏹🎣🤿🥊
            "\uD83E\uDD4B", "\uD83C\uDFBF", "\u26F7\uFE0F", "\uD83C\uDFC2", "\uD83E\uDDF7", // 🥋🏿⛷️🏂🧷
            "\uD83C\uDFC4", "\uD83C\uDFCA", "\uD83D\uDEB4", "\uD83D\uDEB5", "\uD83E\uDD38", // 🏄🏊🚴🚵🤸
            "\uD83E\uDD3A", "\uD83E\uDD3C", "\uD83E\uDD3D", "\uD83E\uDD3E", "\uD83C\uDFCB\uFE0F", // 🤺🤼🤽🤾🏋️
            "\uD83E\uDDD8", "\uD83E\uDDD7", "\uD83C\uDFC7", "\uD83E\uDD3E", "\uD83C\uDFAF", // 🧘🧗🏇🤾🎯
        ]),
        ("\uD83C\uDF89", "Fun", [
            "\uD83C\uDF89", "\uD83C\uDF8A", "\uD83C\uDFC6", "\uD83C\uDF96\uFE0F", "\uD83E\uDD47", // 🎉🎊🏆🎖️🥇
            "\uD83E\uDD48", "\uD83E\uDD49", "\uD83C\uDFAE", "\uD83C\uDFB2", "\uD83C\uDFAD", // 🥈🥉🎮🎲🎭
            "\uD83C\uDFA8", "\uD83C\uDFB5", "\uD83C\uDFB6", "\uD83C\uDFAC", "\uD83D\uDCF7", // 🎨🎵🎶🎬📷
            "\uD83D\uDCF8", "\uD83C\uDFA5", "\uD83C\uDFA4", "\uD83C\uDFB9", "\uD83C\uDFB8", // 📸🎥🎤🎹🎸
            "\uD83C\uDFBA", "\uD83C\uDFBB", "\uD83E\uDE95", "\uD83E\uDE98", "\uD83C\uDFBC", // 🎺🎻🪕🪘🎼
            "\uD83C\uDFA0", "\uD83C\uDFA1", "\uD83C\uDFA2", "\uD83C\uDFAA", "\uD83C\uDFAB", // 🎠🎡🎢🎪🎫
            "\uD83C\uDF80", "\uD83C\uDF81", "\uD83C\uDF83", "\uD83C\uDF84", "\uD83C\uDF86", // 🎀🎁🎃🎄🎆
            "\uD83C\uDF87", "\uD83C\uDF88", "\uD83E\uDDE8", "\uD83E\uDE85", "\uD83C\uDF90", // 🎇🎈🧨🪅🎐
        ]),
        ("\u2764\uFE0F", "Hearts", [
            "\u2764\uFE0F", "\uD83E\uDE77", "\uD83D\uDC9C", "\uD83D\uDC99", "\uD83D\uDC9A", // ❤️🩷💜💙💚
            "\uD83E\uDDE1", "\uD83D\uDC9B", "\uD83E\uDD0D", "\uD83D\uDDA4", "\uD83E\uDD0E", // 🧡💛🤍🖤🤎
            "\uD83D\uDC96", "\uD83D\uDC97", "\uD83D\uDC93", "\uD83D\uDC9E", "\uD83D\uDC95", // 💖💗💓💞💕
            "\u2763\uFE0F", "\uD83D\uDC9D", "\uD83D\uDC98", "\uD83D\uDC94", "\u2764\uFE0F\u200D\uD83D\uDD25", // ❣️💝💘💔❤️‍🔥
            "\u2764\uFE0F\u200D\uD83E\uDE79", "\uD83E\uDE79", "\uD83D\uDC8B", "\uD83D\uDC8C", "\uD83D\uDC8D", // ❤️‍🩹🩹💋💌💍
            "\uD83D\uDC8E", "\uD83D\uDC90", "\uD83E\uDEB7", "\uD83C\uDF39", "\uD83C\uDF3A", // 💎💐🪷🌹🌺
        ]),
    ];

    private void OpenPageEmojiPicker()
    {
        if (_pageIconBtn is null || _pageIconCommand is null) return;
        var btn = _pageIconBtn;
        var cmd = _pageIconCommand;
        var iconPageId = _pageIconPageId;
        ShowEmojiPicker(btn, selectedEmoji =>
        {
            if (btn.Content is TextBlock tb)
                tb.Text = selectedEmoji;
            // Live-update sidebar icon
            if (!string.IsNullOrEmpty(iconPageId) && _pageListIcons.TryGetValue(iconPageId, out var sidebarIcon))
                sidebarIcon.Text = selectedEmoji;
            SendCommandSilent(cmd,
                JsonSerializer.Serialize(new { icon = selectedEmoji }));
        });
    }

    private static void TrackRecentEmoji(string emoji)
    {
        _recentEmojis.Remove(emoji);
        _recentEmojis.Insert(0, emoji);
        if (_recentEmojis.Count > MaxRecentEmojis)
            _recentEmojis.RemoveRange(MaxRecentEmojis, _recentEmojis.Count - MaxRecentEmojis);
        RecentEmojisSaved?.Invoke(_recentEmojis);
    }

    private void ShowEmojiPicker(Control anchor, Action<string> onSelected)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not Window w || w.Content is not Panel rootPanel) return;

        // State for keyboard navigation
        var selectedIndex = -1;
        var activeCategoryIndex = -1; // -1 = all categories
        var filterText = "";

        var allEmojiButtons = new List<(Button Btn, string Emoji)>();
        Grid? overlayGrid = null;

        void CloseOverlay()
        {
            if (overlayGrid != null)
                rootPanel.Children.Remove(overlayGrid);
        }

        void SelectEmoji(string emoji)
        {
            CloseOverlay();
            TrackRecentEmoji(emoji);
            onSelected(emoji);
        }

        void UpdateSelectionHighlight(int newIndex)
        {
            if (allEmojiButtons.Count == 0) return;

            // Clear old
            if (selectedIndex >= 0 && selectedIndex < allEmojiButtons.Count)
                allEmojiButtons[selectedIndex].Btn.Background = Brushes.Transparent;

            selectedIndex = Math.Clamp(newIndex, 0, allEmojiButtons.Count - 1);

            allEmojiButtons[selectedIndex].Btn.Background = PrimarySubtle;
            allEmojiButtons[selectedIndex].Btn.BringIntoView();
        }

        Button MakeEmojiButton(string emoji)
        {
            var btn = new Button
            {
                Content = new TextBlock { Text = emoji, FontSize = FontSize("ThemeFontSize3Xl", 32) },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4),
                Width = 48,
                Height = 48,
                MinWidth = 48,
                MinHeight = 48,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            btn.Click += (_, _) => SelectEmoji(emoji);
            btn.PointerEntered += (s, _) =>
            {
                if (s is Button b)
                {
                    var idx = allEmojiButtons.FindIndex(x => x.Btn == b);
                    if (idx >= 0) UpdateSelectionHighlight(idx);
                }
            };
            return btn;
        }

        // Category tab buttons & "All" tab
        var categoryTabScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        var categoryTabPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 2,
        };
        categoryTabScroll.Content = categoryTabPanel;
        var categoryTabButtons = new List<Button>();

        // Emoji content area
        var emojiContentPanel = new StackPanel { Spacing = 4 };

        // Search box
        var searchBox = new TextBox
        {
            Watermark = "Search emojis...",
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = TextPrimary,
            Margin = new Thickness(4),
            FontSize = FontSize("ThemeFontSizeMd", 14),
        };

        void RebuildEmojiGrid()
        {
            emojiContentPanel.Children.Clear();
            allEmojiButtons.Clear();
            selectedIndex = -1;

            var filter = filterText.Trim();
            var isSearching = filter.Length > 0;

            // Recent section — only when showing all categories and not searching
            if (!isSearching && activeCategoryIndex < 0 && _recentEmojis.Count > 0)
            {
                emojiContentPanel.Children.Add(new TextBlock
                {
                    Text = "Recent",
                    FontSize = FontSize("ThemeFontSizeXsSm", 11),
                    Foreground = TextMuted,
                    Margin = new Thickness(4, 0, 0, 2),
                });
                var recentWrap = new WrapPanel();
                foreach (var emoji in _recentEmojis)
                {
                    var btn = MakeEmojiButton(emoji);
                    recentWrap.Children.Add(btn);
                    allEmojiButtons.Add((btn, emoji));
                }
                emojiContentPanel.Children.Add(recentWrap);
            }

            if (isSearching)
            {
                // Flat filtered view — match against category name and emoji keywords
                var lowerFilter = filter.ToLowerInvariant();
                var flatWrap = new WrapPanel();
                foreach (var cat in EmojiCategories)
                {
                    var catMatch = cat.Name.Contains(lowerFilter, StringComparison.OrdinalIgnoreCase);
                    foreach (var emoji in cat.Emojis)
                    {
                        // Match if category name matches, or emoji char itself matches,
                        // or emoji has a keyword match from the lookup
                        if (catMatch || emoji.Contains(filter)
                            || (EmojiKeywords.TryGetValue(emoji, out var kw) && kw.Contains(lowerFilter, StringComparison.OrdinalIgnoreCase)))
                        {
                            var btn = MakeEmojiButton(emoji);
                            flatWrap.Children.Add(btn);
                            allEmojiButtons.Add((btn, emoji));
                        }
                    }
                }
                emojiContentPanel.Children.Add(flatWrap);
            }
            else if (activeCategoryIndex >= 0 && activeCategoryIndex < EmojiCategories.Length)
            {
                // Single category filtered view
                var cat = EmojiCategories[activeCategoryIndex];
                var wrap = new WrapPanel();
                foreach (var emoji in cat.Emojis)
                {
                    var btn = MakeEmojiButton(emoji);
                    wrap.Children.Add(btn);
                    allEmojiButtons.Add((btn, emoji));
                }
                emojiContentPanel.Children.Add(wrap);
            }
            else
            {
                // All categories
                for (var i = 0; i < EmojiCategories.Length; i++)
                {
                    var cat = EmojiCategories[i];
                    emojiContentPanel.Children.Add(new TextBlock
                    {
                        Text = cat.Name,
                        FontSize = FontSize("ThemeFontSizeXsSm", 11),
                        Foreground = TextMuted,
                        Margin = new Thickness(4, 8, 0, 2),
                    });
                    var wrap = new WrapPanel();
                    foreach (var emoji in cat.Emojis)
                    {
                        var btn = MakeEmojiButton(emoji);
                        wrap.Children.Add(btn);
                        allEmojiButtons.Add((btn, emoji));
                    }
                    emojiContentPanel.Children.Add(wrap);
                }
            }
        }

        void UpdateCategoryTabHighlight()
        {
            for (var i = 0; i < categoryTabButtons.Count; i++)
            {
                // Index 0 = "All" tab, rest are offset by 1
                var isActive = (i == 0 && activeCategoryIndex < 0) ||
                               (i > 0 && activeCategoryIndex == i - 1);
                categoryTabButtons[i].Background = isActive ? PrimarySubtle : Brushes.Transparent;
            }
        }

        void SetCategory(int catIndex)
        {
            activeCategoryIndex = catIndex;
            UpdateCategoryTabHighlight();
            RebuildEmojiGrid();
        }

        // "All" tab
        var allTabBtn = new Button
        {
            Content = new TextBlock { Text = "\u2B50", FontSize = FontSize("ThemeFontSize2Xl", 24) }, // ⭐
            Background = PrimarySubtle,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6),
            MinWidth = 48,
            MinHeight = 48,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        allTabBtn.Click += (_, _) => SetCategory(-1);
        allTabBtn.PointerEntered += (s, _) => { if (s is Button b && activeCategoryIndex >= 0) b.Background = HoverSubtle; };
        allTabBtn.PointerExited += (s, _) => { if (s is Button b) b.Background = activeCategoryIndex < 0 ? PrimarySubtle : Brushes.Transparent; };
        categoryTabButtons.Add(allTabBtn);
        categoryTabPanel.Children.Add(allTabBtn);

        // Per-category tabs
        for (var i = 0; i < EmojiCategories.Length; i++)
        {
            var cat = EmojiCategories[i];
            var catIndex = i;
            var tabBtn = new Button
            {
                Content = new TextBlock { Text = cat.Icon, FontSize = FontSize("ThemeFontSize2Xl", 24) },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 6),
                MinWidth = 48,
                MinHeight = 48,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            tabBtn.Click += (_, _) => SetCategory(catIndex);
            tabBtn.PointerEntered += (s, _) => { if (s is Button b && activeCategoryIndex != catIndex) b.Background = HoverSubtle; };
            tabBtn.PointerExited += (s, _) => { if (s is Button b) b.Background = activeCategoryIndex == catIndex ? PrimarySubtle : Brushes.Transparent; };
            categoryTabButtons.Add(tabBtn);
            categoryTabPanel.Children.Add(tabBtn);
        }

        // Build initial grid
        RebuildEmojiGrid();

        // Search filtering
        searchBox.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.TextProperty)
            {
                filterText = searchBox.Text ?? "";
                RebuildEmojiGrid();
            }
        };

        var scroll = new ScrollViewer
        {
            Content = emojiContentPanel,
            MaxHeight = 400,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var hintText = new TextBlock
        {
            Text = "\u2190 \u2192 \u2191 \u2193 navigate   enter select   esc close",
            FontSize = FontSize("ThemeFontSizeXsSm", 11),
            Foreground = TextMuted,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 8, 2),
        };

        // Inner layout: search, tabs, hints, emoji grid
        var innerGrid = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,Auto,*"),
        };
        Grid.SetRow(searchBox, 0);
        Grid.SetRow(categoryTabScroll, 1);
        Grid.SetRow(hintText, 2);
        Grid.SetRow(scroll, 3);
        innerGrid.Children.Add(searchBox);
        innerGrid.Children.Add(categoryTabScroll);
        innerGrid.Children.Add(hintText);
        innerGrid.Children.Add(scroll);

        // Search separator
        var searchSeparator = new Border
        {
            Height = 1,
            Background = BorderSubtle,
            Margin = new Thickness(4, 0),
        };
        Grid.SetRow(searchSeparator, 0);
        searchSeparator.VerticalAlignment = VerticalAlignment.Bottom;
        innerGrid.Children.Add(searchSeparator);

        var container = new Border
        {
            Background = SurfaceElevated,
            BorderBrush = BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8),
            Width = 500,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 80, 0, 0),
            BoxShadow = BoxShadows.Parse("0 8 32 0 #40000000"),
            Child = innerGrid,
            ClipToBounds = true,
        };

        // Backdrop
        var backdrop = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#80000000")),
        };
        backdrop.PointerPressed += (_, _) => CloseOverlay();

        // Overlay grid
        overlayGrid = new Grid();
        overlayGrid.Children.Add(backdrop);
        overlayGrid.Children.Add(container);

        // ~10 columns at 48px per button within 500px - 16px padding
        const int columnsPerRow = 10;
        var tabFocused = false;
        var focusedTabIndex = 0;

        void HighlightTab(int idx)
        {
            // Clear all tab highlights first
            for (var i = 0; i < categoryTabButtons.Count; i++)
            {
                var isActive = (i == 0 && activeCategoryIndex < 0) ||
                               (i > 0 && activeCategoryIndex == i - 1);
                categoryTabButtons[i].Background = isActive ? PrimarySubtle : Brushes.Transparent;
            }
            // Highlight focused tab
            focusedTabIndex = Math.Clamp(idx, 0, categoryTabButtons.Count - 1);
            categoryTabButtons[focusedTabIndex].Background = Primary;
            categoryTabButtons[focusedTabIndex].BringIntoView();
        }

        void ExitTabFocus()
        {
            tabFocused = false;
            UpdateCategoryTabHighlight();
        }

        overlayGrid.KeyDown += (_, e) =>
        {
            if (tabFocused)
            {
                switch (e.Key)
                {
                    case Key.Escape:
                        CloseOverlay();
                        e.Handled = true;
                        break;
                    case Key.Left:
                        if (focusedTabIndex > 0)
                            HighlightTab(focusedTabIndex - 1);
                        e.Handled = true;
                        break;
                    case Key.Right:
                        if (focusedTabIndex < categoryTabButtons.Count - 1)
                            HighlightTab(focusedTabIndex + 1);
                        e.Handled = true;
                        break;
                    case Key.Enter:
                        // Activate the focused tab's category
                        var catIdx = focusedTabIndex == 0 ? -1 : focusedTabIndex - 1;
                        SetCategory(catIdx);
                        ExitTabFocus();
                        e.Handled = true;
                        break;
                    case Key.Down:
                        ExitTabFocus();
                        if (allEmojiButtons.Count > 0)
                            UpdateSelectionHighlight(0);
                        e.Handled = true;
                        break;
                    case Key.Up:
                        // Already at top — stay in tabs
                        e.Handled = true;
                        break;
                }
                return;
            }

            switch (e.Key)
            {
                case Key.Escape:
                    CloseOverlay();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (selectedIndex >= 0 && selectedIndex < allEmojiButtons.Count)
                    {
                        SelectEmoji(allEmojiButtons[selectedIndex].Emoji);
                        e.Handled = true;
                    }
                    break;
                case Key.Down:
                    UpdateSelectionHighlight(selectedIndex < 0 ? 0 : selectedIndex + columnsPerRow);
                    e.Handled = true;
                    break;
                case Key.Up:
                    if (selectedIndex >= 0 && selectedIndex < columnsPerRow)
                    {
                        // Move into category tab row
                        if (selectedIndex >= 0 && selectedIndex < allEmojiButtons.Count)
                            allEmojiButtons[selectedIndex].Btn.Background = Brushes.Transparent;
                        selectedIndex = -1;
                        tabFocused = true;
                        // Map to "All" tab initially, or current active category
                        var tabIdx = activeCategoryIndex < 0 ? 0 : activeCategoryIndex + 1;
                        HighlightTab(tabIdx);
                    }
                    else if (selectedIndex >= 0)
                    {
                        UpdateSelectionHighlight(selectedIndex - columnsPerRow);
                    }
                    e.Handled = true;
                    break;
                case Key.Right:
                    UpdateSelectionHighlight(selectedIndex < 0 ? 0 : selectedIndex + 1);
                    e.Handled = true;
                    break;
                case Key.Left:
                    if (selectedIndex > 0)
                        UpdateSelectionHighlight(selectedIndex - 1);
                    e.Handled = true;
                    break;
            }
        };

        rootPanel.Children.Add(overlayGrid);

        // Focus search box after overlay is in the tree
        Avalonia.Threading.Dispatcher.UIThread.Post(() => searchBox.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void ShowLinkPicker(Controls.RichTextEditor.RichTextEditor editor)
    {
        if (LinkableItemSearcher is null) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not Window w || w.Content is not Panel rootPanel) return;

        var selectedIndex = -1;
        var resultItems = new List<Models.LinkableItemResult>();
        Grid? overlayGrid = null;
        System.Threading.CancellationTokenSource? debounceTimer = null;

        void CloseOverlay()
        {
            debounceTimer?.Cancel();
            if (overlayGrid != null)
                rootPanel.Children.Remove(overlayGrid);
            editor.Focus();
        }

        void SelectItem(Models.LinkableItemResult item)
        {
            CloseOverlay();
            editor.InsertLink(item.Title, $"privstack://{item.LinkType}/{item.Id}");
            // Flush the editor text immediately so the shadow state has the new content
            // before the re-render triggered by add_link
            editor.FlushTextChange();
            // Register the link relationship and trigger a full refresh so
            // graph + backlinks panel both update in real time
            var addLinkArgs = System.Text.Json.JsonSerializer.Serialize(new { target_id = item.Id, target_type = item.LinkType });
            SendCommand("add_link", addLinkArgs);
        }

        // Results list
        var resultPanel = new StackPanel { Spacing = 0 };
        var resultScroll = new ScrollViewer
        {
            Content = resultPanel,
            MaxHeight = 300,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var emptyLabel = new TextBlock
        {
            Text = "Type to search for items to link...",
            FontSize = FontSize("ThemeFontSizeSmMd", 13),
            Foreground = TextMuted,
            Margin = new Thickness(12, 16),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        void UpdateSelectionHighlight(int newIndex)
        {
            if (resultPanel.Children.Count == 0) return;
            if (selectedIndex >= 0 && selectedIndex < resultPanel.Children.Count
                && resultPanel.Children[selectedIndex] is Border oldB)
                oldB.Background = Brushes.Transparent;

            selectedIndex = Math.Clamp(newIndex, 0, resultPanel.Children.Count - 1);
            if (resultPanel.Children[selectedIndex] is Border newB)
            {
                newB.Background = PrimarySubtle;
                newB.BringIntoView();
            }
        }

        void RebuildResults(IReadOnlyList<Models.LinkableItemResult> items)
        {
            resultPanel.Children.Clear();
            resultItems.Clear();
            resultItems.AddRange(items);
            selectedIndex = -1;

            if (items.Count == 0)
            {
                emptyLabel.Text = "No linkable items found";
                if (!resultPanel.Children.Contains(emptyLabel))
                    resultPanel.Children.Add(emptyLabel);
                return;
            }

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var idx = i;

                // Map icon name to emoji for simple display
                var iconEmoji = item.Icon switch
                {
                    "FileText" => "\U0001F4DD",    // Notes
                    "CheckSquare" => "\u2611",     // Tasks
                    "Calendar" => "\U0001F4C5",    // Calendar
                    "Book" => "\U0001F4D6",        // Journal
                    "Users" => "\U0001F465",       // Contacts
                    "Lock" => "\U0001F512",        // Passwords
                    "Rss" => "\U0001F4E1",         // RSS
                    "Code" => "\U0001F4BB",        // Snippets
                    "Folder" => "\U0001F4C1",      // Files
                    "GitBranch" => "\U0001F517",   // Graph
                    "DollarSign" => "\U0001F4B0", // Ledger
                    "Target" => "\U0001F3AF",      // Deals
                    _ => null
                };

                var badge = new Border
                {
                    Background = PrimarySubtle,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = iconEmoji != null ? $"{iconEmoji} {item.LinkTypeDisplayName}" : item.LinkTypeDisplayName,
                        FontSize = FontSize("ThemeFontSizeXsSm", 11),
                        Foreground = TextMuted,
                    },
                };

                var titleBlock = new TextBlock
                {
                    Text = item.Title,
                    FontSize = FontSize("ThemeFontSizeSmMd", 13),
                    Foreground = TextPrimary,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };

                var textStack = new StackPanel { Spacing = 1 };
                textStack.Children.Add(titleBlock);
                if (!string.IsNullOrEmpty(item.Subtitle))
                {
                    textStack.Children.Add(new TextBlock
                    {
                        Text = item.Subtitle,
                        FontSize = FontSize("ThemeFontSizeXsSm", 11),
                        Foreground = TextMuted,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                }

                var row = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Children = { badge, textStack },
                };

                var rowBorder = new Border
                {
                    Background = Brushes.Transparent,
                    Padding = new Thickness(12, 6),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    Child = row,
                };
                rowBorder.PointerPressed += (_, _) => SelectItem(item);
                rowBorder.PointerEntered += (_, _) => UpdateSelectionHighlight(idx);
                resultPanel.Children.Add(rowBorder);
            }

            // Auto-select first
            if (items.Count > 0)
                UpdateSelectionHighlight(0);
        }

        // Search box
        var searchBox = new TextBox
        {
            Watermark = "Search items to link...",
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = TextPrimary,
            Margin = new Thickness(4),
            FontSize = FontSize("ThemeFontSizeMd", 14),
        };

        var searcher = LinkableItemSearcher;
        searchBox.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty) return;
            var query = searchBox.Text ?? "";

            debounceTimer?.Cancel();
            if (string.IsNullOrWhiteSpace(query))
            {
                resultPanel.Children.Clear();
                resultItems.Clear();
                selectedIndex = -1;
                emptyLabel.Text = "Type to search for items to link...";
                resultPanel.Children.Add(emptyLabel);
                return;
            }

            debounceTimer = new System.Threading.CancellationTokenSource();
            var token = debounceTimer.Token;
            _ = Task.Delay(200, token).ContinueWith(async _ =>
            {
                if (token.IsCancellationRequested) return;
                var results = await searcher(query, 20);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    RebuildResults(results);
                });
            }, token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        };

        var hintText = new TextBlock
        {
            Text = "\u2191 \u2193 navigate   enter select   esc close",
            FontSize = FontSize("ThemeFontSizeXsSm", 11),
            Foreground = TextMuted,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 8, 2),
        };

        var searchSeparator = new Border
        {
            Height = 1,
            Background = BorderSubtle,
            Margin = new Thickness(4, 0),
        };

        var innerStack = new StackPanel { Spacing = 0 };
        innerStack.Children.Add(searchBox);
        innerStack.Children.Add(searchSeparator);
        innerStack.Children.Add(hintText);
        innerStack.Children.Add(resultScroll);

        // Show initial empty state
        emptyLabel.Text = "Type to search for items to link...";
        resultPanel.Children.Add(emptyLabel);

        var container = new Border
        {
            Background = SurfaceElevated,
            BorderBrush = BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8),
            Width = 450,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 80, 0, 0),
            BoxShadow = BoxShadows.Parse("0 8 32 0 #40000000"),
            Child = innerStack,
            ClipToBounds = true,
        };

        var backdrop = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#80000000")),
        };
        backdrop.PointerPressed += (_, _) => CloseOverlay();

        overlayGrid = new Grid();
        overlayGrid.Children.Add(backdrop);
        overlayGrid.Children.Add(container);

        overlayGrid.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    CloseOverlay();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (selectedIndex >= 0 && selectedIndex < resultItems.Count)
                    {
                        SelectItem(resultItems[selectedIndex]);
                        e.Handled = true;
                    }
                    break;
                case Key.Down:
                    if (resultItems.Count > 0)
                        UpdateSelectionHighlight(selectedIndex < 0 ? 0 : selectedIndex + 1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    if (selectedIndex > 0)
                        UpdateSelectionHighlight(selectedIndex - 1);
                    e.Handled = true;
                    break;
            }
        };

        rootPanel.Children.Add(overlayGrid);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => searchBox.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private static Control CreateEmptyState(string message)
    {
        return new TextBlock
        {
            Text = message,
            FontSize = FontSize("ThemeFontSizeMd", 14),
            Foreground = TextMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static Control CreateErrorState(string message)
    {
        return new Border
        {
            Background = DangerBrush,
            CornerRadius = Radius("ThemeRadiusXs"),
            Padding = Thick("ThemePaddingMd"),
            Margin = Thick("ThemePaddingSm"),
            Child = new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                FontSize = FontSize("ThemeFontSizeSmMd", 13),
            },
        };
    }

    private void SendCommand(string commandName, string argsJson)
    {
        var pluginId = PluginId;
        if (string.IsNullOrEmpty(pluginId)) return;

        // Intercept pseudo-command to open a palette overlay
        if (commandName == "__open_palette")
        {
            var paletteId = "add_block";
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.TryGetProperty("palette_id", out var pidElem))
                    paletteId = pidElem.GetString() ?? paletteId;
            }
            catch { /* use default */ }

            PaletteRequested?.Invoke(pluginId, paletteId);
            return;
        }

        // Intercept pseudo-command to import a file via host-native file picker
        if (commandName == "__host_import_file")
        {
            _ = HandleHostImportFileAsync(pluginId, argsJson);
            return;
        }

        // Physics param changes update the live graph control without a full re-render,
        // so the settings flyout stays open while the user adjusts sliders.
        // Toggle physics on the live graph without re-render
        if (commandName == "toggle_graph_physics" && _activeGraphControl != null)
        {
            CommandSender?.Invoke(pluginId, commandName, argsJson);
            if (_activeGraphControl.Physics != null)
            {
                // Physics is on → turn off (stop engine, arrange in circle)
                _activeGraphControl.Physics = null;
                _activeGraphControl.Stop();
                _activeGraphControl.Start();
            }
            else
            {
                // Physics is off → turn on with current params
                _activeGraphControl.Physics = new PhysicsParameters();
                _activeGraphControl.Stop();
                _activeGraphControl.Start();
            }
            return;
        }

        // Depth / link-filter changes: send silently, fetch fresh graph data, update control in-place
        if (commandName == "set_graph_depth" || commandName == "set_graph_link_filter")
        {
            _log.Debug("Graph setting intercepted: {Cmd} args={Args} activeControl={HasControl}",
                commandName, argsJson, _activeGraphControl != null);
            CommandSender?.Invoke(pluginId, commandName, argsJson);
            if (_activeGraphControl != null)
                RefreshGraphControlData(fullRelayout: commandName == "set_graph_depth");
            return;
        }

        if (commandName == "set_graph_physics_param" && _activeGraphControl?.Physics is { } physics)
        {
            CommandSender?.Invoke(pluginId, commandName, argsJson);
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var param = doc.RootElement.GetStringProp("param") ?? "";
                var value = doc.RootElement.TryGetProperty("value", out var vEl) && vEl.ValueKind == JsonValueKind.Number
                    ? vEl.GetDouble() : 0;
                switch (param)
                {
                    case "repulsion": physics.RepulsionStrength = value; break;
                    case "link_distance": physics.LinkDistance = value; break;
                    case "link_strength": physics.LinkStrength = value; break;
                    case "collision_strength": physics.CollisionStrength = value; break;
                    case "center_strength": physics.CenterStrength = value; break;
                    case "velocity_decay": physics.VelocityDecay = value; break;
                }
            }
            catch { /* ignore parse errors */ }
            _activeGraphControl.Reheat();
            return;
        }

        // Inject after_id into add_block commands when a block is focused
        if (commandName == "add_block")
            argsJson = InjectAfterId(argsJson);

        // Cancel any pending deferred refresh to prevent re-entrant FFI calls
        _deferredRefreshTimer?.Stop();
        _deferredRefreshTimer = null;

        _log.Debug("Sending command to {PluginId}: {Command} — THIS TRIGGERS RE-RENDER. Shadow: dirty={Dirty} blocks={Count}",
            pluginId, commandName, _shadowState.IsDirty, _shadowState.Blocks.Count);

        CommandSender?.Invoke(pluginId, commandName, argsJson);

        // Refresh the view state after a command
        ViewStateRefreshRequested?.Invoke();
    }

    /// <summary>
    /// Handles the host-side file import for the Archive plugin.
    /// Shows a native file picker, copies the file to workspace storage,
    /// persists the entity via SDK routing, and notifies the plugin to refresh.
    /// </summary>
    private async Task HandleHostImportFileAsync(string pluginId, string argsJson)
    {
        try
        {
            // Check filesystem permission — prompt with Allow/Deny if not yet granted
            if (PermissionChecker != null && !PermissionChecker(pluginId, "filesystem"))
            {
                if (PermissionPrompter != null)
                {
                    var granted = await PermissionPrompter(pluginId, "Archive", "filesystem", "File System Access");
                    if (!granted)
                    {
                        _log.Debug("HandleHostImportFile: user denied filesystem permission for {PluginId}", pluginId);
                        return;
                    }
                }
                else
                {
                    _log.Warning("HandleHostImportFile: plugin {PluginId} lacks filesystem permission and no prompter", pluginId);
                    return;
                }
            }

            if (GeneralFilePicker is null)
            {
                _log.Warning("HandleHostImportFile: GeneralFilePicker not wired");
                return;
            }

            var filePaths = await GeneralFilePicker();
            if (filePaths.Count == 0)
                return;

            var baseStoragePath = ImageStoragePath;
            var importedCount = 0;

            foreach (var filePath in filePaths)
            {
                if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                    continue;

                var fileName = System.IO.Path.GetFileName(filePath);
                var extension = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
                var fileInfo = new System.IO.FileInfo(filePath);
                var fileSize = (ulong)fileInfo.Length;
                var mimeType = GetMimeTypeFromExtension(extension);
                var fileId = $"file-{Guid.NewGuid():N}";

                var isMedia = mimeType.StartsWith("image/") || mimeType.StartsWith("video/") || mimeType.StartsWith("audio/");

                // Determine current folder from raw plugin view data
                string? currentFolderId = null;
                try
                {
                    var rawData = RawViewDataProvider?.Invoke();
                    if (!string.IsNullOrEmpty(rawData))
                    {
                        using var viewDoc = JsonDocument.Parse(rawData);
                        if (viewDoc.RootElement.TryGetProperty("selected_folder_id", out var fIdEl)
                            && fIdEl.ValueKind == JsonValueKind.String)
                        {
                            currentFolderId = fIdEl.GetString();
                        }
                    }
                }
                catch { /* ignore parse errors */ }

                // Copy file to workspace storage: flat Archive/ directory, prefixed with fileId to avoid collisions
                if (!string.IsNullOrEmpty(baseStoragePath))
                {
                    var workspaceFilesDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(baseStoragePath));
                    if (!string.IsNullOrEmpty(workspaceFilesDir))
                    {
                        var archiveDir = System.IO.Path.Combine(workspaceFilesDir, "Archive");
                        System.IO.Directory.CreateDirectory(archiveDir);
                        var storedFileName = $"{fileId}_{fileName}";
                        var storedPath = System.IO.Path.Combine(archiveDir, storedFileName);
                        System.IO.File.Copy(filePath, storedPath, overwrite: true);

                        if (mimeType.StartsWith("image/"))
                            GenerateArchiveThumbnail(storedPath, archiveDir, fileId);
                    }
                }

                var nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                var payload = new Dictionary<string, object?>
                {
                    ["id"] = fileId,
                    ["name"] = fileName,
                    ["display_name"] = null,
                    ["folder_id"] = currentFolderId,
                    ["mime_type"] = mimeType,
                    ["size"] = fileSize,
                    ["content_hash"] = null,
                    ["tags"] = Array.Empty<string>(),
                    ["description"] = null,
                    ["is_favorite"] = false,
                    ["is_trashed"] = false,
                    ["trashed_at"] = null,
                    ["created_at"] = nowUtcMs,
                    ["modified_at"] = nowUtcMs,
                };

                var entityType = isMedia ? "media_file" : "vault_file";
                var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload);

                var sdkMsg = System.Text.Json.JsonSerializer.Serialize(new
                {
                    action = "update",
                    entity_type = entityType,
                    entity_id = fileId,
                    payload = payloadJson,
                });

                SdkRouter?.Invoke(pluginId, sdkMsg);
                CommandSender?.Invoke(pluginId, "file_imported", payloadJson);

                _log.Debug("HandleHostImportFile: imported {FileName} ({Size} bytes) as {EntityType} id={FileId}",
                    fileName, fileSize, entityType, fileId);
                importedCount++;
            }

            if (importedCount > 0)
            {
                var msg = importedCount == 1
                    ? $"Imported 1 file successfully."
                    : $"Imported {importedCount} files successfully.";
                ToastNotifier?.Invoke(msg, false);
            }

            // Trigger UI refresh once after all files imported
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ViewStateRefreshRequested?.Invoke(),
                Avalonia.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "HandleHostImportFile failed");
            ToastNotifier?.Invoke($"File import failed: {ex.Message}", true);
        }
    }

    /// <summary>
    /// Maps file extensions to MIME types.
    /// </summary>
    private static string GetMimeTypeFromExtension(string extension) => extension switch
    {
        ".pdf" => "application/pdf",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        ".zip" => "application/zip",
        ".gz" or ".gzip" => "application/gzip",
        ".tar" => "application/x-tar",
        ".txt" => "text/plain",
        ".json" => "application/json",
        ".csv" => "text/csv",
        ".xml" => "application/xml",
        ".html" or ".htm" => "text/html",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// Generates a thumbnail (thumb.jpg) for an image file in the given archive directory.
    /// Thumbnail is 160px on the longest side, saved as JPEG.
    /// </summary>
    private void GenerateArchiveThumbnail(string sourceImagePath, string archiveDir, string fileId)
    {
        try
        {
            using var stream = System.IO.File.OpenRead(sourceImagePath);
            var original = new Avalonia.Media.Imaging.Bitmap(stream);

            const int maxThumbSize = 160;
            var srcW = original.PixelSize.Width;
            var srcH = original.PixelSize.Height;
            if (srcW <= 0 || srcH <= 0) return;

            double scale = Math.Min((double)maxThumbSize / srcW, (double)maxThumbSize / srcH);
            scale = Math.Min(scale, 1.0); // Don't upscale
            var thumbW = (int)(srcW * scale);
            var thumbH = (int)(srcH * scale);

            var thumbPath = System.IO.Path.Combine(archiveDir, $"{fileId}_thumb.jpg");
            using var renderTarget = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new Avalonia.PixelSize(thumbW, thumbH), new Avalonia.Vector(96, 96));

            using (var ctx = renderTarget.CreateDrawingContext())
            {
                ctx.DrawImage(original, new Avalonia.Rect(0, 0, srcW, srcH),
                    new Avalonia.Rect(0, 0, thumbW, thumbH));
            }

            renderTarget.Save(thumbPath);
            _log.Debug("GenerateArchiveThumbnail: {ThumbW}x{ThumbH} → {Path}", thumbW, thumbH, thumbPath);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to generate thumbnail for {Path}", sourceImagePath);
        }
    }

    /// <summary>
    /// Resolves the archive directory for a file ID.
    /// Returns null if ImageStoragePath is not set or directory doesn't exist.
    /// </summary>
    private string? ResolveArchiveDir()
    {
        if (string.IsNullOrEmpty(ImageStoragePath)) return null;
        var workspaceFilesDir = System.IO.Path.GetDirectoryName(
            System.IO.Path.GetDirectoryName(ImageStoragePath));
        if (string.IsNullOrEmpty(workspaceFilesDir)) return null;
        var dir = System.IO.Path.Combine(workspaceFilesDir, "Archive");
        return System.IO.Directory.Exists(dir) ? dir : null;
    }

    /// <summary>
    /// Loads a thumbnail bitmap for the given file ID, or null if not available.
    /// Files stored as {fileId}_{filename}, thumbnails as {fileId}_thumb.jpg.
    /// </summary>
    private Avalonia.Media.Imaging.Bitmap? LoadArchiveThumbnail(string fileId)
    {
        var dir = ResolveArchiveDir();
        if (dir is null) return null;
        var thumbPath = System.IO.Path.Combine(dir, $"{fileId}_thumb.jpg");
        if (!System.IO.File.Exists(thumbPath)) return null;
        try
        {
            return new Avalonia.Media.Imaging.Bitmap(thumbPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Loads the original file from the archive directory, or null if not available.
    /// Files stored as {fileId}_{originalFilename} in the flat Archive/ directory.
    /// </summary>
    private (Avalonia.Media.Imaging.Bitmap Bitmap, string FilePath)? LoadArchiveOriginal(string fileId)
    {
        var dir = ResolveArchiveDir();
        if (dir is null) return null;
        var match = System.IO.Directory.GetFiles(dir, $"{fileId}_*")
            .Where(f => !System.IO.Path.GetFileName(f).EndsWith("_thumb.jpg", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (match is null) return null;
        try
        {
            return (new Avalonia.Media.Imaging.Bitmap(match), match);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Send a command to the plugin without triggering a full view refresh.
    /// Used by the block editor for text updates and structural changes where
    /// the editor manages its own UI state.
    /// </summary>
    private void SendCommandSilent(string commandName, string argsJson)
    {
        var pluginId = PluginId;
        if (string.IsNullOrEmpty(pluginId)) return;

        CommandSender?.Invoke(pluginId, commandName, argsJson);
    }

    /// <summary>
    /// Sends a command silently (no immediate re-render) then schedules a
    /// debounced view refresh after 150 ms. Rapid sequential interactions
    /// (checkbox clicks, date picks, stepper taps) coalesce into a single
    /// re-render so the view stays responsive and flicker-free.
    /// </summary>
    private void SendCommandDeferred(string commandName, string argsJson)
    {
        var pluginId = PluginId;
        if (string.IsNullOrEmpty(pluginId)) return;

        CommandSender?.Invoke(pluginId, commandName, argsJson);

        // Reset the debounce timer — only the last interaction in a burst triggers re-render
        _deferredRefreshTimer?.Stop();
        _deferredRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _deferredRefreshTimer.Tick += (_, _) =>
        {
            _deferredRefreshTimer?.Stop();
            _deferredRefreshTimer = null;
            ViewStateRefreshRequested?.Invoke();
        };
        _deferredRefreshTimer.Start();
    }

    /// <summary>
    /// Fetches fresh view state from the plugin and updates the active graph control
    /// with new node/edge data — no full UI re-render.
    /// </summary>
    private void RefreshGraphControlData(bool fullRelayout = false)
    {
        if (_activeGraphControl is null || ViewStateProvider is null)
        {
            _log.Debug("RefreshGraphControlData: skipped — control={HasControl} provider={HasProvider}",
                _activeGraphControl != null, ViewStateProvider != null);
            return;
        }

        var json = ViewStateProvider();
        if (string.IsNullOrEmpty(json))
        {
            _log.Debug("RefreshGraphControlData: ViewStateProvider returned empty");
            return;
        }

        try
        {
            // Do NOT dispose the JsonDocument — the JsonElements stored on the
            // control must remain valid for later Start()/UpdateData() calls.
            var doc = JsonDocument.Parse(json);
            var (nodes, edges, centerId) = ExtractGraphData(doc.RootElement);
            if (nodes != null)
            {
                var depthCounts = new Dictionary<int, int>();
                foreach (var n in nodes)
                {
                    var d = n.TryGetProperty("depth", out var dv) && dv.ValueKind == JsonValueKind.Number ? dv.GetInt32() : -1;
                    depthCounts[d] = depthCounts.GetValueOrDefault(d) + 1;
                }
                var depthStr = string.Join(", ", depthCounts.OrderBy(kv => kv.Key).Select(kv => $"d{kv.Key}={kv.Value}"));
                _log.Debug("RefreshGraphControlData: nodes={NodeCount} edges={EdgeCount} centerId={CenterId} depths=[{Depths}]",
                    nodes.Count, edges?.Count ?? -1, centerId ?? "(null)", depthStr);

                // Update the center ID before updating data so the graph renders with correct center
                if (!string.IsNullOrEmpty(centerId) && _activeGraphControl.CenterId != centerId)
                {
                    _log.Debug("RefreshGraphControlData: Updating CenterId from {Old} to {New}",
                        _activeGraphControl.CenterId ?? "(null)", centerId);
                    _activeGraphControl.CenterId = centerId;
                }
            }
            if (nodes != null)
                _activeGraphControl.UpdateData(nodes, edges ?? [], fullRelayout);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to refresh graph data");
        }
    }

    /// <summary>
    /// Recursively searches the view state JSON for a graph_view component and extracts its nodes/edges/center_id.
    /// </summary>
    private static (List<JsonElement>? Nodes, List<JsonElement>? Edges, string? CenterId) ExtractGraphData(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.GetStringProp("type") == "graph_view")
            {
                var nodes = new List<JsonElement>();
                var edges = new List<JsonElement>();
                if (el.TryGetProperty("nodes", out var n) && n.ValueKind == JsonValueKind.Array)
                    foreach (var node in n.EnumerateArray()) nodes.Add(node);
                if (el.TryGetProperty("edges", out var e) && e.ValueKind == JsonValueKind.Array)
                    foreach (var edge in e.EnumerateArray()) edges.Add(edge);
                var centerId = el.GetStringProp("center_id");
                return (nodes, edges, centerId);
            }

            // Recurse into known container properties
            foreach (var prop in el.EnumerateObject())
            {
                var result = ExtractGraphData(prop.Value);
                if (result.Nodes != null) return result;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in el.EnumerateArray())
            {
                var result = ExtractGraphData(child);
                if (result.Nodes != null) return result;
            }
        }

        return (null, null, null);
    }

    /// <summary>
    /// Merges the currently focused block ID as "after_id" into add_block JSON args.
    /// </summary>
    private string InjectAfterId(string argsJson)
    {
        if (_focusedBlockId is null) return argsJson;

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            // Don't override if already present
            if (root.TryGetProperty("after_id", out var existing) &&
                existing.GetString() is { Length: > 0 })
                return argsJson;

            var dict = new Dictionary<string, object?>();
            foreach (var prop in root.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetInt64(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => prop.Value.GetRawText(),
                };
            }
            dict["after_id"] = _focusedBlockId;
            return JsonSerializer.Serialize(dict);
        }
        catch
        {
            return argsJson;
        }
    }
}

/// <summary>
/// Data structure to hold graph information for deferred hydration.
/// The graph is rendered as a placeholder initially, then hydrated
/// asynchronously after the main content is displayed.
/// Uses pre-parsed GraphData to avoid JsonDocument disposal issues.
/// </summary>
internal sealed record DeferredGraphData(
    NeuronGraphControl Canvas,
    PrivStack.UI.Adaptive.Models.GraphData GraphData,
    PhysicsParameters? Physics,
    string? CenterId);
