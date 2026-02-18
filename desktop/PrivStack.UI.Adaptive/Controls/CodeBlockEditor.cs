// ============================================================================
// File: CodeBlockEditor.cs
// Description: Editable code block with TextMate syntax highlighting and a
//              language selector dropdown. Built on AvaloniaEdit.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace PrivStack.UI.Adaptive.Controls;

/// <summary>
/// An editable code block with syntax highlighting and language picker.
/// Fires <see cref="CodeChanged"/> on text edits and <see cref="LanguageChanged"/>
/// when the user switches language.
/// </summary>
public sealed class CodeBlockEditor : Border
{
    private static readonly string[] CommonLanguages =
    [
        "Plain Text", "Bash", "C", "C++", "C#", "CSS", "Go", "HTML",
        "Java", "JavaScript", "JSON", "Kotlin", "Markdown", "PHP",
        "Python", "Ruby", "Rust", "SQL", "Swift", "TOML", "TypeScript",
        "XML", "YAML"
    ];

    private readonly TextEditor _editor;
    private readonly AutoCompleteBox _languagePicker;
    private Border _langLabel = null!;
    private TextBlock _langLabelText = null!;
    private readonly DockPanel _toolbar;
    private TextMate.Installation? _textMateInstallation;
    private RegistryOptions? _registryOptions;
    private bool _suppressEvents;

    /// <summary>Fired when the code text changes. Args: (blockId, newCode).</summary>
    public event Action<string, string>? CodeChanged;

    /// <summary>Fired when the selected language changes. Args: (blockId, language).</summary>
    public event Action<string, string>? LanguageChanged;

    public string BlockId { get; set; } = "";

    public string Code
    {
        get => _editor.Document.Text;
        set
        {
            _suppressEvents = true;
            _editor.Document.Text = value;
            _suppressEvents = false;
        }
    }

    public string Language
    {
        get => _languagePicker.Text ?? "Plain Text";
        set
        {
            _suppressEvents = true;
            _languagePicker.Text = value;
            _langLabelText.Text = string.IsNullOrWhiteSpace(value) ? "Plain Text" : value;
            ApplyGrammar(value);
            _suppressEvents = false;
        }
    }

    /// <summary>
    /// Controls visibility of the language picker toolbar.
    /// </summary>
    public bool ShowToolbar
    {
        get => _toolbar.IsVisible;
        set => _toolbar.IsVisible = value;
    }

    /// <summary>
    /// Controls word wrap on the underlying text editor.
    /// </summary>
    public bool IsWordWrapEnabled
    {
        get => _editor.WordWrap;
        set => _editor.WordWrap = value;
    }

    public bool IsReadOnly
    {
        get => _editor.IsReadOnly;
        set => _editor.IsReadOnly = value;
    }

