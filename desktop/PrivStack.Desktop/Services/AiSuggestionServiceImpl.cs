using CommunityToolkit.Mvvm.Messaging;
using PrivStack.Sdk.Capabilities;
using PrivStack.Sdk.Messaging;
using PrivStack.Sdk.Services;

namespace PrivStack.Desktop.Services;

/// <summary>
/// Shell-side implementation of <see cref="IAiSuggestionService"/>.
/// Translates Push/Update/Remove calls into WeakReferenceMessenger messages
/// consumed by the unified AI suggestion tray.
/// </summary>
internal sealed class AiSuggestionServiceImpl : IAiSuggestionService
{
    public void Push(ContentSuggestionCard card)
    {
        WeakReferenceMessenger.Default.Send(new ContentSuggestionPushedMessage { Card = card });
    }

    public void Update(string suggestionId, string pluginId,
        ContentSuggestionState? newState = null,
        string? newContent = null,
        string? errorMessage = null,
        IReadOnlyList<SuggestionAction>? newActions = null)
    {
        WeakReferenceMessenger.Default.Send(new ContentSuggestionUpdatedMessage
        {
            SuggestionId = suggestionId,
            PluginId = pluginId,
            NewState = newState,
            NewContent = newContent,
            ErrorMessage = errorMessage,
            NewActions = newActions
        });
    }

    public void Remove(string suggestionId, string pluginId)
    {
        WeakReferenceMessenger.Default.Send(new ContentSuggestionRemovedMessage
        {
            SuggestionId = suggestionId,
            PluginId = pluginId
        });
    }
}
