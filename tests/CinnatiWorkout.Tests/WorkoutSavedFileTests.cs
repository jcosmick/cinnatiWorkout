using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using CinnatiWorkout.Ods;
using CinnatiWorkout.Printing;
using CinnatiWorkout.Workout;
using Xunit;

namespace CinnatiWorkout.Tests;

/// <summary>
/// File salvati dall'app: casi reali (file vecchi con titolo rosso e nota rosa, file risalvati da LibreOffice,
/// nota aggiunta dopo) e controlli che il file resti valido e completo.
/// </summary>
[Collection(LogoCollection.Name)]
public sealed class WorkoutSavedFileTests : IDisposable
{
    static readonly string TemplatePath = Path.Combine(AppContext.BaseDirectory, "Data", "scheda-allenamento.ods");
    static readonly XNamespace Style = OdsDocument.StyleNs, Fo = OdsDocument.FoNs, Table = OdsDocument.TableNs,
        Text = OdsDocument.TextNs, Office = OdsDocument.OfficeNs;

    readonly string _path = Path.Combine(Path.GetTempPath(), $"cinnati_saved_{Guid.NewGuid():N}.ods");

    public WorkoutSavedFileTests()
    {
        File.Copy(TemplatePath, _path);
        WorkoutPrintLayout.LogoSource = null;
    }

    public void Dispose() => File.Delete(_path);

    // --- strumenti -----------------------------------------------------------------------

