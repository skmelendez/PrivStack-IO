using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Controls;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.Services.Plugin;
using PrivStack.Desktop.ViewModels;
using PrivStack.Sdk;
using RichTextEditorControl = PrivStack.UI.Adaptive.Controls.RichTextEditor.RichTextEditor;

namespace PrivStack.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly IAppSettingsService _settings = App.Services.GetRequiredService<IAppSettingsService>();
    private readonly IResponsiveLayoutService _responsiveLayout = App.Services.GetRequiredService<IResponsiveLayoutService>();
    private UniversalSearchService? _universalSearch;
    private bool _isInitialized;
    private Control? _speechTargetControl;
    private bool _chordPrefixActive;
    private System.Timers.Timer? _chordTimer;
    private bool _isResizingInfoPanel;
    private double _infoPanelResizeStartX;
    private double _infoPanelResizeStartWidth;

    public MainWindow()
    {
        InitializeComponent();

        // Tunnel routing: intercept global shortcuts before child controls consume them
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);

        // Enable window dragging from the title bar spacer
        TitleBarSpacer.PointerPressed += OnTitleBarPointerPressed;

        // Info panel drag-to-resize
        var dragHandle = this.FindControl<Border>("InfoPanelDragHandle");
        if (dragHandle != null)
        {
            dragHandle.PointerPressed += OnInfoPanelDragStart;
            dragHandle.PointerMoved += OnInfoPanelDragMove;
            dragHandle.PointerReleased += OnInfoPanelDragEnd;
            dragHandle.PointerCaptureLost += (_, _) => _isResizingInfoPanel = false;
        }

        // Apply saved window settings
        _settings.ApplyToWindow(this);

        // Hook up window events for saving state
        this.Opened += OnWindowOpened;
        this.PositionChanged += OnPositionChanged;
        this.PropertyChanged += OnWindowPropertyChanged;

        // Set up speech recording event handlers
        SetupSpeechRecording();
    }

    private void SetupSpeechRecording()
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.SpeechRecordingVM.TranscriptionReady += OnTranscriptionReady;
        }

        this.DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel newVm)
            {
                newVm.SpeechRecordingVM.TranscriptionReady += OnTranscriptionReady;
            }
        };
    }

    private void OnTranscriptionReady(object? sender, string transcription)
    {
        if (string.IsNullOrWhiteSpace(transcription) || _speechTargetControl == null)
        {
            _speechTargetControl = null;
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            switch (_speechTargetControl)
            {
                case TextBox textBox:
                    InsertTextIntoTextBox(textBox, transcription);
                    break;
                case TextEditor editor:
                    InsertTextIntoEditor(editor, transcription);
                    break;
                case RichTextEditorControl rte:
                    rte.InsertTranscription(transcription);
                    rte.Focus();
                    break;
            }
            _speechTargetControl = null;
        });
    }

    private static void InsertTextIntoTextBox(TextBox textBox, string text)
    {
        var caretIndex = textBox.CaretIndex;
        var currentText = textBox.Text ?? "";
        textBox.Text = currentText.Insert(caretIndex, text);
        textBox.CaretIndex = caretIndex + text.Length;
        textBox.Focus();
    }

    private static void InsertTextIntoEditor(TextEditor editor, string text)
    {
        var offset = editor.CaretOffset;
        editor.Document.Insert(offset, text);
        editor.CaretOffset = offset + text.Length;
        editor.Focus();
    }

    // ========================================================================
    // Window Lifecycle
    // ========================================================================

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        _isInitialized = true;

        App.Services.GetRequiredService<IDialogService>().SetOwner(this);

        if (DataContext is MainWindowViewModel vm)
        {
            vm.PropertyChanged += OnMainVmPropertyChanged;

            _universalSearch = new UniversalSearchService(vm.CommandPaletteVM, vm);
            _universalSearch.SetDropdown(SearchDropdown);

            var lastTab = _settings.Settings.LastActiveTab;
            var pluginRegistry = App.Services.GetRequiredService<IPluginRegistry>();
            if (!string.IsNullOrEmpty(lastTab) && pluginRegistry.GetPluginForNavItem(lastTab) != null)
            {
                vm.SelectTabCommand.Execute(lastTab);
            }
            else if (pluginRegistry.NavigationItems.Count > 0)
            {
                vm.SelectTabCommand.Execute(pluginRegistry.NavigationItems[0].Id);
            }
        }

        UpdateContentAreaWidth();
    }

    private void OnMainVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsSidebarCollapsed))
        {
            UpdateContentAreaWidth();
        }
    }

    private void UpdateContentAreaWidth()
    {
        if (!_isInitialized) return;

        var sidebarCollapsed = (DataContext as MainWindowViewModel)?.IsSidebarCollapsed ?? false;
        var navSidebarWidth = sidebarCollapsed ? 56.0 : 220.0;
        var contentWidth = Bounds.Width - navSidebarWidth;

        if (contentWidth > 0)
        {
            _responsiveLayout.UpdateContentAreaWidth(contentWidth);
        }
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_isInitialized && WindowState == WindowState.Normal)
        {
            _settings.UpdateWindowBounds(this);
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!_isInitialized) return;

        if (e.Property == WidthProperty || e.Property == HeightProperty || e.Property == WindowStateProperty)
        {
            _settings.UpdateWindowBounds(this);
            UpdateContentAreaWidth();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _settings.UpdateWindowBounds(this);
        _settings.Flush();

        if (DataContext is MainWindowViewModel vm)
        {
            vm.Cleanup();
        }

        base.OnClosing(e);
    }

    // ========================================================================
    // Title Bar + Pointer Handlers
    // ========================================================================

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    // ========================================================================
    // Info Panel Drag-to-Resize
    // ========================================================================

    private void OnInfoPanelDragStart(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _isResizingInfoPanel = true;
        _infoPanelResizeStartX = e.GetPosition(this).X;
        _infoPanelResizeStartWidth = vm.InfoPanelVM.PanelWidth;
        e.Pointer.Capture((IInputElement)sender!);
        e.Handled = true;
    }

    private void OnInfoPanelDragMove(object? sender, PointerEventArgs e)
    {
        if (!_isResizingInfoPanel) return;
        if (DataContext is not MainWindowViewModel vm) return;

        var currentX = e.GetPosition(this).X;
        var delta = _infoPanelResizeStartX - currentX;
        var newWidth = Math.Clamp(_infoPanelResizeStartWidth + delta, 220, 600);
        vm.InfoPanelVM.PanelWidth = newWidth;
        e.Handled = true;
    }

    private void OnInfoPanelDragEnd(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isResizingInfoPanel) return;
        _isResizingInfoPanel = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnAiTrayBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.IsAiTrayOpen)
        {
            vm.ToggleAiTrayCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnInfoPanelBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.InfoPanelVM.IsOpen)
        {
            vm.InfoPanelVM.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.IsUserMenuOpen)
        {
            vm.CloseUserMenuCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.CloseAllPanelsCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnQuickActionBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.IsQuickActionOverlayOpen)
        {
            vm.CloseQuickActionOverlay();
            e.Handled = true;
        }
    }

    private void OnQuickActionContentPressed(object? sender, PointerPressedEventArgs e)
    {
        // Prevent backdrop click-through when clicking inside the form
        e.Handled = true;
    }
}
