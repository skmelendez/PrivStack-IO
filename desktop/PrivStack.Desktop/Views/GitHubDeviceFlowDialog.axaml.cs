using Avalonia.Controls;
using Avalonia.Input;
using PrivStack.Desktop.ViewModels;

namespace PrivStack.Desktop.Views;

public partial class GitHubDeviceFlowDialog : UserControl
{
    public GitHubDeviceFlowDialog()
    {
        InitializeComponent();
    }

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ConnectionsViewModel vm)
            vm.CancelConnectCommand.Execute(null);
    }

    private async void OnCopyClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not ConnectionsViewModel vm || string.IsNullOrEmpty(vm.DeviceUserCode))
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        await clipboard.SetTextAsync(vm.DeviceUserCode);

        CopyText.Text = "Copied!";
        vm.CodeCopied = true;

        await Task.Delay(2000);
        CopyText.Text = "Copy";
        vm.CodeCopied = false;
    }
}
