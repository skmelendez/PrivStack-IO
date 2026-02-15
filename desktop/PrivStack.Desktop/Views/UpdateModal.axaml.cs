using Avalonia.Controls;
using Avalonia.Input;
using PrivStack.Desktop.ViewModels;

namespace PrivStack.Desktop.Views;

public partial class UpdateModal : UserControl
{
    public UpdateModal()
    {
        InitializeComponent();
    }

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is UpdateViewModel vm)
        {
            vm.CloseModalCommand.Execute(null);
        }
    }
}
