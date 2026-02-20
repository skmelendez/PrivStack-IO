using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivStack.Desktop.ViewModels;

namespace PrivStack.Desktop.Controls;

public partial class UniversalSearchDropdown : UserControl
{
    public UniversalSearchDropdown()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Positions the dropdown card below the given anchor control.
    /// </summary>
    public void UpdatePosition(Control anchor)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var point = anchor.TranslatePoint(new Point(0, anchor.Bounds.Height + 4), topLevel);
        if (point == null) return;

        DropdownCard.Margin = new Thickness(point.Value.X, point.Value.Y, 0, 0);
    }

    /// <summary>
    /// Handles keyboard navigation forwarded from the search TextBox.
    /// </summary>
    public void HandleKeyDown(KeyEventArgs e)
    {
        if (DataContext is not CommandPaletteViewModel vm) return;

        switch (e.Key)
        {
            case Key.Escape:
                vm.CloseCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Enter:
                vm.ExecuteSelectedCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Down:
                vm.SelectNextCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Up:
                vm.SelectPreviousCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Back:
                if (vm.HasPluginFilter && string.IsNullOrEmpty(vm.SearchQuery))
                {
                    vm.ClearPluginFilter();
                    e.Handled = true;
                }
                break;
        }
    }

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is CommandPaletteViewModel vm)
        {
            vm.CloseCommand.Execute(null);
        }
    }

    private void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control source) return;
        var listBoxItem = source.FindAncestorOfType<ListBoxItem>();
        if (listBoxItem is null) return;

        if (DataContext is CommandPaletteViewModel vm && vm.SelectedCommand is not null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                vm.ExecuteSelectedCommand.Execute(null);
            }, DispatcherPriority.Input);
        }
    }

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control source) return;
        var listBoxItem = source.FindAncestorOfType<ListBoxItem>();
        if (listBoxItem is null) return;

        if (DataContext is CommandPaletteViewModel vm && vm.SelectedCommand is not null)
        {
            vm.ExecuteSelectedCommand.Execute(null);
        }
    }

    private void OnFilterPillPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is CommandPaletteViewModel vm)
            vm.ClearPluginFilter();
    }
}
