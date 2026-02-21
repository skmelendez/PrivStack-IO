using Avalonia.Input;
using Avalonia.Interactivity;
using PrivStack.Desktop.ViewModels;

namespace PrivStack.Desktop.Views;

public partial class MainWindow
{
    /// <summary>
    /// Tunnel-phase handler: intercepts KeyDown before child controls (TextBox, RichTextEditor)
    /// can consume the event. Only handles known modifier combos — regular typing passes through.
    /// </summary>
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

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
                if (vm.CommandPaletteVM.IsOpen)
                    vm.CommandPaletteVM.CloseCommand.Execute(null);
                vm.WorkspaceSwitcherVM.OpenCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        // All remaining shortcuts require Cmd/Ctrl — let regular typing pass through
        if (!isCmdOrCtrl && e.Key != Key.Escape)
            return;

        // Plugin Palette: Cmd+/ or Ctrl+/
        if (isCmdOrCtrl && (e.Key == Key.Oem2 || e.Key == Key.OemQuestion))
        {
            vm.CommandPaletteVM.OpenPluginPaletteForActivePlugin("add_block");
            e.Handled = true;
            return;
        }

        // Universal Search: Cmd+K or Ctrl+K (also starts chord prefix)
        if (isCmdOrCtrl && e.Key == Key.K)
        {
            _chordPrefixActive = true;
            _chordTimer?.Stop();
            _chordTimer?.Dispose();
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
            if (_universalSearch != null)
                _universalSearch.FocusSearchBar();
            else
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

        // Quick navigation shortcuts: Cmd+1 through Cmd+9
        if (isCmdOrCtrl)
        {
            switch (e.Key)
            {
                case Key.D1:
                    vm.SelectTabCommand.Execute("Notes");
                    e.Handled = true;
                    return;
                case Key.D2:
                    vm.SelectTabCommand.Execute("Tasks");
                    e.Handled = true;
                    return;
                case Key.D3:
                    vm.SelectTabCommand.Execute("Calendar");
                    e.Handled = true;
                    return;
                case Key.D4:
                    vm.SelectTabCommand.Execute("Budget");
                    e.Handled = true;
                    return;
                case Key.D5:
                    vm.SelectTabCommand.Execute("Journal");
                    e.Handled = true;
                    return;
                case Key.D6:
                    vm.SelectTabCommand.Execute("Files");
                    e.Handled = true;
                    return;
                case Key.D7:
                    vm.SelectTabCommand.Execute("Snippets");
                    e.Handled = true;
                    return;
                case Key.D8:
                    vm.SelectTabCommand.Execute("RSS");
                    e.Handled = true;
                    return;
                case Key.D9:
                    vm.SelectTabCommand.Execute("Contacts");
                    e.Handled = true;
                    return;
            }
        }

        // Plugin-registered quick action shortcuts (e.g., Cmd+T → "New Quick Task").
        // Runs after all shell-owned shortcuts so plugins cannot shadow Cmd+K, Cmd+I, etc.
        if (isCmdOrCtrl)
        {
            var shortcutHint = BuildShortcutHint(e.KeyModifiers, e.Key);
            if (shortcutHint != null)
            {
                var quickActionService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                    .GetService<Services.QuickActionService>(App.Services);
                var entry = quickActionService?.FindActionByShortcut(shortcutHint);
                if (entry != null)
                {
                    _ = quickActionService!.InvokeActionAsync(entry, vm);
                    e.Handled = true;
                    return;
                }
            }
        }

        // Escape to close overlays/menus
        if (e.Key == Key.Escape)
        {
            if (vm.IsQuickActionOverlayOpen)
            {
                vm.CloseQuickActionOverlay();
                e.Handled = true;
                return;
            }

            if (vm.SubscriptionBadgeVM.IsModalOpen)
            {
                vm.SubscriptionBadgeVM.CloseModalCommand.Execute(null);
                e.Handled = true;
                return;
            }

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

            if (vm.IsAiTrayOpen)
            {
                vm.ToggleAiTrayCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (vm.UpdateVM.IsUpdateModalOpen)
            {
                vm.UpdateVM.CloseModalCommand.Execute(null);
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
    }

    /// <summary>
    /// Converts a key + modifiers into the normalized shortcut hint format
    /// used by <see cref="Sdk.Capabilities.QuickActionDescriptor.DefaultShortcutHint"/>.
    /// Returns null for keys that don't map to a single letter/symbol shortcut.
    /// Format: "Cmd+T", "Cmd+Shift+S", etc.
    /// </summary>
    private static string? BuildShortcutHint(KeyModifiers modifiers, Key key)
    {
        // Only handle single-letter keys (A-Z) for quick action shortcuts
        if (key < Key.A || key > Key.Z)
            return null;

        var parts = new System.Text.StringBuilder();
        parts.Append("Cmd+");

        if (modifiers.HasFlag(KeyModifiers.Shift))
            parts.Append("Shift+");

        if (modifiers.HasFlag(KeyModifiers.Alt))
            parts.Append("Alt+");

        parts.Append((char)('A' + (key - Key.A)));
        return parts.ToString();
    }
}
