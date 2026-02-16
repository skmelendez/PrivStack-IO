using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivStack.Desktop.Plugins.Graph.Models;
using PrivStack.Desktop.Plugins.Graph.Services;
using PrivStack.Sdk;

namespace PrivStack.Desktop.Plugins.Graph.ViewModels;

public enum GraphVisualizationMode { ForceDirected, SolarSystem }

public partial class GraphViewModel : PrivStack.Sdk.ViewModelBase
{
    private readonly GraphDataService _graphService;
    private readonly IInfoPanelService? _infoPanelService;
    private readonly IPluginSettings? _settings;
    private bool _isInitializing = true;
    private bool _isTimelineDragging;
    private GraphData? _fullGraphData;

    [ObservableProperty] private GraphData? _graphData;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLocalView;
    [ObservableProperty] private string? _centerNodeId;
    [ObservableProperty] private string? _selectedNodeId;
    [ObservableProperty] private int _localDepth = 1;
    [ObservableProperty] private int _highlightDepth = 2;
    [ObservableProperty] private bool _hideInactiveNodes;
    [ObservableProperty] private OrphanFilterMode _orphanMode = OrphanFilterMode.Show;
    [ObservableProperty] private int _minLinkCount;
    [ObservableProperty] private int _maxNodes = 200;
    [ObservableProperty] private string _searchText = string.Empty;

    // Sidebar collapsed state
    [ObservableProperty]
    private bool _isGraphSidebarCollapsed;

    [RelayCommand]
    private void ToggleGraphSidebar()
    {
        IsGraphSidebarCollapsed = !IsGraphSidebarCollapsed;
    }

    [ObservableProperty] private List<string> _availableTags = [];
    [ObservableProperty] private HashSet<string> _includeTags = [];
    [ObservableProperty] private HashSet<string> _excludeTags = [];

    // Node type visibility
    [ObservableProperty] private bool _showNotes = true;
    [ObservableProperty] private bool _showTasks = true;
    [ObservableProperty] private bool _showContacts = true;
    [ObservableProperty] private bool _showEvents = true;
    [ObservableProperty] private bool _showJournal = true;
    [ObservableProperty] private bool _showWebClips = true;
    [ObservableProperty] private bool _showTags = true;
    [ObservableProperty] private bool _showOrphanedTags = true;

    // Visualization
    [ObservableProperty] private GraphVisualizationMode _visualizationMode = GraphVisualizationMode.ForceDirected;
    [ObservableProperty] private bool _isExperimentalPanelOpen;
    [ObservableProperty] private int _nodeCount;
    [ObservableProperty] private int _edgeCount;
    [ObservableProperty] private int _totalFilteredCount;
    [ObservableProperty] private bool _isNodeLimitActive;

    // Timeline
    [ObservableProperty] private bool _timelineEnabled;
    [ObservableProperty] private DateTimeOffset _timelineMinDate = DateTimeOffset.Now.AddYears(-1);
    [ObservableProperty] private DateTimeOffset _timelineMaxDate = DateTimeOffset.Now;
    [ObservableProperty] private double _timelineLowerValue;
    [ObservableProperty] private double _timelineUpperValue = 100;

    public DateTimeOffset TimelineStartDate => TimelineMinDate.AddTicks((long)((TimelineMaxDate - TimelineMinDate).Ticks * (TimelineLowerValue / 100.0)));
    public DateTimeOffset TimelineEndDate => TimelineMinDate.AddTicks((long)((TimelineMaxDate - TimelineMinDate).Ticks * (TimelineUpperValue / 100.0)));
    public string TimelineStartLabel => TimelineStartDate.LocalDateTime.ToString("MMM d, yyyy");
    public string TimelineEndLabel => TimelineEndDate.LocalDateTime.ToString("MMM d, yyyy");

    // Repel radius slider (0-100 maps to 200-500)
    [ObservableProperty] private double _repelSlider = 50;

    public double RepelRadius => 200 + (RepelSlider / 100.0 * 300);

