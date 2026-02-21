namespace PrivStack.Desktop.ViewModels.AiTray;

/// <summary>
/// Discriminator for card types in the unified AI suggestion tray.
/// </summary>
public enum AiTrayCardType
{
    Intent,
    Content
}

/// <summary>
/// Common interface for all card view models displayed in the unified AI suggestion tray.
/// Both intent suggestion cards and content suggestion cards implement this.
/// </summary>
[Obsolete("Replaced by AiChatMessageViewModel in the chat-style tray. Retained for backward compatibility.")]
public interface IAiTrayCardViewModel
{
    string CardId { get; }
    string Title { get; }
    string? Summary { get; }
    string SourcePluginId { get; }
    DateTimeOffset CreatedAt { get; }
    AiTrayCardType CardType { get; }
}
