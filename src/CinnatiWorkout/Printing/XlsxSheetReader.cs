using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace CinnatiWorkout.Printing;

/// <summary>
/// Legge i fogli di un .xlsx per la stampa direttamente dall'XML: valori memorizzati (anche delle formule),
/// stili, celle unite, area e impostazioni di stampa, immagini (anche dentro gruppi di forme).
/// </summary>
public static class XlsxSheetReader
{
    static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>Tutti i fogli visibili, nell'ordine della cartella di lavoro.</summary>
    public static List<Sheet> Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var package = new Package(zip);
        var workbook = package.Xml("xl/workbook.xml") ?? throw new InvalidDataException("Il file non è una cartella di lavoro Excel valida.");
        var workbookRels = package.Relationships("xl/workbook.xml");
        var styles = new Styles(package);
        var shared = SharedStrings(package);

        var sheetElements = workbook.Root!.Element(Main + "sheets")?.Elements(Main + "sheet").ToList() ?? [];
        var printAreas = workbook.Root.Element(Main + "definedNames")?.Elements(Main + "definedName")
            .Where(d => (string?)d.Attribute("name") == "_xlnm.Print_Area" && d.Attribute("localSheetId") != null)
            .ToDictionary(d => (int)d.Attribute("localSheetId")!, d => d.Value) ?? [];

