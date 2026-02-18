// ============================================================================
// File: InfiniteCanvasElementRendering.cs
// Description: Element-specific rendering methods for the InfiniteCanvasControl.
//              Extracted from InfiniteCanvasRendering.cs for modularity.
// ============================================================================

using System.Globalization;
using Avalonia;
using Avalonia.Media;
using PrivStack.UI.Adaptive.Models;

namespace PrivStack.UI.Adaptive.Controls.InfiniteCanvas;

public sealed partial class InfiniteCanvasControl
{
    private static readonly Color DefaultNoteColor = Color.FromRgb(255, 242, 153);
    private static readonly Color DefaultRectColor = Color.FromRgb(200, 210, 230);
    private static readonly Color DefaultEllipseColor = Color.FromRgb(200, 230, 210);
    private static readonly Color DefaultGroupColor = Color.FromRgb(220, 220, 240);
    private static readonly Color DefaultPageRefColor = Color.FromRgb(220, 235, 255);
    private static readonly Color DefaultEntityRefColor = Color.FromRgb(240, 240, 245);

    private void DrawDropShadow(DrawingContext ctx, Rect rect, double cornerRadius = 4)
    {
        var shadowOffset = 2.0;
        var shadowRect = new Rect(rect.X + shadowOffset, rect.Y + shadowOffset, rect.Width, rect.Height);
        var shadowColor = GetBrush("ThemeShadowBrush", Brushes.Black) is ISolidColorBrush scbShadow
            ? scbShadow.Color : Colors.Black;
        var shadowBrush = new SolidColorBrush(shadowColor, 0.15);
        ctx.DrawRectangle(shadowBrush, null, shadowRect, cornerRadius, cornerRadius);
    }

    private void DrawNoteCard(DrawingContext ctx, CanvasElement element)
    {
        var rect = ElementToScreenRect(element);
        DrawDropShadow(ctx, rect, 8);
        var color = ParseColor(element.Color, DefaultNoteColor);
        var brush = new SolidColorBrush(color, 0.9);
        var noteStrokeColor = GetBrush("ThemeShadowBrush", Brushes.Black) is ISolidColorBrush scbNoteStroke
            ? scbNoteStroke.Color : Colors.Black;
        var borderPen = new Pen(new SolidColorBrush(noteStrokeColor, 0.15), 1);
        ctx.DrawRectangle(brush, borderPen, rect, 8, 8);

        if (string.IsNullOrEmpty(element.Text)) return;

        using (ctx.PushClip(rect))
        {
            var pad = 10 * Zoom;
            var fontSize = Math.Max(8, element.FontSize * Zoom);
            var noteTextColor = GetBrush("ThemeShadowBrush", Brushes.Black) is ISolidColorBrush scbNoteText
                ? scbNoteText.Color : Colors.Black;
            var textBrush = new SolidColorBrush(noteTextColor, 0.85);
            var ft = new FormattedText(
                element.Text, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                GetSansTypeface(FontWeight.Normal),
                fontSize, textBrush);
            ft.MaxTextWidth = Math.Max(10, rect.Width - pad * 2);
            ft.MaxTextHeight = Math.Max(10, rect.Height - pad * 2);
            ctx.DrawText(ft, new Point(rect.X + pad, rect.Y + pad));
        }
    }

    private void DrawTextElement(DrawingContext ctx, CanvasElement element)
    {
        if (string.IsNullOrEmpty(element.Text)) return;

        var tl = WorldToScreen(element.X, element.Y);
        var fontSize = Math.Max(8, element.FontSize * Zoom);
        var textBrush = GetBrush("ThemeTextPrimaryBrush", Brushes.White);
        var ft = new FormattedText(
            element.Text, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            GetSansTypeface(FontWeight.Normal),
            fontSize, textBrush);
        ft.MaxTextWidth = Math.Max(10, element.Width * Zoom);
        ctx.DrawText(ft, new Point(tl.X, tl.Y));
    }

    private void DrawRectElement(DrawingContext ctx, CanvasElement element)
    {
        var rect = ElementToScreenRect(element);
        DrawDropShadow(ctx, rect);
        var color = ParseColor(element.Color, DefaultRectColor);
        var brush = new SolidColorBrush(color, 0.6);
        var strokeColor = ParseColor(element.StrokeColor, color);
        var pen = new Pen(new SolidColorBrush(strokeColor), element.StrokeWidth * Zoom);
        ctx.DrawRectangle(brush, pen, rect, 4, 4);

        DrawElementLabel(ctx, element, rect);
    }