    // Orphan radio helpers
    public bool IsOrphanHide { get => OrphanMode == OrphanFilterMode.Hide; set { if (value) OrphanMode = OrphanFilterMode.Hide; } }
    public bool IsOrphanShow { get => OrphanMode == OrphanFilterMode.Show; set { if (value) OrphanMode = OrphanFilterMode.Show; } }
    public bool IsOrphanOnly { get => OrphanMode == OrphanFilterMode.Only; set { if (value) OrphanMode = OrphanFilterMode.Only; } }

    // Solar system sliders
    [ObservableProperty] private double _solarSystemScaleSlider = 50;
    [ObservableProperty] private double _starSpacingSlider = 50;
    [ObservableProperty] private double _orbitScaleSlider = 50;
    public double SolarSystemScale => 0.5 + (SolarSystemScaleSlider / 100.0 * 1.5);
    public double StarSpacingMultiplier => 0.5 + (StarSpacingSlider / 100.0 * 2.0);
    public double OrbitScaleMultiplier => 0.5 + (OrbitScaleSlider / 100.0 * 2.0);

    // Events for canvas
    public event EventHandler? RequestReheat;
    public event EventHandler? RequestResetView;
    public event EventHandler? PhysicsParametersChanged;
    public event EventHandler? SolarSystemParametersChanged;
    public event EventHandler<GraphVisualizationMode>? VisualizationModeChanged;

    public GraphViewModel(GraphDataService graphService, IInfoPanelService? infoPanelService = null, IPluginSettings? settings = null)
    {
        _graphService = graphService;
        _infoPanelService = infoPanelService;
        _settings = settings;

        // Load persisted settings (inside _isInitializing=true so handlers don't fire)
        if (_settings != null)
        {
            _showNotes = _settings.Get("show_notes", true);
            _showTasks = _settings.Get("show_tasks", true);
            _showContacts = _settings.Get("show_contacts", true);
            _showEvents = _settings.Get("show_events", true);
            _showJournal = _settings.Get("show_journal", true);
            _showWebClips = _settings.Get("show_web_clips", true);
            _showTags = _settings.Get("show_tags", true);
            _showOrphanedTags = _settings.Get("show_orphaned_tags", true);
            _orphanMode = (OrphanFilterMode)_settings.Get("orphan_mode", (int)OrphanFilterMode.Show);
            _minLinkCount = _settings.Get("min_link_count", 0);
            _maxNodes = _settings.Get("max_nodes", 200);
            _highlightDepth = _settings.Get("highlight_depth", 2);
            _hideInactiveNodes = _settings.Get("hide_inactive_nodes", false);
            _localDepth = _settings.Get("local_depth", 1);
            _timelineEnabled = _settings.Get("timeline_enabled", false);
            _repelSlider = _settings.Get("repel_radius", 70.0);
            _isGraphSidebarCollapsed = _settings.Get("sidebar_collapsed", false);
        }

        _isInitializing = false;
    }

    public void OnTimelineDragStarted() => _isTimelineDragging = true;
    public void OnTimelineDragCompleted()
    {
        _isTimelineDragging = false;
        if (TimelineEnabled) ApplyFilters();
    }