        var result = new List<Sheet>();
        for (int i = 0; i < sheetElements.Count; i++)
        {
            var element = sheetElements[i];
            if ((string?)element.Attribute("state") is "hidden" or "veryHidden") continue;
            var id = (string?)element.Attribute(Rel + "id");
            if (id == null || !workbookRels.TryGetValue(id, out var part)) continue;
            var xml = package.Xml(part);
            if (xml == null) continue;
            result.Add(ReadSheet((string?)element.Attribute("name") ?? $"Foglio{i + 1}", part, xml, package, styles, shared,
                printAreas.GetValueOrDefault(i)));
        }
        return result;
    }

    static Sheet ReadSheet(string name, string part, XDocument xml, Package package, Styles styles, List<string> shared, string? printArea)
    {
        var root = xml.Root!;
        var sheet = new Sheet { Name = name };
        sheet.DefaultFormat = styles.Format(0);
        bool showZeros = (string?)root.Element(Main + "sheetViews")?.Element(Main + "sheetView")?.Attribute("showZeros") is not ("0" or "false");

        // --- dimensioni
        double mdw = styles.MaxDigitWidth;
        double ColumnPixels(double width) => Math.Truncate((256 * width + Math.Truncate(128 / mdw)) / 256 * mdw);

        var formatPr = root.Element(Main + "sheetFormatPr");
        double defaultWidth = (double?)formatPr?.Attribute("defaultColWidth") is { } dcw
            ? ColumnPixels(dcw)
            : ColumnPixels(Math.Truncate(((double?)formatPr?.Attribute("baseColWidth") ?? 8) * mdw + 5) / mdw);
        double defaultHeight = Units.FromPt((double?)formatPr?.Attribute("defaultRowHeight") ?? 15);
        sheet.DefaultColumnWidth = defaultWidth;
        sheet.DefaultRowHeight = defaultHeight;

        var widths = new List<double>();
        foreach (var col in root.Element(Main + "cols")?.Elements(Main + "col") ?? [])
        {
            int min = (int?)col.Attribute("min") ?? 1, max = Math.Min((int?)col.Attribute("max") ?? min, 1024);
            double width = (string?)col.Attribute("hidden") is "1" or "true" ? 0
                : (double?)col.Attribute("width") is { } w ? ColumnPixels(w) : defaultWidth;
            var style = (int?)col.Attribute("style");
            while (widths.Count < max) widths.Add(defaultWidth);
            for (int c = min - 1; c < max; c++)
            {
                widths[c] = width;
                if (style is { } s && s != 0) sheet.ColumnFormats[c] = styles.Format(s);
            }
        }

        // --- celle
        var heights = new List<double>();
        foreach (var row in root.Element(Main + "sheetData")?.Elements(Main + "row") ?? [])
        {
            int r = ((int?)row.Attribute("r") ?? heights.Count + 1) - 1;
            while (heights.Count <= r) heights.Add(defaultHeight);
            if ((string?)row.Attribute("hidden") is "1" or "true") heights[r] = 0;
            else if ((double?)row.Attribute("ht") is { } ht) heights[r] = Units.FromPt(ht);
            if ((string?)row.Attribute("customFormat") is "1" or "true" && (int?)row.Attribute("s") is { } rs)
                sheet.RowFormats[r] = styles.Format(rs);

            int nextCol = 0;
            foreach (var c in row.Elements(Main + "c"))
            {
                var address = (string?)c.Attribute("r");
                var (cr, cc) = address != null ? CellRange.ParseAddress(address) ?? (r, nextCol) : (r, nextCol);
                nextCol = cc + 1;
                int styleIndex = (int?)c.Attribute("s") ?? 0;
                var format = styles.Format(styleIndex);
                var (text, isNumber, isBool) = CellText(c, shared, styles.NumberFormat(styleIndex), showZeros);
                sheet.SetCell(cr, cc, new SheetCell { Text = text, IsNumber = isNumber, IsBoolean = isBool, Format = format });
            }
        }

        foreach (var merge in root.Element(Main + "mergeCells")?.Elements(Main + "mergeCell") ?? [])
            if (CellRange.Parse((string?)merge.Attribute("ref")) is { } range) sheet.AddMerge(range);

        sheet.SetColumnWidths(widths);
        sheet.SetRowHeights(heights);

        foreach (var brk in root.Element(Main + "rowBreaks")?.Elements(Main + "brk") ?? [])
            if ((int?)brk.Attribute("id") is { } id && id > 0) sheet.RowBreaks.Add(id);
        foreach (var brk in root.Element(Main + "colBreaks")?.Elements(Main + "brk") ?? [])
            if ((int?)brk.Attribute("id") is { } id && id > 0) sheet.ColumnBreaks.Add(id);

        ReadPictures(sheet, part, root, package);
        ReadPageSetup(sheet.Page, root);

        // Area di stampa: quella definita, altrimenti l'intervallo usato (celle con testo o segni visibili e immagini).
        var area = printArea != null ? CellRange.Parse(printArea.Split(',')[0].Split('!').Last().Replace("$", "").Replace("'", "")) : null;
        sheet.PrintArea = area ?? UsedRange(sheet);
        return sheet;
    }

    static CellRange UsedRange(Sheet sheet)
    {
        int r1 = int.MaxValue, c1 = int.MaxValue, r2 = 0, c2 = 0;
        void Include(int r, int c) { r1 = Math.Min(r1, r); c1 = Math.Min(c1, c); r2 = Math.Max(r2, r); c2 = Math.Max(c2, c); }
        foreach (var ((r, c), cell) in sheet.Cells)
            if (cell.Text.Length > 0 || cell.Format.IsVisible) Include(r, c);
        foreach (var m in sheet.Merges) { Include(m.Row1, m.Col1); Include(m.Row2, m.Col2); }
        foreach (var p in sheet.Pictures)
        {
            int c = 0, r = 0;
            while (sheet.ColumnX(c + 1) < p.Bounds.Right && c < 1024) c++;
            while (sheet.RowY(r + 1) < p.Bounds.Bottom && r < 100000) r++;
            Include(r, c);
            int cs = 0, rs = 0;
            while (sheet.ColumnX(cs + 1) <= p.Bounds.Left && cs < 1024) cs++;
            while (sheet.RowY(rs + 1) <= p.Bounds.Top && rs < 100000) rs++;
            Include(rs, cs);
        }
        return r1 == int.MaxValue ? new CellRange(0, 0, 0, 0) : new CellRange(r1, c1, r2, c2);
    }

    static (string Text, bool IsNumber, bool IsBool) CellText(XElement c, List<string> shared, string numberFormat, bool showZeros)
    {
        var type = (string?)c.Attribute("t");
        var v = (string?)c.Element(Main + "v");
        switch (type)
        {
            case "s":
                return (int.TryParse(v, out var i) && i >= 0 && i < shared.Count ? shared[i] : "", false, false);
            case "inlineStr":
                return (RichText(c.Element(Main + "is")), false, false);
            case "str":
                return (v ?? "", false, false);
            case "b":
                return (v == "1" ? "VERO" : "FALSO", false, true);
            case "e":
                return (v ?? "", false, true);
            default:
                if (v == null || !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return ("", false, false);
                if (number == 0 && !showZeros) return ("", true, false);
                return (NumberFormatter.Format(number, numberFormat), true, false);
        }
    }

    static string RichText(XElement? si) =>
        si == null ? "" : string.Concat(si.Descendants(Main + "t").Where(t => t.Parent?.Name != Main + "rPh").Select(t => t.Value));

    static List<string> SharedStrings(Package package)
    {
        var xml = package.Xml("xl/sharedStrings.xml");
        return xml?.Root?.Elements(Main + "si").Select(RichText).ToList() ?? [];
    }

    static void ReadPageSetup(PageSetup page, XElement root)
    {
        var setup = root.Element(Main + "pageSetup");
        var paperSize = (int?)setup?.Attribute("paperSize") ?? 9;
        var paper = paperSize switch
        {
            1 => new Size(Units.FromInch(8.5), Units.FromInch(11)),
            5 => new Size(Units.FromInch(8.5), Units.FromInch(14)),
            8 => new Size(Units.FromMm(297), Units.FromMm(420)),
            11 => new Size(Units.FromMm(148), Units.FromMm(210)),
            _ => Units.A4,
        };
        if ((string?)setup?.Attribute("orientation") == "landscape") paper = new Size(paper.Height, paper.Width);
        page.Paper = paper;

        var margins = root.Element(Main + "pageMargins");
        double M(string side, double fallback) => Units.FromInch((double?)margins?.Attribute(side) ?? fallback);
        page.Margins = new Thickness(M("left", 0.7), M("top", 0.75), M("right", 0.7), M("bottom", 0.75));

        var options = root.Element(Main + "printOptions");
        page.CenterHorizontally = (string?)options?.Attribute("horizontalCentered") is "1" or "true";
        page.DownThenOver = (string?)setup?.Attribute("pageOrder") != "overThenDown";

        bool fit = (string?)root.Element(Main + "sheetPr")?.Element(Main + "pageSetUpPr")?.Attribute("fitToPage") is "1" or "true";
        if (fit)
        {
            page.FitWide = (int?)setup?.Attribute("fitToWidth") ?? 1;
            page.FitTall = (int?)setup?.Attribute("fitToHeight") ?? 1;
        }
        else if ((int?)setup?.Attribute("scale") is { } scale && scale > 0)
        {
            page.Scale = scale / 100.0;
        }
    }

    // --- immagini ------------------------------------------------------------------------

    static void ReadPictures(Sheet sheet, string sheetPart, XElement root, Package package)
    {
        var drawingId = (string?)root.Element(Main + "drawing")?.Attribute(Rel + "id");
        if (drawingId == null || !package.Relationships(sheetPart).TryGetValue(drawingId, out var drawingPart)) return;
        var drawing = package.Xml(drawingPart);
        if (drawing?.Root == null) return;
        var images = package.Relationships(drawingPart);

        foreach (var anchor in drawing.Root.Elements())
        {
            Rect? bounds = anchor.Name.LocalName switch
            {
                "twoCellAnchor" => FromTo(sheet, anchor.Element(Xdr + "from"), anchor.Element(Xdr + "to")),
                "oneCellAnchor" => OneCell(sheet, anchor),
                "absoluteAnchor" => Absolute(anchor),
                _ => null,
            };
            if (bounds is not { } rect) continue;
            var shape = anchor.Elements().FirstOrDefault(e => e.Name.LocalName is "pic" or "grpSp");
            if (shape != null) AddShape(sheet, shape, rect, package, images);
        }
    }

    static void AddShape(Sheet sheet, XElement shape, Rect bounds, Package package, Dictionary<string, string> images)
    {
        if ((string?)shape.Descendants(Xdr + "cNvPr").FirstOrDefault()?.Attribute("hidden") is "1" or "true") return;

        if (shape.Name.LocalName == "pic")
        {
            var embed = (string?)shape.Element(Xdr + "blipFill")?.Element(A + "blip")?.Attribute(Rel + "embed");
            if (embed != null && images.TryGetValue(embed, out var imagePart) && package.Image(imagePart) is { } image)
                sheet.Pictures.Add(new SheetPicture(bounds, image));
            return;
        }

        // Gruppo: le forme interne sono espresse nello spazio "figli" (chOff/chExt) da mappare sull'area del gruppo.
        var xfrm = shape.Element(Xdr + "grpSpPr")?.Element(A + "xfrm");
        double chX = Emu(xfrm?.Element(A + "chOff"), "x"), chY = Emu(xfrm?.Element(A + "chOff"), "y");
        double chW = Emu(xfrm?.Element(A + "chExt"), "cx"), chH = Emu(xfrm?.Element(A + "chExt"), "cy");
        if (chW <= 0 || chH <= 0)
        {
            chX = Emu(xfrm?.Element(A + "off"), "x");
            chY = Emu(xfrm?.Element(A + "off"), "y");
            chW = Emu(xfrm?.Element(A + "ext"), "cx");
            chH = Emu(xfrm?.Element(A + "ext"), "cy");
        }
        if (chW <= 0 || chH <= 0) return;
        double sx = bounds.Width / chW, sy = bounds.Height / chH;

        foreach (var child in shape.Elements().Where(e => e.Name.LocalName is "pic" or "grpSp"))
        {
            var props = child.Name.LocalName == "pic" ? child.Element(Xdr + "spPr") : child.Element(Xdr + "grpSpPr");
            var cx = props?.Element(A + "xfrm");
            if (cx == null) continue;
            var rect = new Rect(
                bounds.X + (Emu(cx.Element(A + "off"), "x") - chX) * sx,
                bounds.Y + (Emu(cx.Element(A + "off"), "y") - chY) * sy,
                Math.Max(0, Emu(cx.Element(A + "ext"), "cx") * sx),
                Math.Max(0, Emu(cx.Element(A + "ext"), "cy") * sy));
            AddShape(sheet, child, rect, package, images);
        }
    }

    static double Emu(XElement? element, string attribute) => (double?)element?.Attribute(attribute) ?? 0;

    static Point Marker(Sheet sheet, XElement marker)
    {
        int col = (int?)marker.Element(Xdr + "col") ?? 0, row = (int?)marker.Element(Xdr + "row") ?? 0;
        double colOff = (double?)marker.Element(Xdr + "colOff") ?? 0, rowOff = (double?)marker.Element(Xdr + "rowOff") ?? 0;
        return new Point(sheet.ColumnX(col) + Units.FromEmu(colOff), sheet.RowY(row) + Units.FromEmu(rowOff));
    }

    static Rect? FromTo(Sheet sheet, XElement? from, XElement? to) =>
        from == null || to == null ? null : new Rect(Marker(sheet, from), Marker(sheet, to));

    static Rect? OneCell(Sheet sheet, XElement anchor)
    {
        var from = anchor.Element(Xdr + "from");
        var ext = anchor.Element(Xdr + "ext");
        if (from == null || ext == null) return null;
        var p = Marker(sheet, from);
        return new Rect(p.X, p.Y, Units.FromEmu(Emu(ext, "cx")), Units.FromEmu(Emu(ext, "cy")));
    }

    static Rect? Absolute(XElement anchor)
    {
        var pos = anchor.Element(Xdr + "pos");
        var ext = anchor.Element(Xdr + "ext");
        if (pos == null || ext == null) return null;
        return new Rect(Units.FromEmu(Emu(pos, "x")), Units.FromEmu(Emu(pos, "y")), Units.FromEmu(Emu(ext, "cx")), Units.FromEmu(Emu(ext, "cy")));
    }

    // --- pacchetto -----------------------------------------------------------------------

    sealed class Package(ZipArchive zip)
    {
        readonly Dictionary<string, XDocument?> _xml = [];
        readonly Dictionary<string, BitmapSource?> _images = [];

        public XDocument? Xml(string part)
        {
            if (_xml.TryGetValue(part, out var cached)) return cached;
            var entry = zip.GetEntry(part);
            XDocument? doc = null;
            if (entry != null)
            {
                using var stream = entry.Open();
                doc = XDocument.Load(stream);
            }
            return _xml[part] = doc;
        }

        public BitmapSource? Image(string part)
        {
            if (_images.TryGetValue(part, out var cached)) return cached;
            BitmapSource? image = null;
            var entry = zip.GetEntry(part);
            if (entry != null)
            {
                try
                {
                    var memory = new MemoryStream();
                    using (var stream = entry.Open()) stream.CopyTo(memory);
                    memory.Position = 0;
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = memory;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    image = bitmap;
                }
                catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException)
                {
                    // Formato immagine non gestito da Windows (es. EMF): si stampa senza.
                }
            }
            return _images[part] = image;
        }

        /// <summary>Relazioni di una parte: Id → percorso assoluto nel pacchetto.</summary>
        public Dictionary<string, string> Relationships(string part)
        {
            var folder = part.Contains('/') ? part[..part.LastIndexOf('/')] : "";
            var name = part[(part.LastIndexOf('/') + 1)..];
            var rels = Xml($"{(folder.Length > 0 ? folder + "/" : "")}_rels/{name}.rels");
            var result = new Dictionary<string, string>();
            foreach (var r in rels?.Root?.Elements(PackageRel + "Relationship") ?? [])
            {
                var id = (string?)r.Attribute("Id");
                var target = (string?)r.Attribute("Target");
                if (id == null || target == null || (string?)r.Attribute("TargetMode") == "External") continue;
                result[id] = Resolve(folder, target);
            }
            return result;
        }

        static string Resolve(string folder, string target)
        {
            if (target.StartsWith('/')) return target.TrimStart('/');
            var parts = (folder.Length > 0 ? folder.Split('/') : []).ToList();
            foreach (var piece in target.Split('/'))
            {
                if (piece == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else if (piece != ".") parts.Add(piece);
            }
            return string.Join('/', parts);
        }
    }

    // --- stili ---------------------------------------------------------------------------

    sealed class Styles
    {
        static readonly Dictionary<int, string> BuiltInFormats = new()
        {
            [0] = "General", [1] = "0", [2] = "0.00", [3] = "#,##0", [4] = "#,##0.00",
            [9] = "0%", [10] = "0.00%", [11] = "0.00E+00", [12] = "# ?/?", [13] = "# ??/??",
            [14] = "dd/mm/yyyy", [15] = "d-mmm-yy", [16] = "d-mmm", [17] = "mmm-yy",
            [18] = "h:mm AM/PM", [19] = "h:mm:ss AM/PM", [20] = "h:mm", [21] = "h:mm:ss", [22] = "dd/mm/yyyy h:mm",
            [37] = "#,##0 ;(#,##0)", [38] = "#,##0 ;[Red](#,##0)", [39] = "#,##0.00;(#,##0.00)", [40] = "#,##0.00;[Red](#,##0.00)",
            [45] = "mm:ss", [46] = "[h]:mm:ss", [47] = "mm:ss.0", [48] = "##0.0E+0", [49] = "@",
        };

        readonly List<CellFormat> _formats = [];
        readonly List<string> _numberFormats = [];

        public double MaxDigitWidth { get; }

        public Styles(Package package)
        {
            var xml = package.Xml("xl/styles.xml")?.Root;
            var theme = ThemeColors(package);

            var custom = xml?.Element(Main + "numFmts")?.Elements(Main + "numFmt")
                .ToDictionary(n => (int)n.Attribute("numFmtId")!, n => (string?)n.Attribute("formatCode") ?? "General") ?? [];

            var fonts = xml?.Element(Main + "fonts")?.Elements(Main + "font").Select(f => new
            {
                Name = (string?)f.Element(Main + "name")?.Attribute("val") ?? "Calibri",
                Size = (double?)f.Element(Main + "sz")?.Attribute("val") ?? 11,
                Bold = IsOn(f.Element(Main + "b")),
                Italic = IsOn(f.Element(Main + "i")),
                Underline = f.Element(Main + "u") is { } u && (string?)u.Attribute("val") != "none",
                Strike = IsOn(f.Element(Main + "strike")),
                Color = ResolveColor(f.Element(Main + "color"), theme) ?? Colors.Black,
            }).ToList() ?? [];

            var fills = xml?.Element(Main + "fills")?.Elements(Main + "fill").Select(f =>
            {
                var pattern = f.Element(Main + "patternFill");
                if (pattern != null)
                {
                    var type = (string?)pattern.Attribute("patternType") ?? "none";
                    if (type is "none" or "gray125") return (Color?)null;
                    return ResolveColor(pattern.Element(Main + "fgColor"), theme) ?? ResolveColor(pattern.Element(Main + "bgColor"), theme);
                }
                var stop = f.Element(Main + "gradientFill")?.Elements(Main + "stop").FirstOrDefault();
                return ResolveColor(stop?.Element(Main + "color"), theme);
            }).ToList() ?? [];

            var borders = xml?.Element(Main + "borders")?.Elements(Main + "border").Select(b => new
            {
                Left = Border(b.Element(Main + "left") ?? b.Element(Main + "start"), theme),
                Right = Border(b.Element(Main + "right") ?? b.Element(Main + "end"), theme),
                Top = Border(b.Element(Main + "top"), theme),
                Bottom = Border(b.Element(Main + "bottom"), theme),
            }).ToList() ?? [];

            var defaultFont = fonts.FirstOrDefault();
            MaxDigitWidth = MeasureDigit(defaultFont?.Name ?? "Calibri", defaultFont?.Size ?? 11);

            foreach (var xf in xml?.Element(Main + "cellXfs")?.Elements(Main + "xf") ?? [])
            {
                int fontId = (int?)xf.Attribute("fontId") ?? 0, fillId = (int?)xf.Attribute("fillId") ?? 0;
                int borderId = (int?)xf.Attribute("borderId") ?? 0, numFmtId = (int?)xf.Attribute("numFmtId") ?? 0;
                var font = fontId < fonts.Count ? fonts[fontId] : defaultFont;
                var border = borderId < borders.Count ? borders[borderId] : null;
                var alignment = xf.Element(Main + "alignment");
                double fontSize = Units.FromPt(font?.Size ?? 11);

                _formats.Add(new CellFormat
                {
                    FontFamily = font?.Name ?? "Calibri",
                    FontSize = fontSize,
                    Bold = font?.Bold ?? false,
                    Italic = font?.Italic ?? false,
                    Underline = font?.Underline ?? false,
                    Strike = font?.Strike ?? false,
                    Foreground = font?.Color ?? Colors.Black,
                    Background = fillId < fills.Count ? fills[fillId] : null,
                    Left = border?.Left,
                    Right = border?.Right,
                    Top = border?.Top,
                    Bottom = border?.Bottom,
                    HAlign = (string?)alignment?.Attribute("horizontal") switch
                    {
                        "left" => HAlign.Left,
                        "center" or "centerContinuous" => HAlign.Center,
                        "right" => HAlign.Right,
                        "justify" or "distributed" => HAlign.Justify,
                        _ => HAlign.General,
                    },
                    VAlign = (string?)alignment?.Attribute("vertical") switch
                    {
                        "top" => VAlign.Top,
                        "center" or "justify" or "distributed" => VAlign.Center,
                        _ => VAlign.Bottom,
                    },
                    Wrap = IsOn(alignment?.Attribute("wrapText")),
                    ShrinkToFit = IsOn(alignment?.Attribute("shrinkToFit")),
                    // Ogni livello di rientro vale circa tre cifre del carattere predefinito.
                    Indent = ((int?)alignment?.Attribute("indent") ?? 0) * 3 * MaxDigitWidth,
                });
                _numberFormats.Add(custom.TryGetValue(numFmtId, out var code) ? code : BuiltInFormats.GetValueOrDefault(numFmtId, "General"));
            }
            if (_formats.Count == 0)
            {
                _formats.Add(new CellFormat());
                _numberFormats.Add("General");
            }
        }

        public CellFormat Format(int index) => index >= 0 && index < _formats.Count ? _formats[index] : _formats[0];
        public string NumberFormat(int index) => index >= 0 && index < _numberFormats.Count ? _numberFormats[index] : "General";

        static bool IsOn(XElement? e) => e != null && (string?)e.Attribute("val") is null or "1" or "true";
        static bool IsOn(XAttribute? a) => (string?)a is "1" or "true";

        static BorderLine? Border(XElement? side, List<Color> theme)
        {
            var style = (string?)side?.Attribute("style");
            if (style == null || style == "none") return null;
            var color = ResolveColor(side!.Element(Main + "color"), theme) ?? Colors.Black;
            return style switch
            {
                "hair" => new BorderLine(0.5, color),
                "thin" => new BorderLine(1, color),
                "medium" => new BorderLine(2, color),
                "thick" => new BorderLine(3, color),
                "double" => new BorderLine(3, color, LineKind.Double),
                "dotted" => new BorderLine(1, color, LineKind.Dotted),
                "dashed" or "dashDot" or "dashDotDot" => new BorderLine(1, color, LineKind.Dashed),
                "mediumDashed" or "mediumDashDot" or "mediumDashDotDot" or "slantDashDot" => new BorderLine(2, color, LineKind.Dashed),
                _ => new BorderLine(1, color),
            };
        }

        /// <summary>Larghezza in pixel della cifra più larga del carattere predefinito (unità delle larghezze di colonna).</summary>
        static double MeasureDigit(string font, double sizePt)
        {
            var typeface = new Typeface(new FontFamily(font), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            double max = 0;
            for (char d = '0'; d <= '9'; d++)
            {
                var text = new FormattedText(d.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface,
                    Units.FromPt(sizePt), Brushes.Black, 1.0);
                max = Math.Max(max, text.WidthIncludingTrailingWhitespace);
            }
            return max > 0 ? Math.Round(max) : 7;
        }

        // Ordine dei colori del tema come li indicizza Excel (lt1/dk1 e lt2/dk2 sono invertiti rispetto all'XML).
        static readonly string[] ThemeOrder = ["lt1", "dk1", "lt2", "dk2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink"];

        static List<Color> ThemeColors(Package package)
        {
            var defaults = new[] { "FFFFFF", "000000", "EEECE1", "1F497D", "4F81BD", "C0504D", "9BBB59", "8064A2", "4BACC6", "F79646", "0000FF", "800080" };
            var colors = defaults.Select(h => Units.ParseHexColor(h)!.Value).ToList();
            var scheme = package.Xml("xl/theme/theme1.xml")?.Descendants(A + "clrScheme").FirstOrDefault();
            if (scheme == null) return colors;
            for (int i = 0; i < ThemeOrder.Length; i++)
            {
                var element = scheme.Element(A + ThemeOrder[i]);
                var hex = (string?)element?.Element(A + "srgbClr")?.Attribute("val") ?? (string?)element?.Element(A + "sysClr")?.Attribute("lastClr");
                if (Units.ParseHexColor(hex) is { } c) colors[i] = c;
            }
            return colors;
        }

        static Color? ResolveColor(XElement? element, List<Color> theme)
        {
            if (element == null) return null;
            Color? color = null;
            if ((string?)element.Attribute("rgb") is { } rgb) color = Units.ParseHexColor(rgb);
            else if ((int?)element.Attribute("theme") is { } t && t >= 0 && t < theme.Count) color = theme[t];
            else if ((int?)element.Attribute("indexed") is { } ix) color = Indexed(ix);
            else if (IsOn(element.Attribute("auto"))) color = Colors.Black;
            if (color is { } c && (double?)element.Attribute("tint") is { } tint && tint != 0) color = ApplyTint(c, tint);
            return color;
        }

        static Color? Indexed(int index)
        {
            if (index == 64) return Colors.Black;   // colore di sistema del testo
            if (index == 65) return Colors.White;   // colore di sistema dello sfondo
            if (index >= 8) index -= 8;
            return index >= 0 && index < Palette.Length ? Units.ParseHexColor(Palette[index]) : null;
        }

        // Tavolozza predefinita di Excel (indici 8–63; 0–7 coincidono con i primi otto).
        static readonly string[] Palette =
        [
            "000000", "FFFFFF", "FF0000", "00FF00", "0000FF", "FFFF00", "FF00FF", "00FFFF",
            "800000", "008000", "000080", "808000", "800080", "008080", "C0C0C0", "808080",
            "9999FF", "993366", "FFFFCC", "CCFFFF", "660066", "FF8080", "0066CC", "CCCCFF",
            "000080", "FF00FF", "FFFF00", "00FFFF", "800080", "800000", "008080", "0000FF",
            "00CCFF", "CCFFFF", "CCFFCC", "FFFF99", "99CCFF", "FF99CC", "CC99FF", "FFCC99",
            "3366FF", "33CCCC", "99CC00", "FFCC00", "FF9900", "FF6600", "666699", "969696",
            "003366", "339966", "003300", "333300", "993300", "993366", "333399", "333333",
        ];

        /// <summary>Schiarisce (tint &gt; 0) o scurisce (tint &lt; 0) la luminosità HSL, come Excel.</summary>
        static Color ApplyTint(Color c, double tint)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double l = (max + min) / 2, h = 0, s = 0;
            if (max != min)
            {
                double d = max - min;
                s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
                h /= 6;
            }
            l = tint < 0 ? l * (1 + tint) : l * (1 - tint) + tint;

            double Hue(double p, double q, double t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                if (t < 1.0 / 6) return p + (q - p) * 6 * t;
                if (t < 0.5) return q;
                if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
                return p;
            }
            if (s == 0) r = g = b = l;
            else
            {
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
                r = Hue(p, q, h + 1.0 / 3);
                g = Hue(p, q, h);
                b = Hue(p, q, h - 1.0 / 3);
            }
            return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
        }
    }
}
