using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Services;

namespace PrivStack.Desktop.Views.Controls;

public partial class ToastContainer : UserControl
{
    private ToastService? _toastService;

    public ToastContainer()
    {
        InitializeComponent();

        // Resolve immediately so DataContext is set before bindings evaluate.
        // App.Services is guaranteed to exist before MainWindow is constructed.
        _toastService = App.Services.GetService<ToastService>();
        if (_toastService != null)
            DataContext = _toastService;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Listen for new toast card Borders to apply type-based CSS classes
        this.AddHandler(Border.LoadedEvent, OnBorderLoaded, handledEventsToo: true);
    }

    /// <summary>
    /// When a toast card Border loads, apply the type-specific class
    /// (e.g., "toast-success") so XAML style selectors can set bg/border colors.
    /// </summary>
    private void OnBorderLoaded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Border border && border.DataContext is ActiveToast toast)
        {
            var typeClass = toast.TypeClass;
            if (!border.Classes.Contains(typeClass))
                border.Classes.Add(typeClass);
        }
    }

    private void OnDismissClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ActiveToast toast })
            _toastService?.Dismiss(toast);
    }

    private void OnActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ActiveToast toast })
        {
            toast.Action?.Invoke();
            _toastService?.Dismiss(toast);
        }
    }
}