    public async Task LoadGraphAsync(bool forceReload = false)
    {
        if (forceReload || _fullGraphData == null)
        {
            IsLoading = true;
            try
            {
                _fullGraphData = IsLocalView && !string.IsNullOrEmpty(CenterNodeId)
                    ? await _graphService.LoadLocalGraphAsync(CenterNodeId, LocalDepth)
                    : await _graphService.LoadGlobalGraphAsync();

                if (_fullGraphData?.Nodes.Count > 0)
                {
                    AvailableTags = _fullGraphData.Nodes.Values.SelectMany(n => n.Tags).Distinct().OrderBy(t => t).ToList();
                    var dates = _fullGraphData.Nodes.Values.Where(n => n.ModifiedAt != default).Select(n => n.ModifiedAt).ToList();
                    if (dates.Count > 0)
                    {
                        TimelineMinDate = dates.Min();
                        TimelineMaxDate = dates.Max();
                        if (TimelineMinDate == TimelineMaxDate) { TimelineMinDate = TimelineMinDate.AddDays(-1); TimelineMaxDate = TimelineMaxDate.AddDays(1); }
                    }
                }
            }
            finally { IsLoading = false; }
        }
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        if (_fullGraphData == null) { GraphData = null; NodeCount = 0; EdgeCount = 0; return; }

        var includeNodeTypes = new HashSet<NodeType>();
        if (ShowNotes) { includeNodeTypes.Add(NodeType.Note); includeNodeTypes.Add(NodeType.WikiSource); }
        if (ShowTasks) { includeNodeTypes.Add(NodeType.Task); includeNodeTypes.Add(NodeType.Project); }
        if (ShowContacts) { includeNodeTypes.Add(NodeType.Contact); includeNodeTypes.Add(NodeType.Company); includeNodeTypes.Add(NodeType.ContactGroup); }
        if (ShowEvents) includeNodeTypes.Add(NodeType.Event);
        if (ShowJournal) includeNodeTypes.Add(NodeType.Journal);
        if (ShowWebClips) includeNodeTypes.Add(NodeType.WebClip);
        if (ShowTags) includeNodeTypes.Add(NodeType.Tag);

        var candidateNodes = _fullGraphData.Nodes.Where(kv => MatchesBasicFilters(kv.Value, includeNodeTypes)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var filteredEdges = _fullGraphData.Edges.Where(e => candidateNodes.ContainsKey(e.SourceId) && candidateNodes.ContainsKey(e.TargetId)).ToList();

        var filteredLinkCounts = new Dictionary<string, int>();
        foreach (var nodeId in candidateNodes.Keys) filteredLinkCounts[nodeId] = 0;
        foreach (var edge in filteredEdges)
        {
            if (filteredLinkCounts.ContainsKey(edge.SourceId)) filteredLinkCounts[edge.SourceId]++;
            if (filteredLinkCounts.ContainsKey(edge.TargetId)) filteredLinkCounts[edge.TargetId]++;
        }

        var finalNodes = new Dictionary<string, GraphNode>();
        foreach (var kv in candidateNodes)
        {
            var filteredLinkCount = filteredLinkCounts.GetValueOrDefault(kv.Key, 0);
            var isOrphan = filteredLinkCount == 0;
            if (filteredLinkCount < MinLinkCount) continue;
            if (kv.Value.NodeType == NodeType.Tag && isOrphan && !ShowOrphanedTags) continue;
            if (OrphanMode == OrphanFilterMode.Hide && isOrphan) continue;
            if (OrphanMode == OrphanFilterMode.Only && !isOrphan) continue;
            finalNodes[kv.Key] = kv.Value;
        }

        // Apply max node limit — keep most-connected nodes (preserves graph skeleton)
        var preCapCount = finalNodes.Count;
        if (MaxNodes > 0 && finalNodes.Count > MaxNodes)
        {
            var topNodes = finalNodes
                .OrderByDescending(kv => filteredLinkCounts.GetValueOrDefault(kv.Key, 0))
                .ThenByDescending(kv => kv.Value.ModifiedAt)
                .Take(MaxNodes)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            finalNodes = topNodes;
        }

        var finalEdges = filteredEdges.Where(e => finalNodes.ContainsKey(e.SourceId) && finalNodes.ContainsKey(e.TargetId)).ToList();

        var filteredGraph = new GraphData();
        foreach (var kv in finalNodes) filteredGraph.Nodes[kv.Key] = kv.Value;
        foreach (var edge in finalEdges) filteredGraph.Edges.Add(edge);
        filteredGraph.BuildAdjacencyList();

        GraphData = filteredGraph;
        NodeCount = GraphData.NodeCount;
        EdgeCount = GraphData.EdgeCount;
        TotalFilteredCount = preCapCount;
        IsNodeLimitActive = preCapCount > MaxNodes;
    }

    private bool MatchesBasicFilters(GraphNode node, HashSet<NodeType> includeNodeTypes)
    {
        if (includeNodeTypes.Count > 0 && !includeNodeTypes.Contains(node.NodeType)) return false;
        if (!string.IsNullOrWhiteSpace(SearchText) && !node.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase)) return false;
        if (node.NodeType != NodeType.Tag && node.ModifiedAt != default && TimelineEnabled)
        {
            if (node.ModifiedAt < TimelineStartDate || node.ModifiedAt > TimelineEndDate) return false;
        }
        if (IncludeTags.Count > 0 && !node.Tags.Any(t => IncludeTags.Contains(t))) return false;
        if (ExcludeTags.Count > 0 && node.Tags.Any(t => ExcludeTags.Contains(t))) return false;
        return true;
    }

