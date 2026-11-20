using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CinnatiWorkout.Ods;

/// <summary>
/// Accesso minimale al primo foglio di un file .ods, modificando direttamente content.xml
/// così stili, bordi e impaginazione restano quelli del file originale.
/// </summary>
public sealed class OdsDocument
{
    public static readonly XNamespace TableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    public static readonly XNamespace OfficeNs = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    public static readonly XNamespace TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    public static readonly XNamespace CalcExtNs = "urn:org:documentfoundation:names:experimental:calc:xmlns:calcext:1.0";
    public static readonly XNamespace StyleNs = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    public static readonly XNamespace FoNs = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";

    public const int MaxColumns = 32;
    const int ExtraRows = 40;

    static readonly XName RowName = TableNs + "table-row";
    static readonly XName CellName = TableNs + "table-cell";
    static readonly XName CoveredCellName = TableNs + "covered-table-cell";
    static readonly XName RowsRepeated = TableNs + "number-rows-repeated";
    static readonly XName ColumnsRepeated = TableNs + "number-columns-repeated";
    static readonly XName ColumnsSpanned = TableNs + "number-columns-spanned";
    static readonly XName StyleName = TableNs + "style-name";
    static readonly XName Paragraph = TextNs + "p";

    static readonly XName[] ValueAttributes =
    [
        OfficeNs + "value-type", CalcExtNs + "value-type", OfficeNs + "value", OfficeNs + "date-value",
        OfficeNs + "time-value", OfficeNs + "boolean-value", OfficeNs + "string-value", TableNs + "formula",
    ];

    readonly XDocument _content;

    public string FilePath { get; }
    XElement Table { get; }

    OdsDocument(string path, XDocument content)
    {
        FilePath = path;
        _content = content;
        Table = content.Descendants(TableNs + "table").FirstOrDefault()
            ?? throw new InvalidDataException("Il file non contiene fogli.");
        ExpandRepeats();
    }

