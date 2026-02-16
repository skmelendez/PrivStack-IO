// ============================================================================
// File: Icons.cs
// Description: Lucide-style icon path data and IconControl for rendering SVG icons.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PrivStack.Desktop.Controls;

/// <summary>
/// Static class containing Lucide-style SVG path data for icons.
/// All icons are designed for a 24x24 viewbox with stroke-based rendering.
/// </summary>
public static class IconData
{
    // Navigation icons
    public const string Document = "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z M14 2v6h6 M16 13H8 M16 17H8 M10 9H8";
    public const string FileText = Document;
    public const string Notes = Document;

    public const string CheckSquare = "M9 11l3 3L22 4 M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11";
    public const string Tasks = CheckSquare;

    public const string Calendar = "M19 4H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2z M16 2v4 M8 2v4 M3 10h18";

    public const string Book = "M4 19.5A2.5 2.5 0 0 1 6.5 17H20 M4 19.5A2.5 2.5 0 0 0 6.5 22H20V2H6.5A2.5 2.5 0 0 0 4 4.5v15z";
    public const string Journal = Book;

    public const string Lock = "M19 11H5a2 2 0 0 0-2 2v7a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7a2 2 0 0 0-2-2z M7 11V7a5 5 0 0 1 10 0v4";
    public const string Passwords = Lock;

    public const string Folder = "M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z";
    public const string Files = Folder;

    public const string Code = "M16 18l6-6-6-6 M8 6l-6 6 6 6";
    public const string Snippets = Code;

    public const string Rss = "M4 11a9 9 0 0 1 9 9 M4 4a16 16 0 0 1 16 16 M5 19a1 1 0 1 0 0-2 1 1 0 0 0 0 2z";

    public const string Users = "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 M9 7a4 4 0 1 0 0-8 4 4 0 0 0 0 8z M22 21v-2a4 4 0 0 0-3-3.87 M16 3.13a4 4 0 0 1 0 7.75";
    public const string Contacts = Users;

    public const string Network = "M12 2a4 4 0 0 0-4 4v1H4a2 2 0 0 0-2 2v2a2 2 0 0 0 2 2h4v5a4 4 0 0 0 8 0v-5h4a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2h-4V6a4 4 0 0 0-4-4z";

    // Mind graph - nodes connected by lines
    public const string MindGraph = "M5.5 8.5a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M18.5 8.5a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M12 21.5a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M8.5 7l3 8.5 M15.5 7l-3 8.5";
    public const string Graph = MindGraph;
    public const string BrainCircuit = "M12 5a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M5 12a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M19 12a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M12 22a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M12 5v11 M5 9l14 0 M7.5 10.5l4.5 5.5 M16.5 10.5l-4.5 5.5";

    public const string TrendingUp = "M22 7l-8.5 8.5-5-5L2 17 M16 7h6v6";
    public const string Pipeline = TrendingUp;

    public const string Wallet = "M21 12V7H5a2 2 0 0 1 0-4h14v4 M3 5v14a2 2 0 0 0 2 2h16v-5 M18 12a1 1 0 1 0 0 2 1 1 0 0 0 0-2z";
    public const string Ledger = Wallet;

    // Lucide "store" — storefront with awning
    public const string Store = "M3 9l1-4h16l1 4 M3 9h18v1a3 3 0 0 1-6 0 3 3 0 0 1-6 0 3 3 0 0 1-6 0V9z M5 21V10 M19 21V10 M5 21h14 M9 21v-6h6v6";

    // Package / Box icons (Lucide)
    public const string Box = "M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z M3.27 6.96L12 12.01l8.73-5.05 M12 22.08V12";
    public const string Package = "M16.5 9.4l-9-5.19 M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z M3.27 6.96L12 12.01l8.73-5.05 M12 22.08V12";

    public const string Database = "M12 2C6.48 2 2 4.02 2 6.5S6.48 11 12 11s10-2.02 10-4.5S17.52 2 12 2z M2 6.5v5C2 13.98 6.48 16 12 16s10-2.02 10-4.5v-5 M2 11.5v5C2 18.98 6.48 21 12 21s10-2.02 10-4.5v-5";

