using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using CinnatiWorkout.Ods;
using CinnatiWorkout.Printing;

namespace CinnatiWorkout.Workout;

/// <summary>
/// Layout di stampa delle schede allenamento (testi ingranditi, righe più alte, al massimo 2 schede per foglio,
/// margini, colori, logo). Lo stesso calcolo serve per la stampa dall'app e viene scritto nel file .ods a ogni
/// salvataggio, così il file sincronizzato su Drive, aperto e stampato altrove, ha lo stesso aspetto.
/// </summary>
/// <remarks>
/// I testi si ingrandiscono una volta sola: il file porta un segno nei metadati (<see cref="MarkerName"/>) e da lì in poi
/// si ingrandiscono solo le celle con lo stile della nota creato dall'app. Righe, interruzioni di pagina, scala e logo
/// si ricalcolano a ogni salvataggio, così seguono le schede aggiunte o tolte.
/// </remarks>
public static class WorkoutPrintLayout
{
    const string MarkerName = "CinnatiLayoutStampa";
    const string LogoName = "CinnatiLogo";
    const string LogoPart = "Pictures/cinnati-logo.png";
    const string ManifestPart = "META-INF/manifest.xml";
    /// <summary>Stile della nota creata dall'app (testo ancora da ingrandire).</summary>
    public const string NoteStyleName = "ceCinnatiNote";

    static readonly XNamespace Table = OdsDocument.TableNs;
    static readonly XNamespace Style = OdsDocument.StyleNs;
    static readonly XNamespace Fo = OdsDocument.FoNs;
    static readonly XNamespace Office = OdsDocument.OfficeNs;
    static readonly XNamespace Meta = "urn:oasis:names:tc:opendocument:xmlns:meta:1.0";
    static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    static readonly XNamespace LoExt = "urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0";

    static CultureInfo Inv => CultureInfo.InvariantCulture;

    /// <summary>Logo da mettere nei file salvati (quello delle impostazioni).</summary>
    public static Func<BitmapSource?>? LogoSource { get; set; }

