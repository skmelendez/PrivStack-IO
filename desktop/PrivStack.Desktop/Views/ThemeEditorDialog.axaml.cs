using Avalonia.Controls;
using Avalonia.Input;
using PrivStack.Desktop.ViewModels;

namespace PrivStack.Desktop.Views;

public partial class ThemeEditorDialog : UserControl
{
    public ThemeEditorDialog()
    {
        InitializeComponent();
    }

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ThemeEditorViewModel vm)
        {
            vm.CancelCommand.Execute(null);
        }
    }
}
