namespace PrivStack.Sdk.Capabilities;

/// <summary>
/// Composite result returned by <see cref="IGraphDataProvider.GetGraphContributionAsync"/>.
/// </summary>
public sealed record GraphContribution
{
    public IReadOnlyList<GraphNodeContribution> Nodes { get; init; } = [];
    public IReadOnlyList<GraphEdgeContribution> StructuralEdges { get; init; } = [];
    public IReadOnlyList<ContentField> ContentFields { get; init; } = [];
    public IReadOnlyList<ExplicitLinkContribution> ExplicitLinks { get; init; } = [];
}

/// <summary>
/// A single entity represented as a graph node.
/// The plugin is responsible for setting its own <see cref="LinkType"/> and <see cref="NodeType"/>
/// to support multi-entity plugins (e.g., Notes contributes both "page" and "wiki_source").
/// </summary>
public sealed record GraphNodeContribution
{
    /// <summary>Entity ID (without link type prefix).</summary>
    public required string Id { get; init; }

    /// <summary>Display title for the node.</summary>
    public required string Title { get; init; }

    /// <summary>Link type key (e.g., "page", "task", "contact"). Must match ILinkableItemProvider.LinkType.</summary>
    public required string LinkType { get; init; }

    /// <summary>Node type for graph visualization grouping (e.g., "note", "task", "contact").</summary>
    public required string NodeType { get; init; }

    /// <summary>Icon name for display.</summary>
    public string? Icon { get; init; }

    /// <summary>Tags attached to this entity.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Last modified timestamp.</summary>
    public DateTimeOffset ModifiedAt { get; init; }

    /// <summary>Composite key in "linkType:id" format.</summary>
    public string CompositeKey => $"{LinkType}:{Id}";
}

/// <summary>
/// A structural relationship between two entities (parent-child, company membership, project membership, etc.).
/// </summary>
public sealed record GraphEdgeContribution
{
    /// <summary>Source composite key in "linkType:id" format.</summary>
    public required string SourceKey { get; init; }

    /// <summary>Target composite key in "linkType:id" format.</summary>
    public required string TargetKey { get; init; }

    /// <summary>Edge type (e.g., "parent", "company", "group", "project", "wiki_source").</summary>
    public required string EdgeType { get; init; }

    /// <summary>Optional display label for the edge.</summary>
    public string? Label { get; init; }
}

/// <summary>
/// Raw text content from an entity, used for centralized wiki-link parsing.
/// </summary>
public sealed record ContentField
{
    /// <summary>Owner composite key in "linkType:id" format.</summary>
    public required string OwnerKey { get; init; }

    /// <summary>Concatenated text content for wiki-link parsing.</summary>
    public required string Content { get; init; }
}

/// <summary>
/// An explicit cross-entity link (from custom_fields linked_items, task_links, etc.).
/// </summary>
public sealed record ExplicitLinkContribution
{
    /// <summary>Source composite key in "linkType:id" format.</summary>
    public required string SourceKey { get; init; }

    /// <summary>Target composite key in "linkType:id" format.</summary>
    public required string TargetKey { get; init; }
}
