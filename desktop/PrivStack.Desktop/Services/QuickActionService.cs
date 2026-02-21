using PrivStack.Desktop.Services.Plugin;
using PrivStack.Desktop.ViewModels;
using PrivStack.Sdk.Capabilities;
using Serilog;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Aggregates <see cref="IQuickActionProvider"/> actions from all plugins and exposes them
/// as command palette entries. Handles invocation of both immediate and UI-based actions.
/// </summary>
public sealed class QuickActionService : ICommandProvider
{
    private static readonly ILogger _log = Log.ForContext<QuickActionService>();

    private readonly IPluginRegistry _pluginRegistry;
    private List<QuickActionEntry>? _cachedActions;

    public QuickActionService(IPluginRegistry pluginRegistry)
    {
        _pluginRegistry = pluginRegistry;
    }

    public int Priority => 50;

    /// <summary>
    /// Invalidates the cached action list. Call when plugins are activated/deactivated.
    /// </summary>
    public void Invalidate() => _cachedActions = null;

    /// <summary>
    /// Gets all quick actions from all active IQuickActionProvider plugins.
    /// </summary>
    public IReadOnlyList<QuickActionEntry> GetAllActions()
    {
        if (_cachedActions != null)
            return _cachedActions;

        var actions = new List<QuickActionEntry>();
        var providers = _pluginRegistry.GetCapabilityProviders<IQuickActionProvider>();

        foreach (var provider in providers)
        {
            try
            {
                foreach (var descriptor in provider.GetQuickActions())
                {
                    actions.Add(new QuickActionEntry(descriptor, provider));
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to get quick actions from provider");
            }
        }

        _cachedActions = actions;
        return actions;
    }

    /// <summary>
    /// Finds a quick action by its action ID.
    /// </summary>
    public QuickActionEntry? FindAction(string actionId)
    {
        return GetAllActions().FirstOrDefault(a =>
            string.Equals(a.Descriptor.ActionId, actionId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds a quick action by its <see cref="QuickActionDescriptor.DefaultShortcutHint"/>.
    /// The hint uses a normalized format like "Cmd+T", "Cmd+Shift+S", etc.
    /// </summary>
    public QuickActionEntry? FindActionByShortcut(string shortcutHint)
    {
        return GetAllActions().FirstOrDefault(a =>
            string.Equals(a.Descriptor.DefaultShortcutHint, shortcutHint, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Invokes a quick action. For HasUI actions, shows the overlay via MainWindowViewModel.
    /// For immediate actions, calls ExecuteQuickActionAsync directly.
    /// </summary>
    public async Task InvokeActionAsync(QuickActionEntry entry, MainWindowViewModel mainVm)
    {
        if (entry.Descriptor.HasUI)
        {
            try
            {
                var content = entry.Provider.CreateQuickActionContent(entry.Descriptor.ActionId);
                if (content != null)
                {
                    // Subscribe to CloseRequested so the shell handles closing — no reflection needed in plugins
                    if (content is IQuickActionForm form)
                    {
                        form.CloseRequested += () =>
                            Avalonia.Threading.Dispatcher.UIThread.Post(mainVm.CloseQuickActionOverlay);
                    }

                    mainVm.ShowQuickActionOverlay(entry.Descriptor.DisplayName, content);
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to create quick action UI for {ActionId}", entry.Descriptor.ActionId);
            }
        }
        else
        {
            try
            {
                await entry.Provider.ExecuteQuickActionAsync(entry.Descriptor.ActionId);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to execute quick action {ActionId}", entry.Descriptor.ActionId);
            }
        }
    }

    // ========================================================================
    // ICommandProvider — surfaces quick actions in the command palette
    // ========================================================================

    public IEnumerable<CommandDefinition> GetCommands()
    {
        var mainVm = _pluginRegistry.GetMainViewModel();
        if (mainVm == null) yield break;

        foreach (var entry in GetAllActions())
        {
            var capturedEntry = entry;
            yield return new CommandDefinition
            {
                Name = entry.Descriptor.DisplayName,
                Description = entry.Descriptor.Description ?? "Quick action",
                Keywords = $"quick action {entry.Descriptor.DisplayName.ToLowerInvariant()}",
                Category = entry.Descriptor.Category,
                Icon = entry.Descriptor.Icon,
                Execute = () => _ = InvokeActionAsync(capturedEntry, mainVm),
            };
        }
    }
}

/// <summary>
/// Pairs a <see cref="QuickActionDescriptor"/> with its owning <see cref="IQuickActionProvider"/>.
/// </summary>
public sealed record QuickActionEntry(QuickActionDescriptor Descriptor, IQuickActionProvider Provider);
