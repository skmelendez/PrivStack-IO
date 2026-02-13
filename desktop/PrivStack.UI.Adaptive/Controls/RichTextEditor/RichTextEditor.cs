// ============================================================================
// File: RichTextEditor.cs
// Description: Custom Avalonia Control for WYSIWYG editing of inline-formatted
//              text within a single block. Renders styled text via DrawingContext,
//              handles caret, selection, keyboard/pointer input, and formatting.
//              Storage format is markdown; user never sees raw markdown.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace PrivStack.UI.Adaptive.Controls.RichTextEditor;

/// <summary>
/// A single-block rich text editor. Each block (paragraph, heading) gets one instance.
/// Set <see cref="Markdown"/> to populate, read it back to persist.
/// </summary>
public sealed class RichTextEditor : Control
{
    private TextDocument _doc = new();
    private readonly TextLayoutEngine _layout = new();
    private readonly CaretState _caret = new();
    private InlineStyle _activeStyle = InlineStyle.None;
    private bool _isFocused;
    private bool _isDragging;
    private double _renderYOffset;
    private string _blockId = "";
    private System.Timers.Timer? _saveTimer;
    private HashSet<string> _trackedInternalLinks = [];
    private bool _suppressContextMenu;

    // ---- Public properties ----

    public static readonly StyledProperty<string> MarkdownProperty =
        AvaloniaProperty.Register<RichTextEditor, string>(nameof(Markdown), "");

    public string Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public string BlockId
    {
        get => _blockId;
        set => _blockId = value;
    }

    /// <summary>Placeholder text shown when the document is empty and not focused.</summary>
    public string? Placeholder { get; set; }

    public int CaretPosition => _caret.Position;

    public void InsertAtCaret(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _doc.PushUndo();
        DeleteSelectionIfAny();
        _doc.Insert(_caret.Position, text, _activeStyle);
        _caret.Position += text.Length;
        _caret.ClearSelection();
        OnContentChanged();
    }

    /// <summary>
    /// Inserts transcription text, splitting on double-newlines into separate paragraph blocks.
    /// The first paragraph is inserted at the caret; remaining paragraphs are dispatched via
    /// <see cref="ParagraphsInsertRequested"/> for the host to create as new blocks.
    /// Falls back to <see cref="InsertAtCaret"/> when there are no paragraph breaks.
    /// </summary>
    public void InsertTranscription(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        if (paragraphs.Length <= 1)
        {
            InsertAtCaret(text.Trim());
            return;
        }

        // Insert the first paragraph into the current block
        InsertAtCaret(paragraphs[0].Trim());

        // Flush so the host sees the updated text before structural changes
        FlushTextChange();

        // Ask the host to create new paragraph blocks for the rest
        var remaining = new string[paragraphs.Length - 1];
        for (int i = 1; i < paragraphs.Length; i++)
            remaining[i - 1] = paragraphs[i].Trim();

        ParagraphsInsertRequested?.Invoke(_blockId, remaining);
    }

    /// <summary>
    /// Inserts an emoji at the caret with no formatting (no bold, italic, etc.)
    /// and a trailing thin space for visual padding.
    /// </summary>
    public void InsertEmoji(string emoji)
    {
        if (string.IsNullOrEmpty(emoji)) return;
        _doc.PushUndo();
        DeleteSelectionIfAny();
        // Insert emoji with no style so it's never bold/italic/etc.
        _doc.Insert(_caret.Position, emoji + " ", InlineStyle.None);
        _caret.Position += emoji.Length + 1;
        _caret.ClearSelection();
        OnContentChanged();
    }

    public double FontSize
    {
        get => _layout.FontSize;
        set { _layout.FontSize = value; InvalidateMeasure(); }
    }

    public FontWeight BaseFontWeight
    {
        get => _layout.BaseFontWeight;
        set { _layout.BaseFontWeight = value; InvalidateMeasure(); }
    }

    public FontStyle BaseFontStyle
    {
        get => _layout.BaseFontStyle;
        set { _layout.BaseFontStyle = value; InvalidateMeasure(); }
    }

    // ---- Events for block-level operations ----

    /// <summary>Fired when Enter is pressed (no shift). Args: (blockId, markdownAfterCaret).</summary>
    public event Action<string, string>? SplitRequested;

    /// <summary>Fired when a multi-paragraph transcription is inserted.
    /// Args: (blockId, paragraphs). The first paragraph is already inserted into this block;
    /// remaining paragraphs should be created as new blocks by the host.</summary>
    public event Action<string, string[]>? ParagraphsInsertRequested;

    /// <summary>Fired when Backspace at position 0. Args: blockId.</summary>
    public event Action<string>? MergeWithPreviousRequested;

    /// <summary>Fired when Up at first line or Down at last line. Args: (blockId, direction).</summary>
    public event Action<string, int>? FocusAdjacentRequested;

    /// <summary>Fired when block text changes (debounced). Args: (blockId, markdown).</summary>
    public event Action<string, string>? TextChanged;

    /// <summary>Fired when Tab is pressed. Args: blockId. Host can use this for list indentation.</summary>
    public event Action<string>? IndentRequested;

    /// <summary>Fired when Shift+Tab is pressed. Args: blockId.</summary>
    public event Action<string>? OutdentRequested;

    /// <summary>Fired when Cmd/Ctrl+E is pressed. Args: blockId. Host opens emoji picker.</summary>
    public event Action<string>? EmojiPickerRequested;

    /// <summary>Fired when the active inline style changes (caret moved, style toggled, etc.).</summary>
    public event Action<InlineStyle>? ActiveStyleChanged;

    /// <summary>Fired when a link is right-clicked. Args: (editor, linkStart, linkLength, linkText, linkUrl).</summary>
    public event Action<RichTextEditor, int, int, string, string>? LinkEditRequested;