    private void DrawEllipseElement(DrawingContext ctx, CanvasElement element)
    {
        var rect = ElementToScreenRect(element);
        DrawDropShadow(ctx, rect);
        var color = ParseColor(element.Color, DefaultEllipseColor);
        var brush = new SolidColorBrush(color, 0.6);
        var strokeColor = ParseColor(element.StrokeColor, color);
        var pen = new Pen(new SolidColorBrush(strokeColor), element.StrokeWidth * Zoom);
        var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        ctx.DrawEllipse(brush, pen, center, rect.Width / 2, rect.Height / 2);

        DrawElementLabel(ctx, element, rect);
    }

    private void DrawPageReference(DrawingContext ctx, CanvasElement element)
    {
        var rect = ElementToScreenRect(element);
        var brush = new SolidColorBrush(DefaultPageRefColor, 0.9);
        var borderPen = new Pen(GetBrush("ThemePrimaryBrush", Brushes.CornflowerBlue), 2 * Zoom);
        ctx.DrawRectangle(brush, borderPen, rect, 6, 6);

        // Page icon (small doc indicator)
        var iconSize = 16 * Zoom;
        var pad = 10 * Zoom;
        var iconBrush = GetBrush("ThemePrimaryBrush", Brushes.CornflowerBlue);
        var iconRect = new Rect(rect.X + pad, rect.Y + pad, iconSize, iconSize);
        ctx.DrawRectangle(null, new Pen(iconBrush, 1.5 * Zoom), iconRect, 2, 2);

        // Page title text
        var title = string.IsNullOrEmpty(element.Text) ? "Page Reference" : element.Text;
        var fontSize = Math.Max(8, 13 * Zoom);
        var ft = new FormattedText(
            title, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            GetSansTypeface(FontWeight.SemiBold),
            fontSize, iconBrush);
        ft.MaxTextWidth = Math.Max(10, rect.Width - pad * 2 - iconSize - 4 * Zoom);
        ctx.DrawText(ft, new Point(rect.X + pad + iconSize + 4 * Zoom, rect.Y + pad));
    }

    private void DrawEntityReference(DrawingContext ctx, CanvasElement element)
    {
        var rect = ElementToScreenRect(element);
        var badgeColor = ParseColor(element.Color, DefaultEntityRefColor);
        var bgBrush = new SolidColorBrush(DefaultEntityRefColor, 0.95);
        var borderPen = new Pen(new SolidColorBrush(badgeColor, 0.6), 2 * Zoom);
        ctx.DrawRectangle(bgBrush, borderPen, rect, 8, 8);

        var pad = 10 * Zoom;
        var badgeRadius = 14 * Zoom;

        // Badge circle with first letter of display name
        var badgeLetter = "?";
        var styles = EntityRefStyles;
        if (styles != null && !string.IsNullOrEmpty(element.EntityType)
            && styles.TryGetValue(element.EntityType, out var style)
            && !string.IsNullOrEmpty(style.DisplayName))
        {
            badgeLetter = style.DisplayName[..1].ToUpperInvariant();
        }

        var badgeCenterX = rect.X + pad + badgeRadius;
        var badgeCenterY = rect.Y + rect.Height / 2;
        var badgeBrush = new SolidColorBrush(badgeColor);
        ctx.DrawEllipse(badgeBrush, null, new Point(badgeCenterX, badgeCenterY), badgeRadius, badgeRadius);

        var letterFontSize = Math.Max(8, 12 * Zoom);
        var letterFt = new FormattedText(
            badgeLetter, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            GetSansTypeface(FontWeight.Bold),
            letterFontSize, GetBrush("ThemeTextPrimaryBrush", Brushes.White));
        ctx.DrawText(letterFt, new Point(
            badgeCenterX - letterFt.Width / 2,
            badgeCenterY - letterFt.Height / 2));

        // Title text (SemiBold)
        var textX = rect.X + pad + badgeRadius * 2 + 8 * Zoom;
        var textMaxW = Math.Max(10, rect.Right - textX - pad);

        var title = string.IsNullOrEmpty(element.Text) ? "Entity Reference" : element.Text;
        var titleFontSize = Math.Max(8, 13 * Zoom);
        var titleBrush = GetBrush("ThemeTextPrimaryBrush", Brushes.White);
        var titleFt = new FormattedText(
            title, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            GetSansTypeface(FontWeight.SemiBold),
            titleFontSize, titleBrush);
        titleFt.MaxTextWidth = textMaxW;
        ctx.DrawText(titleFt, new Point(textX, rect.Y + pad));

        // Subtitle text (muted)
        if (!string.IsNullOrEmpty(element.Label))
        {
            var subtitleFontSize = Math.Max(7, 11 * Zoom);
            var subtitleBrush = GetBrush("ThemeTextMutedBrush", Brushes.Gray);
            var subtitleFt = new FormattedText(
                element.Label, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                GetSansTypeface(FontWeight.Normal),
                subtitleFontSize, subtitleBrush);
            subtitleFt.MaxTextWidth = textMaxW;
            ctx.DrawText(subtitleFt, new Point(textX, rect.Y + pad + titleFt.Height + 2 * Zoom));
        }
    }

