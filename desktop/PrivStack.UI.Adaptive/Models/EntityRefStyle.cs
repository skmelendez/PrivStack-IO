// ============================================================================
// File: EntityRefStyle.cs
// Description: Display configuration for entity reference cards on the canvas.
// ============================================================================

namespace PrivStack.UI.Adaptive.Models;

/// <summary>
/// Defines how a specific entity type renders as a canvas reference card.
/// Keyed by entity type string (e.g. "task", "contact") in the styles dictionary.
/// </summary>
public sealed record EntityRefStyle(
    string DisplayName,
    string BadgeColor,
    string BadgeIcon);
