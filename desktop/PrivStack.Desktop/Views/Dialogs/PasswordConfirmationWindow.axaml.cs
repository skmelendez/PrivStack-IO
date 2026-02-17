using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PrivStack.Desktop.Views.Dialogs;

public partial class PasswordConfirmationWindow : Window
{
    public bool Confirmed { get; private set; }
    public string Password => PasswordBox.Text ?? string.Empty;

    public PasswordConfirmationWindow()
    {
        InitializeComponent();
    }

    public void SetContent(string title, string message, string confirmButtonText = "Confirm",
        string? pluginName = null, string? pluginIcon = null)
    {
        TitleText.Text = title;
        Title = title;
        ConfirmButton.Content = confirmButtonText;

        if (!string.IsNullOrEmpty(pluginName))
        {
            PluginContextText.Text = $"{pluginName} is requesting access to encrypted storage";

            if (!string.IsNullOrEmpty(pluginIcon))
                PluginIcon.Icon = pluginIcon;
            else
                PluginIcon.IsVisible = false;

            PluginContextBorder.IsVisible = true;
            MessageText.IsVisible = false;
        }
        else
        {
            MessageText.Text = message;
            MessageText.IsVisible = true;
            PluginContextBorder.IsVisible = false;
        }
    }

    public void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBorder.IsVisible = true;
        PasswordBox.Text = string.Empty;
        PasswordBox.Focus();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        PasswordBox.Focus();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordBox.Text))
        {
            ShowError("Password is required.");
            return;
        }

        Confirmed = true;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close(false);
    }

    private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnConfirm(sender, e);
        else if (e.Key == Key.Escape)
            OnCancel(sender, e);
    }

    private void OnDragRegionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}