    public static OdsDocument Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("content.xml")
            ?? throw new InvalidDataException("Il file non è un foglio di calcolo ODS valido.");
        using var stream = entry.Open();
        return new OdsDocument(path, XDocument.Load(stream, LoadOptions.PreserveWhitespace));
    }

    public List<XElement> Rows => Table.Descendants(RowName).ToList();

    public string GetText(XElement row, int col) => ReadText(Cell(row, col));

    public void SetText(XElement row, int col, string? value)
    {
        var cell = Cell(row, col);
        if (cell.Name == CoveredCellName) return;
        value = (value ?? "").Replace("\r", "").TrimEnd();
        if (ReadText(cell) == value) return;

        ClearCell(cell);
        if (value.Length == 0) return;
        cell.SetAttributeValue(OfficeNs + "value-type", "string");
        cell.SetAttributeValue(CalcExtNs + "value-type", "string");
        foreach (var line in value.Split('\n')) cell.Add(BuildParagraph(line));
    }

    /// <summary>Prima cella non vuota della riga (colonna e testo), oppure (-1, null).</summary>
    public (int Column, string? Text) FirstText(XElement row)
    {
        var cells = Cells(row);
        for (int c = 0; c < cells.Count && c < MaxColumns; c++)
        {
            var text = ReadText(cells[c]).Trim();
            if (text.Length > 0) return (c, text);
        }
        return (-1, null);
    }

    public bool IsEmpty(XElement row) => FirstText(row).Text is null;

    public int ColumnSpan(XElement row, int col) => (int?)Cell(row, col).Attribute(ColumnsSpanned) ?? 1;

    public string? CellStyle(XElement row, int col) => (string?)Cell(row, col).Attribute(StyleName);

    /// <summary>Inserisce dopo <paramref name="anchor"/> una copia di <paramref name="template"/>.</summary>
    public XElement CloneRowAfter(XElement anchor, XElement template, bool clearText)
    {
        var row = new XElement(template);
        row.Attribute(RowsRepeated)?.Remove();
        if (clearText)
            foreach (var cell in Cells(row)) ClearCell(cell);
        anchor.AddAfterSelf(row);
        return row;
    }

    /// <summary>Unisce <paramref name="span"/> celle a partire da <paramref name="col"/> (le successive diventano "coperte").</summary>
    public void MergeAcross(XElement row, int col, int span)
    {
        if (span <= 1) return;
        var first = Cell(row, col);
        first.SetAttributeValue(ColumnsSpanned, span);
        first.SetAttributeValue(TableNs + "number-rows-spanned", 1);
        for (int i = 1; i < span; i++)
        {
            var cell = Cell(row, col + i);
            ClearCell(cell);
            cell.Attribute(ColumnsSpanned)?.Remove();
            cell.Attribute(TableNs + "number-rows-spanned")?.Remove();
            cell.Name = CoveredCellName;
        }
    }

    public void SetCellStyle(XElement row, int col, string styleName) => Cell(row, col).SetAttributeValue(StyleName, styleName);

    /// <summary>Aggiunge uno stile di cella automatico (se non esiste già) e ne restituisce il nome.</summary>
    public string EnsureCellStyle(string name, XElement definition)
    {
        var styles = _content.Root!.Element(OfficeNs + "automatic-styles");
        if (styles == null)
        {
            styles = new XElement(OfficeNs + "automatic-styles");
            var body = _content.Root.Element(OfficeNs + "body");
            if (body != null) body.AddBeforeSelf(styles);
            else _content.Root.Add(styles);
        }
        if (!styles.Elements(StyleNs + "style").Any(s => (string?)s.Attribute(StyleNs + "name") == name))
        {
            var style = new XElement(definition);
            style.SetAttributeValue(StyleNs + "name", name);
            styles.Add(style);
        }
        return name;
    }

    public XElement InsertBlankRowAfter(XElement anchor)
    {
        var row = new XElement(RowName, new XElement(CellName, new XAttribute(ColumnsRepeated, MaxColumns)));
        var style = anchor.Attribute(StyleName);
        if (style != null) row.SetAttributeValue(StyleName, style.Value);
        anchor.AddAfterSelf(row);
        ExpandCells(row);
        return row;
    }

    public void Save()
    {
        // Si lavora su una copia temporanea e la si sostituisce solo a fine scrittura,
        // così un errore a metà non corrompe il file sincronizzato su Drive.
        var temp = Path.Combine(Path.GetTempPath(), $"cinnati_{Guid.NewGuid():N}.ods");
        File.Copy(FilePath, temp, overwrite: true);
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Update))
            {
                zip.GetEntry("content.xml")?.Delete();
                var entry = zip.CreateEntry("content.xml", CompressionLevel.Optimal);
                using var stream = entry.Open();
                using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
                _content.Save(writer);
            }
            File.Copy(temp, FilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    // --- dettagli interni -------------------------------------------------------------

    static bool IsCell(XElement e) => e.Name == CellName || e.Name == CoveredCellName;

    static List<XElement> Cells(XElement row) => row.Elements().Where(IsCell).ToList();

    static int Repeat(XElement e, XName attribute) => (int?)e.Attribute(attribute) ?? 1;

    XElement Cell(XElement row, int col)
    {
        var cells = Cells(row);
        while (cells.Count <= col)
        {
            var cell = new XElement(CellName);
            row.Add(cell);
            cells.Add(cell);
        }
        return cells[col];
    }

    /// <summary>
    /// ODS comprime righe/celle uguali con number-*-repeated: le espandiamo nella zona utile
    /// così ogni riga e colonna corrisponde a un elemento XML indirizzabile.
    /// </summary>
    void ExpandRepeats()
    {
        int index = 0, lastContent = 0;
        foreach (var row in Rows)
        {
            int repeat = Repeat(row, RowsRepeated);
            if (row.Descendants(Paragraph).Any()) lastContent = index + repeat;
            index += repeat;
        }

        int limit = lastContent + ExtraRows;
        index = 0;
        foreach (var row in Rows)
        {
            if (index >= limit) break;
            int repeat = Repeat(row, RowsRepeated);
            Split(row, RowsRepeated, limit - index);
            index += repeat;
        }

        foreach (var row in Rows.Take(limit)) ExpandCells(row);
    }

    static void ExpandCells(XElement row)
    {
        int index = 0;
        foreach (var cell in Cells(row))
        {
            if (index >= MaxColumns) break;
            int repeat = Repeat(cell, ColumnsRepeated);
            Split(cell, ColumnsRepeated, MaxColumns - index);
            index += repeat;
        }
    }

    static void Split(XElement element, XName attribute, int singles)
    {
        int repeat = Repeat(element, attribute);
        if (repeat <= 1) return;
        int count = Math.Min(singles, repeat);
        element.Attribute(attribute)?.Remove();
        var last = element;
        for (int i = 1; i < count; i++)
        {
            var copy = new XElement(element);
            last.AddAfterSelf(copy);
            last = copy;
        }
        if (repeat > count)
        {
            var rest = new XElement(element);
            rest.SetAttributeValue(attribute, repeat - count);
            last.AddAfterSelf(rest);
        }
    }

    static void ClearCell(XElement cell)
    {
        cell.Elements(Paragraph).Remove();
        foreach (var attribute in ValueAttributes) cell.Attribute(attribute)?.Remove();
    }

    internal static string ReadText(XElement cell) => string.Join("\n", cell.Elements(Paragraph).Select(InlineText));

    static string InlineText(XElement element)
    {
        var sb = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            if (node is XText text) sb.Append(text.Value);
            else if (node is XElement child)
            {
                if (child.Name == TextNs + "s") sb.Append(' ', (int?)child.Attribute(TextNs + "c") ?? 1);
                else if (child.Name == TextNs + "tab") sb.Append('\t');
                else if (child.Name == TextNs + "line-break") sb.Append('\n');
                else sb.Append(InlineText(child));
            }
        }
        return sb.ToString();
    }

    /// <summary>Crea un text:p; gli spazi multipli vanno codificati con text:s per non essere collassati.</summary>
    static XElement BuildParagraph(string line)
    {
        var p = new XElement(Paragraph);
        var run = new StringBuilder();
        int i = 0;
        while (i < line.Length)
        {
            if (line[i] == ' ' && (i == 0 || (i + 1 < line.Length && line[i + 1] == ' ')))
            {
                int start = i;
                while (i < line.Length && line[i] == ' ') i++;
                int spaces = i - start;
                if (start > 0) { run.Append(' '); spaces--; }
                if (run.Length > 0) { p.Add(new XText(run.ToString())); run.Clear(); }
                if (spaces > 0) p.Add(new XElement(TextNs + "s", spaces > 1 ? new XAttribute(TextNs + "c", spaces) : null));
                continue;
            }
            run.Append(line[i++]);
        }
        if (run.Length > 0) p.Add(new XText(run.ToString()));
        return p;
    }
}
