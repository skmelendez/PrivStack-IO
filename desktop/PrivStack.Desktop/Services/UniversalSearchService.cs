// ============================================================================
// File: UniversalSearchService.cs
// Description: Bridges PluginToolbar search ↔ CommandPaletteViewModel ↔
//              UniversalSearchDropdown. Discovers the active toolbar on tab
//              switch and wires focus/text/keyboard forwarding.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivStack.Desktop.Controls;
using PrivStack.Desktop.ViewModels;
using PrivStack.UI.Adaptive.Controls;

namespace PrivStack.Desktop.Services;

public sealed class UniversalSearchService
{
    private readonly CommandPaletteViewModel _paletteVm;
    private readonly MainWindowViewModel _mainVm;

    private PluginToolbar? _activeToolbar;
    private UniversalSearchDropdown? _dropdown;
    private bool _isSyncingText;

    public UniversalSearchService(CommandPaletteViewModel paletteVm, MainWindowViewModel mainVm)
    {
        _paletteVm = paletteVm;
        _mainVm = mainVm;

        // Listen for tab changes to discover the new toolbar
        _mainVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.CurrentViewModel))
                ScheduleToolbarDiscovery();
        };
    }

    /// <summary>
    /// Sets the dropdown control reference (called once from MainWindow).
    /// </summary>
    public void SetDropdown(UniversalSearchDropdown dropdown)
    {
        _dropdown = dropdown;
    }

    /// <summary>
    /// Focuses the active toolbar's search box and opens the dropdown.
    /// Called from MainWindow on Cmd+K.
    /// </summary>
    public void FocusSearchBar()
    {
        if (_activeToolbar != null)
        {
            _activeToolbar.FocusSearchBox();
        }
        else
        {
            // Fallback: toggle palette directly if no toolbar found
            _paletteVm.ToggleCommand.Execute(null);
        }
    }

    private void ScheduleToolbarDiscovery()
    {
        // Delay slightly to allow the visual tree to render the new plugin view
        Dispatcher.UIThread.Post(() => DiscoverToolbar(), DispatcherPriority.Loaded);
    }

    private void DiscoverToolbar()
    {
        UnsubscribeFromToolbar();

        // Walk the visual tree from the content area to find the PluginToolbar
        var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

        if (mainWindow == null) return;

        _activeToolbar = FindDescendant<PluginToolbar>(mainWindow);
        if (_activeToolbar == null) return;

        _activeToolbar.SearchGotFocus += OnSearchGotFocus;
        _activeToolbar.SearchLostFocus += OnSearchLostFocus;
        _activeToolbar.SearchKeyDown += OnSearchKeyDown;

        // Sync toolbar text → VM whenever it changes
        _activeToolbar.PropertyChanged += OnToolbarPropertyChanged;
    }

    private void UnsubscribeFromToolbar()
    {
        if (_activeToolbar == null) return;

        _activeToolbar.SearchGotFocus -= OnSearchGotFocus;
        _activeToolbar.SearchLostFocus -= OnSearchLostFocus;
        _activeToolbar.SearchKeyDown -= OnSearchKeyDown;
        _activeToolbar.PropertyChanged -= OnToolbarPropertyChanged;
        _activeToolbar = null;
    }

    private void OnSearchGotFocus(object? sender, EventArgs e)
    {
        _paletteVm.IsOpen = true;
        PositionDropdown();
    }

    private void OnSearchLostFocus(object? sender, EventArgs e)
    {
        // Delay close to allow click on dropdown items to register first
        Dispatcher.UIThread.Post(() =>
        {
            // Check if focus moved to the dropdown itself
            if (_dropdown?.IsPointerOver == true) return;

            _paletteVm.IsOpen = false;
        }, DispatcherPriority.Input);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_paletteVm.IsOpen) return;

        _dropdown?.HandleKeyDown(e);

        // On Escape, also unfocus the search box
        if (e.Key == Key.Escape && _activeToolbar != null)
        {
            // Move focus away from search box
            (Avalonia.Application.Current?.ApplicationLifetime as
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow?.Focus();
        }
    }

    private void OnToolbarPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != PluginToolbar.SearchTextProperty) return;
        if (_isSyncingText) return;

        _isSyncingText = true;
        _paletteVm.SearchQuery = _activeToolbar?.SearchText ?? "";
        _isSyncingText = false;
    }

    private void PositionDropdown()
    {
        if (_dropdown == null || _activeToolbar == null) return;

        var anchor = _activeToolbar.ActiveSearchPill;
        if (anchor != null)
        {
            _dropdown.UpdatePosition(anchor);
        }
    }

    private static T? FindDescendant<T>(Visual root) where T : Visual
    {
        foreach (var child in root.GetVisualDescendants())
        {
            if (child is T match)
                return match;
        }
        return null;
    }
}
