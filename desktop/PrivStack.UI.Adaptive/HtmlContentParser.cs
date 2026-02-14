// ============================================================================
// File: HtmlContentParser.cs
// Description: Lightweight HTML-to-Avalonia converter for rich content rendering.
//              Parses a subset of HTML into SelectableTextBlock elements with
//              inline formatting (bold, italic, underline, links) and block-level
//              elements (paragraphs, headers, lists, images).
// ============================================================================

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Serilog;

namespace PrivStack.UI.Adaptive;

/// <summary>
/// Parses HTML content into Avalonia controls for display in the adaptive renderer.
/// Supports: bold, italic, underline, links, images, paragraphs, headers, lists, line breaks.
/// </summary>
internal sealed class HtmlContentParser
{
    private static readonly ILogger _log = Log.ForContext<HtmlContentParser>();

    private readonly Func<string, double, IBrush> _brush;
    private readonly Func<string, double, double> _fontSize;
    private readonly Func<string, FontFamily> _font;
    private readonly Func<string, Task<byte[]?>>? _fetchUrl;

    /// <param name="brush">Resolves a theme brush key with a fallback.</param>
    /// <param name="fontSize">Resolves a theme font-size key with a fallback.</param>
    /// <param name="font">Resolves a theme font-family key.</param>
    /// <param name="fetchUrl">Fetches a URL through the permission-checked network layer. May be null (images won't load).</param>
    public HtmlContentParser(
        Func<string, double, IBrush> brush,
        Func<string, double, double> fontSize,
        Func<string, FontFamily> font,
        Func<string, Task<byte[]?>>? fetchUrl = null)
    {
        _brush = brush;
        _fontSize = fontSize;
        _font = font;
        _fetchUrl = fetchUrl;
    }

    private IBrush TextPrimary => _brush("ThemeTextPrimaryBrush", 0);
    private IBrush TextMuted => _brush("ThemeTextMutedBrush", 0);
    private IBrush PrimaryBrush => _brush("ThemePrimaryBrush", 0);
    private double BaseFontSize => _fontSize("ThemeFontSizeMd", 14);
    private FontFamily SansFont => _font("ThemeFontSans");

    // Brush helper that ignores the double (just needs the key)
    private IBrush Brush(string key) => _brush(key, 0);

    /// <summary>
    /// Parses HTML and appends resulting controls to the given panel.
    /// </summary>
    public void Parse(string html, StackPanel target)
    {
        if (string.IsNullOrWhiteSpace(html))
            return;

        var tokens = Tokenize(html);
        var blocks = BuildBlocks(tokens);

        foreach (var block in blocks)
        {
            target.Children.Add(block);
        }
    }

    // ================================================================
    // Tokenizer
    // ================================================================

    private enum TokenKind { Text, OpenTag, CloseTag, SelfClosingTag }

    private sealed record Token(TokenKind Kind, string Value, Dictionary<string, string>? Attributes = null);

