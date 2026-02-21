using Avalonia.Controls;
using Avalonia.Input;
using PrivStack.Desktop.ViewModels.AiTray;

namespace PrivStack.Desktop.Views;

public partial class AiSuggestionTray : UserControl
{
    public AiSuggestionTray()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is AiSuggestionTrayViewModel vm)
        {
            vm.ScrollToBottomRequested -= OnScrollToBottomRequested;
            vm.ScrollToBottomRequested += OnScrollToBottomRequested;
        }
    }

    private void OnScrollToBottomRequested(object? sender, System.EventArgs e)
    {
        var sv = this.FindControl<ScrollViewer>("MessageScrollViewer");
        if (sv != null)
        {
            // Defer to allow layout pass
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                sv.ScrollToEnd();
            }, Avalonia.Threading.DispatcherPriority.Render);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var input = this.FindControl<TextBox>("ChatInputBox");
            if (input?.IsFocused == true && DataContext is AiSuggestionTrayViewModel vm)
            {
                if (vm.SendChatMessageCommand.CanExecute(null))
                {
                    vm.SendChatMessageCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }
    }
}