    public CodeBlockEditor()
    {
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(0);
        Margin = new Thickness(0, 4);
        BorderThickness = new Thickness(1);
        HorizontalAlignment = HorizontalAlignment.Stretch;

        _editor = new TextEditor
        {
            ShowLineNumbers = true,
            WordWrap = false,
            FontFamily = ThemeFont("ThemeFontMono"),
            FontSize = 13,
            IsReadOnly = false,
            Padding = new Thickness(8, 8, 8, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            MinHeight = 60,
        };
        _editor.Bind(TextEditor.FontSizeProperty, _editor.GetResourceObservable("ThemeFontSizeSmMd"));

        _editor.TextChanged += OnEditorTextChanged;

        _languagePicker = new AutoCompleteBox
        {
            ItemsSource = CommonLanguages,
            FilterMode = AutoCompleteFilterMode.Contains,
            Watermark = "Type to filter...",
            MinWidth = 140,
            MaxWidth = 180,
            FontSize = 11, // initial; overridden by bind below
            FontFamily = ThemeFont("ThemeFontMono"),
            Margin = new Thickness(0),
            Padding = new Thickness(6, 2),
            Opacity = 0,
            IsHitTestVisible = false,
            MinimumPrefixLength = 0,
            MaxDropDownHeight = 200,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((lang, _) =>
            {
                var tb = new TextBlock
                {
                    Text = lang,
                    FontSize = ThemeDouble("ThemeFontSizeSm", 12),
                    FontFamily = ThemeFont("ThemeFontMono"),
                    Padding = new Thickness(10, 6),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var border = new Border
                {
                    Child = tb,
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(2, 1),
                    Background = Brushes.Transparent,
                };
                border.PointerEntered += (s, _) =>
                {
                    if (s is Border b) b.Background = GetBrush("ThemeHoverBrush", new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)));
                };
                border.PointerExited += (s, _) =>
                {
                    if (s is Border b) b.Background = Brushes.Transparent;
                };
                return border;
            }),
        };
        _languagePicker.Bind(AutoCompleteBox.FontSizeProperty, _languagePicker.GetResourceObservable("ThemeFontSizeXsSm"));
        _languagePicker.TextChanged += OnLanguageTextChanged;
        _languagePicker.SelectionChanged += OnLanguageSelectionChanged;
        _languagePicker.LostFocus += (_, _) =>
        {
            // Switch back to pill display
            _languagePicker.Opacity = 0;
            _languagePicker.IsHitTestVisible = false;
            _languagePicker.IsDropDownOpen = false;
            _langLabel.Opacity = 0.7;
            _langLabel.IsHitTestVisible = true;
        };
        // Close dropdown when parent scrolls to prevent it floating detached
        // We listen on the CodeBlockEditor itself — if a scroll event reaches us
        // (bubbles up from inside the dropdown), that's fine. But we want to catch
        // scroll events from ancestor ScrollViewers too.
        AttachedToVisualTree += (_, _) =>
        {
            // Walk up to find the nearest ScrollViewer ancestor
            var parent = Parent as Control;
            while (parent != null)
            {
                if (parent is ScrollViewer sv)
                {
                    sv.ScrollChanged += OnParentScrollChanged;
                    break;
                }
                parent = parent.Parent as Control;
            }
        };
        // Handle Enter/Escape in the picker
        _languagePicker.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape)
            {
                _languagePicker.Opacity = 0;
                _languagePicker.IsHitTestVisible = false;
                _langLabel.Opacity = 0.7;
                _langLabel.IsHitTestVisible = true;
                _editor.Focus();
                e.Handled = true;
            }
        };

        // Pill-style label that shows current language
        _langLabel = new Border
        {
            Child = _langLabelText = new TextBlock
            {
                Text = "Plain Text",
                FontSize = ThemeDouble("ThemeFontSizeXs", 10),
                FontFamily = ThemeFont("ThemeFontMono"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
            Padding = new Thickness(8, 2),
            CornerRadius = new CornerRadius(10),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
        };
        _langLabel.PointerEntered += (_, _) =>
        {
            if (_langLabel.IsHitTestVisible) _langLabel.Opacity = 1.0;
        };
        _langLabel.PointerExited += (_, _) =>
        {
            if (_langLabel.IsHitTestVisible) _langLabel.Opacity = 0.7;
        };
        _langLabel.PointerReleased += (_, e) =>
        {
            // Hide pill, show autocomplete
            _langLabel.Opacity = 0;
            _langLabel.IsHitTestVisible = false;
            _languagePicker.Opacity = 1;
            _languagePicker.IsHitTestVisible = true;

            _suppressEvents = true;
            _languagePicker.Text = "";
            _suppressEvents = false;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _languagePicker.Focus();
                _languagePicker.IsDropDownOpen = true;
            }, Avalonia.Threading.DispatcherPriority.Background);
            e.Handled = true;
        };

        // Use a Panel overlay so pill and picker share the same space
        var pickerHost = new Panel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        pickerHost.Children.Add(_languagePicker);
        pickerHost.Children.Add(_langLabel);

        _toolbar = new DockPanel
        {
            Margin = new Thickness(8, 2, 8, 0),
            MaxHeight = 28,
        };
        DockPanel.SetDock(pickerHost, Dock.Right);
        _toolbar.Children.Add(pickerHost);
        _toolbar.Children.Add(new Control()); // spacer

        var layout = new DockPanel
        {
            LastChildFill = true,
        };
        DockPanel.SetDock(_toolbar, Dock.Top);
        layout.Children.Add(_toolbar);
        layout.Children.Add(_editor);

        Child = layout;

        // Defer TextMate setup until attached to visual tree (theme available)
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        SetupTextMate();
        ActualThemeVariantChanged += OnThemeChanged;
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        _textMateInstallation?.Dispose();
        _textMateInstallation = null;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _textMateInstallation?.Dispose();
        _textMateInstallation = null;
        SetupTextMate();
    }

    private void SetupTextMate()
    {
        var theme = ActualThemeVariant == ThemeVariant.Light
            ? ThemeName.LightPlus
            : ThemeName.DarkPlus;

        _registryOptions = new RegistryOptions(theme);
        _textMateInstallation = _editor.InstallTextMate(_registryOptions);

        // Apply theme colors
        Background = GetBrush("ThemeCodeBlockBrush", null)
                  ?? GetBrush("ThemeSurfaceElevatedBrush", new SolidColorBrush(Color.FromRgb(30, 30, 30)));
        BorderBrush = GetBrush("ThemeBorderSubtleBrush", Brushes.Gray);

        if (GetBrush("ThemeTextMutedBrush", null) is IBrush lineNumBrush)
            _editor.LineNumbersForeground = lineNumBrush;

        if (GetBrush("ThemeSelectionBrush", null) is IBrush selBrush)
            _editor.TextArea.SelectionBrush = selBrush;

        _editor.TextArea.SelectionForeground = null;
        _editor.Foreground = GetBrush("ThemeTextPrimaryBrush", Brushes.White)!;

        _editor.Background = Brushes.Transparent;
        _editor.TextArea.Background = Brushes.Transparent;

        // Style the language pill
        _langLabel.Background = GetBrush("ThemeHoverSubtleBrush", new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)));
        _langLabelText.Foreground = GetBrush("ThemeTextMutedBrush", Brushes.Gray);

        ApplyGrammar(Language);
    }

    private IBrush? GetBrush(string key, IBrush? fallback)
    {
        if (this.TryGetResource(key, ActualThemeVariant, out var obj) && obj is IBrush brush)
            return brush;
        // Fallback: look up from Application resources directly
        var app = Application.Current;
        if (app != null && app.Resources.TryGetResource(key, app.ActualThemeVariant, out var appObj) && appObj is IBrush appBrush)
            return appBrush;
        return fallback;
    }

    private void ApplyGrammar(string? language)
    {
        if (_textMateInstallation is null || _registryOptions is null) return;

        if (string.IsNullOrWhiteSpace(language) || language == "Plain Text")
        {
            _textMateInstallation.SetGrammar(null);
            return;
        }

        var ext = MapLanguageToExtension(language);
        if (ext is null)
        {
            _textMateInstallation.SetGrammar(null);
            return;
        }

        try
        {
            var lang = _registryOptions.GetLanguageByExtension(ext);
            if (lang != null)
            {
                var scope = _registryOptions.GetScopeByLanguageId(lang.Id);
                _textMateInstallation.SetGrammar(scope);
                return;
            }
        }
        catch { /* extension not recognized */ }

        _textMateInstallation.SetGrammar(null);
    }

    private static string? MapLanguageToExtension(string language) =>
        language.ToLowerInvariant() switch
        {
            "bash" or "shell" or "sh" => ".sh",
            "c" => ".c",
            "c++" or "cpp" => ".cpp",
            "c#" or "csharp" => ".cs",
            "css" => ".css",
            "go" => ".go",
            "html" => ".html",
            "java" => ".java",
            "javascript" or "js" => ".js",
            "json" => ".json",
            "kotlin" or "kt" => ".kt",
            "markdown" or "md" => ".md",
            "php" => ".php",
            "python" or "py" => ".py",
            "ruby" or "rb" => ".rb",
            "rust" or "rs" => ".rs",
            "sql" => ".sql",
            "swift" => ".swift",
            "toml" => ".toml",
            "typescript" or "ts" => ".ts",
            "xml" => ".xml",
            "yaml" or "yml" => ".yaml",
            _ => $".{language.ToLowerInvariant()}"
        };

    private void OnParentScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (!_languagePicker.IsDropDownOpen) return;
        _languagePicker.IsDropDownOpen = false;
        _languagePicker.Opacity = 0;
        _languagePicker.IsHitTestVisible = false;
        _langLabel.Opacity = 0.7;
        _langLabel.IsHitTestVisible = true;
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressEvents) return;
        CodeChanged?.Invoke(BlockId, _editor.Document.Text);
    }

    private void OnLanguageTextChanged(object? sender, EventArgs e)
    {
        if (_suppressEvents) return;
        // Apply grammar immediately as user types
        ApplyGrammar(_languagePicker.Text);
    }

    private void OnLanguageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var lang = _languagePicker.SelectedItem as string ?? _languagePicker.Text ?? "Plain Text";
        if (string.IsNullOrWhiteSpace(lang)) return;
        _langLabelText.Text = lang;
        ApplyGrammar(lang);
        LanguageChanged?.Invoke(BlockId, lang);
        // Reset picker state so it works cleanly next time
        _suppressEvents = true;
        _languagePicker.SelectedItem = null;
        _languagePicker.Text = "";
        _suppressEvents = false;
        // Switch back to pill display
        _languagePicker.Opacity = 0;
        _languagePicker.IsHitTestVisible = false;
        _languagePicker.IsDropDownOpen = false;
        _langLabel.Opacity = 0.7;
        _langLabel.IsHitTestVisible = true;
    }

    private static double ThemeDouble(string key, double fallback)
    {
        var app = Application.Current;
        if (app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var v) == true && v is double d)
            return d;
        return fallback;
    }

    private static FontFamily ThemeFont(string key) =>
        Application.Current?.FindResource(key) as FontFamily ?? FontFamily.Default;
}
