using System.Windows;
using System.Windows.Media;

namespace CinnatiWorkout;

/// <summary>
/// Colore principale dell'app: dal colore scelto ricava tutte le varianti usate dal tema
/// (hover, premuto, sfondo tenue, testo leggibile su bianco, testo leggibile sul colore).
/// Le risorse sono referenziate con DynamicResource, quindi il cambio è immediato.
/// </summary>
public static class ThemeManager
{
    public const string DefaultAccent = "#0D9488";

    public static readonly (string Name, string Hex)[] Presets =
    [
        ("Verde acqua", "#0D9488"),
        ("Smeraldo", "#059669"),
        ("Blu", "#2563EB"),
        ("Indaco", "#4F46E5"),
        ("Viola", "#7C3AED"),
        ("Rosa", "#DB2777"),
        ("Rosso", "#DC2626"),
        ("Arancione", "#EA580C"),
        ("Ambra", "#D97706"),
        ("Ardesia", "#475569"),
    ];

    static readonly Color White = Colors.White;
    static readonly Color Black = Colors.Black;
    static readonly Color DarkText = Color.FromRgb(0x1E, 0x29, 0x3B);

    public static void Apply(string? hex)
    {
        var accent = TryParse(hex) ?? TryParse(DefaultAccent)!.Value;
        var resources = Application.Current.Resources;

        resources["AccentBrush"] = Brush(accent);
        resources["AccentHoverBrush"] = Brush(Mix(accent, Black, 0.12));
        resources["AccentPressedBrush"] = Brush(Mix(accent, Black, 0.24));
        resources["AccentSoftBrush"] = Brush(Mix(accent, White, 0.90));
        resources["AccentLightBrush"] = Brush(Mix(accent, White, 0.55));
        resources["AccentTextBrush"] = Brush(ReadableOnWhite(accent));
        var foreground = Contrast(accent, White) >= 3.0 ? White : DarkText;
        resources["AccentForegroundBrush"] = Brush(foreground);

        // Logo (barra laterale, impostazioni) e icona delle finestre seguono il colore scelto.
        var logo = BuildLogo(accent, foreground);
        var image = new DrawingImage(logo);
        image.Freeze();
        resources["AppLogo"] = image;
        resources["AppIconBitmap"] = RenderIcon(logo, 64);
    }

    /// <summary>Manubrio su quadrato arrotondato con sfumatura del colore principale (stesso disegno di Assets\app.ico).</summary>
    static DrawingGroup BuildLogo(Color accent, Color foreground)
    {
        const double margin = 12, radius = 56;
        var square = new RectangleGeometry(new Rect(margin, margin, 256 - 2 * margin, 256 - 2 * margin), radius, radius);
        var background = new LinearGradientBrush(Mix(accent, White, 0.25), Mix(accent, Black, 0.25), new Point(0, 0), new Point(1, 1));
        var gloss = new LinearGradientBrush(Color.FromArgb(40, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), new Point(0.5, 0), new Point(0.5, 0.6));

        var dumbbell = new GeometryGroup { Transform = new RotateTransform(-35, 128, 128) };
        void Part(double x, double y, double w, double h, double r) => dumbbell.Children.Add(new RectangleGeometry(new Rect(x, y, w, h), r, r));
        Part(70, 119, 116, 18, 9);       // barra
        Part(52, 72, 26, 112, 9);        // disco interno sinistro
        Part(178, 72, 26, 112, 9);       // disco interno destro
        Part(27, 91, 20, 74, 8);         // disco esterno sinistro
        Part(209, 91, 20, 74, 8);        // disco esterno destro

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(background, null, square));
        group.Children.Add(new GeometryDrawing(gloss, null, square));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(foreground), null, dumbbell));
        group.Freeze();
        return group;
    }

    static ImageSource RenderIcon(Drawing drawing, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            dc.DrawDrawing(drawing);
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static Color? TryParse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var text = hex.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length != 7) return null;
        try
        {
            return (Color)ColorConverter.ConvertFromString(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Scurisce il colore finché il testo su sfondo bianco ha contrasto sufficiente (WCAG 4.5:1).</summary>
    static Color ReadableOnWhite(Color accent)
    {
        for (double t = 0.15; t < 0.8; t += 0.05)
        {
            var candidate = Mix(accent, Black, t);
            if (Contrast(candidate, White) >= 4.5) return candidate;
        }
        return Mix(accent, Black, 0.8);
    }

    static SolidColorBrush Brush(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
