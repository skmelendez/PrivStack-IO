using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivStack.Desktop.ViewModels;

namespace PrivStack.Desktop.Views;

public partial class CommandPalette : UserControl
{
    public CommandPalette()
    {
        InitializeComponent();

        // Use tunneling route so we intercept keys before ListBox/TextBox consume them
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Focus the search box when the palette opens
        if (DataContext is CommandPaletteViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && vm.IsOpen)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        SearchBox.Focus();
                        SearchBox.SelectAll();
                    }, DispatcherPriority.Input);
                }
            };
        }
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
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
                // Backspace on empty search box clears the plugin scope filter
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
        // Only execute if the tap originated from within a ListBoxItem
        if (e.Source is not Control source) return;
        var listBoxItem = source.FindAncestorOfType<ListBoxItem>();
        if (listBoxItem is null) return;

        if (DataContext is CommandPaletteViewModel vm && vm.SelectedCommand is not null)
        {
            // Small delay to let SelectedItem binding update first
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
