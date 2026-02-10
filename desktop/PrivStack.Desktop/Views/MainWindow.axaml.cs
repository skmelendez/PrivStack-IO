using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaEdit;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Desktop.ViewModels;
using PrivStack.Sdk;
using RichTextEditorControl = PrivStack.UI.Adaptive.Controls.RichTextEditor.RichTextEditor;

namespace PrivStack.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly IAppSettingsService _settings = App.Services.GetRequiredService<IAppSettingsService>();
    private readonly IResponsiveLayoutService _responsiveLayout = App.Services.GetRequiredService<IResponsiveLayoutService>();
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

        // Also handle when DataContext changes
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

        // Insert transcription into the target control
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
                    rte.InsertAtCaret(transcription);
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

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        _isInitialized = true;

        // Wire up the dialog service so modal dialogs have an owner window
        App.Services.GetRequiredService<IDialogService>().SetOwner(this);

        // Subscribe to sidebar collapse changes for responsive layout recalculation
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PropertyChanged += OnMainVmPropertyChanged;

            var lastTab = _settings.Settings.LastActiveTab;
            if (!string.IsNullOrEmpty(lastTab))
            {
                vm.SelectTabCommand.Execute(lastTab);
            }
        }

        // Initial content area measurement
        UpdateContentAreaWidth();
    }

    private void OnMainVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsSidebarCollapsed))
        {
            // Sidebar toggle changes available content width
            UpdateContentAreaWidth();
        }
    }

    /// <summary>
    /// Computes content area width (window width minus nav sidebar) and feeds it
    /// to the responsive layout service.
    /// </summary>
    private void UpdateContentAreaWidth()
    {
        if (!_isInitialized) return;

        // Nav sidebar is 220px expanded or 56px collapsed (from MainWindow.axaml styles)
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

        // Save when size or state changes
        if (e.Property == WidthProperty || e.Property == HeightProperty || e.Property == WindowStateProperty)
        {
            _settings.UpdateWindowBounds(this);
            UpdateContentAreaWidth();
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only drag on left mouse button
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            base.OnKeyDown(e);
            return;
        }

        // Check for modifier key (Cmd on macOS, Ctrl on other platforms)
        var isCmdOrCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Meta) ||
                          e.KeyModifiers.HasFlag(KeyModifiers.Control);

        // Chord second key: if chord prefix is active, check the next key
        if (_chordPrefixActive)
        {
            _chordPrefixActive = false;
            _chordTimer?.Stop();
            _chordTimer?.Dispose();
            _chordTimer = null;

            if (e.Key == Key.W)
            {
                // Close palette (was opened immediately on Cmd+K) and run chord action
                if (vm.CommandPaletteVM.IsOpen)
                    vm.CommandPaletteVM.CloseCommand.Execute(null);
                vm.WorkspaceSwitcherVM.OpenCommand.Execute(null);
                e.Handled = true;
                return;
            }
            // Other chord keys can be added here
            // If unrecognized second key, fall through to normal handling
        }

        // Plugin Palette: Cmd+/ or Ctrl+/ — open the active plugin's "add_block" palette
        if (isCmdOrCtrl && (e.Key == Key.Oem2 || e.Key == Key.OemQuestion))
        {
            vm.CommandPaletteVM.OpenPluginPaletteForActivePlugin("add_block");
            e.Handled = true;
            return;
        }

        // Command Palette: Cmd+K or Ctrl+K (also starts chord prefix)
        if (isCmdOrCtrl && e.Key == Key.K)
        {
            // Open the palette immediately AND start chord prefix.
            // If a chord key (e.g. W) is pressed before KeyUp, we'll close the palette
            // and execute the chord action instead. This eliminates the 1-second delay.
            _chordPrefixActive = true;
            _chordTimer?.Stop();
            _chordTimer?.Dispose();
            // Short timeout to clear chord state if no second key arrives
            _chordTimer = new System.Timers.Timer(300);
            _chordTimer.AutoReset = false;
            _chordTimer.Elapsed += (_, _) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _chordPrefixActive = false;
                });
            };
            _chordTimer.Start();
            vm.CommandPaletteVM.ToggleCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Toggle sidebar: Cmd+\ or Ctrl+\
        if (isCmdOrCtrl && e.Key == Key.OemBackslash)
        {
            vm.ToggleSidebarCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // New page from template: Cmd+Shift+N or Ctrl+Shift+N (when in Notes)
        if (isCmdOrCtrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.N)
        {
            // Use reflection-free check: if the current VM has OpenTemplatePickerCommand
            if (vm.CurrentViewModel is CommunityToolkit.Mvvm.ComponentModel.ObservableObject currentVm
                && vm.SelectedTab == "Notes")
            {
                var cmdProp = currentVm.GetType().GetProperty("OpenTemplatePickerCommand");
                if (cmdProp?.GetValue(currentVm) is System.Windows.Input.ICommand cmd)
                {
                    cmd.Execute(null);
                    e.Handled = true;
                    return;
                }
            }
        }

        // Info Panel: Cmd+I or Ctrl+I
        if (isCmdOrCtrl && e.Key == Key.I)
        {
            vm.ToggleInfoPanelCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Speech-to-text: Cmd+M or Ctrl+M
        if (isCmdOrCtrl && e.Key == Key.M)
        {
            _ = HandleSpeechToTextAsync(vm);
            e.Handled = true;
            return;
        }

        // Quick navigation shortcuts
        if (isCmdOrCtrl)
        {
            switch (e.Key)
            {
                // Cmd+1: Notes
                case Key.D1:
                    vm.SelectTabCommand.Execute("Notes");
                    e.Handled = true;
                    return;

                // Cmd+2: Tasks
                case Key.D2:
                    vm.SelectTabCommand.Execute("Tasks");
                    e.Handled = true;
                    return;

                // Cmd+3: Calendar
                case Key.D3:
                    vm.SelectTabCommand.Execute("Calendar");
                    e.Handled = true;
                    return;

                // Cmd+4: Budget
                case Key.D4:
                    vm.SelectTabCommand.Execute("Budget");
                    e.Handled = true;
                    return;

                // Cmd+5: Journal
                case Key.D5:
                    vm.SelectTabCommand.Execute("Journal");
                    e.Handled = true;
                    return;

                // Cmd+6: Files
                case Key.D6:
                    vm.SelectTabCommand.Execute("Files");
                    e.Handled = true;
                    return;

                // Cmd+7: Snippets
                case Key.D7:
                    vm.SelectTabCommand.Execute("Snippets");
                    e.Handled = true;
                    return;

                // Cmd+8: RSS
                case Key.D8:
                    vm.SelectTabCommand.Execute("RSS");
                    e.Handled = true;
                    return;

                // Cmd+9: Contacts
                case Key.D9:
                    vm.SelectTabCommand.Execute("Contacts");
                    e.Handled = true;
                    return;
            }
        }

        // Escape to close overlays/menus
        if (e.Key == Key.Escape)
        {
            if (vm.WorkspaceSwitcherVM.IsOpen)
            {
                vm.WorkspaceSwitcherVM.CloseCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (vm.CommandPaletteVM.IsOpen)
            {
                vm.CommandPaletteVM.CloseCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (vm.SettingsVM.ThemeEditor.IsOpen)
            {
                vm.SettingsVM.ThemeEditor.CancelCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (vm.IsUserMenuOpen)
            {
                vm.CloseUserMenuCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (vm.IsSettingsPanelOpen)
            {
                vm.CloseSettingsCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (vm.IsSyncPanelOpen)
            {
                vm.ToggleSyncPanelCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (vm.IsUpdatePanelOpen)
            {
                vm.ToggleUpdatePanelCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (vm.InfoPanelVM.IsOpen)
            {
                vm.InfoPanelVM.CloseCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);
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
        var delta = _infoPanelResizeStartX - currentX; // drag left = wider
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

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Close user menu when clicking outside of it
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

    private async Task HandleSpeechToTextAsync(MainWindowViewModel vm)
    {
        var speechVm = vm.SpeechRecordingVM;

        // If in download flow, let the overlay handle interactions
        if (speechVm.IsPromptingDownload || speechVm.IsDownloading)
        {
            return;
        }

        // If already recording, stop and transcribe
        if (speechVm.IsRecording)
        {
            await speechVm.StopAndTranscribeAsync();
            return;
        }

        // If transcribing, ignore
        if (speechVm.IsTranscribing)
        {
            return;
        }

        // Get the currently focused element and store it for text insertion
        var focusedElement = FocusManager?.GetFocusedElement();
        var targetControl = FindTextInputControl(focusedElement);

        if (targetControl != null)
        {
            _speechTargetControl = targetControl;
            // TryStartAsync handles: feature enabled check, model download prompt, and recording start
            await speechVm.TryStartAsync();
        }
    }

    private static Control? FindTextInputControl(object? focusedElement)
    {
        if (focusedElement is TextBox textBox)
            return textBox;

        if (focusedElement is TextEditor editor)
            return editor;

        if (focusedElement is RichTextEditorControl rte)
            return rte;

        // Check parent chain for text editors (sometimes focus is on inner elements)
        if (focusedElement is Control control)
        {
            var parent = control.Parent;
            while (parent != null)
            {
                if (parent is TextBox parentTextBox)
                    return parentTextBox;
                if (parent is TextEditor parentEditor)
                    return parentEditor;
                if (parent is RichTextEditorControl parentRte)
                    return parentRte;
                parent = parent.Parent;
            }
        }

        return null;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Save final window state
        _settings.UpdateWindowBounds(this);
        _settings.Flush();

        // Cleanup resources when window closes
        if (DataContext is MainWindowViewModel vm)
        {
            vm.Cleanup();
        }

        base.OnClosing(e);
    }
}
