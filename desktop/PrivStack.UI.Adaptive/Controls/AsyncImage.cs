using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace PrivStack.UI.Adaptive.Controls;

/// <summary>
/// An image control that loads images asynchronously from URLs.
/// </summary>
public class AsyncImage : Control
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<AsyncImage, string?>(nameof(Source));

    public static readonly StyledProperty<double> MaxImageHeightProperty =
        AvaloniaProperty.Register<AsyncImage, double>(nameof(MaxImageHeight), 300);

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<AsyncImage, Stretch>(nameof(Stretch), Stretch.Uniform);

    private Bitmap? _loadedBitmap;
    private bool _isLoading;
    private bool _hasError;
    private string? _loadedUrl;

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double MaxImageHeight
    {
        get => GetValue(MaxImageHeightProperty);
        set => SetValue(MaxImageHeightProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    static AsyncImage()
    {
        AffectsRender<AsyncImage>(SourceProperty);
        AffectsMeasure<AsyncImage>(SourceProperty, MaxImageHeightProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty)
        {
            LoadImage();
        }
    }

    private async void LoadImage()
    {
        var url = Source;

        // Skip if same URL already loaded
        if (url == _loadedUrl && _loadedBitmap != null)
            return;

        // Clear current state
        _loadedBitmap?.Dispose();
        _loadedBitmap = null;
        _hasError = false;
        _loadedUrl = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            InvalidateVisual();
            InvalidateMeasure();
            return;
        }

        _isLoading = true;
        InvalidateVisual();

        try
        {
            Bitmap? bitmap = null;

            // Handle data URIs
            if (url.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                var base64Start = url.IndexOf(",", StringComparison.Ordinal);
                if (base64Start > 0)
                {
                    var base64Data = url[(base64Start + 1)..];
                    var bytes = Convert.FromBase64String(base64Data);
                    using var stream = new MemoryStream(bytes);
                    bitmap = new Bitmap(stream);
                }
            }
            // Handle HTTP/HTTPS URLs
            else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var response = await SharedHttpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    using var stream = new MemoryStream(bytes);
                    bitmap = new Bitmap(stream);
                }
            }
            // Handle file paths
            else if (File.Exists(url))
            {
                await using var stream = File.OpenRead(url);
                bitmap = new Bitmap(stream);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _loadedBitmap = bitmap;
                _loadedUrl = url;
                _isLoading = false;
                InvalidateVisual();
                InvalidateMeasure();
            });
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _hasError = true;
                _isLoading = false;
                InvalidateVisual();
            });
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_loadedBitmap == null)
        {
            return new Size(Math.Min(availableSize.Width, 200), 50);
        }

        var imageSize = new Size(_loadedBitmap.PixelSize.Width, _loadedBitmap.PixelSize.Height);

        // Scale to fit within available width and max height
        var scale = Math.Min(
            availableSize.Width / imageSize.Width,
            MaxImageHeight / imageSize.Height
        );

        if (scale < 1)
        {
            return new Size(imageSize.Width * scale, imageSize.Height * scale);
        }

        // Don't scale up, but respect max height
        if (imageSize.Height > MaxImageHeight)
        {
            var heightScale = MaxImageHeight / imageSize.Height;
            return new Size(imageSize.Width * heightScale, MaxImageHeight);
        }

        return imageSize;
    }

    private static IBrush GetBrush(string key, IBrush fallback)
    {
        var app = Avalonia.Application.Current;
        if (app is null) return fallback;
        if (app.Resources.TryGetResource(key, app.ActualThemeVariant, out var v) && v is IBrush b)
            return b;
        return fallback;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);

        if (_isLoading)
        {
            // Draw loading state
            var loadingBrush = GetBrush("ThemeHoverBrush", new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)));
            context.DrawRectangle(loadingBrush, null, bounds, 4, 4);

            var textBrush = GetBrush("ThemeTextMutedBrush", new SolidColorBrush(Color.FromArgb(128, 128, 128, 128)));
            var formattedText = new FormattedText(
                "Loading...",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default),
                12,
                textBrush
            );
            context.DrawText(formattedText, new Point(
                (bounds.Width - formattedText.Width) / 2,
                (bounds.Height - formattedText.Height) / 2
            ));
        }
        else if (_hasError)
        {
            // Draw error state
            var errorBrush = GetBrush("ThemeDangerMutedBrush", new SolidColorBrush(Color.FromArgb(30, 200, 50, 50)));
            context.DrawRectangle(errorBrush, null, bounds, 4, 4);

            var textBrush = GetBrush("ThemeDangerBrush", new SolidColorBrush(Color.FromArgb(180, 200, 50, 50)));
            var formattedText = new FormattedText(
                "Failed to load image",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default),
                12,
                textBrush
            );
            context.DrawText(formattedText, new Point(
                (bounds.Width - formattedText.Width) / 2,
                (bounds.Height - formattedText.Height) / 2
            ));
        }
        else if (_loadedBitmap != null)
        {
            // Draw the image
            context.DrawImage(_loadedBitmap, bounds);
        }
        else if (!string.IsNullOrEmpty(Source))
        {
            // No URL - draw placeholder
            var placeholderBrush = GetBrush("ThemeHoverSubtleBrush", new SolidColorBrush(Color.FromArgb(20, 128, 128, 128)));
            context.DrawRectangle(placeholderBrush, null, bounds, 4, 4);

            var textBrush = GetBrush("ThemeTextMutedBrush", new SolidColorBrush(Color.FromArgb(100, 128, 128, 128)));
            var formattedText = new FormattedText(
                "Enter image URL",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default),
                12,
                textBrush
            );
            context.DrawText(formattedText, new Point(
                (bounds.Width - formattedText.Width) / 2,
                (bounds.Height - formattedText.Height) / 2
            ));
        }
    }
}
