using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using CinnatiWorkout.Ods;
using CinnatiWorkout.Printing;
using CinnatiWorkout.Workout;
using Xunit;

namespace CinnatiWorkout.Tests;

/// <summary>Il file salvato ha lo stesso layout della stampa dall'app, e lo mantiene ai salvataggi successivi.</summary>
[Collection(LogoCollection.Name)]
public sealed class WorkoutPrintLayoutTests : IDisposable
{
    static readonly string TemplatePath = Path.Combine(AppContext.BaseDirectory, "Data", "scheda-allenamento.ods");
    static readonly XNamespace Style = OdsDocument.StyleNs, Fo = OdsDocument.FoNs, Table = OdsDocument.TableNs;
    static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    readonly string _path = Path.Combine(Path.GetTempPath(), $"cinnati_test_{Guid.NewGuid():N}.ods");

    public WorkoutPrintLayoutTests()
    {
        File.Copy(TemplatePath, _path);
        WorkoutPrintLayout.LogoSource = null;
    }

    public void Dispose()
    {
        WorkoutPrintLayout.LogoSource = null;
        File.Delete(_path);
    }

    static Sheet ReadPrepared(string path)
    {
        var sheet = OdsSheetReader.Read(path)[0];
        WorkoutPrintLayout.Prepare(sheet, WorkoutPrintLayout.IsApplied(path));
        return sheet;
    }

    static (int Row, int Col) Find(Sheet sheet, string text) =>
        sheet.Cells.First(c => c.Value.Text.Contains(text, StringComparison.OrdinalIgnoreCase)).Key;