    /// <summary>true se il file ha già il layout di stampa scritto dall'app.</summary>
    public static bool IsApplied(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("meta.xml");
            if (entry == null) return false;
            using var stream = entry.Open();
            return HasMarker(XDocument.Load(stream));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or XmlException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static bool HasMarker(XDocument? meta) =>
        meta?.Descendants(Meta + "user-defined").Any(e => (string?)e.Attribute(Meta + "name") == MarkerName) == true;

    /// <summary>Prepara per la stampa un foglio letto da una scheda allenamento: lo stesso calcolo del salvataggio.</summary>
    public static void Prepare(Sheet sheet, bool applied)
    {
        // Dopo il primo salvataggio l'area di stampa del file è quella scritta dall'app: si ricalcola sulle celle usate,
        // così segue le schede aggiunte.
        // Le righe del file sono già allungate: non si allungano di nuovo (l'allungamento ha un limite, e ripeterlo
        // a ogni salvataggio o stampa le farebbe crescere ogni volta).
        if (applied) sheet.PrintArea = sheet.UsedArea;
        SheetRenderer.PrepareReadablePrint(sheet, maxRowStretch: applied ? 1 : 1.35,
            alreadyEnlarged: applied ? (r, c) => sheet.GetCell(r, c)?.StyleName != NoteStyleName : null);
    }

    /// <summary>
    /// Scrive il layout di stampa nel documento in memoria e restituisce le altre parti del file da aggiornare
    /// (stili di pagina, metadati, logo) da passare a <see cref="OdsDocument.Save"/>.
    /// </summary>
    public static Dictionary<string, byte[]?> Apply(OdsDocument doc, BitmapSource? logo)
    {
        var styles = doc.ReadPart("styles.xml");
        var meta = doc.ReadPart("meta.xml");
        var manifest = doc.ReadPart(ManifestPart);
        bool applied = HasMarker(meta);

        var sheet = OdsSheetReader.ReadFirst(doc.Content, styles);
        var before = sheet.Cells.ToDictionary(c => c.Key, c => c.Value.Format);
        Prepare(sheet, applied);
        var pages = SheetRenderer.Paginate(sheet);
        var rows = RowIndex(doc);

        WriteCells(doc, sheet, before, rows);
        WriteRows(doc, sheet, pages.Skip(1).Select(p => p.Range.Row1).ToHashSet(), rows);
        WritePrintRange(doc, sheet.PrintArea);
        RemoveUnusedStyles(doc);

        var parts = new Dictionary<string, byte[]?>();
        if (styles != null && WritePageSetup(doc, styles, sheet.Page, pages[0].Scale)) parts["styles.xml"] = Bytes(styles);

        bool manifestChanged = WriteLogo(doc, sheet, logo, manifest, parts);
        if (!applied)
        {
            if (meta == null)
            {
                meta = new XDocument(new XElement(Office + "document-meta",
                    new XAttribute(XNamespace.Xmlns + "office", Office.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "meta", Meta.NamespaceName),
                    new XAttribute(Office + "version", "1.2"),
                    new XElement(Office + "meta")));
                manifestChanged |= EnsureManifestEntry(manifest, "meta.xml", "text/xml");
            }
            var office = meta.Root!.Element(Office + "meta") ?? new XElement(Office + "meta");
            if (office.Parent == null) meta.Root.Add(office);
            office.Add(new XElement(Meta + "user-defined", new XAttribute(Meta + "name", MarkerName), "1"));
            parts["meta.xml"] = Bytes(meta);
        }
        if (manifestChanged && manifest != null) parts[ManifestPart] = Bytes(manifest);
        return parts;
    }

    /// <summary>Righe del foglio per indice (solo quelle non ripetute, cioè la zona utile già espansa).</summary>
    static Dictionary<int, XElement> RowIndex(OdsDocument doc)
    {
        var map = new Dictionary<int, XElement>();
        int index = 0;
        foreach (var row in doc.Rows)
        {
            int repeat = (int?)row.Attribute(Table + "number-rows-repeated") ?? 1;
            if (repeat == 1) map[index] = row;
            index += repeat;
        }
        return map;
    }

    // --- celle -----------------------------------------------------------------------------

    static void WriteCells(OdsDocument doc, Sheet sheet, Dictionary<(int Row, int Col), CellFormat> before, Dictionary<int, XElement> rows)
    {
        var area = sheet.PrintArea;
        var created = new Dictionary<string, string>();
        foreach (var (key, cell) in sheet.Cells.ToList())
        {
            if (!area.Contains(key.Row, key.Col) || !before.TryGetValue(key, out var old) || !rows.TryGetValue(key.Row, out var row)) continue;
            var now = cell.Format;
            double? size = Math.Abs(now.FontSize - old.FontSize) > 0.01 ? now.FontSize : null;
            Color? foreground = now.Foreground != old.Foreground ? now.Foreground : null;
            bool background = now.Background != old.Background;
            bool wrap = now.Wrap && !old.Wrap;
            if (size == null && foreground == null && !background && !wrap) continue;

            var element = doc.CellElement(row, key.Col);
            if (OdsDocument.IsCovered(element)) continue;
            var name = DerivedCellStyle(doc, cell.StyleName, size, foreground, background ? now.Background ?? Colors.Transparent : null, wrap, created);
            element.SetAttributeValue(Table + "style-name", name);
        }
    }

    /// <summary>Nuovo stile automatico: copia dello stile della cella (o figlio, se è uno stile comune) con le modifiche.</summary>
    static string DerivedCellStyle(OdsDocument doc, string? baseName, double? size, Color? foreground, Color? background, bool wrap,
        Dictionary<string, string> created)
    {
        var key = $"{baseName}|{size?.ToString("0.###", Inv)}|{foreground}|{background}|{wrap}";
        if (created.TryGetValue(key, out var existing)) return existing;

        var auto = doc.AutomaticStyles;
        var source = baseName == null ? null : auto.Elements(Style + "style")
            .FirstOrDefault(s => (string?)s.Attribute(Style + "name") == baseName && (string?)s.Attribute(Style + "family") == "table-cell");
        var style = source != null
            ? new XElement(source)
            : new XElement(Style + "style", new XAttribute(Style + "family", "table-cell"), new XAttribute(Style + "parent-style-name", baseName ?? "Default"));
        var name = UniqueName(auto, "ceCw");
        style.SetAttributeValue(Style + "name", name);

        if (size is { } s)
        {
            var text = Properties(style, "text-properties");
            var pt = (s * 72 / 96).ToString("0.##", Inv) + "pt";
            text.SetAttributeValue(Fo + "font-size", pt);
            text.SetAttributeValue(Style + "font-size-asian", pt);
            text.SetAttributeValue(Style + "font-size-complex", pt);
        }
        if (foreground is { } f)
        {
            var text = Properties(style, "text-properties");
            text.SetAttributeValue(Fo + "color", Hex(f));
            text.Elements(LoExt + "char-color").Remove();
        }
        if (background is { } b)
            Properties(style, "table-cell-properties").SetAttributeValue(Fo + "background-color", b.A == 0 ? "transparent" : Hex(b));
        if (wrap)
            Properties(style, "table-cell-properties").SetAttributeValue(Fo + "wrap-option", "wrap");

        auto.Add(style);
        created[key] = name;
        return name;
    }

    static readonly string[] PropertyOrder = ["table-row-properties", "table-cell-properties", "paragraph-properties", "text-properties"];

    /// <summary>Elemento di proprietà dello stile (creato se manca, nell'ordine richiesto da ODF).</summary>
    static XElement Properties(XElement style, string localName)
    {
        var element = style.Element(Style + localName);
        if (element != null) return element;
        element = new XElement(Style + localName);
        int rank = Array.IndexOf(PropertyOrder, localName);
        var next = style.Elements().FirstOrDefault(e => e.Name.Namespace == Style && Array.IndexOf(PropertyOrder, e.Name.LocalName) > rank);
        if (next != null) next.AddBeforeSelf(element);
        else style.Add(element);
        return element;
    }

    static string UniqueName(XElement auto, string prefix)
    {
        int max = 0;
        foreach (var s in auto.Elements(Style + "style"))
            if ((string?)s.Attribute(Style + "name") is { } n && n.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(n[prefix.Length..], NumberStyles.None, Inv, out var i))
                max = Math.Max(max, i);
        return prefix + (max + 1).ToString(Inv);
    }

    static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    // --- righe, area di stampa, pagina -------------------------------------------------------

    static void WriteRows(OdsDocument doc, Sheet sheet, HashSet<int> breaks, Dictionary<int, XElement> rows)
    {
        var area = sheet.PrintArea;
        var auto = doc.AutomaticStyles;
        var created = new Dictionary<string, string>();
        for (int r = area.Row1; r <= area.Row2; r++)
        {
            double height = sheet.RowHeight(r);
            if (height <= 0 || !rows.TryGetValue(r, out var row)) continue;
            var baseName = (string?)row.Attribute(Table + "style-name");
            var cm = (height * 2.54 / 96).ToString("0.###", Inv) + "cm";
            bool pageBreak = breaks.Contains(r);
            var key = $"{baseName}|{cm}|{pageBreak}";
            if (!created.TryGetValue(key, out var name))
            {
                var rowStyles = auto.Elements(Style + "style").Where(s => (string?)s.Attribute(Style + "family") == "table-row").ToList();
                var source = baseName == null ? null : rowStyles.FirstOrDefault(s => (string?)s.Attribute(Style + "name") == baseName);
                // Uno stile di riga già uguale (di solito quello del salvataggio precedente) si riusa: il file non cresce.
                bool Matches(XElement s) => s.Element(Style + "table-row-properties") is { } p
                    && (string?)p.Attribute(Style + "row-height") == cm
                    && (string?)p.Attribute(Style + "use-optimal-row-height") == "false"
                    && ((string?)p.Attribute(Fo + "break-before") == "page") == pageBreak;
                var same = source != null && Matches(source) ? source : rowStyles.FirstOrDefault(Matches);
                if (same != null)
                {
                    created[key] = name = (string)same.Attribute(Style + "name")!;
                    row.SetAttributeValue(Table + "style-name", name);
                    continue;
                }
                var style = source != null
                    ? new XElement(source)
                    : new XElement(Style + "style", new XAttribute(Style + "family", "table-row"));
                name = UniqueName(auto, "roCw");
                style.SetAttributeValue(Style + "name", name);
                var props = Properties(style, "table-row-properties");
                props.SetAttributeValue(Style + "row-height", cm);
                props.SetAttributeValue(Style + "use-optimal-row-height", "false");
                props.SetAttributeValue(Fo + "break-before", pageBreak ? "page" : "auto");
                auto.Add(style);
                created[key] = name;
            }
            row.SetAttributeValue(Table + "style-name", name);
        }
    }

    /// <summary>Toglie gli stili creati dall'app (ceCw…, roCw…) che nessuna cella, riga o colonna usa più.</summary>
    static void RemoveUnusedStyles(OdsDocument doc)
    {
        var used = doc.Content.Descendants().Attributes()
            .Where(a => a.Name == Table + "style-name" || a.Name == Table + "default-cell-style-name")
            .Select(a => a.Value)
            .ToHashSet();
        foreach (var style in doc.AutomaticStyles.Elements(Style + "style").ToList())
            if ((string?)style.Attribute(Style + "name") is { } name && (name.StartsWith("ceCw") || name.StartsWith("roCw")) && !used.Contains(name))
                style.Remove();
    }

    static void WritePrintRange(OdsDocument doc, CellRange area)
    {
        var table = doc.TableElement;
        var sheetName = "$'" + ((string?)table.Attribute(Table + "name") ?? "Foglio1").Replace("'", "''") + "'";
        table.SetAttributeValue(Table + "print-ranges", $"{sheetName}.{Address(area.Row1, area.Col1)}:{sheetName}.{Address(area.Row2, area.Col2)}");
    }

    static string Address(int row, int col)
    {
        var letters = "";
        for (int c = col + 1; c > 0; c = (c - 1) / 26) letters = (char)('A' + (c - 1) % 26) + letters;
        return $"${letters}${row + 1}";
    }

    /// <summary>Margini, scala e centratura dello stile di pagina del foglio; niente intestazione e piè di pagina (come la stampa dall'app).</summary>
    static bool WritePageSetup(OdsDocument doc, XDocument styles, PageSetup page, double scale)
    {
        var tableStyleName = (string?)doc.TableElement.Attribute(Table + "style-name");
        var tableStyle = doc.Content.Descendants(Style + "style")
            .FirstOrDefault(s => (string?)s.Attribute(Style + "name") == tableStyleName && (string?)s.Attribute(Style + "family") == "table");
        var masterName = (string?)tableStyle?.Attribute(Style + "master-page-name") ?? "Default";
        var masters = styles.Descendants(Style + "master-page").ToList();
        var master = masters.FirstOrDefault(m => (string?)m.Attribute(Style + "name") == masterName)
                     ?? masters.FirstOrDefault(m => (string?)m.Attribute(Style + "name") == "Default");
        var layoutName = (string?)master?.Attribute(Style + "page-layout-name");
        var props = styles.Descendants(Style + "page-layout")
            .FirstOrDefault(l => (string?)l.Attribute(Style + "name") == layoutName)?.Element(Style + "page-layout-properties");
        if (master == null || props == null) return false;

        string Cm(double v) => (v * 2.54 / 96).ToString("0.###", Inv) + "cm";
        props.Attribute(Fo + "margin")?.Remove();
        props.SetAttributeValue(Fo + "margin-left", Cm(page.Margins.Left));
        props.SetAttributeValue(Fo + "margin-top", Cm(page.Margins.Top));
        props.SetAttributeValue(Fo + "margin-right", Cm(page.Margins.Right));
        props.SetAttributeValue(Fo + "margin-bottom", Cm(page.Margins.Bottom));
        foreach (var name in new[] { "scale-to-pages", "scale-to-X", "scale-to-Y" }) props.Attribute(Style + name)?.Remove();
        props.SetAttributeValue(Style + "scale-to", Math.Max(10, Math.Floor(scale * 100)).ToString(Inv) + "%");
        props.SetAttributeValue(Style + "table-centering", page.CenterHorizontally ? "horizontal" : "none");
        foreach (var name in new[] { "header", "footer" })
            master.Element(Style + name)?.SetAttributeValue(Style + "display", "false");
        return true;
    }

    // --- logo e parti del file ---------------------------------------------------------------

    /// <summary>Logo nell'angolo in alto a sinistra, nella stessa posizione della stampa dall'app. true se il manifest è cambiato.</summary>
    static bool WriteLogo(OdsDocument doc, Sheet sheet, BitmapSource? logo, XDocument? manifest, Dictionary<string, byte[]?> parts)
    {
        var table = doc.TableElement;
        foreach (var frame in table.Descendants(Draw + "frame").Where(f => (string?)f.Attribute(Draw + "name") == LogoName).ToList())
            frame.Remove();

        if (logo == null || manifest == null)
        {
            foreach (var empty in table.Elements(Table + "shapes").Where(s => !s.HasElements).ToList()) empty.Remove();
            parts[LogoPart] = null;
            return RemoveManifestEntry(manifest, LogoPart);
        }

        // Le immagini già nel file al posto del logo (es. un vecchio logo inserito a mano) non compaiono nella stampa
        // dall'app, che usa il logo delle impostazioni: si tolgono, altrimenti nel file finirebbero sotto il logo nuovo.
        var bounds = SheetRenderer.LogoBounds(sheet, logo);
        foreach (var frame in table.Descendants(Draw + "frame").Where(f => f.Element(Draw + "image") != null).ToList())
            if (FrameBounds(sheet, frame).IntersectsWith(bounds) || AnchorCellBounds(sheet, frame) is { } cell && cell.IntersectsWith(bounds))
                frame.Remove();
        foreach (var empty in table.Elements(Table + "shapes").Where(s => !s.HasElements).ToList()) empty.Remove();

        string Cm(double v) => (v * 2.54 / 96).ToString("0.###", Inv) + "cm";
        var shapes = table.Element(Table + "shapes");
        if (shapes == null)
        {
            shapes = new XElement(Table + "shapes");
            if (table.Element(Office + "forms") is { } forms) forms.AddAfterSelf(shapes);
            else table.AddFirst(shapes);
        }
        shapes.Add(new XElement(Draw + "frame",
            new XAttribute(Draw + "name", LogoName),
            new XAttribute(Draw + "z-index", 0),
            new XAttribute(Svg + "width", Cm(bounds.Width)),
            new XAttribute(Svg + "height", Cm(bounds.Height)),
            new XAttribute(Svg + "x", Cm(bounds.X)),
            new XAttribute(Svg + "y", Cm(bounds.Y)),
            new XElement(Draw + "image",
                new XAttribute(XLink + "href", LogoPart),
                new XAttribute(XLink + "type", "simple"),
                new XAttribute(XLink + "show", "embed"),
                new XAttribute(XLink + "actuate", "onLoad"))));

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(logo));
        using var png = new MemoryStream();
        encoder.Save(png);
        parts[LogoPart] = png.ToArray();
        return EnsureManifestEntry(manifest, LogoPart, "image/png");
    }

