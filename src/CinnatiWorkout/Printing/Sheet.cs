using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CinnatiWorkout.Printing;

// Modello neutro di un foglio da stampare, comune a .ods e .xlsx.
// Tutte le misure sono in unità WPF (1/96 di pollice) e nelle coordinate del foglio
// (la colonna 0 / riga 0 parte da x = 0 / y = 0).

public enum HAlign { General, Left, Center, Right, Justify }

public enum VAlign { Top, Center, Bottom }

public enum LineKind { Solid, Dashed, Dotted, Double }

public sealed record BorderLine(double Width, Color Color, LineKind Kind = LineKind.Solid);

public sealed record CellFormat
{
    public static readonly CellFormat Empty = new();

    public string FontFamily { get; init; } = "Calibri";
    public double FontSize { get; init; } = Units.FromPt(11);
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Strike { get; init; }
    public Color Foreground { get; init; } = Colors.Black;
    public Color? Background { get; init; }
    public BorderLine? Left { get; init; }
    public BorderLine? Top { get; init; }
    public BorderLine? Right { get; init; }
    public BorderLine? Bottom { get; init; }
    public HAlign HAlign { get; init; }
    public VAlign VAlign { get; init; } = VAlign.Bottom;
    public bool Wrap { get; init; }
    public bool ShrinkToFit { get; init; }
    /// <summary>Rientro aggiuntivo del testo dal bordo sinistro (o destro se allineato a destra).</summary>
    public double Indent { get; init; }
    /// <summary>Spazio tra bordo della cella e testo, in orizzontale e in verticale.</summary>
    public double PaddingX { get; init; } = 2.5;
    public double PaddingY { get; init; } = 1.5;

    /// <summary>true se la cella, anche vuota, lascia un segno sulla carta (sfondo o bordi).</summary>
    public bool IsVisible =>
        (Background is { } b && b != Colors.White && b.A > 0) || Left != null || Top != null || Right != null || Bottom != null;
}

public sealed class SheetCell
{
    public string Text { get; init; } = "";
    public bool IsNumber { get; init; }
    public bool IsBoolean { get; init; }
    public CellFormat Format { get; init; } = CellFormat.Empty;
}

/// <summary>Area rettangolare di celle (estremi inclusi): celle unite, area di stampa, pagina.</summary>
public readonly record struct CellRange(int Row1, int Col1, int Row2, int Col2)
{
    public bool Contains(int row, int col) => row >= Row1 && row <= Row2 && col >= Col1 && col <= Col2;

    /// <summary>Indirizzo "B7" → riga e colonna a base 0, o null se non valido.</summary>
    public static (int Row, int Col)? ParseAddress(string text)
    {
        int i = 0, col = 0;
        while (i < text.Length && char.IsLetter(text[i])) col = col * 26 + (char.ToUpperInvariant(text[i++]) - 'A' + 1);
        return i > 0 && int.TryParse(text[i..], out var row) ? (row - 1, col - 1) : null;
    }

    /// <summary>"A1:L60" (o una sola cella) → area normalizzata; <paramref name="address"/> interpreta ogni estremo.</summary>
    public static CellRange? Parse(string? text, Func<string, (int Row, int Col)?>? address = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        address ??= ParseAddress;
        var parts = text.Split(':');
        var a = address(parts[0]);
        var b = parts.Length > 1 ? address(parts[1]) : a;
        if (a == null || b == null) return null;
        return new CellRange(Math.Min(a.Value.Row, b.Value.Row), Math.Min(a.Value.Col, b.Value.Col),
            Math.Max(a.Value.Row, b.Value.Row), Math.Max(a.Value.Col, b.Value.Col));
    }
}

public sealed record SheetPicture(Rect Bounds, BitmapSource Image);

public sealed class PageSetup
{
    /// <summary>Dimensioni del foglio di carta, già orientate (orizzontale = più largo che alto).</summary>
    public Size Paper { get; set; } = Units.A4;
    public Thickness Margins { get; set; } = new(Units.FromCm(2));
    public bool CenterHorizontally { get; set; }
    /// <summary>
    /// Scala fissa del file (1 = 100%). In stampa si usa solo se l'area di stampa non ha larghezza:
    /// altrimenti il contenuto viene sempre scalato per riempire tutta la larghezza utile.
    /// </summary>
    public double Scale { get; set; } = 1;
    /// <summary>Pagine in larghezza da riempire con il contenuto (0 o 1 = una pagina).</summary>
    public int FitWide { get; set; }
    /// <summary>Pagine in altezza indicate dal file; letto ma non usato in stampa (l'altezza segue la scala della larghezza).</summary>
    public int FitTall { get; set; }
    /// <summary>Ordine delle pagine: prima verso il basso e poi a destra (Excel, LibreOffice predefinito).</summary>
    public bool DownThenOver { get; set; } = true;

    public bool IsLandscape => Paper.Width > Paper.Height;
}

public sealed class Sheet
{
    readonly Dictionary<(int Row, int Col), SheetCell> _cells = [];
    readonly Dictionary<(int Row, int Col), int> _mergeIndex = [];
    readonly List<CellRange> _merges = [];
    double[] _colX = [0], _rowY = [0];

