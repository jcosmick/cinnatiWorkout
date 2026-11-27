using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CinnatiWorkout.Ods;

namespace CinnatiWorkout.Printing;

/// <summary>
/// Legge il primo foglio di un .ods per la stampa: celle, stili (con ereditarietà), celle unite,
/// larghezze/altezze e impostazioni di pagina dello stile di pagina del foglio.
/// </summary>
public static class OdsSheetReader
{
    static readonly XNamespace Table = OdsDocument.TableNs;
    static readonly XNamespace Office = OdsDocument.OfficeNs;
    static readonly XNamespace Style = OdsDocument.StyleNs;
    static readonly XNamespace Fo = OdsDocument.FoNs;
    static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";

    // Limiti alle ripetizioni enormi che ODS usa per "tutto il resto del foglio".
    const int MaxColumns = 256;
    const int MaxRepeatedRows = 200;

    public static List<Sheet> Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var content = Load(zip, "content.xml") ?? throw new InvalidDataException("Il file non è un foglio di calcolo ODS valido.");
        var styles = Load(zip, "styles.xml");
        var ctx = new StyleContext(content, styles);

        var table = content.Descendants(Table + "table").FirstOrDefault()
            ?? throw new InvalidDataException("Il file non contiene fogli.");
        return [ReadTable(table, ctx)];
    }

    /// <summary>Primo foglio da un content.xml già in memoria (con lo styles.xml del file, se c'è).</summary>
    public static Sheet ReadFirst(XDocument content, XDocument? styles)
    {
        var table = content.Descendants(Table + "table").FirstOrDefault()
            ?? throw new InvalidDataException("Il file non contiene fogli.");
        return ReadTable(table, new StyleContext(content, styles));
    }

    static XDocument? Load(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry == null) return null;
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    static Sheet ReadTable(XElement table, StyleContext ctx)
    {
        var sheet = new Sheet { Name = (string?)table.Attribute(Table + "name") ?? "Foglio1" };
        sheet.DefaultFormat = ctx.CellFormat(null);

        // --- colonne
        var widths = new List<double>();
        var columnStyles = new List<string?>();
        foreach (var col in table.Descendants(Table + "table-column"))
        {
            int repeat = Math.Min((int?)col.Attribute(Table + "number-columns-repeated") ?? 1, MaxColumns - widths.Count);
            var props = ctx.Find((string?)col.Attribute(Table + "style-name"))?.Element(Style + "table-column-properties");
            double width = Units.ParseLength((string?)props?.Attribute(Style + "column-width")) ?? Units.FromCm(2.258);
            if ((string?)col.Attribute(Table + "visibility") is "collapse" or "filter") width = 0;
            var cellStyle = (string?)col.Attribute(Table + "default-cell-style-name");
            bool breakBefore = (string?)props?.Attribute(Fo + "break-before") == "page";
            for (int i = 0; i < repeat; i++)
            {
                if (breakBefore && i == 0 && widths.Count > 0) sheet.ColumnBreaks.Add(widths.Count);
                widths.Add(width);
                columnStyles.Add(cellStyle);
            }
            if (widths.Count >= MaxColumns) break;
        }
        for (int c = 0; c < columnStyles.Count; c++)
            if (columnStyles[c] != null) sheet.ColumnFormats[c] = ctx.CellFormat(columnStyles[c]);

        // --- righe e celle
        var heights = new List<double>();
        var optimalRows = new HashSet<int>();
        int lastUsedRow = -1, lastUsedCol = -1;
        var rows = table.Descendants(Table + "table-row").ToList();

        // Le ripetizioni finali (spesso oltre un milione di righe vuote) non servono.
        foreach (var row in rows)
        {
            int repeat = (int?)row.Attribute(Table + "number-rows-repeated") ?? 1;
            bool hasContent = row.Descendants(OdsDocument.TextNs + "p").Any();
            if (repeat > MaxRepeatedRows && !hasContent) repeat = 1;

            var props = ctx.Find((string?)row.Attribute(Table + "style-name"))?.Element(Style + "table-row-properties");
            double height = Units.ParseLength((string?)props?.Attribute(Style + "row-height")) ?? Units.FromCm(0.452);
            if ((string?)row.Attribute(Table + "visibility") is "collapse" or "filter") height = 0;
            bool optimal = (string?)props?.Attribute(Style + "use-optimal-row-height") == "true";
            bool breakBefore = (string?)props?.Attribute(Fo + "break-before") == "page";
            var rowStyle = (string?)row.Attribute(Table + "default-cell-style-name");

            for (int k = 0; k < repeat; k++)
            {
                int r = heights.Count;
                if (breakBefore && k == 0 && r > 0) sheet.RowBreaks.Add(r);
                heights.Add(height);
                if (optimal) optimalRows.Add(r);

                int c = 0;
                foreach (var cell in row.Elements().Where(e => e.Name == Table + "table-cell" || e.Name == Table + "covered-table-cell"))
                {
                    int cellRepeat = (int?)cell.Attribute(Table + "number-columns-repeated") ?? 1;
                    var styleName = (string?)cell.Attribute(Table + "style-name")
                                    ?? rowStyle
                                    ?? (c < columnStyles.Count ? columnStyles[c] : null);
                    var format = ctx.CellFormat(styleName);
                    var text = OdsDocument.ReadText(cell);
                    bool covered = cell.Name == Table + "covered-table-cell";
                    var valueType = (string?)cell.Attribute(Office + "value-type");

                    if (cellRepeat > MaxColumns && text.Length == 0) cellRepeat = Math.Max(0, MaxColumns - c);
                    for (int j = 0; j < cellRepeat && c < MaxColumns; j++, c++)
                    {
                        if (!covered && text.Length == 0 && !format.IsVisible && styleName == null) continue;
                        sheet.SetCell(r, c, new SheetCell
                        {
                            Text = covered ? "" : text,
                            IsNumber = valueType is "float" or "percentage" or "currency" or "date" or "time",
                            IsBoolean = valueType == "boolean",
                            Format = format,
                            StyleName = styleName,
                        });
                        if (!covered)
                        {
                            int colSpan = (int?)cell.Attribute(Table + "number-columns-spanned") ?? 1;
                            int rowSpan = (int?)cell.Attribute(Table + "number-rows-spanned") ?? 1;
                            if (colSpan > 1 || rowSpan > 1) sheet.AddMerge(new CellRange(r, c, r + rowSpan - 1, c + colSpan - 1));
                        }
                        if (text.Length > 0 || format.IsVisible)
                        {
                            lastUsedRow = Math.Max(lastUsedRow, r);
                            lastUsedCol = Math.Max(lastUsedCol, c);
                        }
                    }
                }
            }
        }

        sheet.DefaultColumnWidth = Units.FromCm(2.258);
        sheet.DefaultRowHeight = Units.FromCm(0.452);
        sheet.SetColumnWidths(widths);

        // Le celle unite di un .ods portano lo stile sulla prima cella: le celle coperte
        // ne ereditano bordi e sfondo, così il bordo esterno dell'unione si disegna per intero.
        foreach (var merge in sheet.Merges)
        {
            var anchor = sheet.GetCell(merge.Row1, merge.Col1);
            if (anchor == null) continue;
            for (int r = merge.Row1; r <= merge.Row2; r++)
                for (int c = merge.Col1; c <= merge.Col2; c++)
                    if (r != merge.Row1 || c != merge.Col1)
                        sheet.SetCell(r, c, new SheetCell { Format = anchor.Format, StyleName = sheet.GetCell(r, c)?.StyleName });
            lastUsedRow = Math.Max(lastUsedRow, merge.Row2);
            lastUsedCol = Math.Max(lastUsedCol, merge.Col2);
        }

        // Le righe si allungano quando il testo (es. una nota andata a capo) non ci sta: per le righe ad altezza
        // ottimale lo fa anche LibreOffice; per quelle ad altezza fissa evita che sulla carta il testo venga tagliato.
        sheet.SetRowHeights(heights);
        for (int r = 0; r < heights.Count; r++)
        {
            if (heights[r] <= 0) continue;
            double needed = SheetRenderer.MeasureRowHeight(sheet, r, wrappedOnly: !optimalRows.Contains(r));
            if (needed > heights[r]) heights[r] = needed;
        }
        sheet.SetRowHeights(heights);

        sheet.UsedArea = new CellRange(0, 0, Math.Max(lastUsedRow, 0), Math.Max(lastUsedCol, 0));
        sheet.PrintArea = ParsePrintRange((string?)table.Attribute(Table + "print-ranges")) ?? sheet.UsedArea;
        ReadPageSetup(sheet.Page, ctx, (string?)table.Attribute(Table + "style-name"));
        return sheet;
    }

    /// <summary>
    /// "Foglio1.A1:L60" (eventualmente con $, nome del foglio tra apici e più aree separate da spazio: si usa la prima).
    /// </summary>
    internal static CellRange? ParsePrintRange(string? ranges)
    {
        if (string.IsNullOrWhiteSpace(ranges)) return null;
        // Gli spazi dentro un nome di foglio tra apici ('Foglio 1') non separano le aree.
        var first = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char ch in ranges.Trim())
        {
            if (ch == '\'') quoted = !quoted;
            else if (ch == ' ' && !quoted) break;
            first.Append(ch);
        }
        var parts = Regex.Split(first.ToString(), ":(?=(?:[^']*'[^']*')*[^']*$)");
        var a = ParseAddress(parts[0].Replace("$", ""));
        var b = parts.Length > 1 ? ParseAddress(parts[1].Replace("$", "")) : a;
        if (a == null || b == null) return null;
        return new CellRange(Math.Min(a.Value.Row, b.Value.Row), Math.Min(a.Value.Col, b.Value.Col),
            Math.Max(a.Value.Row, b.Value.Row), Math.Max(a.Value.Col, b.Value.Col));
    }

    /// <summary>"Foglio1.B7" → come <see cref="CellRange.ParseAddress"/>, ignorando il nome del foglio.</summary>
    static (int Row, int Col)? ParseAddress(string text)
    {
        int dot = text.LastIndexOf('.');
        return CellRange.ParseAddress(dot >= 0 ? text[(dot + 1)..] : text);
    }

    static void ReadPageSetup(PageSetup page, StyleContext ctx, string? tableStyle)
    {
        var master = (string?)ctx.Find(tableStyle)?.Attribute(Style + "master-page-name") ?? "Default";
        var masterPage = ctx.MasterPage(master) ?? ctx.MasterPage("Default");
        var layout = ctx.PageLayout((string?)masterPage?.Attribute(Style + "page-layout-name"));
        var props = layout?.Element(Style + "page-layout-properties");
        if (props == null) return;

        double width = Units.ParseLength((string?)props.Attribute(Fo + "page-width")) ?? Units.A4.Width;
        double height = Units.ParseLength((string?)props.Attribute(Fo + "page-height")) ?? Units.A4.Height;
        bool landscape = (string?)props.Attribute(Style + "print-orientation") == "landscape";
        if (landscape != width > height) (width, height) = (height, width);
        page.Paper = new Size(width, height);

        double M(string side) => Units.ParseLength((string?)props.Attribute(Fo + $"margin-{side}"))
                                 ?? Units.ParseLength((string?)props.Attribute(Fo + "margin"))
                                 ?? Units.FromCm(2);
        page.Margins = new Thickness(M("left"), M("top"), M("right"), M("bottom"));

        var centering = (string?)props.Attribute(Style + "table-centering");
        page.CenterHorizontally = centering is "horizontal" or "both";
        page.DownThenOver = (string?)props.Attribute(Style + "print-page-order") != "ltr";

        if ((int?)props.Attribute(Style + "scale-to-pages") is { } pages and > 0)
        {
            // LibreOffice riduce finché tutto sta in N pagine: con N = 1 è "adatta a una pagina".
            page.FitWide = 1;
            page.FitTall = pages;
        }
        else if ((int?)props.Attribute(Style + "scale-to-X") is not null || (int?)props.Attribute(Style + "scale-to-Y") is not null)
        {
            page.FitWide = (int?)props.Attribute(Style + "scale-to-X") ?? 0;
            page.FitTall = (int?)props.Attribute(Style + "scale-to-Y") ?? 0;
        }
        else if (((string?)props.Attribute(Style + "scale-to"))?.TrimEnd('%') is { } percent
                 && double.TryParse(percent, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && scale > 0)
        {
            page.Scale = scale / 100;
        }
    }

    /// <summary>Risolve gli stili di cella ODS seguendo la catena parent-style-name fino allo stile predefinito.</summary>
    sealed class StyleContext
    {
        readonly Dictionary<string, XElement> _styles = [];
        readonly Dictionary<string, XElement> _pageLayouts = [];
        readonly Dictionary<string, XElement> _masterPages = [];
        readonly Dictionary<string, string> _fontFaces = [];
        readonly Dictionary<string, CellFormat> _cache = [];
        readonly XElement? _defaultCellStyle;

        public StyleContext(XDocument content, XDocument? styles)
        {
            foreach (var doc in new[] { styles, content })
            {
                if (doc == null) continue;
                foreach (var s in doc.Descendants(Style + "style"))
                    if ((string?)s.Attribute(Style + "name") is { } name) _styles[Key(name, (string?)s.Attribute(Style + "family"))] = s;
                foreach (var l in doc.Descendants(Style + "page-layout"))
                    if ((string?)l.Attribute(Style + "name") is { } name) _pageLayouts[name] = l;
                foreach (var m in doc.Descendants(Style + "master-page"))
                    if ((string?)m.Attribute(Style + "name") is { } name) _masterPages[name] = m;
                foreach (var f in doc.Descendants(Style + "font-face"))
                    if ((string?)f.Attribute(Style + "name") is { } name)
                        _fontFaces[name] = ((string?)f.Attribute(Svg + "font-family") ?? name).Trim('\'', '"');
            }
            _defaultCellStyle = styles?.Descendants(Style + "default-style")
                .FirstOrDefault(s => (string?)s.Attribute(Style + "family") == "table-cell");
        }

        static string Key(string name, string? family) => $"{family}|{name}";

        /// <summary>Stile per nome, qualunque famiglia (riga, colonna, tabella o cella).</summary>
        public XElement? Find(string? name)
        {
            if (name == null) return null;
            foreach (var family in new[] { "table-cell", "table-row", "table-column", "table" })
                if (_styles.TryGetValue(Key(name, family), out var s)) return s;
            return null;
        }

        public XElement? PageLayout(string? name) => name != null ? _pageLayouts.GetValueOrDefault(name) : null;
        public XElement? MasterPage(string? name) => name != null ? _masterPages.GetValueOrDefault(name) : null;

        public CellFormat CellFormat(string? styleName)
        {
            var key = styleName ?? "";
            if (_cache.TryGetValue(key, out var cached)) return cached;

            // Catena dallo stile della cella fino al predefinito: vince il primo che definisce l'attributo.
            var chain = new List<XElement>();
            var current = styleName != null ? _styles.GetValueOrDefault(Key(styleName, "table-cell")) : _styles.GetValueOrDefault(Key("Default", "table-cell"));
            for (int guard = 0; current != null && guard < 20; guard++)
            {
                chain.Add(current);
                var parent = (string?)current.Attribute(Style + "parent-style-name");
                current = parent != null ? _styles.GetValueOrDefault(Key(parent, "table-cell")) : null;
                if (current == null && parent == null && chain[^1] != _styles.GetValueOrDefault(Key("Default", "table-cell")))
                    current = _styles.GetValueOrDefault(Key("Default", "table-cell"));
            }
            if (_defaultCellStyle != null) chain.Add(_defaultCellStyle);

            string? Get(string element, XName attribute)
            {
                foreach (var s in chain)
                    if ((string?)s.Element(Style + element)?.Attribute(attribute) is { } v) return v;
                return null;
            }

            string? Cell(XName a) => Get("table-cell-properties", a);
            string? Para(XName a) => Get("paragraph-properties", a);
            string? Text(XName a) => Get("text-properties", a);

            var fontName = Text(Style + "font-name");
            var family = fontName != null && _fontFaces.TryGetValue(fontName, out var ff) ? ff : Text(Fo + "font-family") ?? fontName ?? "Arial";

            var border = Cell(Fo + "border");
            BorderLine? Side(string side) => ParseBorder(Cell(Fo + $"border-{side}") ?? border);

            var align = Para(Fo + "text-align");
            bool alignFromValue = Cell(Style + "text-align-source") == "value-type";
            var hAlign = alignFromValue ? HAlign.General : align switch
            {
                "start" or "left" => HAlign.Left,
                "center" => HAlign.Center,
                "end" or "right" => HAlign.Right,
                "justify" => HAlign.Justify,
                _ => HAlign.General,
            };

            var underline = Text(Style + "text-underline-style");
            var strike = Text(Style + "text-line-through-style");
            var padding = Units.ParseLength(Cell(Fo + "padding"));

            var format = new CellFormat
            {
                FontFamily = Units.MapFont(family),
                FontSize = Units.ParseLength(Text(Fo + "font-size")) ?? Units.FromPt(10),
                Bold = Text(Fo + "font-weight") is "bold" or "600" or "700" or "800" or "900",
                Italic = Text(Fo + "font-style") is "italic" or "oblique",
                Underline = underline != null && underline != "none",
                Strike = strike != null && strike != "none",
                Foreground = Units.ParseHexColor(Text(Fo + "color")) ?? Colors.Black,
                Background = Cell(Fo + "background-color") is { } bg && bg != "transparent" ? Units.ParseHexColor(bg) : null,
                Left = Side("left"),
                Top = Side("top"),
                Right = Side("right"),
                Bottom = Side("bottom"),
                HAlign = hAlign,
                VAlign = Cell(Style + "vertical-align") switch
                {
                    "top" => VAlign.Top,
                    "middle" => VAlign.Center,
                    _ => VAlign.Bottom,
                },
                Wrap = Cell(Fo + "wrap-option") == "wrap",
                ShrinkToFit = Cell(Style + "shrink-to-fit") == "true",
                Indent = Units.ParseLength(Para(Fo + "margin-left")) is { } m && m > 0 ? m : 0,
                PaddingX = padding ?? Units.FromCm(0.035) + 1,
                PaddingY = padding ?? 1,
            };
            _cache[key] = format;
            return format;
        }

        /// <summary>"0.74pt solid #000000", "none", "2.01pt double #808080".</summary>
        static BorderLine? ParseBorder(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Trim() is "none" or "hidden") return null;
            double width = 1;
            var kind = LineKind.Solid;
            var color = Colors.Black;
            foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token is "none" or "hidden") return null;
                if (token.StartsWith('#')) color = Units.ParseHexColor(token) ?? color;
                else if (Units.ParseLength(token) is { } w && char.IsDigit(token[0])) width = w;
                else kind = token switch
                {
                    "double" => LineKind.Double,
                    "dashed" or "fine-dashed" or "dash-dot" or "dash-dot-dot" => LineKind.Dashed,
                    "dotted" => LineKind.Dotted,
                    _ => kind,
                };
            }
            return new BorderLine(Math.Max(width, 0.5), color, kind);
        }
    }
}