    /// <summary>Riquadro dell'immagine dalle sue coordinate svg (nel foglio).</summary>
    static System.Windows.Rect FrameBounds(Sheet sheet, XElement frame)
    {
        double L(string name) => Units.ParseLength((string?)frame.Attribute(Svg + name)) ?? 0;
        return new System.Windows.Rect(L("x"), L("y"), Math.Max(0, L("width")), Math.Max(0, L("height")));
    }

    /// <summary>Riquadro della cella a cui è ancorata l'immagine, o null se l'immagine non è dentro una cella.</summary>
    static System.Windows.Rect? AnchorCellBounds(Sheet sheet, XElement frame)
    {
        var cell = frame.Ancestors().FirstOrDefault(a => a.Name == Table + "table-cell" || a.Name == Table + "covered-table-cell");
        var row = cell?.Parent;
        if (cell == null || row == null || row.Name != Table + "table-row") return null;

        int r = 0;
        foreach (var other in row.Document!.Descendants(Table + "table-row"))
        {
            if (other == row) break;
            r += (int?)other.Attribute(Table + "number-rows-repeated") ?? 1;
        }
        int c = cell.ElementsBeforeSelf()
            .Where(e => e.Name == Table + "table-cell" || e.Name == Table + "covered-table-cell")
            .Sum(e => (int?)e.Attribute(Table + "number-columns-repeated") ?? 1);
        return sheet.CellRect(r, c);
    }

    static bool EnsureManifestEntry(XDocument? manifest, string path, string mediaType)
    {
        if (manifest?.Root == null) return false;
        if (manifest.Root.Elements(Manifest + "file-entry").Any(e => (string?)e.Attribute(Manifest + "full-path") == path)) return false;
        manifest.Root.Add(new XElement(Manifest + "file-entry",
            new XAttribute(Manifest + "full-path", path), new XAttribute(Manifest + "media-type", mediaType)));
        return true;
    }

    static bool RemoveManifestEntry(XDocument? manifest, string path)
    {
        var entries = manifest?.Root?.Elements(Manifest + "file-entry").Where(e => (string?)e.Attribute(Manifest + "full-path") == path).ToList();
        if (entries is not { Count: > 0 }) return false;
        entries.Remove();
        return true;
    }

    static byte[] Bytes(XDocument doc)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
            doc.Save(writer);
        return stream.ToArray();
    }
}