    private static List<Token> Tokenize(string html)
    {
        var tokens = new List<Token>();
        int i = 0;

        while (i < html.Length)
        {
            if (html[i] == '<')
            {
                var end = html.IndexOf('>', i);
                if (end < 0)
                {
                    // Malformed — treat rest as text
                    tokens.Add(new Token(TokenKind.Text, WebUtility.HtmlDecode(html[i..])));
                    break;
                }

                var tagContent = html[(i + 1)..end].Trim();
                i = end + 1;

                if (tagContent.StartsWith("!--"))
                {
                    // Comment — skip to -->
                    var commentEnd = html.IndexOf("-->", i, StringComparison.Ordinal);
                    if (commentEnd >= 0) i = commentEnd + 3;
                    continue;
                }

                if (tagContent.StartsWith('!') || tagContent.StartsWith('?'))
                {
                    // Doctype or processing instruction — skip
                    continue;
                }

                if (tagContent.StartsWith('/'))
                {
                    var tagName = tagContent[1..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    tokens.Add(new Token(TokenKind.CloseTag, tagName.ToLowerInvariant()));
                }
                else
                {
                    var isSelfClosing = tagContent.EndsWith('/');
                    if (isSelfClosing)
                        tagContent = tagContent[..^1].Trim();

                    var parts = tagContent.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    var tagName = (parts.Length > 0 ? parts[0] : "").ToLowerInvariant();
                    var attrs = parts.Length > 1 ? ParseAttributes(parts[1]) : null;

                    // Void elements are always self-closing
                    var isVoid = tagName is "br" or "hr" or "img" or "input" or "meta" or "link" or "source" or "embed";

                    tokens.Add(new Token(
                        isSelfClosing || isVoid ? TokenKind.SelfClosingTag : TokenKind.OpenTag,
                        tagName, attrs));
                }
            }
            else
            {
                var next = html.IndexOf('<', i);
                var text = next < 0 ? html[i..] : html[i..next];
                i = next < 0 ? html.Length : next;

                var decoded = WebUtility.HtmlDecode(text);
                if (!string.IsNullOrEmpty(decoded))
                    tokens.Add(new Token(TokenKind.Text, decoded));
            }
        }

        return tokens;
    }

    private static Dictionary<string, string> ParseAttributes(string attrString)
    {
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = Regex.Matches(attrString, @"(\w[\w-]*)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|(\S+)))?");

        foreach (Match m in matches)
        {
            var key = m.Groups[1].Value;
            var val = m.Groups[2].Success ? m.Groups[2].Value
                    : m.Groups[3].Success ? m.Groups[3].Value
                    : m.Groups[4].Success ? m.Groups[4].Value
                    : "";
            attrs[key] = val;
        }

        return attrs;
    }

    // ================================================================
    // Block builder — converts token stream into controls
    // ================================================================

    private readonly struct FormatState
    {
        public bool Bold { get; init; }
        public bool Italic { get; init; }
        public bool Underline { get; init; }
        public string? LinkHref { get; init; }
    }

    private List<Control> BuildBlocks(List<Token> tokens)
    {
        var blocks = new List<Control>();
        var currentInlines = new List<Inline>();
        // Map link Run instances to their href for click handling
        var currentLinkRuns = new Dictionary<Run, string>();
        var formatStack = new Stack<FormatState>();
        formatStack.Push(new FormatState());

        var listStack = new Stack<(bool Ordered, int Counter)>();

        void FlushInlines()
        {
            if (currentInlines.Count == 0) return;

            var tb = CreateTextBlock();
            foreach (var inline in currentInlines)
                tb.Inlines!.Add(inline);

            // Apply list indentation if inside a list
            if (listStack.Count > 0)
            {
                tb.Margin = new Thickness(listStack.Count * 16, 0, 0, 0);
            }

            // Wire up link click handling if this block has any links
            if (currentLinkRuns.Count > 0)
            {
                var linkMap = new Dictionary<Run, string>(currentLinkRuns);
                tb.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Arrow);
                tb.PointerPressed += (_, e) =>
                {
                    var point = e.GetPosition(tb);
                    var hit = tb.TextLayout.HitTestPoint(point);
                    var charIndex = hit.TextPosition;

                    // Walk inlines to find which Run was clicked
                    int offset = 0;
                    foreach (var inline in tb.Inlines!)
                    {
                        if (inline is Run run)
                        {
                            var len = run.Text?.Length ?? 0;
                            if (charIndex >= offset && charIndex < offset + len && linkMap.TryGetValue(run, out var href))
                            {
                                OpenUrl(href);
                                e.Handled = true;
                                return;
                            }
                            offset += len;
                        }
                        else if (inline is LineBreak)
                        {
                            offset += 1;
                        }
                    }
                };
                // Change cursor on hover over link runs
                tb.PointerMoved += (_, e) =>
                {
                    var point = e.GetPosition(tb);
                    var hit = tb.TextLayout.HitTestPoint(point);
                    var charIndex = hit.TextPosition;

                    int offset = 0;
                    var overLink = false;
                    foreach (var inline in tb.Inlines!)
                    {
                        if (inline is Run run)
                        {
                            var len = run.Text?.Length ?? 0;
                            if (charIndex >= offset && charIndex < offset + len && linkMap.ContainsKey(run))
                            {
                                overLink = true;
                                break;
                            }
                            offset += len;
                        }
                        else if (inline is LineBreak)
                        {
                            offset += 1;
                        }
                    }
                    tb.Cursor = overLink
                        ? new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                        : new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Arrow);
                };
                currentLinkRuns.Clear();
            }

            blocks.Add(tb);
            currentInlines.Clear();
        }

