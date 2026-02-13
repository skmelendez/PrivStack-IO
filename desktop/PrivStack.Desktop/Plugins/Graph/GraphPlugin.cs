using PrivStack.Desktop.Plugins.Graph.Services;
using PrivStack.Desktop.Plugins.Graph.ViewModels;
using PrivStack.Sdk;

namespace PrivStack.Desktop.Plugins.Graph;

public sealed class GraphPlugin : PluginBase<GraphViewModel>
{
    private GraphDataService? _graphService;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "privstack.graph",
        Name = "Graph",
        Description = "Knowledge graph visualization with force-directed layout",
        Version = new Version(1, 0, 0),
        Author = "PrivStack",
        Icon = "Graph",
        NavigationOrder = 105,
        Category = PluginCategory.Productivity,
        Tags = ["graph", "visualization", "knowledge", "links", "backlinks"],
        CanDisable = false,
        SupportsInfoPanel = true
    };

    public override NavigationItem NavigationItem => new()
    {
        Id = "Graph",
        DisplayName = "Graph",
        Subtitle = "Your second brain, visualized",
        Icon = "Graph",
        Tooltip = "Graph - Your second brain, visualized (Cmd+G)",
        Order = Metadata.NavigationOrder,
        ShortcutHint = "Cmd+G"
    };

    // ========================================================================
    // Plugin Lifecycle
    // ========================================================================

    protected override async Task<bool> OnInitializeAsync(CancellationToken cancellationToken)
    {
        _graphService = new GraphDataService(Host!.Sdk);
        return await Task.FromResult(true);
    }

    protected override GraphViewModel CreateViewModelCore()
    {
        return new GraphViewModel(_graphService!, Host?.InfoPanel, Host?.Settings);
    }

    public override async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        await base.OnNavigatedToAsync(cancellationToken);
        if (ViewModel != null) await ViewModel.LoadGraphAsync();
    }

    public async Task ShowLocalGraphAsync(string noteId)
    {
        if (ViewModel == null) return;
        ViewModel.SwitchToLocalViewCommand.Execute(noteId);
        await ViewModel.LoadGraphAsync();
    }

    public async Task ShowGlobalGraphAsync()
    {
        if (ViewModel == null) return;
        ViewModel.SwitchToGlobalViewCommand.Execute(null);
        await ViewModel.LoadGraphAsync();
    }
}
