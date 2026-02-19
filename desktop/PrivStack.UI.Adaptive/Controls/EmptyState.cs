// ============================================================================
// File: EmptyState.cs
// Description: Reusable empty state control. Displays a themed icon bubble,
//              heading, message, and optional primary/secondary action buttons.
//              Variant property changes bubble color semantics (Default/Search/
//              Error/Permission). All colors resolve from the active theme.
// ============================================================================

using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;

namespace PrivStack.UI.Adaptive.Controls;

/// <summary>Semantic variant that controls the icon bubble color palette.</summary>
public enum EmptyStateVariant
{
    /// <summary>Default accent color — "no items yet" states.</summary>
    Default,
    /// <summary>Info blue — "no search results" states.</summary>
    Search,
    /// <summary>Danger red — error or failed states.</summary>
    Error,
    /// <summary>Warning amber — permission / access states.</summary>
    Permission,
}

/// <summary>
/// Centered empty-state panel with icon bubble, heading, message, and optional
/// primary/secondary actions. Compose directly in AXAML or construct in code;
/// all visual tokens are resolved from the active PrivStack theme.
/// </summary>
public sealed class EmptyState : Border
{
    // -------------------------------------------------------------------------
    // Styled properties
    // -------------------------------------------------------------------------

    public static readonly StyledProperty<Geometry?> IconDataProperty =
        AvaloniaProperty.Register<EmptyState, Geometry?>(nameof(IconData));

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> MessageProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(Message), string.Empty);

    public static readonly StyledProperty<string?> ActionLabelProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(ActionLabel));

    public static readonly StyledProperty<ICommand?> ActionCommandProperty =
        AvaloniaProperty.Register<EmptyState, ICommand?>(nameof(ActionCommand));

    public static readonly StyledProperty<string?> SecondaryActionLabelProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(SecondaryActionLabel));

    public static readonly StyledProperty<ICommand?> SecondaryActionCommandProperty =
        AvaloniaProperty.Register<EmptyState, ICommand?>(nameof(SecondaryActionCommand));

    public static readonly StyledProperty<EmptyStateVariant> VariantProperty =
        AvaloniaProperty.Register<EmptyState, EmptyStateVariant>(nameof(Variant), EmptyStateVariant.Default);

    // -------------------------------------------------------------------------
    // CLR accessors
    // -------------------------------------------------------------------------

    public Geometry? IconData
    {
        get => GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string? ActionLabel
    {
        get => GetValue(ActionLabelProperty);
        set => SetValue(ActionLabelProperty, value);
    }

    public ICommand? ActionCommand
    {
        get => GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    public string? SecondaryActionLabel
    {
        get => GetValue(SecondaryActionLabelProperty);
        set => SetValue(SecondaryActionLabelProperty, value);
    }

    public ICommand? SecondaryActionCommand
    {
        get => GetValue(SecondaryActionCommandProperty);
        set => SetValue(SecondaryActionCommandProperty, value);
    }

    public EmptyStateVariant Variant
    {
        get => GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    // -------------------------------------------------------------------------
    // Private children
    // -------------------------------------------------------------------------

    private readonly Border _iconBubble;
    private readonly AvaloniaPath _iconPath;
    private readonly TextBlock _titleBlock;
    private readonly TextBlock _messageBlock;
    private readonly Button _primaryButton;
    private readonly Button _secondaryButton;
    private readonly StackPanel _actionsPanel;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public EmptyState()
    {
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        // Icon path — geometry set by consumer
        _iconPath = new AvaloniaPath
        {
            Width = 36,
            Height = 36,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Colored bubble behind the icon
        _iconBubble = new Border
        {
            Width = 72,
            Height = 72,
            CornerRadius = new CornerRadius(20),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = _iconPath,
        };

        // Title
        _titleBlock = new TextBlock
        {
            TextAlignment = TextAlignment.Center,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _titleBlock.Bind(TextBlock.FontSizeProperty,
            _titleBlock.GetResourceObservable("ThemeFontSizeHeading2"));
        _titleBlock.Bind(TextBlock.ForegroundProperty,
            _titleBlock.GetResourceObservable("ThemeTextPrimaryBrush"));

        // Message
        _messageBlock = new TextBlock
        {
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _messageBlock.Bind(TextBlock.FontSizeProperty,
            _messageBlock.GetResourceObservable("ThemeFontSizeBody"));
        _messageBlock.Bind(TextBlock.ForegroundProperty,
            _messageBlock.GetResourceObservable("ThemeTextMutedBrush"));

        var textStack = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        textStack.Children.Add(_titleBlock);
        textStack.Children.Add(_messageBlock);

        // Primary action button (accent style)
        _primaryButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            IsVisible = false,
        };
        _primaryButton.Classes.Add("accent");

        // Secondary action button (ghost style)
        _secondaryButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            IsVisible = false,
        };
        _secondaryButton.Classes.Add("ghost");

        // Actions container — hidden until at least one button is visible
        _actionsPanel = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsVisible = false,
        };
        _actionsPanel.Children.Add(_primaryButton);
        _actionsPanel.Children.Add(_secondaryButton);

        // Root stack
        var outerStack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 24,
        };
        outerStack.Children.Add(_iconBubble);
        outerStack.Children.Add(textStack);
        outerStack.Children.Add(_actionsPanel);

        Child = outerStack;
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyVariantColors();
        ActualThemeVariantChanged += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyVariantColors();

    // -------------------------------------------------------------------------
    // Property reactions
    // -------------------------------------------------------------------------

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IconDataProperty)
        {
            _iconPath.Data = change.GetNewValue<Geometry?>();
        }
        else if (change.Property == TitleProperty)
        {
            _titleBlock.Text = change.GetNewValue<string>();
        }
        else if (change.Property == MessageProperty)
        {
            _messageBlock.Text = change.GetNewValue<string>();
        }
        else if (change.Property == ActionLabelProperty)
        {
            var label = change.GetNewValue<string?>();
            _primaryButton.Content = label;
            _primaryButton.IsVisible = !string.IsNullOrEmpty(label);
            SyncActionsVisibility();
        }
        else if (change.Property == ActionCommandProperty)
        {
            _primaryButton.Command = change.GetNewValue<ICommand?>();
        }
        else if (change.Property == SecondaryActionLabelProperty)
        {
            var label = change.GetNewValue<string?>();
            _secondaryButton.Content = label;
            _secondaryButton.IsVisible = !string.IsNullOrEmpty(label);
            SyncActionsVisibility();
        }
        else if (change.Property == SecondaryActionCommandProperty)
        {
            _secondaryButton.Command = change.GetNewValue<ICommand?>();
        }
        else if (change.Property == VariantProperty)
        {
            ApplyVariantColors();
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Binds icon bubble background and icon fill to variant-appropriate token keys.</summary>
    private void ApplyVariantColors()
    {
        var (bgKey, fgKey) = Variant switch
        {
            EmptyStateVariant.Search     => ("ThemeInfoMutedBrush",    "ThemeInfoBrush"),
            EmptyStateVariant.Error      => ("ThemeDangerMutedBrush",  "ThemeDangerBrush"),
            EmptyStateVariant.Permission => ("ThemeWarningMutedBrush", "ThemeWarningBrush"),
            _                            => ("ThemePrimaryMutedBrush", "ThemePrimaryBrush"),
        };

        _iconBubble.Bind(BackgroundProperty,
            _iconBubble.GetResourceObservable(bgKey));
        _iconPath.Bind(AvaloniaPath.FillProperty,
            _iconPath.GetResourceObservable(fgKey));
    }

    private void SyncActionsVisibility()
    {
        _actionsPanel.IsVisible = _primaryButton.IsVisible || _secondaryButton.IsVisible;
    }
}