        int i = 0;
        while (i < tokens.Count)
        {
            var token = tokens[i];

            switch (token.Kind)
            {
                case TokenKind.Text:
                    var state = formatStack.Peek();
                    var run = CreateRun(token.Value, state);
                    currentInlines.Add(run);
                    if (state.LinkHref != null && run is Run linkRun)
                        currentLinkRuns[linkRun] = state.LinkHref;
                    break;

                case TokenKind.OpenTag:
                    switch (token.Value)
                    {
                        case "b" or "strong":
                            formatStack.Push(formatStack.Peek() with { Bold = true });
                            break;

                        case "i" or "em":
                            formatStack.Push(formatStack.Peek() with { Italic = true });
                            break;

                        case "u":
                            formatStack.Push(formatStack.Peek() with { Underline = true });
                            break;

                        case "a":
                            var href = token.Attributes?.GetValueOrDefault("href");
                            formatStack.Push(formatStack.Peek() with { LinkHref = href, Underline = true });
                            break;

                        case "p" or "div":
                            FlushInlines();
                            break;

                        case "h1":
                            FlushInlines();
                            formatStack.Push(new FormatState { Bold = true });
                            break;

                        case "h2":
                            FlushInlines();
                            formatStack.Push(new FormatState { Bold = true });
                            break;

                        case "h3":
                            FlushInlines();
                            formatStack.Push(new FormatState { Bold = true });
                            break;

                        case "h4" or "h5" or "h6":
                            FlushInlines();
                            formatStack.Push(new FormatState { Bold = true });
                            break;

                        case "ul":
                            FlushInlines();
                            listStack.Push((false, 0));
                            break;

                        case "ol":
                            FlushInlines();
                            listStack.Push((true, 0));
                            break;

                        case "li":
                            FlushInlines();
                            if (listStack.Count > 0)
                            {
                                var (ordered, counter) = listStack.Pop();
                                counter++;
                                listStack.Push((ordered, counter));
                                var bullet = ordered ? $"{counter}. " : "\u2022 ";
                                currentInlines.Add(CreateRun(bullet, formatStack.Peek()));
                            }
                            break;

                        case "blockquote":
                            FlushInlines();
                            break;

                        case "pre" or "code":
                            // Push same state, we'll render with mono font at flush
                            formatStack.Push(formatStack.Peek());
                            break;

                        case "video":
                            FlushInlines();
                            var videoSrc = token.Attributes?.GetValueOrDefault("src");
                            var posterSrc = token.Attributes?.GetValueOrDefault("poster");
                            // If no src attr, look for a <source> child
                            if (string.IsNullOrEmpty(videoSrc))
                            {
                                for (int j = i + 1; j < tokens.Count && j < i + 10; j++)
                                {
                                    if (tokens[j].Kind == TokenKind.SelfClosingTag && tokens[j].Value == "source")
                                    {
                                        videoSrc = tokens[j].Attributes?.GetValueOrDefault("src");
                                        break;
                                    }
                                    if (tokens[j].Kind == TokenKind.CloseTag && tokens[j].Value == "video")
                                        break;
                                }
                            }
                            if (!string.IsNullOrEmpty(videoSrc))
                                blocks.Add(CreateVideoPlaceholder(videoSrc, posterSrc));
                            break;

                        case "iframe":
                            FlushInlines();
                            var iframeSrc = token.Attributes?.GetValueOrDefault("src");
                            if (!string.IsNullOrEmpty(iframeSrc))
                                blocks.Add(CreateVideoPlaceholder(iframeSrc, null));
                            break;

                        case "figure" or "figcaption" or "picture" or "span" or "section" or "article" or "main" or "header" or "footer" or "nav" or "aside":
                            // Layout tags — pass through
                            break;

                        default:
                            // Unknown tag — ignore
                            break;
                    }
                    break;

                case TokenKind.CloseTag:
                    switch (token.Value)
                    {
                        case "b" or "strong" or "i" or "em" or "u" or "a" or "pre" or "code":
                            if (formatStack.Count > 1) formatStack.Pop();
                            break;

                        case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                            // Flush as a header block
                            if (currentInlines.Count > 0)
                            {
                                var headerSize = token.Value switch
                                {
                                    "h1" => _fontSize("ThemeFontSizeXl", 20),
                                    "h2" => _fontSize("ThemeFontSizeLg", 16),
                                    "h3" => _fontSize("ThemeFontSizeMd", 14),
                                    _ => _fontSize("ThemeFontSizeSmMd", 13),
                                };
                                var tb = CreateTextBlock();
                                tb.FontSize = headerSize;
                                tb.FontWeight = FontWeight.Bold;
                                tb.Margin = new Thickness(0, 8, 0, 4);
                                foreach (var inline in currentInlines)
                                    tb.Inlines!.Add(inline);
                                blocks.Add(tb);
                                currentInlines.Clear();
                            }
                            if (formatStack.Count > 1) formatStack.Pop();
                            break;

                        case "p" or "div":
                            FlushInlines();
                            break;

                        case "li":
                            FlushInlines();
                            break;

                        case "ul" or "ol":
                            FlushInlines();
                            if (listStack.Count > 0) listStack.Pop();
                            break;

                        case "blockquote":
                            FlushInlines();
                            break;
                    }
                    break;

                case TokenKind.SelfClosingTag:
                    switch (token.Value)
                    {
                        case "br":
                            currentInlines.Add(new LineBreak());
                            break;

                        case "hr":
                            FlushInlines();
                            blocks.Add(new Separator
                            {
                                Margin = new Thickness(0, 8),
                                Background = _brush("ThemeBorderSubtleBrush", 0),
                            });
                            break;

                        case "img":
                            var src = token.Attributes?.GetValueOrDefault("src");
                            if (!string.IsNullOrEmpty(src))
                            {
                                FlushInlines();
                                blocks.Add(CreateImageControl(src,
                                    token.Attributes?.GetValueOrDefault("alt")));
                            }
                            break;
                    }
                    break;
            }

            i++;
        }