    static XDocument Part(string path, string name)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry(name)!.Open();
        return XDocument.Load(stream);
    }

    [Fact]
    public void SavedFile_PrintsLikeTheAppPrint()
    {
        // Stampa dall'app del modello originale.
        var expected = ReadPrepared(_path);

        var plan = WorkoutPlan.Load(_path);
        plan.Note = "Bere molta acqua";
        plan.Save();
        Assert.True(WorkoutPrintLayout.IsApplied(_path));

        // Il file salvato, letto così com'è (senza ingrandire), ha già testi e righe della stampa.
        var raw = OdsSheetReader.Read(_path)[0];
        var title = Find(expected, "PERSONAL TRAINER");
        Assert.Equal(expected.GetCell(title.Row, title.Col)!.Format.FontSize, raw.GetCell(title.Row, title.Col)!.Format.FontSize, 1);
        var scheda = Find(expected, "SCHEDA A");
        var rawScheda = Find(raw, "SCHEDA A");
        Assert.Equal(expected.GetCell(scheda.Row, scheda.Col)!.Format.FontSize, raw.GetCell(rawScheda.Row, rawScheda.Col)!.Format.FontSize, 1);

        // Nota su grigio chiaro.
        var note = Find(raw, "Bere molta acqua");
        Assert.Equal(SheetRenderer.NoteBackground, raw.GetCell(note.Row, note.Col)!.Format.Background);

        // Impostazioni di pagina della stampa: margini 1 cm, scala fissa, niente intestazione/piè di pagina, 2 schede per foglio.
        var styles = Part(_path, "styles.xml");
        var props = styles.Descendants(Style + "page-layout-properties").First(p => p.Attribute(Style + "scale-to") != null);
        Assert.Equal("1cm", (string?)props.Attribute(Fo + "margin-top"));
        Assert.Null(props.Attribute(Style + "scale-to-pages"));
        Assert.All(styles.Descendants(Style + "master-page").First(m => (string?)m.Attribute(Style + "name") == "Default")
            .Elements().Where(e => e.Name == Style + "header" || e.Name == Style + "footer"),
            e => Assert.Equal("false", (string?)e.Attribute(Style + "display")));

        var printed = ReadPrepared(_path);
        Assert.Equal(raw.UsedArea, printed.PrintArea);
        Assert.Equal(SheetRenderer.Paginate(printed).Count - 1, raw.RowBreaks.Count);
    }

    [Fact]
    public void SavingAgain_DoesNotEnlargeTwice_AndKeepsTheSameLayout()
    {
        var plan = WorkoutPlan.Load(_path);
        plan.Save();
        var first = OdsSheetReader.Read(_path)[0];

        plan = WorkoutPlan.Load(_path);
        plan.Blocks[0].Rows[0][0] = "Squat";
        plan.Save();
        plan.Save();
        var second = OdsSheetReader.Read(_path)[0];

        var title = Find(first, "PERSONAL TRAINER");
        Assert.Equal(first.GetCell(title.Row, title.Col)!.Format.FontSize, second.GetCell(title.Row, title.Col)!.Format.FontSize, 2);
        for (int r = first.PrintArea.Row1; r <= first.PrintArea.Row2; r++)
            Assert.Equal(first.RowHeight(r), second.RowHeight(r), 0);

        // La stampa dall'app del file salvato non cambia più nulla.
        var printed = ReadPrepared(_path);
        for (int r = second.PrintArea.Row1; r <= second.PrintArea.Row2; r++)
            Assert.Equal(second.RowHeight(r), printed.RowHeight(r), 0);
    }

    [Fact]
    public void BlockAddedAfterSaving_IsEnlargedLikeTheOthers()
    {
        var plan = WorkoutPlan.Load(_path);
        plan.Save();
        plan.AddBlock();
        plan.Blocks[^1].Rows[0][0] = "Panca piana";
        plan.Save();

        var sheet = OdsSheetReader.Read(_path)[0];
        var a = Find(sheet, "SCHEDA A");
        var last = sheet.Cells.Where(c => c.Value.Text.TrimStart().StartsWith("SCHEDA", StringComparison.OrdinalIgnoreCase)).MaxBy(c => c.Key.Row).Key;
        Assert.NotEqual(a, last);
        Assert.Equal(sheet.GetCell(a.Row, a.Col)!.Format.FontSize, sheet.GetCell(last.Row, last.Col)!.Format.FontSize, 2);
        var exercise = Find(sheet, "Panca piana");
        Assert.True(sheet.PrintArea.Contains(exercise.Row, exercise.Col));
    }

    [Fact]
    public void OldPictureWhereTheLogoGoes_IsReplaced()
    {
        // Un vecchio logo inserito a mano nell'angolo in alto a sinistra del file.
        using (var zip = ZipFile.Open(_path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("content.xml")!;
            XDocument content;
            using (var stream = entry.Open()) content = XDocument.Load(stream);
            var table = content.Descendants(Table + "table").First();
            table.AddFirst(new XElement(Table + "shapes", new XElement(Draw + "frame",
                new XAttribute(Draw + "name", "Immagine 4"),
                new XAttribute(Svg + "x", "0cm"), new XAttribute(Svg + "y", "0cm"),
                new XAttribute(Svg + "width", "2.5cm"), new XAttribute(Svg + "height", "2.8cm"),
                new XElement(Draw + "image", new XAttribute(XLink + "href", "Pictures/vecchio.jpg")))));
            entry.Delete();
            using var output = zip.CreateEntry("content.xml").Open();
            content.Save(output);
        }

        var logo = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 2 * 4], 4 * 4);
        WorkoutPrintLayout.LogoSource = () => logo;
        WorkoutPlan.Load(_path).Save();

        var frame = Assert.Single(Part(_path, "content.xml").Descendants(Draw + "frame"));
        Assert.Equal("CinnatiLogo", (string?)frame.Attribute(Draw + "name"));
    }

    [Fact]
    public void Logo_IsEmbedded_AndRemovedWhenUnset()
    {
        var logo = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 2 * 4], 4 * 4);
        WorkoutPrintLayout.LogoSource = () => logo;
        var plan = WorkoutPlan.Load(_path);
        plan.Save();

        using (var zip = ZipFile.OpenRead(_path)) Assert.NotNull(zip.GetEntry("Pictures/cinnati-logo.png"));
        var frame = Assert.Single(Part(_path, "content.xml").Descendants(Draw + "frame"));
        Assert.Equal("CinnatiLogo", (string?)frame.Attribute(Draw + "name"));

        WorkoutPrintLayout.LogoSource = null;
        plan.Save();
        using (var zip = ZipFile.OpenRead(_path)) Assert.Null(zip.GetEntry("Pictures/cinnati-logo.png"));
        Assert.Empty(Part(_path, "content.xml").Descendants(Draw + "frame"));
    }
}
