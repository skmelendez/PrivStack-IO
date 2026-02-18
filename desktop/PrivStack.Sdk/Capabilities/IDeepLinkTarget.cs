namespace PrivStack.Sdk.Capabilities;

/// <summary>
/// Capability interface for plugins that support deep-link navigation to a specific item.
/// The shell uses <see cref="SupportedLinkTypes"/> to route incoming link requests to the correct plugin.
/// </summary>
public interface IDeepLinkTarget
{
    /// <summary>
    /// The primary link type this plugin handles (e.g., "task", "note", "contact", "event", "journal").
    /// </summary>
    string LinkType { get; }

    /// <summary>
    /// All link types this plugin can navigate to. Plugins that manage multiple entity types
    /// (e.g., Contacts managing "contact", "company", "contact_group") should override this.
    /// Defaults to <c>[LinkType]</c> for backward compatibility.
    /// </summary>
    IReadOnlyList<string> SupportedLinkTypes => [LinkType];

    /// <summary>
    /// Navigates to the specified item within this plugin's view.
    /// Called by the shell after switching to this plugin's tab.
    /// </summary>
    Task NavigateToItemAsync(string itemId);

    /// <summary>
    /// Navigates to the specified item with link-type context, allowing the plugin to
    /// dispatch to the correct sub-view (e.g., contacts vs. groups vs. companies).
    /// Default implementation delegates to <see cref="NavigateToItemAsync(string)"/>.
    /// </summary>
    Task NavigateToItemAsync(string linkType, string itemId) => NavigateToItemAsync(itemId);

    /// <summary>
    /// Navigates to the specified item with the original search query context.
    /// Plugins can use this to pre-filter their view to match what the user searched for.
    /// Default implementation delegates to <see cref="NavigateToItemAsync(string)"/>.
    /// </summary>
    Task NavigateToSearchedItemAsync(string itemId, string? searchQuery) => NavigateToItemAsync(itemId);
}
