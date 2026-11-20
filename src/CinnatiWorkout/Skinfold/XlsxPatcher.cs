using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ClosedXML.Excel;

namespace CinnatiWorkout.Skinfold;

/// <summary>
/// Modifica i valori delle celle direttamente nell'XML di un .xlsx, senza toccare il resto
/// (immagini, gruppi di forme, impostazioni di stampa), che le librerie tendono a riscrivere male.
/// </summary>
sealed class XlsxPatcher : IDisposable
{
    static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace PackageRels = "http://schemas.openxmlformats.org/package/2006/relationships";
    static readonly Regex Address = new(@"^([A-Z]+)(\d+)$");

    readonly ZipArchive _zip;
    readonly Dictionary<string, string> _sheetParts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, XDocument> _changed = new();

    public XlsxPatcher(string path)
    {
        _zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var workbook = Read("xl/workbook.xml");
        var rels = Read("xl/_rels/workbook.xml.rels");
        var targets = rels.Root!.Elements(PackageRels + "Relationship")
            .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);
        foreach (var sheet in workbook.Root!.Element(Main + "sheets")!.Elements(Main + "sheet"))
        {
            var target = targets[(string)sheet.Attribute(RelNs + "id")!];
            _sheetParts[(string)sheet.Attribute("name")!] = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
        }
    }

    /// <summary>Valore inserito dall'utente: sostituisce il contenuto della cella.</summary>
    public void SetValue(string sheet, string address, XLCellValue value)
    {
        var cell = Cell(sheet, address);
        cell.Elements(Main + "f").Remove();
        WriteValue(cell, value, formula: false);
    }

    /// <summary>Risultato di una formula: aggiorna solo il valore memorizzato, la formula resta.</summary>
    public void SetCachedResult(string sheet, string address, XLCellValue value)
    {
        var cell = Cell(sheet, address);
        WriteValue(cell, value, formula: true);
    }

    /// <summary>Chiede a Excel di ricalcolare tutto all'apertura.</summary>
    public void ForceFullCalculationOnLoad()
    {
        var workbook = Load("xl/workbook.xml");
        var calc = workbook.Root!.Element(Main + "calcPr");
        if (calc == null)
        {
            calc = new XElement(Main + "calcPr");
            var anchor = workbook.Root.Element(Main + "definedNames") ?? workbook.Root.Element(Main + "sheets")!;
            anchor.AddAfterSelf(calc);
        }
        calc.SetAttributeValue("fullCalcOnLoad", "1");
    }

    public void Dispose()
    {
        foreach (var (part, doc) in _changed)
        {
            _zip.GetEntry(part)?.Delete();
            using var stream = _zip.CreateEntry(part, CompressionLevel.Optimal).Open();
            using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
            doc.Save(writer);
        }
        _zip.Dispose();
    }

    static void WriteValue(XElement cell, XLCellValue value, bool formula)
    {
        cell.Elements(Main + "v").Remove();
        cell.Elements(Main + "is").Remove();
        cell.Attribute("t")?.Remove();

        string? text = null, type = null;
        if (value.IsNumber) text = Num(value.GetNumber());
        else if (value.IsDateTime) text = Num(value.GetDateTime().ToOADate());
        else if (value.IsTimeSpan) text = Num(value.GetTimeSpan().TotalDays);
        else if (value.IsBoolean) (text, type) = (value.GetBoolean() ? "1" : "0", "b");
        else if (value.IsError) (text, type) = (value.ToString(), "e");
        else if (value.IsText)
        {
            if (formula) (text, type) = (value.GetText(), "str");
            else
            {
                cell.SetAttributeValue("t", "inlineStr");
                cell.Add(new XElement(Main + "is", new XElement(Main + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), value.GetText())));
                return;
            }
        }

        if (text == null) return;
        if (type != null) cell.SetAttributeValue("t", type);
        cell.Add(new XElement(Main + "v", text));
    }

    static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    XElement Cell(string sheet, string address)
    {
        var doc = Load(_sheetParts[sheet]);
        var data = doc.Root!.Element(Main + "sheetData")!;
        var match = Address.Match(address);
        int rowNumber = int.Parse(match.Groups[2].Value);
        int column = ColumnIndex(match.Groups[1].Value);

        var row = data.Elements(Main + "row").FirstOrDefault(r => (int)r.Attribute("r")! == rowNumber);
        if (row == null)
        {
            row = new XElement(Main + "row", new XAttribute("r", rowNumber));
            var next = data.Elements(Main + "row").FirstOrDefault(r => (int)r.Attribute("r")! > rowNumber);
            if (next != null) next.AddBeforeSelf(row); else data.Add(row);
        }

        var cell = row.Elements(Main + "c").FirstOrDefault(c => (string?)c.Attribute("r") == address);
        if (cell == null)
        {
            cell = new XElement(Main + "c", new XAttribute("r", address));
            var next = row.Elements(Main + "c").FirstOrDefault(c => ColumnIndex(Address.Match((string)c.Attribute("r")!).Groups[1].Value) > column);
            if (next != null) next.AddBeforeSelf(cell); else row.Add(cell);
        }
        return cell;
    }

    static int ColumnIndex(string letters) => letters.Aggregate(0, (acc, ch) => acc * 26 + (ch - 'A' + 1));

    XDocument Load(string part)
    {
        if (!_changed.TryGetValue(part, out var doc)) _changed[part] = doc = Read(part);
        return doc;
    }

    XDocument Read(string part)
    {
        var entry = _zip.GetEntry(part) ?? throw new InvalidDataException($"Parte {part} mancante nel file.");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    /// <summary>Numero di gruppi di forme nei disegni (le versioni 1.0/1.1 dell'app ne perdevano una parte).</summary>
    public static int CountShapeGroups(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        int count = 0;
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/drawings/drawing", StringComparison.OrdinalIgnoreCase)))
        {
            using var reader = new StreamReader(entry.Open());
            count += Regex.Matches(reader.ReadToEnd(), "<xdr:grpSp>").Count;
        }
        return count;
    }
}