    /// <summary>Fired when user types [[ to invoke the link picker. Args: blockId.</summary>
    public event Action<string>? LinkPickerRequested;

    /// <summary>Fired when a privstack:// internal link is clicked. Args: (linkType, itemId).</summary>
    public event Action<string, string>? InternalLinkActivated;

    /// <summary>Fired when mouse enters a privstack:// internal link. Args: (linkType, itemId). Used for prefetch.</summary>
    public event Action<string, string>? InternalLinkHovered;

    /// <summary>Fired when mouse leaves a privstack:// internal link. Args: (linkType, itemId). Used to cancel prefetch.</summary>
    public event Action<string, string>? InternalLinkUnhovered;

    // Track the currently hovered internal link for hover/unhover events
    private (string LinkType, string ItemId)? _hoveredInternalLink;

    /// <summary>Fired when a privstack:// internal link is removed from content (by editing text).
    /// Args: linkUrl (the full privstack://type/id URL).</summary>
    public event Action<string>? InternalLinkRemoved;

    /// <summary>When true, prevents all text mutations (typing, paste, delete, split, etc.).</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>When false, Enter inserts a newline instead of splitting the block (fires SplitRequested).
    /// Set to false for standalone usage where no block host handles SplitRequested.</summary>
    public bool AllowBlockSplit { get; set; } = true;

    // Track the last focused editor so we can clear its selection when a new one gets focus
    private static WeakReference<RichTextEditor>? _lastFocusedEditor;

    /// <summary>Clears selection in this editor (called externally when another editor gets focus).</summary>
    public void ClearSelection()
    {
        _caret.ClearSelection();
        InvalidateVisual();
    }

    /// <summary>Current active inline style at the caret position.</summary>
    public InlineStyle ActiveStyle => _activeStyle;

    /// <summary>Whether there is currently a text selection.</summary>
    public bool HasSelection => _caret.HasSelection;

    /// <summary>Returns (start, end) of the current selection. Only valid when HasSelection is true.</summary>
    public (int Start, int End) SelectionRange => _caret.SelectionRange;

    /// <summary>The underlying text document.</summary>
    public TextDocument Document => _doc;

    /// <summary>Toggle a style flag on the current selection or active style. Used by external toolbar buttons.</summary>
    public void ToggleStyle(InlineStyle flag)
    {
        ToggleStyleOnSelectionOrActive(flag);
    }

    /// <summary>Apply foreground color to the current selection.</summary>
    public void SetFgColor(TextColor color)
    {
        if (!_caret.HasSelection) return;
        _doc.PushUndo();
        var (s, end) = _caret.SelectionRange;
        _doc.SetFgColor(s, end - s, color);
        OnContentChanged();
    }

    /// <summary>Apply background highlight color to the current selection.</summary>
    public void SetBgColor(TextColor color)
    {
        if (!_caret.HasSelection) return;
        _doc.PushUndo();
        var (s, end) = _caret.SelectionRange;
        _doc.SetBgColor(s, end - s, color);
        OnContentChanged();
    }

    /// <summary>Toggle a style flag on a specific character range. Used by toolbar when selection was saved externally.</summary>
    public void ToggleStyleAtRange(int start, int end, InlineStyle flag)
    {
        if (start < 0 || end > _doc.Length || start >= end) return;
        _doc.PushUndo();
        _doc.ToggleStyle(start, end - start, flag);
        OnContentChanged();
    }

    /// <summary>Apply foreground color to a specific character range.</summary>
    public void SetFgColorAtRange(int start, int end, TextColor color)
    {
        if (start < 0 || end > _doc.Length || start >= end) return;
        _doc.PushUndo();
        _doc.SetFgColor(start, end - start, color);
        OnContentChanged();
    }

    /// <summary>Apply background highlight color to a specific character range.</summary>
    public void SetBgColorAtRange(int start, int end, TextColor color)
    {
        if (start < 0 || end > _doc.Length || start >= end) return;
        _doc.PushUndo();
        _doc.SetBgColor(start, end - start, color);
        OnContentChanged();
    }

    /// <summary>Programmatically set selection range (for restoring after toolbar actions).</summary>
    public void SetSelection(int start, int end)
    {
        if (start < 0 || end > _doc.Length) return;
        _caret.SelectionAnchor = start;
        _caret.Position = end;
        InvalidateVisual();
    }

