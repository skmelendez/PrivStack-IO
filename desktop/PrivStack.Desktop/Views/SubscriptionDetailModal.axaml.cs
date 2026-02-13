using Avalonia.Controls;
using Avalonia.Input;
using PrivStack.Desktop.ViewModels;

namespace PrivStack.Desktop.Views;

public partial class SubscriptionDetailModal : UserControl
{
    public SubscriptionDetailModal()
    {
        InitializeComponent();
    }

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is SubscriptionBadgeViewModel vm)
        {
            vm.CloseModalCommand.Execute(null);
        }
    }
}
