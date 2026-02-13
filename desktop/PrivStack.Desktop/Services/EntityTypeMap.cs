namespace PrivStack.Desktop.Services;

/// <summary>
/// Shared mapping between SDK entity types, link types, icons, and display names.
/// Used by BacklinkService, InfoPanelViewModel, and EntityMetadataService.
/// </summary>
public static class EntityTypeMap
{
    public sealed record EntityTypeInfo(string EntityType, string LinkType, string Icon, string DisplayName);

    /// <summary>
    /// All known entity types in the system.
    /// LinkType MUST match ILinkableItemProvider.LinkType so wiki-link targets resolve correctly.
    /// </summary>
    public static readonly EntityTypeInfo[] All =
    [
        new("page", "page", "Document", "Notes"),
        new("task", "task", "CheckSquare", "Tasks"),
        new("contact", "contact", "User", "Contacts"),
        new("event", "event", "Calendar", "Calendar"),
        new("journal_entry", "journal", "Book", "Journal"),
        new("project", "project", "Folder", "Projects"),
        new("deal", "deal", "Briefcase", "Deals"),
        new("transaction", "transaction", "DollarSign", "Ledger"),
        new("snippet", "snippet", "Code", "Snippets"),
        new("rss_article", "rss_article", "Rss", "RSS"),
        new("credential", "credential", "Lock", "Passwords"),
        new("vault_file", "file", "FileText", "Files"),
        new("sticky_note", "sticky_note", "Edit", "Sticky Notes"),
        new("wiki_source", "wiki_source", "Globe", "Wiki Sources"),
    ];

    private static readonly Dictionary<string, EntityTypeInfo> _byLinkType =
        All.ToDictionary(e => e.LinkType);

    private static readonly Dictionary<string, EntityTypeInfo> _byEntityType =
        All.ToDictionary(e => e.EntityType);

    public static string? GetEntityType(string linkType) =>
        _byLinkType.TryGetValue(linkType, out var info) ? info.EntityType : null;

    public static string? GetIcon(string linkType) =>
        _byLinkType.TryGetValue(linkType, out var info) ? info.Icon : null;

    public static string? GetDisplayName(string linkType) =>
        _byLinkType.TryGetValue(linkType, out var info) ? info.DisplayName : null;

    public static string? GetLinkTypeForEntityType(string entityType) =>
        _byEntityType.TryGetValue(entityType, out var info) ? info.LinkType : null;

    public static EntityTypeInfo? GetByLinkType(string linkType) =>
        _byLinkType.TryGetValue(linkType, out var info) ? info : null;
}