    public const string Cpu = "M4 4h16v16H4z M9 1v3 M15 1v3 M9 20v3 M15 20v3 M20 9h3 M20 14h3 M1 9h3 M1 14h3";

    // Utility icons
    public const string Settings = "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z M12 16a4 4 0 1 0 0-8 4 4 0 0 0 0 8z";

    public const string RefreshCw = "M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8 M21 3v5h-5 M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16 M8 16H3v5";
    public const string Sync = RefreshCw;

    public const string Download = "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M7 10l5 5 5-5 M12 15V3";
    public const string Updates = Download;

    public const string User = "M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2 M12 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8z";

    public const string Menu = "M4 12h16 M4 6h16 M4 18h16";
    public const string Hamburger = Menu;

    public const string ChevronLeft = "M15 18l-6-6 6-6";
    public const string ChevronRight = "M9 18l6-6-6-6";
    public const string ChevronDown = "M6 9l6 6 6-6";
    public const string ChevronUp = "M18 15l-6-6-6 6";

    public const string Search = "M11 3a8 8 0 1 0 0 16 8 8 0 0 0 0-16z M21 21l-4.35-4.35";
    public const string Command = "M18 3a3 3 0 0 0-3 3v12a3 3 0 0 0 3 3 3 3 0 0 0 3-3 3 3 0 0 0-3-3H6a3 3 0 0 0-3 3 3 3 0 0 0 3 3 3 3 0 0 0 3-3V6a3 3 0 0 0-3-3 3 3 0 0 0-3 3 3 3 0 0 0 3 3h12a3 3 0 0 0 3-3 3 3 0 0 0-3-3z";

    public const string Plus = "M12 5v14 M5 12h14";
    public const string X = "M18 6L6 18 M6 6l12 12";
    public const string Check = "M20 6L9 17l-5-5";

    public const string Info = "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20z M12 16v-4 M12 8h.01";
    public const string AlertCircle = "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20z M12 8v4 M12 16h.01";

    public const string PanelLeftClose = "M3 3h18v18H3z M9 3v18 M15 9l-3 3 3 3";
    public const string PanelLeftOpen = "M3 3h18v18H3z M9 3v18 M14 9l3 3-3 3";
    public const string Sidebar = "M3 3h18v18H3z M9 3v18";

    // Lucide "pencil" — edit icon
    public const string Pencil = "M17 3a2.85 2.85 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z M15 5l4 4";
    public const string Edit = Pencil;

    // Lucide "trash-2"
    public const string Trash2 = "M3 6h18 M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6 M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2 M10 11v6 M14 11v6";

    // Lucide "layout"
    public const string Layout = "M19 3H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V5a2 2 0 0 0-2-2z M3 9h18 M9 21V9";

    // Lucide "layout-dashboard" — 4-panel asymmetric grid
    public const string LayoutDashboard = "M3 3h7v9H3z M14 3h7v5h-7z M14 12h7v9h-7z M3 16h7v5H3z";

    // Communication icons
    public const string Phone = "M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6 19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72 12.84 12.84 0 0 0 .7 2.81 2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45 12.84 12.84 0 0 0 2.81.7A2 2 0 0 1 22 16.92z";
    public const string Mail = "M4 4h16c1.1 0 2 .9 2 2v12c0 1.1-.9 2-2 2H4c-1.1 0-2-.9-2-2V6c0-1.1.9-2 2-2z M22 6l-10 7L2 6";
    public const string MessageSquare = "M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z";
    public const string Globe = "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20z M2 12h20 M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z";

    // Lucide-style "whiteboard" — monitor/canvas with stand
    public const string Whiteboard = "M3 3h18a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H3a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M8 21h8 M12 17v4";

    public const string MoreHorizontal = "M12 13a1 1 0 1 0 0-2 1 1 0 0 0 0 2z M19 13a1 1 0 1 0 0-2 1 1 0 0 0 0 2z M5 13a1 1 0 1 0 0-2 1 1 0 0 0 0 2z";

    // Lucide "link"
    public const string Link = "M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71 M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71";