    [RelayCommand] private async Task RefreshAsync() { RequestResetView?.Invoke(this, EventArgs.Empty); await LoadGraphAsync(forceReload: true); }
    [RelayCommand] private void SwitchToGlobalView() { IsLocalView = false; CenterNodeId = null; _ = LoadGraphAsync(); }
    [RelayCommand] private void SwitchToLocalView(string nodeId) { IsLocalView = true; CenterNodeId = nodeId; _ = LoadGraphAsync(); }
    [RelayCommand] private void IncreaseDepth() { if (LocalDepth < 5) { LocalDepth++; if (IsLocalView) _ = LoadGraphAsync(); } }
    [RelayCommand] private void DecreaseDepth() { if (LocalDepth > 1) { LocalDepth--; if (IsLocalView) _ = LoadGraphAsync(); } }
    [RelayCommand] private void ToggleIncludeTag(string tag) { if (IncludeTags.Contains(tag)) IncludeTags.Remove(tag); else IncludeTags.Add(tag); OnPropertyChanged(nameof(IncludeTags)); _ = LoadGraphAsync(); }
    [RelayCommand] private void ToggleExcludeTag(string tag) { if (ExcludeTags.Contains(tag)) ExcludeTags.Remove(tag); else ExcludeTags.Add(tag); OnPropertyChanged(nameof(ExcludeTags)); _ = LoadGraphAsync(); }
    [RelayCommand] private void ClearFilters() { SearchText = string.Empty; IncludeTags.Clear(); ExcludeTags.Clear(); MinLinkCount = 0; MaxNodes = 200; OrphanMode = OrphanFilterMode.Show; ShowNotes = ShowTasks = ShowContacts = ShowEvents = ShowJournal = ShowWebClips = ShowTags = true; OnPropertyChanged(nameof(IncludeTags)); OnPropertyChanged(nameof(ExcludeTags)); _ = LoadGraphAsync(); }
    [RelayCommand] private void ReheatSimulation() => RequestReheat?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void ToggleExperimentalPanel() => IsExperimentalPanelOpen = !IsExperimentalPanelOpen;
    [RelayCommand] private void SetVisualizationMode(GraphVisualizationMode mode) { if (VisualizationMode != mode) { VisualizationMode = mode; VisualizationModeChanged?.Invoke(this, mode); } }
    [RelayCommand] private void SwitchToForceDirected() => SetVisualizationMode(GraphVisualizationMode.ForceDirected);
    [RelayCommand] private void SwitchToSolarSystem() => SetVisualizationMode(GraphVisualizationMode.SolarSystem);

    public void OnNodeClicked(string nodeId)
    {
        SelectedNodeId = nodeId;

        // Report selected node to the InfoPanel
        if (_infoPanelService != null)
        {
            var parts = nodeId.Split(':', 2);
            if (parts.Length == 2)
            {
                var linkType = parts[0];
                var entityId = parts[1];
                var title = _fullGraphData?.Nodes.TryGetValue(nodeId, out var node) == true
                    ? node.Title : nodeId;
                _infoPanelService.SetActiveItem(linkType, entityId, title);
            }
        }
    }
    public void OnNodeDeselected()
    {
        SelectedNodeId = null;
        _infoPanelService?.ClearActiveItem();
    }

    public void OnNodeDoubleClicked(string nodeId) { /* Navigation handled by host app */ }

    private void Save<T>(string key, T value) { if (!_isInitializing) _settings?.Set(key, value); }