    static XDocument Part(string path, string name)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry(name)!.Open();
        return XDocument.Load(stream);
    }

    /// <summary>Modifica content.xml del file come farebbe un altro programma.</summary>
    static void PatchContent(string path, Action<XDocument> change)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry("content.xml")!;
        XDocument content;
        using (var stream = entry.Open()) content = XDocument.Load(stream);
        change(content);
        entry.Delete();
        using var output = zip.CreateEntry("content.xml").Open();
        content.Save(output);
    }

    static (int Row, int Col) Find(Sheet sheet, string text) =>
        sheet.Cells.First(c => c.Value.Text.Contains(text, StringComparison.OrdinalIgnoreCase)).Key;

    static SheetCell Cell(Sheet sheet, string text)
    {
        var (r, c) = Find(sheet, text);
        return sheet.GetCell(r, c)!;
    }

    static List<string> Texts(Sheet sheet) =>
        sheet.Cells.Select(c => c.Value.Text.Trim()).Where(t => t.Length > 0).Order(StringComparer.Ordinal).ToList();

    static bool IsScheda(string text) => text.TrimStart().StartsWith("SCHEDA", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Il modello come i file vecchi (es. quello di Giada): titolo "PERSONAL TRAINER" rosa/rosso e una nota
    /// su sfondo rosa sotto l'intestazione.
    /// </summary>
    void MakeOldStyleFile()
    {
        PatchContent(_path, content =>
        {
            var titleCell = content.Descendants(Table + "table-cell").First(c => c.Value.Contains("PERSONAL TRAINER"));
            var auto = content.Root!.Element(Office + "automatic-styles")!;
            var red = new XElement(auto.Elements(Style + "style").First(s => (string?)s.Attribute(Style + "name") == (string?)titleCell.Attribute(Table + "style-name")));
            red.SetAttributeValue(Style + "name", "ceRosso");
            red.Element(Style + "text-properties")!.SetAttributeValue(Fo + "color", "#e16173");
            auto.Add(red);
            titleCell.SetAttributeValue(Table + "style-name", "ceRosso");

            auto.Add(new XElement(Style + "style",
                new XAttribute(Style + "name", "ceRosa"), new XAttribute(Style + "family", "table-cell"), new XAttribute(Style + "parent-style-name", "Default"),
                new XElement(Style + "table-cell-properties", new XAttribute(Fo + "background-color", "#f7d1d5"), new XAttribute(Fo + "wrap-option", "wrap"))));
            var heading = content.Descendants(Table + "table-row").First(r => r.Value.Contains("OTTOBRE - NOVEMBRE"));
            heading.AddAfterSelf(new XElement(Table + "table-row", new XAttribute(Table + "style-name", "ro1"),
                new XElement(Table + "table-cell", new XAttribute(Table + "style-name", "ceRosa"),
                    new XAttribute(Office + "value-type", "string"), new XAttribute(Table + "number-columns-spanned", 12),
                    new XElement(Text + "p", "Nota rosa")),
                new XElement(Table + "covered-table-cell", new XAttribute(Table + "number-columns-repeated", 11))));
        });
    }

    // --- colori ----------------------------------------------------------------------------

    [Fact]
    public void OldFile_SavedWithBlackTitleAndLightGreyNote()
    {
        MakeOldStyleFile();
        var before = OdsSheetReader.Read(_path)[0];
        Assert.NotEqual(System.Windows.Media.Colors.Black, Cell(before, "PERSONAL TRAINER").Format.Foreground);
        Assert.Equal(System.Windows.Media.Color.FromRgb(0xF7, 0xD1, 0xD5), Cell(before, "Nota rosa").Format.Background);

        var plan = WorkoutPlan.Load(_path);
        Assert.Equal("Nota rosa", plan.Note);
        plan.Save();

        var after = OdsSheetReader.Read(_path)[0];
        Assert.Equal(System.Windows.Media.Colors.Black, Cell(after, "PERSONAL TRAINER").Format.Foreground);
        Assert.Equal(SheetRenderer.NoteBackground, Cell(after, "Nota rosa").Format.Background);
        // Le altre celle colorate (intestazioni rosse delle tabelle) restano come sono.
        Assert.Equal(Cell(before, "ESERCIZIO").Format.Foreground, Cell(after, "ESERCIZIO").Format.Foreground);
    }

    [Fact]
    public void NoteAddedAfterFirstSave_IsEnlargedAndGrey()
    {
        var plan = WorkoutPlan.Load(_path);
        plan.Save();
        Assert.True(WorkoutPrintLayout.IsApplied(_path));

        plan.Note = "Riscaldamento 10 minuti";
        plan.Save();

        var sheet = OdsSheetReader.Read(_path)[0];
        var note = Cell(sheet, "Riscaldamento 10 minuti");
        Assert.Equal(SheetRenderer.NoteBackground, note.Format.Background);
        Assert.Equal(Units.FromPt(11) * 1.25, note.Format.FontSize, 1);

        // Salvando ancora non cresce più.
        WorkoutPlan.Load(_path).Save();
        Assert.Equal(note.Format.FontSize, Cell(OdsSheetReader.Read(_path)[0], "Riscaldamento 10 minuti").Format.FontSize, 2);
    }

    // --- stabilità ----------------------------------------------------------------------------

    [Fact]
    public void FileResavedByLibreOffice_IsNotEnlargedAgain()
    {
        WorkoutPlan.Load(_path).Save();
        var first = OdsSheetReader.Read(_path)[0];

        // LibreOffice, salvando, rinomina gli stili automatici: i nomi dati dall'app spariscono, i metadati restano.
        PatchContent(_path, content =>
        {
            foreach (var a in content.Descendants().Attributes().Where(a => a.Value.StartsWith("ceCw") || a.Value.StartsWith("roCw")).ToList())
                a.Value = a.Value[..2] + "Lo" + a.Value[4..];
        });
        WorkoutPlan.Load(_path).Save();
        var second = OdsSheetReader.Read(_path)[0];

        Assert.Equal(Cell(first, "PERSONAL TRAINER").Format.FontSize, Cell(second, "PERSONAL TRAINER").Format.FontSize, 2);
        Assert.Equal(Cell(first, "PLANK").Format.FontSize, Cell(second, "PLANK").Format.FontSize, 2);
        for (int r = first.PrintArea.Row1; r <= first.PrintArea.Row2; r++)
            Assert.Equal(first.RowHeight(r), second.RowHeight(r), 0);
    }

    [Fact]
    public void ManySaves_FileDoesNotGrow()
    {
        var plan = WorkoutPlan.Load(_path);
        plan.Save();
        long size = new FileInfo(_path).Length;
        int styles = Part(_path, "content.xml").Descendants(Style + "style").Count();
        for (int i = 0; i < 5; i++) plan.Save();

        Assert.Equal(styles, Part(_path, "content.xml").Descendants(Style + "style").Count());
        Assert.InRange(new FileInfo(_path).Length, size - 200, size + 200);
    }

    // --- contenuto e validità del file ------------------------------------------------------

    [Fact]
    public void Saving_KeepsEveryText()
    {
        var before = Texts(OdsSheetReader.Read(_path)[0]);
        WorkoutPlan.Load(_path).Save();
        Assert.Equal(before, Texts(OdsSheetReader.Read(_path)[0]));
    }

    [Fact]
    public void SavedFile_IsAValidOds()
    {
        WorkoutPrintLayout.LogoSource = () => System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, new byte[16], 8);
        try { WorkoutPlan.Load(_path).Save(); }
        finally { WorkoutPrintLayout.LogoSource = null; }

        using var zip = ZipFile.OpenRead(_path);
        // "mimetype" deve restare la prima voce, non compressa.
        Assert.Equal("mimetype", zip.Entries[0].FullName);
        Assert.Equal(zip.Entries[0].Length, zip.Entries[0].CompressedLength);
        foreach (var name in new[] { "content.xml", "styles.xml", "meta.xml", "META-INF/manifest.xml" })
        {
            using var stream = zip.GetEntry(name)!.Open();
            XDocument.Load(stream);
        }
        // Ogni file nel pacchetto dichiarato nel manifest (immagini comprese).
        var manifest = Part(_path, "META-INF/manifest.xml");
        XNamespace m = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
        var declared = manifest.Descendants(m + "file-entry").Select(e => (string?)e.Attribute(m + "full-path")).ToHashSet();
        Assert.Contains("Pictures/cinnati-logo.png", declared);
        Assert.Contains("meta.xml", declared);
    }

    // --- impaginazione scritta nel file ---------------------------------------------------------

    [Fact]
    public void PrintRange_WithSpacesInSheetName_IsReadBack()
    {
        var table = Part(_path, "content.xml").Descendants(Table + "table").First();
        Assert.Contains(' ', (string)table.Attribute(Table + "name")!);

        WorkoutPlan.Load(_path).Save();
        var sheet = OdsSheetReader.Read(_path)[0];
        Assert.NotNull(Part(_path, "content.xml").Descendants(Table + "table").First().Attribute(Table + "print-ranges"));
        Assert.Equal(sheet.UsedArea, sheet.PrintArea);
    }

    [Fact]
    public void PageBreaks_AreBeforeASchedaAndKeepTwoPerPage()
    {
        var plan = WorkoutPlan.Load(_path);
        plan.AddBlock();
        plan.AddBlock();
        plan.Save();

        var sheet = OdsSheetReader.Read(_path)[0];
        var schede = sheet.Cells.Where(c => IsScheda(c.Value.Text)).Select(c => c.Key.Row).Distinct().Order().ToList();
        Assert.Equal(5, schede.Count);
        // 5 schede → 3 fogli: interruzioni prima della 3ª e della 5ª scheda.
        Assert.Equal(new[] { schede[2], schede[4] }, sheet.RowBreaks.Order().ToArray());
    }

    [Fact]
    public void PageScale_MatchesTheAppPrint()
    {
        WorkoutPlan.Load(_path).Save();

        var printed = OdsSheetReader.Read(_path)[0];
        WorkoutPrintLayout.Prepare(printed, applied: true);
        double appScale = SheetRenderer.Paginate(printed)[0].Scale;

        var props = Part(_path, "styles.xml").Descendants(Style + "page-layout-properties").First(p => p.Attribute(Style + "scale-to") != null);
        double fileScale = double.Parse(((string)props.Attribute(Style + "scale-to")!).TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture) / 100;
        Assert.InRange(appScale - fileScale, 0, 0.01);

        // Con la scala del file ogni foglio sta nella pagina.
        double availH = printed.Page.Paper.Height - printed.Page.Margins.Top - printed.Page.Margins.Bottom;
        foreach (var chunk in SheetRenderer.PageChunks(printed))
            Assert.True((printed.RowY(chunk.Row2 + 1) - printed.RowY(chunk.Row1)) * fileScale <= availH);
    }

    [Fact]
    public void IsApplied_IsFalseForOriginalOrUnreadableFiles()
    {
        Assert.False(WorkoutPrintLayout.IsApplied(_path));
        Assert.False(WorkoutPrintLayout.IsApplied(Path.Combine(Path.GetTempPath(), "non-esiste.ods")));
        var broken = Path.Combine(Path.GetTempPath(), $"cinnati_rotto_{Guid.NewGuid():N}.ods");
        File.WriteAllText(broken, "non è uno zip");
        try { Assert.False(WorkoutPrintLayout.IsApplied(broken)); }
        finally { File.Delete(broken); }
    }
}

/// <summary>Test che impostano il logo (statico, condiviso): non devono girare in parallelo tra loro.</summary>
[CollectionDefinition(Name)]
public sealed class LogoCollection
{
    public const string Name = "Logo dei file salvati";
}
