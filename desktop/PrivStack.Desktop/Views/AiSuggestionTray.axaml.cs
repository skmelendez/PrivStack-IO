using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PrivStack.Desktop.ViewModels.AiTray;

namespace PrivStack.Desktop.Views;

public partial class AiSuggestionTray : UserControl
{
    private AiSuggestionTrayViewModel? _currentVm;

    public AiSuggestionTray()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_currentVm != null)
        {
            _currentVm.ScrollToBottomRequested -= OnScrollToBottomRequested;
            _currentVm.PropertyChanged -= OnVmPropertyChanged;
        }

        _currentVm = DataContext as AiSuggestionTrayViewModel;
        if (_currentVm != null)
        {
            _currentVm.ScrollToBottomRequested += OnScrollToBottomRequested;
            _currentVm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AiSuggestionTrayViewModel.SelectedTabIndex) && _currentVm != null)
            ApplyTabVisibility(_currentVm.SelectedTabIndex);
    }

    private void OnScrollToBottomRequested(object? sender, System.EventArgs e)
    {
        var sv = this.FindControl<ScrollViewer>("MessageScrollViewer");
        if (sv != null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                sv.ScrollToEnd();
            }, Avalonia.Threading.DispatcherPriority.Render);
        }
    }

    internal void OnTabChat(object? sender, RoutedEventArgs e) => SwitchTab(0);
    internal void OnTabIntents(object? sender, RoutedEventArgs e) => SwitchTab(1);
    internal void OnTabHistory(object? sender, RoutedEventArgs e) => SwitchTab(2);

    private void SwitchTab(int index)
    {
        if (_currentVm != null)
            _currentVm.SelectedTabIndex = index;
        ApplyTabVisibility(index);
    }

    private void ApplyTabVisibility(int index)
    {
        var chatPanel = this.FindControl<Panel>("ChatPanel");
        var intentsPanel = this.FindControl<Panel>("IntentsPanel");
        var historyPanel = this.FindControl<Panel>("HistoryPanel");

        if (chatPanel != null) chatPanel.IsVisible = index == 0;
        if (intentsPanel != null) intentsPanel.IsVisible = index == 1;
        if (historyPanel != null) historyPanel.IsVisible = index == 2;
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
