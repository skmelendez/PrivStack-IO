// ============================================================================
// File: HtmlContentControl.cs
// Description: A custom Avalonia control that renders HTML content with support
//              for images, links, and basic formatting. Designed for RSS content.
// ============================================================================

using System.ComponentModel;
using System.Net.Http;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using PrivStack.Desktop.Services;
using PrivStack.Desktop.Services.Abstractions;
using PrivStack.Sdk;

namespace PrivStack.Desktop.Controls;

/// <summary>
/// Renders HTML content with support for common tags used in RSS feeds.
/// Supports: p, br, img, a, b, strong, i, em, code, pre, h1-h6, ul, ol, li, blockquote
/// </summary>
public partial class HtmlContentControl : UserControl
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public static readonly StyledProperty<string?> HtmlContentProperty =
        AvaloniaProperty.Register<HtmlContentControl, string?>(nameof(HtmlContent));

    public static readonly StyledProperty<double> BaseFontSizeProperty =
        AvaloniaProperty.Register<HtmlContentControl, double>(nameof(BaseFontSize), 14.0);

    public static readonly StyledProperty<double> LineHeightProperty =
        AvaloniaProperty.Register<HtmlContentControl, double>(nameof(LineHeight), 1.6);

    public static readonly StyledProperty<double> MaxImageWidthProperty =
        AvaloniaProperty.Register<HtmlContentControl, double>(nameof(MaxImageWidth), 600.0);

    public string? HtmlContent
    {
        get => GetValue(HtmlContentProperty);
        set => SetValue(HtmlContentProperty, value);
    }

    public double BaseFontSize
    {
        get => GetValue(BaseFontSizeProperty);
        set => SetValue(BaseFontSizeProperty, value);
    }

    public double LineHeight
    {
        get => GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    public double MaxImageWidth
    {
        get => GetValue(MaxImageWidthProperty);
        set => SetValue(MaxImageWidthProperty, value);
    }

    public HtmlContentControl()
    {
        PropertyChanged += OnPropertyChanged;
        App.Services.GetRequiredService<IFontScaleService>().PropertyChanged += OnFontScaleChanged;
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

    private void OnThemeVariantChanged(object? sender, EventArgs e) => RenderContent();

    private void OnFontScaleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IFontScaleService.ScaleMultiplier))
        {
            RenderContent();
        }
    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == HtmlContentProperty)
        {
            RenderContent();
        }
    }

    /// <summary>
    /// Gets the scaled font size based on the current scale multiplier.
    /// </summary>
    private double GetScaledFontSize(double baseSize) => App.Services.GetRequiredService<IFontScaleService>().GetScaledSize(baseSize);

    private static FontFamily GetThemeFont(string key) =>
        Application.Current?.FindResource(key) as FontFamily ?? FontFamily.Default;

    private void RenderContent()
    {
        var html = HtmlContent;
        if (string.IsNullOrWhiteSpace(html))
        {
            Content = null;
            return;
        }

        var container = new StackPanel { Spacing = 12 };
        ParseAndRender(html, container);
        Content = container;
    }

    private void ParseAndRender(string html, StackPanel container)
    {
        // Decode HTML entities first
        html = DecodeHtmlEntities(html);

        // Split content by block-level elements
        var blocks = SplitIntoBlocks(html);

        foreach (var block in blocks)
        {
            // Skip empty blocks, but allow img blocks (they have data in Attributes, not Content)
            if (string.IsNullOrWhiteSpace(block.Content) && block.Type != "img")
                continue;

            var control = CreateBlockControl(block);
            if (control != null)
            {
                container.Children.Add(control);
            }
        }
    }

    private record HtmlBlock(string Type, string Content, Dictionary<string, string>? Attributes = null);

    private List<HtmlBlock> SplitIntoBlocks(string html)
    {
        var blocks = new List<HtmlBlock>();

        // Match block-level elements or standalone images
        var blockPattern = BlockElementPattern();
        var matches = blockPattern.Matches(html);

        int lastIndex = 0;

        foreach (Match match in matches)
        {
            // Add any text before this block as a paragraph
            if (match.Index > lastIndex)
            {
                var betweenText = html[lastIndex..match.Index].Trim();
                // Only add if it has actual text content (not just whitespace/tags)
                var cleanText = StripTags(betweenText);
                if (!string.IsNullOrWhiteSpace(cleanText))
                {
                    blocks.Add(new HtmlBlock("p", betweenText));
                }
            }

            var tagName = match.Groups["tag"].Value.ToLowerInvariant();

            // Check if this is an img tag (the "tag" group won't match for img)
            if (string.IsNullOrEmpty(tagName) && match.Value.StartsWith("<img", StringComparison.OrdinalIgnoreCase))
            {
                var imgAttrs = ParseAttributes(match.Value);
                blocks.Add(new HtmlBlock("img", "", imgAttrs));
            }
            else if (!string.IsNullOrEmpty(tagName))
            {
                var content = match.Groups["content"].Value;
                var attributes = ParseAttributes(match.Groups["attrs"].Value);
                blocks.Add(new HtmlBlock(tagName, content, attributes));
            }

            lastIndex = match.Index + match.Length;
        }

        // Add remaining text as a paragraph
        if (lastIndex < html.Length)
        {
            var remainingText = html[lastIndex..].Trim();
            var cleanText = StripTags(remainingText);
            if (!string.IsNullOrWhiteSpace(cleanText))
            {
                blocks.Add(new HtmlBlock("p", remainingText));
            }
        }

        // If no blocks found, treat entire content as a paragraph
        if (blocks.Count == 0 && !string.IsNullOrWhiteSpace(html))
        {
            blocks.Add(new HtmlBlock("p", html));
        }

        return blocks;
    }

    // Only match text-bearing block elements, NOT container divs (which cause duplicate nested content)
    [GeneratedRegex(@"<(?<tag>p|h[1-6]|blockquote|pre|ul|ol|li|figure|figcaption|article)(?<attrs>[^>]*)>(?<content>.*?)</\k<tag>>|<img(?<imgattrs>[^>]*)/?>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex BlockElementPattern();

    private static Dictionary<string, string> ParseAttributes(string attrString)
    {
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var attrPattern = AttrPattern();
        foreach (Match match in attrPattern.Matches(attrString))
        {
            var name = match.Groups["name"].Value;
            var value = match.Groups["value"].Value;
            attrs[name] = value;
        }
        return attrs;
    }

    [GeneratedRegex(@"(?<name>\w+)\s*=\s*[""'](?<value>[^""']*)[""']")]
    private static partial Regex AttrPattern();

    private Control? CreateBlockControl(HtmlBlock block)
    {
        return block.Type switch
        {
            "img" => CreateImageControl(block.Attributes),
            "h1" => CreateHeadingControl(block.Content, 1),
            "h2" => CreateHeadingControl(block.Content, 2),
            "h3" => CreateHeadingControl(block.Content, 3),
            "h4" => CreateHeadingControl(block.Content, 4),
            "h5" => CreateHeadingControl(block.Content, 5),
            "h6" => CreateHeadingControl(block.Content, 6),
            "blockquote" => CreateBlockquoteControl(block.Content),
            "pre" => CreatePreformattedControl(block.Content),
            "ul" => CreateListControl(block.Content, ordered: false),
            "ol" => CreateListControl(block.Content, ordered: true),
            "figure" => CreateFigureControl(block.Content),
            "article" => CreateArticleControl(block.Content),
            _ => CreateParagraphControl(block.Content)
        };
    }

    private Control? CreateArticleControl(string content)
    {
        // Recursively parse article content
        var container = new StackPanel { Spacing = 12 };
        ParseAndRender(content, container);
        return container.Children.Count > 0 ? container : null;
    }

    private Control? CreateImageControl(Dictionary<string, string>? attrs)
    {
        if (attrs == null)
            return null;

        var src = attrs.GetValueOrDefault("src");
        var alt = attrs.GetValueOrDefault("alt", "");

        if (string.IsNullOrEmpty(src))
            return null;

        var container = new StackPanel { Spacing = 4 };

        var image = new Image
        {
            MaxWidth = MaxImageWidth,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 8),
            MinHeight = 50 // Placeholder height until loaded
        };

        // Load image asynchronously
        _ = LoadImageAsync(image, src);

        container.Children.Add(image);

        // Add alt text as caption if present
        if (!string.IsNullOrWhiteSpace(alt))
        {
            container.Children.Add(new TextBlock
            {
                Text = alt,
                FontSize = GetScaledFontSize(BaseFontSize - 2),
                Opacity = 0.6,
                FontStyle = FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap
            });
        }

        return container;
    }

    private static async Task LoadImageAsync(Image imageControl, string src)
    {
        try
        {
            // Handle data URIs
            if (src.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                var base64Start = src.IndexOf(",", StringComparison.Ordinal);
                if (base64Start > 0)
                {
                    var base64Data = src[(base64Start + 1)..];
                    var bytes = Convert.FromBase64String(base64Data);
                    using var stream = new MemoryStream(bytes);
                    var bitmap = new Bitmap(stream);
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        imageControl.Source = bitmap;
                    });
                }
                return;
            }

            // Ensure absolute URL
            if (!src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Fetch remote image
            using var response = await SharedHttpClient.GetAsync(src);

            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync();

                // Create bitmap on background, set source on UI thread
                using var stream = new MemoryStream(bytes);
                var bitmap = new Bitmap(stream);

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    imageControl.Source = bitmap;
                });
            }
        }
        catch
        {
            // Silently fail - image just won't load
        }
    }

    private Control CreateHeadingControl(string content, int level)
    {
        var fontSize = level switch
        {
            1 => GetScaledFontSize(BaseFontSize + 12),
            2 => GetScaledFontSize(BaseFontSize + 8),
            3 => GetScaledFontSize(BaseFontSize + 4),
            4 => GetScaledFontSize(BaseFontSize + 2),
            _ => GetScaledFontSize(BaseFontSize)
        };

        var textBlock = new SelectableTextBlock
        {
            FontSize = fontSize,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 4)
        };

        RenderInlineContent(StripTags(content), textBlock.Inlines!);
        return textBlock;
    }

    private Control CreateBlockquoteControl(string content)
    {
        var innerPanel = new StackPanel { Spacing = 8 };
        var innerBlocks = SplitIntoBlocks(content);

        foreach (var block in innerBlocks)
        {
            if (string.IsNullOrWhiteSpace(block.Content)) continue;

            var textBlock = new SelectableTextBlock
            {
                FontSize = GetScaledFontSize(BaseFontSize),
                FontStyle = FontStyle.Italic,
                Opacity = 0.9,
                TextWrapping = TextWrapping.Wrap
            };
            RenderInlineContent(StripTags(block.Content), textBlock.Inlines!);
            innerPanel.Children.Add(textBlock);
        }

        return new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Application.Current?.FindResource("ThemeBorderBrush") as IBrush ?? Brushes.Gray,
            Padding = new Thickness(16, 8, 8, 8),
            Margin = new Thickness(0, 8, 0, 8),
            Child = innerPanel
        };
    }

    private Control CreatePreformattedControl(string content)
    {
        // Strip code tags if present
        var codeContent = CodeTagPattern().Replace(content, "$1");
        codeContent = StripTags(codeContent);

        return new Border
        {
            Background = Application.Current?.FindResource("ThemeCodeBlockBrush") as IBrush
                          ?? new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 8, 0, 8),
            Child = new SelectableTextBlock
            {
                Text = codeContent,
                FontFamily = GetThemeFont("ThemeFontMono"),
                FontSize = GetScaledFontSize(BaseFontSize - 1),
                TextWrapping = TextWrapping.Wrap
            }
        };
    }

    [GeneratedRegex(@"<code[^>]*>(.*?)</code>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CodeTagPattern();

    private Control CreateListControl(string content, bool ordered)
    {
        var panel = new StackPanel { Spacing = 4 };
        var listItems = ListItemPattern().Matches(content);
        var index = 1;

        foreach (Match item in listItems)
        {
            var itemContent = item.Groups[1].Value;
            var bullet = ordered ? $"{index}." : "•";

            var itemPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };

            itemPanel.Children.Add(new TextBlock
            {
                Text = bullet,
                FontSize = GetScaledFontSize(BaseFontSize),
                Width = ordered ? 24 : 16,
                TextAlignment = TextAlignment.Right
            });

            var textBlock = new SelectableTextBlock
            {
                FontSize = GetScaledFontSize(BaseFontSize),
                TextWrapping = TextWrapping.Wrap
            };
            RenderInlineContent(StripTags(itemContent), textBlock.Inlines!);
            itemPanel.Children.Add(textBlock);

            panel.Children.Add(itemPanel);
            index++;
        }

        return new Border
        {
            Margin = new Thickness(0, 4, 0, 4),
            Child = panel
        };
    }

    [GeneratedRegex(@"<li[^>]*>(.*?)</li>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ListItemPattern();

    private Control? CreateFigureControl(string content)
    {
        var container = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 8) };

        // Extract img
        var imgMatch = ImgTagPattern().Match(content);
        if (imgMatch.Success)
        {
            var attrs = ParseAttributes(imgMatch.Value);
            var imgControl = CreateImageControl(attrs);
            if (imgControl != null)
            {
                container.Children.Add(imgControl);
            }
        }

        // Extract figcaption
        var captionMatch = FigcaptionPattern().Match(content);
        if (captionMatch.Success)
        {
            container.Children.Add(new TextBlock
            {
                Text = StripTags(captionMatch.Groups[1].Value),
                FontSize = GetScaledFontSize(BaseFontSize - 2),
                Opacity = 0.6,
                FontStyle = FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }

        return container.Children.Count > 0 ? container : null;
    }

    [GeneratedRegex(@"<img[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImgTagPattern();

    [GeneratedRegex(@"<figcaption[^>]*>(.*?)</figcaption>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FigcaptionPattern();

    private Control? CreateParagraphControl(string content)
    {
        // Check for standalone images in the paragraph
        var imgMatches = ImgTagPattern().Matches(content);
        if (imgMatches.Count > 0)
        {
            var container = new StackPanel { Spacing = 8 };

            // Process content around images
            int lastIndex = 0;
            foreach (Match imgMatch in imgMatches)
            {
                // Add text before image
                if (imgMatch.Index > lastIndex)
                {
                    var textBefore = content[lastIndex..imgMatch.Index].Trim();
                    if (!string.IsNullOrWhiteSpace(textBefore))
                    {
                        var textBlock = CreateTextBlock(textBefore);
                        container.Children.Add(textBlock);
                    }
                }

                // Add image
                var attrs = ParseAttributes(imgMatch.Value);
                var imgControl = CreateImageControl(attrs);
                if (imgControl != null)
                {
                    container.Children.Add(imgControl);
                }

                lastIndex = imgMatch.Index + imgMatch.Length;
            }

            // Add remaining text
            if (lastIndex < content.Length)
            {
                var textAfter = content[lastIndex..].Trim();
                if (!string.IsNullOrWhiteSpace(textAfter))
                {
                    var textBlock = CreateTextBlock(textAfter);
                    container.Children.Add(textBlock);
                }
            }

            return container;
        }

        // No images, just create a text block
        var cleanContent = StripTags(content).Trim();
        if (string.IsNullOrWhiteSpace(cleanContent))
            return null;

        return CreateTextBlock(content);
    }

    private SelectableTextBlock CreateTextBlock(string content)
    {
        var scaledBaseFontSize = GetScaledFontSize(BaseFontSize);
        var textBlock = new SelectableTextBlock
        {
            FontSize = scaledBaseFontSize,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = scaledBaseFontSize * LineHeight
        };
        RenderInlineContent(content, textBlock.Inlines!);
        return textBlock;
    }

    private void RenderInlineContent(string content, InlineCollection inlines)
    {
        // Process inline elements (a, b, strong, i, em, code, br)
        var position = 0;
        var inlinePattern = InlineElementPattern();

        foreach (Match match in inlinePattern.Matches(content))
        {
            // Add text before this match
            if (match.Index > position)
            {
                var textBefore = content[position..match.Index];
                if (!string.IsNullOrEmpty(textBefore))
                {
                    inlines.Add(new Run(StripTags(textBefore)));
                }
            }

            var tag = match.Groups["tag"].Value.ToLowerInvariant();
            var innerContent = match.Groups["content"].Value;
            var attrs = ParseAttributes(match.Groups["attrs"].Value);

            switch (tag)
            {
                case "br":
                    inlines.Add(new LineBreak());
                    break;
                case "b":
                case "strong":
                    inlines.Add(new Run(StripTags(innerContent)) { FontWeight = FontWeight.Bold });
                    break;
                case "i":
                case "em":
                    inlines.Add(new Run(StripTags(innerContent)) { FontStyle = FontStyle.Italic });
                    break;
                case "code":
                    inlines.Add(new Run(StripTags(innerContent))
                    {
                        FontFamily = GetThemeFont("ThemeFontMono"),
                        Background = Application.Current?.FindResource("ThemeCodeBlockBrush") as IBrush
                                      ?? new SolidColorBrush(Color.FromArgb(40, 128, 128, 128))
                    });
                    break;
                case "a":
                    var href = attrs.GetValueOrDefault("href", "");
                    var linkText = StripTags(innerContent);
                    var linkBrush = Application.Current?.FindResource("ThemeLinkBrush") as IBrush ?? Brushes.DeepSkyBlue;
                    var hyperlink = new Run(linkText)
                    {
                        Foreground = linkBrush,
                        TextDecorations = TextDecorations.Underline
                    };
                    // Note: Run doesn't support click handlers directly
                    // For full link support, would need InlineUIContainer with a button
                    inlines.Add(hyperlink);
                    break;
                default:
                    inlines.Add(new Run(StripTags(innerContent)));
                    break;
            }

            position = match.Index + match.Length;
        }

        // Add remaining text
        if (position < content.Length)
        {
            var remaining = content[position..];
            if (!string.IsNullOrEmpty(remaining))
            {
                inlines.Add(new Run(StripTags(remaining)));
            }
        }

        // If nothing was added, add the whole content
        if (inlines.Count == 0 && !string.IsNullOrWhiteSpace(content))
        {
            inlines.Add(new Run(StripTags(content)));
        }
    }

    [GeneratedRegex(@"<(?<tag>a|b|strong|i|em|code|span)(?<attrs>[^>]*)>(?<content>.*?)</\k<tag>>|<br\s*/?>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex InlineElementPattern();

    private static string StripTags(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;

        // Replace br with newline
        html = BrTagPattern().Replace(html, "\n");

        // Remove all other tags
        html = HtmlTagPattern().Replace(html, "");

        // Decode entities again after stripping
        html = DecodeHtmlEntities(html);

        // Normalize whitespace
        html = MultipleSpacesPattern().Replace(html, " ");
        html = MultipleNewlinesPattern().Replace(html, "\n\n");

        return html.Trim();
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrTagPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagPattern();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex MultipleSpacesPattern();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultipleNewlinesPattern();

    private static string DecodeHtmlEntities(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;

        // Common HTML entities
        html = html.Replace("&nbsp;", " ");
        html = html.Replace("&amp;", "&");
        html = html.Replace("&lt;", "<");
        html = html.Replace("&gt;", ">");
        html = html.Replace("&quot;", "\"");
        html = html.Replace("&apos;", "'");
        html = html.Replace("&#39;", "'");
        html = html.Replace("&mdash;", "—");
        html = html.Replace("&ndash;", "–");
        html = html.Replace("&hellip;", "…");
        html = html.Replace("&rsquo;", "'");
        html = html.Replace("&lsquo;", "'");
        html = html.Replace("&rdquo;", "\u201D");
        html = html.Replace("&ldquo;", "\u201C");
        html = html.Replace("&copy;", "©");
        html = html.Replace("&reg;", "®");
        html = html.Replace("&trade;", "™");

        // Numeric entities
        html = NumericEntityPattern().Replace(html, match =>
        {
            var numStr = match.Groups[1].Value;
            if (int.TryParse(numStr, out var num) && num is >= 0 and <= 0x10FFFF)
            {
                return char.ConvertFromUtf32(num);
            }
            return match.Value;
        });

        // Hex entities
        html = HexEntityPattern().Replace(html, match =>
        {
            var hexStr = match.Groups[1].Value;
            if (int.TryParse(hexStr, System.Globalization.NumberStyles.HexNumber, null, out var num) && num is >= 0 and <= 0x10FFFF)
            {
                return char.ConvertFromUtf32(num);
            }
            return match.Value;
        });

        return html;
    }

    [GeneratedRegex(@"&#(\d+);")]
    private static partial Regex NumericEntityPattern();

    [GeneratedRegex(@"&#x([0-9a-fA-F]+);")]
    private static partial Regex HexEntityPattern();
}
