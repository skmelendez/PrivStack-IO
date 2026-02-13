using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PrivStack.Desktop.Views;

public partial class SetupWizardView : UserControl
{
    public SetupWizardView()
    {
        InitializeComponent();
    }

    private void OnRenewLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }
}