        FlushInlines();
        return blocks;
    }

    private Inline CreateRun(string text, FormatState state)
    {
        var run = new Run(text);

        if (state.Bold)
            run.FontWeight = FontWeight.Bold;
        if (state.Italic)
            run.FontStyle = FontStyle.Italic;
        if (state.Underline)
            run.TextDecorations = TextDecorations.Underline;
        if (state.LinkHref != null)
            run.Foreground = PrimaryBrush;

        return run;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return;

            // Only allow http/https
            if (uri.Scheme is not ("http" or "https"))
                return;

            Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to open URL: {Url}", url);
        }
    }

    private SelectableTextBlock CreateTextBlock()
    {
        return new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = BaseFontSize,
            LineHeight = BaseFontSize * 1.6,
            Foreground = TextPrimary,
            FontFamily = SansFont,
            Inlines = new InlineCollection(),
        };
    }

    private Control CreateImageControl(string src, string? alt)
    {
        var container = new StackPanel { Spacing = 4 };

        var image = new Image
        {
            MaxWidth = 600,
            MaxHeight = 400,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        // Load async
        _ = LoadImageAsync(image, src);

        container.Children.Add(image);

        if (!string.IsNullOrWhiteSpace(alt))
        {
            container.Children.Add(new TextBlock
            {
                Text = alt,
                FontSize = _fontSize("ThemeFontSizeXsSm", 11),
                Foreground = TextMuted,
                FontStyle = FontStyle.Italic,
            });
        }

        return container;
    }

    private Control CreateVideoPlaceholder(string src, string? posterUrl)
    {
        // Convert YouTube embed URLs to watchable URLs
        var displayUrl = src;
        if (src.Contains("youtube.com/embed/", StringComparison.OrdinalIgnoreCase))
        {
            var videoId = src.Split("/embed/", 2, StringSplitOptions.None).ElementAtOrDefault(1)?.Split('?')[0];
            if (!string.IsNullOrEmpty(videoId))
                displayUrl = $"https://www.youtube.com/watch?v={videoId}";
        }
        else if (src.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase))
        {
            displayUrl = src; // Already a watchable link
        }

        var container = new StackPanel { Spacing = 4 };

        var placeholder = new Border
        {
            Width = 480,
            Height = 270,
            Background = _brush("ThemeSurfaceElevatedBrush", 0),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "\u25B6\uFE0E",
                        FontSize = ThemeDouble("ThemeFontSize4Xl", 48),
                        Foreground = PrimaryBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = "Open video in browser",
                        FontSize = _fontSize("ThemeFontSizeSm", 12),
                        Foreground = TextMuted,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
        };

        // If there's a poster image, load it as the background
        if (!string.IsNullOrEmpty(posterUrl))
        {
            var posterImage = new Image
            {
                Width = 480,
                Height = 270,
                Stretch = Stretch.UniformToFill,
                Opacity = 0.4,
            };
            _ = LoadImageAsync(posterImage, posterUrl);

            var overlayPanel = new Panel();
            overlayPanel.Children.Add(posterImage);
            overlayPanel.Children.Add(placeholder.Child);
            placeholder.Child = overlayPanel;
        }

        var capturedUrl = displayUrl;
        placeholder.PointerPressed += (_, _) => OpenUrl(capturedUrl);

        container.Children.Add(placeholder);

        // Show the URL as a clickable caption
        var linkText = new TextBlock
        {
            Text = displayUrl,
            FontSize = _fontSize("ThemeFontSizeXsSm", 11),
            Foreground = PrimaryBrush,
            TextDecorations = TextDecorations.Underline,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        linkText.PointerPressed += (_, _) => OpenUrl(capturedUrl);
        container.Children.Add(linkText);

        return container;
    }

    private async Task LoadImageAsync(Image imageControl, string url)
    {
        if (_fetchUrl == null)
            return;

        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
                return;

            var data = await _fetchUrl(url);
            if (data == null || data.Length == 0)
                return;

            using var stream = new MemoryStream(data);
            var bitmap = new Bitmap(stream);

            imageControl.Source = bitmap;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to load image from {Url}", url);
        }
    }

    private static double ThemeDouble(string key, double fallback)
    {
        var app = Application.Current;
        if (app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var v) == true && v is double d)
            return d;
        return fallback;
    }
}
