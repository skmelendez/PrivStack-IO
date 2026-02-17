using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using PrivStack.Desktop.Services;

namespace PrivStack.Desktop.Views.Controls;

public partial class ToastContainer : UserControl
{
    private ToastService? _toastService;

    public ToastContainer()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (_toastService == null)
        {
            _toastService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetService<ToastService>(App.Services);
            if (_toastService != null)
                DataContext = _toastService;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Listen for new items to apply type-based CSS classes to the Border
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