    private void DrawGroupFrame(DrawingContext ctx, CanvasElement element)
    {
        var rect = ElementToScreenRect(element);
        var color = ParseColor(element.Color, DefaultGroupColor);
        var brush = new SolidColorBrush(color, 0.15);
        var pen = new Pen(new SolidColorBrush(color, 0.5), 2 * Zoom)
        {
            DashStyle = DashStyle.Dash,
        };
        ctx.DrawRectangle(brush, pen, rect, 8, 8);

        // Label at top
        if (!string.IsNullOrEmpty(element.Label))
        {
            var fontSize = Math.Max(8, 12 * Zoom);
            var textBrush = GetBrush("ThemeTextMutedBrush", Brushes.Gray);
            var ft = new FormattedText(
                element.Label, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                GetSansTypeface(FontWeight.SemiBold),
                fontSize, textBrush);
            ctx.DrawText(ft, new Point(rect.X + 8 * Zoom, rect.Y + 4 * Zoom));
        }
    }

    private void DrawImagePlaceholder(DrawingContext ctx, CanvasElement element)
    {
        var rect = ElementToScreenRect(element);
        var imgPlaceholderColor = GetBrush("ThemeTextMutedBrush", Brushes.Gray) is ISolidColorBrush scbImg
            ? scbImg.Color : Colors.Gray;
        var brush = new SolidColorBrush(imgPlaceholderColor, 0.2);
        var pen = new Pen(new SolidColorBrush(imgPlaceholderColor, 0.4), 1);
        ctx.DrawRectangle(brush, pen, rect, 4, 4);

        // Placeholder text
        var fontSize = Math.Max(8, 12 * Zoom);
        var textBrush = GetBrush("ThemeTextMutedBrush", Brushes.Gray);
        var ft = new FormattedText(
            "Image", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            GetSansTypeface(FontWeight.Normal),
            fontSize, textBrush);
        var tx = rect.X + (rect.Width - ft.Width) / 2;
        var ty = rect.Y + (rect.Height - ft.Height) / 2;
        ctx.DrawText(ft, new Point(tx, ty));
    }

    private void DrawElementLabel(DrawingContext ctx, CanvasElement element, Rect rect)
    {
        if (string.IsNullOrEmpty(element.Text)) return;

        var fontSize = Math.Max(8, element.FontSize * Zoom);
        var textBrush = GetBrush("ThemeTextPrimaryBrush", Brushes.White);
        var ft = new FormattedText(
            element.Text, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            GetSansTypeface(FontWeight.Normal),
            fontSize, textBrush);
        ft.MaxTextWidth = Math.Max(10, rect.Width - 16 * Zoom);
        var tx = rect.X + (rect.Width - Math.Min(ft.Width, ft.MaxTextWidth)) / 2;
        var ty = rect.Y + (rect.Height - ft.Height) / 2;
        ctx.DrawText(ft, new Point(tx, ty));
    }

    internal static Color ParseColor(string? colorStr, Color fallback)
    {
        if (string.IsNullOrEmpty(colorStr))
            return fallback;

        try
        {
            if (colorStr.StartsWith('#'))
                return Color.Parse(colorStr);

            return colorStr switch
            {
                "yellow" => Color.FromRgb(255, 242, 153),
                "blue" => Color.FromRgb(153, 204, 255),
                "green" => Color.FromRgb(153, 230, 179),
                "pink" => Color.FromRgb(255, 179, 204),
                "purple" => Color.FromRgb(204, 179, 255),
                "orange" => Color.FromRgb(255, 204, 153),
                "red" => Color.FromRgb(255, 153, 153),
                "gray" => Color.FromRgb(200, 200, 200),
                _ => fallback,
            };
        }
        catch
        {
            return fallback;
        }
    }
}