    partial void OnSearchTextChanged(string value) { if (!_isInitializing) ApplyFilters(); }
    partial void OnMinLinkCountChanged(int value) { Save("min_link_count", value); _ = LoadGraphAsync(); }
    partial void OnMaxNodesChanged(int value) { Save("max_nodes", value); if (!_isInitializing) ApplyFilters(); }
    partial void OnOrphanModeChanged(OrphanFilterMode value) { Save("orphan_mode", (int)value); OnPropertyChanged(nameof(IsOrphanHide)); OnPropertyChanged(nameof(IsOrphanShow)); OnPropertyChanged(nameof(IsOrphanOnly)); if (!_isInitializing) _ = LoadGraphAsync(); }
    partial void OnShowNotesChanged(bool value) { Save("show_notes", value); if (!_isInitializing) _ = LoadGraphAsync(); }
    partial void OnShowTasksChanged(bool value) { Save("show_tasks", value); if (!_isInitializing) _ = LoadGraphAsync(); }
    partial void OnShowContactsChanged(bool value) { Save("show_contacts", value); if (!_isInitializing) _ = LoadGraphAsync(); }
    partial void OnShowEventsChanged(bool value) { Save("show_events", value); if (!_isInitializing) _ = LoadGraphAsync(); }
    partial void OnShowJournalChanged(bool value) { Save("show_journal", value); if (!_isInitializing) _ = LoadGraphAsync(); }
    partial void OnShowWebClipsChanged(bool value) { Save("show_web_clips", value); if (!_isInitializing) _ = LoadGraphAsync(); }
    partial void OnShowTagsChanged(bool value) { Save("show_tags", value); if (!_isInitializing) _ = LoadGraphAsync(); }
    partial void OnShowOrphanedTagsChanged(bool value) { Save("show_orphaned_tags", value); if (!_isInitializing) ApplyFilters(); }
    partial void OnHighlightDepthChanged(int value) { Save("highlight_depth", value); }
    partial void OnHideInactiveNodesChanged(bool value) { Save("hide_inactive_nodes", value); }
    partial void OnVisualizationModeChanged(GraphVisualizationMode value) => VisualizationModeChanged?.Invoke(this, value);
    partial void OnTimelineEnabledChanged(bool value) { Save("timeline_enabled", value); ApplyFilters(); }
    partial void OnTimelineLowerValueChanged(double value) { OnPropertyChanged(nameof(TimelineStartDate)); OnPropertyChanged(nameof(TimelineStartLabel)); if (TimelineEnabled && !_isTimelineDragging) ApplyFilters(); }
    partial void OnTimelineUpperValueChanged(double value) { OnPropertyChanged(nameof(TimelineEndDate)); OnPropertyChanged(nameof(TimelineEndLabel)); if (TimelineEnabled && !_isTimelineDragging) ApplyFilters(); }
    partial void OnTimelineMinDateChanged(DateTimeOffset value) { OnPropertyChanged(nameof(TimelineStartDate)); OnPropertyChanged(nameof(TimelineStartLabel)); }
    partial void OnTimelineMaxDateChanged(DateTimeOffset value) { OnPropertyChanged(nameof(TimelineEndDate)); OnPropertyChanged(nameof(TimelineEndLabel)); }
    partial void OnLocalDepthChanged(int value) { Save("local_depth", value); if (!_isInitializing && IsLocalView) _ = LoadGraphAsync(); }
    partial void OnIsGraphSidebarCollapsedChanged(bool value) { Save("sidebar_collapsed", value); }
    partial void OnRepelSliderChanged(double value) { Save("repel_radius", value); NotifyPhysics(nameof(RepelRadius)); }
    private void NotifyPhysics(string prop) { OnPropertyChanged(prop); if (!_isInitializing) PhysicsParametersChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnSolarSystemScaleSliderChanged(double value) => NotifySolar(nameof(SolarSystemScale));
    partial void OnStarSpacingSliderChanged(double value) => NotifySolar(nameof(StarSpacingMultiplier));
    partial void OnOrbitScaleSliderChanged(double value) => NotifySolar(nameof(OrbitScaleMultiplier));
    private void NotifySolar(string prop) { OnPropertyChanged(prop); if (!_isInitializing) SolarSystemParametersChanged?.Invoke(this, EventArgs.Empty); }
}
