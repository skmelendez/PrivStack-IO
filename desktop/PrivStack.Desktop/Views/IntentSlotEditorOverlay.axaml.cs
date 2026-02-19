using Avalonia.Controls;
using Avalonia.Input;

namespace PrivStack.Desktop.Views;

public partial class IntentSlotEditorOverlay : UserControl
{
    public IntentSlotEditorOverlay()
    {
        InitializeComponent();
    }

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ViewModels.IntentSlotEditorViewModel vm)
            vm.CancelCommand.Execute(null);
    }
}