    /// <summary>
    /// Insert a markdown link at the caret. If there is a selection, the selected
    /// text is replaced with the link text. Otherwise, the link text is inserted.
    /// </summary>
    public void InsertLink(string text, string url)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(url)) return;
        _doc.PushUndo();
        DeleteSelectionIfAny();
        _doc.Insert(_caret.Position, text, _activeStyle | InlineStyle.Link, linkUrl: url);
        _caret.Position += text.Length;
        _caret.ClearSelection();
        OnContentChanged();
    }

    static RichTextEditor()
    {
        FocusableProperty.OverrideDefaultValue<RichTextEditor>(true);
        FocusAdornerProperty.OverrideDefaultValue<RichTextEditor>(null);
        MarkdownProperty.Changed.AddClassHandler<RichTextEditor>((editor, _) => editor.OnMarkdownChanged());
    }

    public RichTextEditor()
    {
        _layout.BaseFont = FontFamily.Default;
        _layout.MonoFont = FontFamily.Default;
        _caret.BlinkChanged += () => InvalidateVisual();
        Cursor = new Cursor(StandardCursorType.Ibeam);

        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_suppressContextMenu)
        {
            e.Handled = true;
            _suppressContextMenu = false;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged += OnThemeVariantChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged -= OnThemeVariantChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void OnMarkdownChanged()
    {
        _doc = TextDocument.FromMarkdown(Markdown);
        SnapshotInternalLinks();
        Relayout();
    }

    private void Relayout()
    {
        _layout.SetContent(_doc.GetSpans(), _doc.Text);
        _layout.Layout();
        InvalidateMeasure();
        InvalidateVisual();
    }

    // ---- Measurement ----

    protected override Size MeasureOverride(Size availableSize)
    {
        var isWidthConstrained = !double.IsInfinity(availableSize.Width) && availableSize.Width > 0;
        _layout.MaxWidth = isWidthConstrained ? availableSize.Width : double.MaxValue;
        ResolveFonts();
        _layout.Layout();
        var minHeight = _layout.FontSize * 1.6;
        var width = isWidthConstrained ? availableSize.Width : Math.Max(_layout.ContentWidth, 20);
        return new Size(width, Math.Max(_layout.TotalHeight, minHeight));
    }

    private void ResolveFonts()
    {
        var app = Application.Current;
        if (app is null) return;
        if (app.Resources.TryGetResource("ThemeFontSans", app.ActualThemeVariant, out var sans) && sans is FontFamily sf)
            _layout.BaseFont = sf;
        if (app.Resources.TryGetResource("ThemeFontMono", app.ActualThemeVariant, out var mono) && mono is FontFamily mf)
            _layout.MonoFont = mf;
    }

    // ---- Rendering ----

    private static IBrush GetBrush(string key, IBrush fallback)
    {
        var app = Application.Current;
        if (app is null) return fallback;
        if (app.Resources.TryGetResource(key, app.ActualThemeVariant, out var v) && v is IBrush b)
            return b;
        return app.FindResource(key) as IBrush ?? fallback;
    }

    private static readonly Dictionary<TextColor, Color> FgColorMap = new()
    {
        [TextColor.Gray] = Color.Parse("#9B9A97"),
        [TextColor.Brown] = Color.Parse("#64473A"),
        [TextColor.Orange] = Color.Parse("#D9730D"),
        [TextColor.Yellow] = Color.Parse("#DFAB01"),
        [TextColor.Green] = Color.Parse("#0F7B6C"),
        [TextColor.Blue] = Color.Parse("#0B6E99"),
        [TextColor.Purple] = Color.Parse("#6940A5"),
        [TextColor.Pink] = Color.Parse("#AD1A72"),
        [TextColor.Red] = Color.Parse("#E03E3E"),
    };

    private static readonly Dictionary<TextColor, Color> BgColorMap = new()
    {
        [TextColor.Gray] = Color.FromArgb(60, 155, 154, 151),
        [TextColor.Brown] = Color.FromArgb(60, 100, 71, 58),
        [TextColor.Orange] = Color.FromArgb(60, 217, 115, 13),
        [TextColor.Yellow] = Color.FromArgb(60, 223, 171, 1),
        [TextColor.Green] = Color.FromArgb(60, 15, 123, 108),
        [TextColor.Blue] = Color.FromArgb(60, 11, 110, 153),
        [TextColor.Purple] = Color.FromArgb(60, 105, 64, 165),
        [TextColor.Pink] = Color.FromArgb(60, 173, 26, 114),
        [TextColor.Red] = Color.FromArgb(60, 224, 62, 62),
    };

    private static IBrush GetFgBrush(TextColor color) =>
        FgColorMap.TryGetValue(color, out var c) ? new SolidColorBrush(c) : Brushes.Transparent;

    private static IBrush GetBgBrush(TextColor color) =>
        BgColorMap.TryGetValue(color, out var c) ? new SolidColorBrush(c) : Brushes.Transparent;

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        // Draw a transparent fill so Avalonia's visual hit testing recognizes the area.
        ctx.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));

        var textBrush = GetBrush("ThemeTextPrimaryBrush", Brushes.White);
        var selectionBrush = GetBrush("ThemePrimaryBrush", Brushes.DodgerBlue);
        var codeBgBrush = GetBrush("ThemeSurfaceElevatedBrush", Brushes.DarkGray);
        var mutedBrush = GetBrush("ThemeTextMutedBrush", Brushes.Gray);

        // Vertically center content when shorter than bounds (single-line editors)
        _renderYOffset = _layout.TotalHeight < Bounds.Height
            ? (Bounds.Height - _layout.TotalHeight) / 2
            : 0;
        var yOffset = _renderYOffset;
        using var _ = yOffset > 0
            ? ctx.PushTransform(Matrix.CreateTranslation(0, yOffset))
            : default;

        // 0. Placeholder when empty
        if (_doc.Length == 0 && !string.IsNullOrEmpty(Placeholder))
        {
            var ft = new FormattedText(Placeholder,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(_layout.BaseFont, Avalonia.Media.FontStyle.Italic),
                _layout.FontSize,
                mutedBrush);
            // Center placeholder vertically within the line height, same as regular text runs
            var lineH = _layout.Lines.Count > 0 ? _layout.Lines[0].Height : _layout.FontSize * 1.6;
            var placeholderY = (lineH - ft.Height) / 2;
            ctx.DrawText(ft, new Point(0, placeholderY));
        }

        // 1. Selection highlights
        if (_caret.HasSelection)
        {
            var (selStart, selEnd) = _caret.SelectionRange;
            var rects = _layout.GetSelectionRects(selStart, selEnd);
            foreach (var rect in rects)
            {
                ctx.DrawRectangle(
                    new SolidColorBrush(((ISolidColorBrush)selectionBrush).Color, 0.3),
                    null, rect);
            }
        }

        // 2. Text runs
        var linkBrush = GetBrush("ThemeLinkBrush", new SolidColorBrush(Color.Parse("#0B6E99")));
        var internalLinkBgBrush = new SolidColorBrush(Color.FromArgb(35, 100, 150, 255));

        // Pre-pass: draw internal link pill backgrounds grouped per-link (avoids per-word overlap)
        foreach (var line in _layout.Lines)
        {
            string? activeLinkUrl = null;
            double pillLeft = 0, pillRight = 0;

            foreach (var run in line.Runs)
            {
                if (run.Length == 0) continue;

                var isInternal = run.Style.HasFlag(InlineStyle.Link) &&
                    !string.IsNullOrEmpty(run.LinkUrl) &&
                    run.LinkUrl.StartsWith("privstack://", StringComparison.OrdinalIgnoreCase);

                if (isInternal && run.LinkUrl == activeLinkUrl)
                {
                    pillRight = run.X + run.Width;
                }
                else
                {
                    if (activeLinkUrl != null)
                    {
                        var pillRect = new Rect(pillLeft - 4, line.Y + 2, (pillRight - pillLeft) + 8, line.Height - 4);
                        ctx.DrawRectangle(internalLinkBgBrush, null, pillRect, 4, 4);
                    }
                    if (isInternal)
                    {
                        activeLinkUrl = run.LinkUrl;
                        pillLeft = run.X;
                        pillRight = run.X + run.Width;
                    }
                    else
                    {
                        activeLinkUrl = null;
                    }
                }
            }
            if (activeLinkUrl != null)
            {
                var pillRect = new Rect(pillLeft - 4, line.Y + 2, (pillRight - pillLeft) + 8, line.Height - 4);
                ctx.DrawRectangle(internalLinkBgBrush, null, pillRect, 4, 4);
            }
        }

        foreach (var line in _layout.Lines)
        {
            foreach (var run in line.Runs)
            {
                if (run.FormattedText is null || run.Length == 0) continue;

                // Check if this is an internal link (privstack://)
                var isInternalLink = run.Style.HasFlag(InlineStyle.Link) &&
                    !string.IsNullOrEmpty(run.LinkUrl) &&
                    run.LinkUrl.StartsWith("privstack://", StringComparison.OrdinalIgnoreCase);

                // Determine text brush: fg color overrides theme default; links use primary
                IBrush brush;
                if (run.FgColor != TextColor.Default)
                    brush = GetFgBrush(run.FgColor);
                else if (run.Style.HasFlag(InlineStyle.Link))
                    brush = linkBrush;
                else if (run.Style.HasFlag(InlineStyle.Code))
                    brush = mutedBrush;
                else
                    brush = textBrush;

#pragma warning disable CS0618
                var ft = new FormattedText(
                    _doc.Text.Substring(run.StartIndex, run.Length),
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    run.Typeface,
                    run.RunFontSize,
                    brush);
#pragma warning restore CS0618
                // Center text vertically within the line height, apply superscript YOffset
                var textYOffset = (line.Height - ft.Height) / 2 + run.YOffset;

                // Background color highlight
                if (run.BgColor != TextColor.Default)
                {
                    var bgRect = new Rect(run.X, line.Y + textYOffset, run.Width, ft.Height);
                    ctx.DrawRectangle(GetBgBrush(run.BgColor), null, bgRect, 2, 2);
                }

                // Code background (aligned with centered text)
                if (run.Style.HasFlag(InlineStyle.Code))
                {
                    var bgRect = new Rect(run.X - 2, line.Y + textYOffset - 1, run.Width + 4, ft.Height + 2);
                    ctx.DrawRectangle(codeBgBrush, null, bgRect, 3, 3);
                }

                ctx.DrawText(ft, new Point(run.X, line.Y + textYOffset));

                // Strikethrough (centered with text)
                if (run.Style.HasFlag(InlineStyle.Strikethrough))
                {
                    var y = line.Y + textYOffset + ft.Height * 0.5;
                    ctx.DrawLine(new Pen(textBrush, 1),
                        new Point(run.X, y),
                        new Point(run.X + run.Width, y));
                }

                // Underline (at baseline)
                if (run.Style.HasFlag(InlineStyle.Underline))
                {
                    var y = line.Y + textYOffset + ft.Height - 1;
                    ctx.DrawLine(new Pen(brush, 1),
                        new Point(run.X, y),
                        new Point(run.X + run.Width, y));
                }

                // Link underline for external links only (internal links have pill background instead)
                if (run.Style.HasFlag(InlineStyle.Link) && !run.Style.HasFlag(InlineStyle.Underline) && !isInternalLink)
                {
                    var y = line.Y + textYOffset + ft.Height - 1;
                    ctx.DrawLine(new Pen(brush, 1),
                        new Point(run.X, y),
                        new Point(run.X + run.Width, y));
                }
            }
        }

        // 3. Caret
        if (_isFocused && _caret.IsVisible && !_caret.HasSelection)
        {
            var caretRect = _layout.GetCaretRect(_caret.Position);
            ctx.DrawRectangle(textBrush, null, caretRect);
        }
    }

    // ---- Focus ----

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        _isFocused = true;
        _caret.StartBlinking();

        // Clear selection in the previously focused editor
        if (_lastFocusedEditor != null && _lastFocusedEditor.TryGetTarget(out var prev) && prev != this)
            prev.ClearSelection();
        _lastFocusedEditor = new WeakReference<RichTextEditor>(this);

        InvalidateVisual();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _isFocused = false;
        _caret.StopBlinking();
        _isDragging = false;
        // Don't clear selection — toolbar buttons need it to apply styles
        InvalidateVisual();
    }

    // ---- Pointer input ----

    /// <summary>Find the start index and length of the link span containing the given char index.</summary>
    private (int Start, int Length, string Text, string Url)? FindLinkAt(int charIndex)
    {
        var url = _doc.GetLinkUrlAt(charIndex);
        if (string.IsNullOrEmpty(url)) return null;

        var text = _doc.Text;
        var start = charIndex;
        while (start > 0 && _doc.GetLinkUrlAt(start - 1) == url)
            start--;
        var end = charIndex;
        while (end < text.Length && _doc.GetLinkUrlAt(end) == url)
            end++;
        return (start, end - start, text[start..end], url);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        e.Handled = true;

        var rawPos = e.GetPosition(this);
        var pos = new Point(rawPos.X, rawPos.Y - _renderYOffset);
        var charIndex = _layout.HitTest(pos);

        // Right-click on a link: fire edit event
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            var link = FindLinkAt(charIndex);
            if (link is { } l)
            {
                _suppressContextMenu = true;
                LinkEditRequested?.Invoke(this, l.Start, l.Length, l.Text, l.Url);
                return;
            }
        }

        // Left-click on a link: handle internal privstack:// links or open external URLs
        if (props.IsLeftButtonPressed && e.ClickCount == 1 && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var linkUrl = _doc.GetLinkUrlAt(charIndex);
            if (!string.IsNullOrEmpty(linkUrl))
            {
                if (linkUrl.StartsWith("privstack://", StringComparison.OrdinalIgnoreCase))
                {
                    // Parse privstack://linkType/itemId
                    var path = linkUrl["privstack://".Length..];
                    var slash = path.IndexOf('/');
                    if (slash > 0)
                    {
                        var linkType = path[..slash];
                        var itemId = path[(slash + 1)..];
                        InternalLinkActivated?.Invoke(linkType, itemId);
                    }
                }
                else
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo(linkUrl) { UseShellExecute = true };
                        System.Diagnostics.Process.Start(psi);
                    }
                    catch { /* ignore bad URLs */ }
                }
                return;
            }
        }

        // Double-click: select word (text between special characters)
        if (e.ClickCount == 2)
        {
            var (wordStart, wordEnd) = FindWordBoundary(charIndex);
            _caret.SelectionAnchor = wordStart;
            _caret.Position = wordEnd;
            _isDragging = false;
            _caret.ResetBlink();
            UpdateActiveStyle();
            InvalidateVisual();
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _caret.SelectionAnchor ??= _caret.Position;
            _caret.Position = charIndex;
        }
        else
        {
            _caret.Position = charIndex;
            _caret.ClearSelection();
            _caret.SelectionAnchor = charIndex; // prepare for drag
        }

        _isDragging = true;
        e.Pointer.Capture(this);
        _caret.ResetBlink();
        UpdateActiveStyle();
        InvalidateVisual();
    }

    /// <summary>
    /// Find the word boundary around a character index.
    /// A "word" is text between special characters (punctuation, brackets, etc.).
    /// Spaces are included in the word.
    /// </summary>
    private (int Start, int End) FindWordBoundary(int index)
    {
        var text = _doc.Text;
        if (text.Length == 0) return (0, 0);
        index = Math.Clamp(index, 0, text.Length - 1);

        // Delimiters that break word boundaries
        static bool IsDelimiter(char c) => c is '\n' or '\r' or '\t' or ' '
            or '.' or ',' or ';' or ':' or '!' or '?' or '"' or '\'' or '(' or ')'
            or '[' or ']' or '{' or '}' or '<' or '>' or '/' or '\\' or '|'
            or '@' or '#' or '$' or '%' or '^' or '&' or '=' or '+'
            or '~' or '`' or '*' or '_';

        // If clicked on a delimiter, just select that character
        if (IsDelimiter(text[index]))
            return (index, index + 1);

        // Scan left to find start
        var start = index;
        while (start > 0 && !IsDelimiter(text[start - 1]))
            start--;

        // Scan right to find end
        var end = index;
        while (end < text.Length && !IsDelimiter(text[end]))
            end++;

        return (start, end);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var rawPos = e.GetPosition(this);
        var pos = new Point(rawPos.X, rawPos.Y - _renderYOffset);
        var charIndex = _layout.HitTest(pos);

        // Show hand cursor over links and fire hover/unhover events for prefetch
        if (!_isDragging)
        {
            var linkUrl = _doc.GetLinkUrlAt(charIndex);
            Cursor = string.IsNullOrEmpty(linkUrl)
                ? new Cursor(StandardCursorType.Ibeam)
                : new Cursor(StandardCursorType.Hand);

            // Track internal link hover for prefetch
            (string LinkType, string ItemId)? currentLink = null;
            if (!string.IsNullOrEmpty(linkUrl) && linkUrl.StartsWith("privstack://", StringComparison.OrdinalIgnoreCase))
            {
                var path = linkUrl["privstack://".Length..];
                var slash = path.IndexOf('/');
                if (slash > 0)
                    currentLink = (path[..slash], path[(slash + 1)..]);
            }

            // Fire unhover if we left the previous link
            if (_hoveredInternalLink != null && _hoveredInternalLink != currentLink)
            {
                InternalLinkUnhovered?.Invoke(_hoveredInternalLink.Value.LinkType, _hoveredInternalLink.Value.ItemId);
            }

            // Fire hover if we entered a new link
            if (currentLink != null && currentLink != _hoveredInternalLink)
            {
                InternalLinkHovered?.Invoke(currentLink.Value.LinkType, currentLink.Value.ItemId);
            }

            _hoveredInternalLink = currentLink;
        }

        if (!_isDragging) return;

        _caret.Position = charIndex;
        _caret.ResetBlink();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
        }

        // If anchor == position after release, clear selection
        if (_caret.SelectionAnchor == _caret.Position)
            _caret.ClearSelection();
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        // Clear hovered link and fire unhover event if we were hovering
        if (_hoveredInternalLink != null)
        {
            InternalLinkUnhovered?.Invoke(_hoveredInternalLink.Value.LinkType, _hoveredInternalLink.Value.ItemId);
            _hoveredInternalLink = null;
        }
    }

    // ---- Keyboard input ----

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text)) return;
        if (IsReadOnly) { e.Handled = true; return; }
        e.Handled = true;

        _doc.PushUndo();
        DeleteSelectionIfAny();
        _doc.Insert(_caret.Position, e.Text, _activeStyle);
        _caret.Position += e.Text.Length;
        _caret.ClearSelection();

        // Detect [[ trigger for link picker
        if (e.Text == "[" && _caret.Position >= 2
            && _doc.Text[_caret.Position - 2] == '[')
        {
            // Remove the two [[ characters
            _caret.Position -= 2;
            _doc.Delete(_caret.Position, 2);
            OnContentChanged();
            LinkPickerRequested?.Invoke(_blockId);
            return;
        }

        OnContentChanged();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Do NOT call base — it can route Space/Tab to parent controls (scroll, focus nav)
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                   e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // When read-only, allow navigation/selection/copy but block mutations
        if (IsReadOnly)
        {
            switch (e.Key)
            {
                case Key.Left: case Key.Right: case Key.Up: case Key.Down:
                case Key.Home: case Key.End:
                    break; // fall through to normal handling
                case Key.A when ctrl:
                case Key.C when ctrl:
                    break; // allow select-all and copy
                default:
                    e.Handled = true;
                    return;
            }
        }

        switch (e.Key)
        {
            case Key.Back:
                _doc.PushUndo();
                HandleBackspace();
                e.Handled = true;
                break;

            case Key.Delete:
                _doc.PushUndo();
                HandleDelete();
                e.Handled = true;
                break;

            case Key.Return when shift:
                // Shift+Enter: insert a newline within the block
                _doc.PushUndo();
                DeleteSelectionIfAny();
                _doc.Insert(_caret.Position, "\n", _activeStyle);
                _caret.Position++;
                _caret.ClearSelection();
                OnContentChanged();
                e.Handled = true;
                break;

            case Key.Return:
                _doc.PushUndo();
                if (AllowBlockSplit)
                {
                    HandleEnter();
                }
                else
                {
                    // Insert newline (same as Shift+Enter) when block splitting is disabled
                    DeleteSelectionIfAny();
                    _doc.Insert(_caret.Position, "\n", _activeStyle);
                    _caret.Position++;
                    _caret.ClearSelection();
                    OnContentChanged();
                }
                e.Handled = true;
                break;

            case Key.Tab when shift:
                OutdentRequested?.Invoke(_blockId);
                e.Handled = true;
                break;

            case Key.Tab:
                IndentRequested?.Invoke(_blockId);
                e.Handled = true;
                break;

            case Key.Space:
                // Handle Space explicitly so parent ScrollViewer doesn't intercept
                _doc.PushUndo();
                DeleteSelectionIfAny();
                _doc.Insert(_caret.Position, " ", _activeStyle);
                _caret.Position++;
                _caret.ClearSelection();
                OnContentChanged();
                e.Handled = true;
                break;

            case Key.Left:
                HandleArrow(-1, shift, ctrl, e.KeyModifiers.HasFlag(KeyModifiers.Alt));
                e.Handled = true;
                break;

            case Key.Right:
                HandleArrow(1, shift, ctrl, e.KeyModifiers.HasFlag(KeyModifiers.Alt));
                e.Handled = true;
                break;

            case Key.Up:
                HandleVerticalArrow(-1, shift);
                e.Handled = true;
                break;

            case Key.Down:
                HandleVerticalArrow(1, shift);
                e.Handled = true;
                break;

            case Key.Home:
                HandleHome(shift);
                e.Handled = true;
                break;

            case Key.End:
                HandleEnd(shift);
                e.Handled = true;
                break;

            case Key.A when ctrl:
                _caret.SelectAll(_doc.Length);
                _caret.ResetBlink();
                InvalidateVisual();
                e.Handled = true;
                break;

            case Key.B when ctrl:
                ToggleStyleOnSelectionOrActive(InlineStyle.Bold);
                e.Handled = true;
                break;

            case Key.I when ctrl:
                ToggleStyleOnSelectionOrActive(InlineStyle.Italic);
                e.Handled = true;
                break;

            case Key.U when ctrl:
                ToggleStyleOnSelectionOrActive(InlineStyle.Underline);
                e.Handled = true;
                break;

            case Key.E when ctrl:
                EmojiPickerRequested?.Invoke(_blockId);
                e.Handled = true;
                break;

            case Key.S when ctrl && shift:
                ToggleStyleOnSelectionOrActive(InlineStyle.Strikethrough);
                e.Handled = true;
                break;

            case Key.OemPeriod when ctrl && shift:
                ToggleStyleOnSelectionOrActive(InlineStyle.Superscript);
                e.Handled = true;
                break;

            case Key.C when ctrl:
                HandleCopy();
                e.Handled = true;
                break;

            case Key.X when ctrl:
                HandleCut();
                e.Handled = true;
                break;

            case Key.V when ctrl:
                HandlePaste();
                e.Handled = true;
                break;

            case Key.Z when ctrl && shift:
                HandleRedo();
                e.Handled = true;
                break;

            case Key.Z when ctrl:
                HandleUndo();
                e.Handled = true;
                break;

            case Key.Escape:
                // Deselect: clear selection, unfocus the editor
                _caret.ClearSelection();
                _isFocused = false;
                InvalidateVisual();
                // Move focus to a parent so the block wrapper loses keyboard focus
                if (Parent is Control p)
                {
                    var ancestor = p;
                    while (ancestor != null)
                    {
                        if (ancestor.Focusable)
                        {
                            ancestor.Focus();
                            break;
                        }
                        ancestor = ancestor.Parent as Control;
                    }
                }
                e.Handled = true;
                break;
        }
    }

    // ---- Input handlers ----

    private void HandleBackspace()
    {
        if (_caret.HasSelection)
        {
            DeleteSelectionIfAny();
            OnContentChanged();
            return;
        }

        if (_caret.Position == 0)
        {
            MergeWithPreviousRequested?.Invoke(_blockId);
            return;
        }

        // Delete full surrogate pair (2 chars) when backspacing over an emoji/symbol
        var deleteCount = 1;
        if (_caret.Position >= 2 && char.IsLowSurrogate(_doc.Text[_caret.Position - 1])
                                 && char.IsHighSurrogate(_doc.Text[_caret.Position - 2]))
            deleteCount = 2;

        _caret.Position -= deleteCount;
        _doc.Delete(_caret.Position, deleteCount);
        OnContentChanged();
    }

    private void HandleDelete()
    {
        if (_caret.HasSelection)
        {
            DeleteSelectionIfAny();
            OnContentChanged();
            return;
        }

        if (_caret.Position < _doc.Length)
        {
            // Delete full surrogate pair (2 chars) when deleting an emoji/symbol
            var deleteCount = 1;
            if (_caret.Position + 1 < _doc.Length && char.IsHighSurrogate(_doc.Text[_caret.Position])
                                                   && char.IsLowSurrogate(_doc.Text[_caret.Position + 1]))
                deleteCount = 2;

            _doc.Delete(_caret.Position, deleteCount);
            OnContentChanged();
        }
    }

    private void HandleEnter()
    {
        DeleteSelectionIfAny();
        var after = _doc.Split(_caret.Position);
        var afterMd = after.ToMarkdown();
        // Flush text change immediately (cancel debounce) so the plugin
        // sees the truncated text before the structural split command.
        FlushTextChange();
        SplitRequested?.Invoke(_blockId, afterMd);
    }

    private void HandleArrow(int direction, bool shift, bool cmdOrCtrl = false, bool alt = false)
    {
        if (shift)
            _caret.SelectionAnchor ??= _caret.Position;
        else if (_caret.HasSelection)
        {
            var (s, end) = _caret.SelectionRange;
            _caret.Position = direction < 0 ? s : end;
            _caret.ClearSelection();
            _caret.ResetBlink();
            InvalidateVisual();
            return;
        }

        int newPos;
        if (cmdOrCtrl)
        {
            // Cmd/Ctrl+Arrow: jump to line start/end
            var lineIndex = _layout.GetLineIndex(_caret.Position);
            var line = _layout.Lines[lineIndex];
            var lineStart = line.Runs.Count > 0 ? line.Runs[0].StartIndex : 0;
            var lastRun = line.Runs.Count > 0 ? line.Runs[^1] : null;
            var lineEnd = lastRun != null ? lastRun.StartIndex + lastRun.Length : lineStart;
            newPos = direction < 0 ? lineStart : lineEnd;
        }
        else if (alt)
        {
            // Option/Alt+Arrow: jump by word
            newPos = FindWordEdge(_caret.Position, direction);
        }
        else
        {
            newPos = _caret.Position + direction;
            // Skip over surrogate pairs so caret doesn't land between surrogates
            if (direction > 0 && newPos < _doc.Length && char.IsHighSurrogate(_doc.Text[_caret.Position]))
                newPos++;
            else if (direction < 0 && newPos > 0 && char.IsLowSurrogate(_doc.Text[newPos]))
                newPos--;
        }
        _caret.Position = Math.Clamp(newPos, 0, _doc.Length);

        if (!shift) _caret.ClearSelection();
        _caret.ResetBlink();
        UpdateActiveStyle();
        InvalidateVisual();
    }

    /// Jump to the next/previous word edge from the given position.
    private int FindWordEdge(int pos, int direction)
    {
        var text = _doc.Text;
        if (text.Length == 0) return 0;

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        if (direction > 0)
        {
            // Skip current word chars, then skip non-word chars
            while (pos < text.Length && IsWordChar(text[pos])) pos++;
            while (pos < text.Length && !IsWordChar(text[pos])) pos++;
        }
        else
        {
            // Move back one, skip non-word chars, then skip word chars
            if (pos > 0) pos--;
            while (pos > 0 && !IsWordChar(text[pos])) pos--;
            while (pos > 0 && IsWordChar(text[pos - 1])) pos--;
        }
        return pos;
    }

    private void HandleVerticalArrow(int direction, bool shift)
    {
        // Shift+Up/Down: jump to adjacent block immediately
        if (shift)
        {
            FocusAdjacentRequested?.Invoke(_blockId, direction);
            return;
        }

        var lineIndex = _layout.GetLineIndex(_caret.Position);

        if (direction < 0 && lineIndex == 0)
        {
            FocusAdjacentRequested?.Invoke(_blockId, -1);
            return;
        }
        if (direction > 0 && lineIndex == _layout.Lines.Count - 1)
        {
            FocusAdjacentRequested?.Invoke(_blockId, 1);
            return;
        }

        // Move to same X position on target line
        var caretRect = _layout.GetCaretRect(_caret.Position);
        var targetLine = _layout.Lines[lineIndex + direction];
        var targetPoint = new Point(caretRect.X, targetLine.Y + targetLine.Height / 2);
        _caret.Position = _layout.HitTest(targetPoint);

        _caret.ClearSelection();
        _caret.ResetBlink();
        InvalidateVisual();
    }

    private void HandleHome(bool shift)
    {
        if (shift) _caret.SelectionAnchor ??= _caret.Position;
        var lineIndex = _layout.GetLineIndex(_caret.Position);
        _caret.Position = _layout.GetLineStart(lineIndex);
        if (!shift) _caret.ClearSelection();
        _caret.ResetBlink();
        InvalidateVisual();
    }

    private void HandleEnd(bool shift)
    {
        if (shift) _caret.SelectionAnchor ??= _caret.Position;
        var lineIndex = _layout.GetLineIndex(_caret.Position);
        _caret.Position = _layout.GetLineEnd(lineIndex);
        if (!shift) _caret.ClearSelection();
        _caret.ResetBlink();
        InvalidateVisual();
    }

    private async void HandleCopy()
    {
        if (!_caret.HasSelection) return;
        var (s, end) = _caret.SelectionRange;
        var text = _doc.Text[s..end];
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    private async void HandleCut()
    {
        if (!_caret.HasSelection) return;
        _doc.PushUndo();
        var (s, end) = _caret.SelectionRange;
        var text = _doc.Text[s..end];
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
        DeleteSelectionIfAny();
        OnContentChanged();
    }

    private async void HandlePaste()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
#pragma warning disable CS0618 // GetTextAsync is obsolete
        var text = await clipboard.GetTextAsync();
#pragma warning restore CS0618
        if (string.IsNullOrEmpty(text)) return;

        // Strip newlines — single block
        text = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

        _doc.PushUndo();
        DeleteSelectionIfAny();
        _doc.Insert(_caret.Position, text, _activeStyle);
        _caret.Position += text.Length;
        _caret.ClearSelection();
        OnContentChanged();
    }

    private void HandleUndo()
    {
        if (_doc.Undo())
        {
            _caret.Position = Math.Min(_caret.Position, _doc.Length);
            _caret.ClearSelection();
            Relayout();
            _caret.ResetBlink();
            UpdateActiveStyle();
            FlushTextChange();
        }
    }

    private void HandleRedo()
    {
        if (_doc.Redo())
        {
            _caret.Position = Math.Min(_caret.Position, _doc.Length);
            _caret.ClearSelection();
            Relayout();
            _caret.ResetBlink();
            UpdateActiveStyle();
            FlushTextChange();
        }
    }

    private void ToggleStyleOnSelectionOrActive(InlineStyle flag)
    {
        if (_caret.HasSelection)
        {
            _doc.PushUndo();
            var (s, end) = _caret.SelectionRange;
            _doc.ToggleStyle(s, end - s, flag);
            OnContentChanged();
        }
        else
        {
            _activeStyle ^= flag;
            ActiveStyleChanged?.Invoke(_activeStyle);
        }
        InvalidateVisual();
    }

    private void DeleteSelectionIfAny()
    {
        if (!_caret.HasSelection) return;
        var (s, end) = _caret.SelectionRange;
        _doc.Delete(s, end - s);
        _caret.Position = s;
        _caret.ClearSelection();
    }

    // ---- Content change + debounced save ----

    private void UpdateActiveStyle()
    {
        var prev = _activeStyle;
        if (_caret.Position > 0 && _caret.Position <= _doc.Length)
            _activeStyle = _doc.GetStyleAt(_caret.Position - 1);
        if (_activeStyle != prev)
            ActiveStyleChanged?.Invoke(_activeStyle);
    }

    public void OnContentChanged()
    {
        Relayout();
        _caret.ResetBlink();
        UpdateActiveStyle();
        CheckForRemovedLinks();
        DebounceSave();
    }

    private void SnapshotInternalLinks()
    {
        _trackedInternalLinks = new HashSet<string>(
            _doc.GetAllLinkUrls().Where(u => u.StartsWith("privstack://", StringComparison.OrdinalIgnoreCase)));
    }

    private void CheckForRemovedLinks()
    {
        var currentLinks = new HashSet<string>(
            _doc.GetAllLinkUrls().Where(u => u.StartsWith("privstack://", StringComparison.OrdinalIgnoreCase)));

        foreach (var url in _trackedInternalLinks)
        {
            if (!currentLinks.Contains(url))
                InternalLinkRemoved?.Invoke(url);
        }

        _trackedInternalLinks = currentLinks;
    }

    /// <summary>
    /// Immediately flush any pending text change (cancel the debounce timer).
    /// Call before structural operations (split, merge) so the plugin sees
    /// the current text before the structural command arrives.
    /// </summary>
    public void FlushTextChange()
    {
        _saveTimer?.Stop();
        _saveTimer?.Dispose();
        _saveTimer = null;

        Relayout();
        _caret.ResetBlink();
        UpdateActiveStyle();

        var md = _doc.ToMarkdown();
        SetCurrentValue(MarkdownProperty, md);
        TextChanged?.Invoke(_blockId, md);
    }

    private void DebounceSave()
    {
        _saveTimer?.Stop();
        _saveTimer?.Dispose();
        _saveTimer = new System.Timers.Timer(300) { AutoReset = false };
        _saveTimer.Elapsed += (_, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var md = _doc.ToMarkdown();
                SetCurrentValue(MarkdownProperty, md);
                TextChanged?.Invoke(_blockId, md);
            });
        };
        _saveTimer.Start();
    }

    /// <summary>
    /// Programmatically set caret to end and focus (used after merge).
    /// </summary>
    public void SetCaretToEnd()
    {
        _caret.Position = _doc.Length;
        _caret.ClearSelection();
        Focus();
    }

    /// <summary>
    /// Programmatically set caret to start and focus.
    /// </summary>
    public void SetCaretToStart()
    {
        _caret.Position = 0;
        _caret.ClearSelection();
        Focus();
    }

    /// <summary>
    /// Programmatically set caret to a specific position and focus.
    /// </summary>
    public void SetCaretToPosition(int position)
    {
        _caret.Position = Math.Clamp(position, 0, _doc.Length);
        _caret.ClearSelection();
        Focus();
    }

    /// <summary>
    /// Append markdown text (used during merge).
    /// </summary>
    public void AppendMarkdown(string markdown)
    {
        var other = TextDocument.FromMarkdown(markdown);
        var insertPos = _doc.Length;
        _doc.Append(other);
        _caret.Position = insertPos;
        Relayout();
    }
}