    public required string Name { get; init; }
    public PageSetup Page { get; } = new();
    public List<SheetPicture> Pictures { get; } = [];
    /// <summary>Logo da stampare in alto a sinistra sulla prima pagina del foglio (impostazioni; solo schede allenamento).</summary>
    public BitmapSource? Logo { get; set; }
    /// <summary>Interruzioni di pagina manuali: indice della prima riga/colonna della nuova pagina.</summary>
    public HashSet<int> RowBreaks { get; } = [];
    public HashSet<int> ColumnBreaks { get; } = [];

    public double DefaultColumnWidth { get; set; } = 64;
    public double DefaultRowHeight { get; set; } = 20;
    public CellFormat DefaultFormat { get; set; } = CellFormat.Empty;
    public Dictionary<int, CellFormat> ColumnFormats { get; } = [];
    public Dictionary<int, CellFormat> RowFormats { get; } = [];

    /// <summary>Area di stampa (estremi inclusi).</summary>
    public CellRange PrintArea { get; set; }

    public IReadOnlyList<CellRange> Merges => _merges;
    public IEnumerable<KeyValuePair<(int Row, int Col), SheetCell>> Cells => _cells;

    public void SetColumnWidths(IReadOnlyList<double> widths) => _colX = Prefix(widths);
    public void SetRowHeights(IReadOnlyList<double> heights) => _rowY = Prefix(heights);

    public double ColumnX(int col) => Position(_colX, DefaultColumnWidth, col);
    public double RowY(int row) => Position(_rowY, DefaultRowHeight, row);
    public double ColumnWidth(int col) => ColumnX(col + 1) - ColumnX(col);
    public double RowHeight(int row) => RowY(row + 1) - RowY(row);

    public Rect CellRect(int row, int col) => CellRect(new CellRange(row, col, row, col));
    public Rect CellRect(CellRange r) =>
        new(ColumnX(r.Col1), RowY(r.Row1), ColumnX(r.Col2 + 1) - ColumnX(r.Col1), RowY(r.Row2 + 1) - RowY(r.Row1));

    public void SetCell(int row, int col, SheetCell cell) => _cells[(row, col)] = cell;

    public SheetCell? GetCell(int row, int col) => _cells.GetValueOrDefault((row, col));

    /// <summary>Formato della cella, anche se non esiste: quello di riga o colonna, altrimenti il predefinito.</summary>
    public CellFormat FormatAt(int row, int col) =>
        _cells.TryGetValue((row, col), out var cell) ? cell.Format
        : RowFormats.TryGetValue(row, out var rf) ? rf
        : ColumnFormats.TryGetValue(col, out var cf) ? cf
        : DefaultFormat;

    public void AddMerge(CellRange range)
    {
        if (range.Row1 == range.Row2 && range.Col1 == range.Col2) return;
        int index = _merges.Count;
        _merges.Add(range);
        for (int r = range.Row1; r <= range.Row2; r++)
            for (int c = range.Col1; c <= range.Col2; c++)
                _mergeIndex[(r, c)] = index;
    }

    public CellRange? MergeAt(int row, int col) => _mergeIndex.TryGetValue((row, col), out var i) ? _merges[i] : null;

    static double[] Prefix(IReadOnlyList<double> sizes)
    {
        var result = new double[sizes.Count + 1];
        for (int i = 0; i < sizes.Count; i++) result[i + 1] = result[i] + Math.Max(0, sizes[i]);
        return result;
    }

    static double Position(double[] prefix, double fallback, int index)
    {
        if (index <= 0) return 0;
        int known = prefix.Length - 1;
        return index <= known ? prefix[index] : prefix[known] + (index - known) * fallback;
    }
}

public static class Units
{
    public static readonly Size A4 = new(FromMm(210), FromMm(297));

    public static double FromPt(double pt) => pt * 96 / 72;
    public static double FromInch(double inch) => inch * 96;
    public static double FromCm(double cm) => cm * 96 / 2.54;
    public static double FromMm(double mm) => mm * 96 / 25.4;
    public static double FromEmu(double emu) => emu * 96 / 914400;

    /// <summary>Lunghezza ODF/CSS come "0.74pt", "3.5cm", "12mm", "1in"; null se non riconosciuta.</summary>
    public static double? ParseLength(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        int i = 0;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] is '.' or '-' or '+')) i++;
        if (!double.TryParse(text[..i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
            return null;
        return text[i..].Trim().ToLowerInvariant() switch
        {
            "pt" => FromPt(value),
            "cm" => FromCm(value),
            "mm" => FromMm(value),
            "in" or "inch" => FromInch(value),
            "px" or "" => value,
            "pc" => FromPt(value * 12),
            _ => null,
        };
    }

    public static Color? ParseHexColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().TrimStart('#');
        if (text.Length == 8) text = text[2..]; // ARGB di Excel: l'alfa è sempre FF o ignorato
        if (text.Length != 6 || !int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var rgb)) return null;
        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// <summary>Nomi dei caratteri metrici compatibili di LibreOffice → caratteri installati su Windows.</summary>
    public static string MapFont(string name) => name.Trim().Trim('\'', '"') switch
    {
        "Liberation Sans" or "Arimo" => "Arial",
        "Liberation Serif" or "Tinos" => "Times New Roman",
        "Liberation Mono" or "Cousine" => "Courier New",
        "Carlito" => "Calibri",
        "Caladea" => "Cambria",
        var n => n,
    };
}