    // Lucide "external-link"
    public const string ExternalLink = "M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6 M15 3h6v6 M10 14L21 3";

    /// <summary>
    /// Gets the icon path data for a given icon name.
    /// </summary>
    public static string? GetIcon(string? iconName)
    {
        if (string.IsNullOrEmpty(iconName)) return null;

        return iconName switch
        {
            "Document" or "FileText" or "Notes" => Document,
            "CheckSquare" or "Tasks" => CheckSquare,
            "Calendar" => Calendar,
            "Book" or "BookOpen" or "Journal" => Book,
            "Lock" or "Passwords" => Lock,
            "Folder" or "FolderLock" or "Files" => Folder,
            "Code" or "Snippets" => Code,
            "Rss" or "RSS" => Rss,
            "Users" or "Contacts" => Users,
            "Network" => Network,
            "Graph" or "MindGraph" => MindGraph,
            "BrainCircuit" => BrainCircuit,
            "TrendingUp" or "Pipeline" => TrendingUp,
            "Wallet" or "Ledger" => Wallet,
            "Store" or "Marketplace" => Store,
            "Box" => Box,
            "Package" => Package,
            "Database" => Database,
            "Cpu" => Cpu,
            "Settings" => Settings,
            "RefreshCw" or "Sync" => Sync,
            "Download" or "Updates" => Updates,
            "User" => User,
            "Menu" or "Hamburger" => Menu,
            "ChevronLeft" => ChevronLeft,
            "ChevronRight" => ChevronRight,
            "ChevronDown" => ChevronDown,
            "ChevronUp" => ChevronUp,
            "Search" => Search,
            "Command" => Command,
            "Plus" => Plus,
            "X" => X,
            "Check" => Check,
            "Info" => Info,
            "AlertCircle" => AlertCircle,
            "PanelLeftClose" => PanelLeftClose,
            "PanelLeftOpen" => PanelLeftOpen,
            "Sidebar" => Sidebar,
            "Pencil" or "Edit" => Pencil,
            "Trash2" or "Trash" => Trash2,
            "Layout" => Layout,
            "LayoutDashboard" or "Dashboard" => LayoutDashboard,
            "Link" => Link,
            "ExternalLink" => ExternalLink,
            "Phone" => Phone,
            "Mail" => Mail,
            "MessageSquare" => MessageSquare,
            "Globe" => Globe,
            "Whiteboard" or "Canvas" => Whiteboard,
            "MoreHorizontal" => MoreHorizontal,
            _ => null
        };
    }
}

/// <summary>
/// Control that renders an icon from path data.
/// </summary>
public class IconControl : Control
{
    private Geometry? _geometry;

    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<IconControl, string?>(nameof(Icon));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<IconControl, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<IconControl, double>(nameof(StrokeThickness), 2.0);

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<IconControl, double>(nameof(Size), 20.0);

    /// <summary>
    /// The icon name to render (e.g., "Document", "Lock", "Settings").
    /// </summary>
    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>
    /// The stroke brush for the icon.
    /// </summary>
    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>
    /// The stroke thickness (default 2.0).
    /// </summary>
    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>
    /// The size of the icon (width and height).
    /// </summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    static IconControl()
    {
        AffectsRender<IconControl>(IconProperty, StrokeProperty, StrokeThicknessProperty, SizeProperty);
        AffectsMeasure<IconControl>(SizeProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IconProperty)
        {
            UpdateGeometry();
        }
    }

    private void UpdateGeometry()
    {
        var pathData = IconData.GetIcon(Icon);
        if (string.IsNullOrEmpty(pathData))
        {
            _geometry = null;
            return;
        }

        try
        {
            _geometry = Geometry.Parse(pathData);
        }
        catch
        {
            _geometry = null;
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_geometry == null)
        {
            UpdateGeometry();
        }

        if (_geometry == null) return;

        var size = Size;
        var scale = size / 24.0; // Icons are designed for 24x24 viewbox

        var stroke = Stroke ?? Brushes.White;
        var pen = new Pen(stroke, StrokeThickness / scale)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };

        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            context.DrawGeometry(null, pen, _geometry);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(Size, Size);
    }
}
