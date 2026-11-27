using System.Windows;
using CinnatiWorkout.Printing;
using Xunit;

namespace CinnatiWorkout.Tests;

/// <summary>Stampa delle schede allenamento: un foglio per scheda, testi e tabella ingranditi.</summary>
public class ReadablePrintTests
{
    static readonly string TemplatePath = System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "scheda-allenamento.ods");

    static Sheet Template() => OdsSheetReader.Read(TemplatePath)[0];

    /// <summary>Foglio sintetico: <paramref name="rows"/> righe × <paramref name="cols"/> colonne con testo e bordi.</summary>
    static Sheet Synthetic(int rows, int cols, double rowHeight = 20, double colWidth = 80, double fontPt = 11)
    {
        var sheet = new Sheet { Name = "Test" };
        sheet.Page.Paper = Units.A4;
        sheet.Page.Margins = new Thickness(Units.FromCm(2));
        sheet.SetColumnWidths(Enumerable.Repeat(colWidth, cols).ToList());
        sheet.SetRowHeights(Enumerable.Repeat(rowHeight, rows).ToList());
        var line = new BorderLine(1, System.Windows.Media.Colors.Black);
        var format = new CellFormat { FontSize = Units.FromPt(fontPt), Left = line, Top = line, Right = line, Bottom = line };
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                sheet.SetCell(r, c, new SheetCell { Text = $"R{r}C{c}", Format = format });
        sheet.PrintArea = new CellRange(0, 0, rows - 1, cols - 1);
        return sheet;
    }

    static double ContentWidth(Sheet s) => s.ColumnX(s.PrintArea.Col2 + 1) - s.ColumnX(s.PrintArea.Col1);
    static double ContentHeight(Sheet s) => s.RowY(s.PrintArea.Row2 + 1) - s.RowY(s.PrintArea.Row1);
    static double AvailableWidth(Sheet s) => s.Page.Paper.Width - s.Page.Margins.Left - s.Page.Margins.Right;
    static double AvailableHeight(Sheet s) => s.Page.Paper.Height - s.Page.Margins.Top - s.Page.Margins.Bottom;

    static void AssertFitsOnPaper(PrintPage page)
    {
        var s = page.Sheet;
        Assert.True(ContentWidth(s) * page.Scale <= AvailableWidth(s) + 0.5, "la tabella esce dal foglio in larghezza");
        double height = s.RowY(page.Range.Row2 + 1) - s.RowY(page.Range.Row1);
        Assert.True(height * page.Scale <= AvailableHeight(s) + 0.5, "la tabella esce dal foglio in altezza");
    }

    // --- modello reale -------------------------------------------------------------------

    [Fact]
    public void Template_PrintsAtMostTwoSchedePerPage_WithoutLosingRows()
    {
        var sheet = Template();
        SheetRenderer.PrepareReadablePrint(sheet);
        var pages = SheetRenderer.Paginate(sheet);

        Assert.All(pages, p => Assert.InRange(SchedeOn(p), 1, 2));
        Assert.All(pages, AssertFitsOnPaper);
        AssertCoversPrintAreaInOrder(sheet, pages);
    }

    [Fact]
    public void Template_NoSingleLineTextIsWiderThanTheTable()
    {
        var sheet = Template();
        SheetRenderer.PrepareReadablePrint(sheet);
        double tableWidth = ContentWidth(sheet);

        foreach (var (key, cell) in sheet.Cells)
        {
            if (cell.Text.Length == 0 || cell.Format.Wrap || cell.IsNumber) continue;
            Assert.True(TextWidth(cell.Text, cell.Format) <= tableWidth, $"testo in {key} più largo della tabella");
        }
    }

    [Fact]
    public void Template_IsPrintedLargerThanBefore()
    {
        var before = SheetRenderer.Paginate(Template())[0];
        var sheet = Template();
        SheetRenderer.PrepareReadablePrint(sheet);
        var after = SheetRenderer.Paginate(sheet)[0];

        Assert.True(after.Scale > before.Scale, $"scala {after.Scale:0.000} non maggiore di {before.Scale:0.000}");
        // Larghezza stampata della tabella e altezza occupata sulla carta crescono entrambe.
        Assert.True(ContentWidth(sheet) * after.Scale > ContentWidth(before.Sheet) * before.Scale);
        Assert.True(ContentHeight(sheet) * after.Scale > ContentHeight(before.Sheet) * before.Scale);
    }

    [Fact]
    public void Template_TextOnPaperIsLarger()
    {
        var original = Template();
        double beforeScale = SheetRenderer.Paginate(original)[0].Scale;
        var sizesBefore = original.Cells.ToDictionary(c => c.Key, c => c.Value.Format.FontSize * beforeScale);

        var sheet = Template();
        SheetRenderer.PrepareReadablePrint(sheet);
        double afterScale = SheetRenderer.Paginate(sheet)[0].Scale;

        foreach (var (key, cell) in sheet.Cells.Where(c => c.Value.Text.Length > 0))
            Assert.True(cell.Format.FontSize * afterScale > sizesBefore[key] * 1.25, $"testo in {key} non abbastanza ingrandito");
    }

    // --- preparazione --------------------------------------------------------------------

    [Fact]
    public void Prepare_ScalesFontsBy25Percent_AndKeepsText()
    {
        var sheet = Synthetic(5, 4, fontPt: 10);
        SheetRenderer.PrepareReadablePrint(sheet);

        Assert.All(sheet.Cells, c => Assert.Equal(Units.FromPt(12.5), c.Value.Format.FontSize, 6));
        Assert.Equal("R2C3", sheet.GetCell(2, 3)!.Text);
    }

    [Fact]
    public void Prepare_CustomTextScale_IsApplied()
    {
        var sheet = Synthetic(5, 4, fontPt: 10);
        SheetRenderer.PrepareReadablePrint(sheet, textScale: 1.1);
        Assert.All(sheet.Cells, c => Assert.Equal(Units.FromPt(11), c.Value.Format.FontSize, 6));
    }

    [Fact]
    public void Prepare_ReducesMarginsTo1Cm_ButNeverEnlargesThem()
    {
        var sheet = Synthetic(5, 4);
        sheet.Page.Margins = new Thickness(Units.FromCm(2), Units.FromCm(0.5), Units.FromCm(2), Units.FromCm(2));
        SheetRenderer.PrepareReadablePrint(sheet);

        Assert.Equal(Units.FromCm(1), sheet.Page.Margins.Left, 6);
        Assert.Equal(Units.FromCm(0.5), sheet.Page.Margins.Top, 6);
        Assert.Equal(Units.FromCm(1), sheet.Page.Margins.Right, 6);
        Assert.Equal(Units.FromCm(1), sheet.Page.Margins.Bottom, 6);
        Assert.True(sheet.Page.FitToOnePage);
    }

    [Fact]
    public void Prepare_GrowsRowsSoEnlargedTextFits()
    {
        // Righe basse (12 px) con testo da 11 pt: ingrandito non ci starebbe più.
        var sheet = Synthetic(60, 4, rowHeight: 12);
        SheetRenderer.PrepareReadablePrint(sheet);

        for (int r = 0; r <= sheet.PrintArea.Row2; r++)
            Assert.True(sheet.RowHeight(r) + 0.01 >= SheetRenderer.MeasureRowHeight(sheet, r), $"riga {r} troppo bassa per il testo");
    }

    [Fact]
    public void Prepare_StretchesRowsToFillHeight_WhenWidthIsTheLimit()
    {
        // Tabella larga e corta: avanza molta altezza, le righe si allungano al massimo consentito.
        var sheet = Synthetic(10, 8, rowHeight: 40);
        SheetRenderer.PrepareReadablePrint(sheet, maxRowStretch: 1.35);

        Assert.All(Enumerable.Range(0, 10), r => Assert.Equal(40 * 1.35, sheet.RowHeight(r), 6));
        AssertFitsOnPaper(Assert.Single(SheetRenderer.Paginate(sheet)));
    }

    [Fact]
    public void Prepare_StretchNeverPushesContentOffThePage()
    {
        // Avanza solo un po' di altezza: le righe si allungano meno del massimo e la tabella resta nel foglio.
        // 21 righe × 40 px su 8 colonne × 80 px: a tutta larghezza (margini 1 cm) occupa circa il 90% dell'altezza utile.
        var sheet = Synthetic(21, 8, rowHeight: 40);
        SheetRenderer.PrepareReadablePrint(sheet, maxRowStretch: 1.35);

        double stretch = sheet.RowHeight(0) / 40;
        Assert.InRange(stretch, 1.01, 1.34);
        var page = Assert.Single(SheetRenderer.Paginate(sheet));
        AssertFitsOnPaper(page);
        // Resta limitata dalla larghezza (riempita tutta) e l'altezza è quasi tutta usata.
        Assert.True(ContentWidth(sheet) * page.Scale > AvailableWidth(sheet) * 0.99);
        Assert.True(ContentHeight(sheet) * page.Scale > AvailableHeight(sheet) * 0.98);
    }

    [Fact]
    public void Prepare_DoesNotStretchRows_WhenHeightIsTheLimit()
    {
        // Tabella stretta e lunghissima: nessuno spazio in avanzo, le righe restano come sono.
        var sheet = Synthetic(150, 2, rowHeight: 30);
        SheetRenderer.PrepareReadablePrint(sheet);

        Assert.All(Enumerable.Range(0, 150), r => Assert.Equal(30, sheet.RowHeight(r), 6));
    }

    [Fact]
    public void Prepare_KeepsHiddenRowsHidden()
    {
        var sheet = Synthetic(10, 4);
        var heights = Enumerable.Range(0, 10).Select(r => r == 3 ? 0.0 : 20.0).ToList();
        sheet.SetRowHeights(heights);
        SheetRenderer.PrepareReadablePrint(sheet);

        Assert.Equal(0, sheet.RowHeight(3));
    }

    // --- impaginazione -------------------------------------------------------------------

    [Fact]
    public void LongSheet_FitToOnePage_StaysOnASingleSheetOfPaper()
    {
        var sheet = Synthetic(200, 6);
        SheetRenderer.PrepareReadablePrint(sheet);

        var page = Assert.Single(SheetRenderer.Paginate(sheet));
        Assert.Equal(sheet.PrintArea, page.Range);
        AssertFitsOnPaper(page);
        // Limitata dall'altezza: riempie quasi tutta l'altezza utile.
        Assert.True(ContentHeight(sheet) * page.Scale > AvailableHeight(sheet) * 0.99);
    }

    [Fact]
    public void LongSheet_FitToOnePage_IgnoresManualRowBreaks()
    {
        var sheet = Synthetic(40, 6);
        sheet.RowBreaks.Add(20);
        SheetRenderer.PrepareReadablePrint(sheet);

        Assert.Single(SheetRenderer.Paginate(sheet));
    }

    [Fact]
    public void LongSheet_WithoutFitToOnePage_StillContinuesOnNextPages()
    {
        // Comportamento invariato per gli altri fogli (es. pliche): l'altezza continua sulle pagine successive.
        var sheet = Synthetic(200, 6);
        var pages = SheetRenderer.Paginate(sheet);

        Assert.True(pages.Count > 1);
        Assert.Equal(0, pages[0].Range.Row1);
        Assert.Equal(199, pages[^1].Range.Row2);
    }

    [Fact]
    public void LandscapePaper_FitToOnePage_FitsToo()
    {
        var sheet = Synthetic(80, 10);
        sheet.Page.Paper = new Size(Units.A4.Height, Units.A4.Width);
        SheetRenderer.PrepareReadablePrint(sheet);

        AssertFitsOnPaper(Assert.Single(SheetRenderer.Paginate(sheet)));
    }

    // --- al massimo 2 schede per facciata ---------------------------------------------------

    /// <summary>Intestazione di 3 righe, poi <paramref name="schede"/> blocchi da 11 righe che iniziano con "SCHEDA X:".</summary>
    static Sheet WithSchede(int schede)
    {
        var sheet = Synthetic(3 + schede * 11, 6, rowHeight: 30);
        sheet.SetCell(0, 0, new SheetCell { Text = "PERSONAL TRAINER" });
        for (int i = 0; i < schede; i++)
            sheet.SetCell(3 + i * 11, 0, new SheetCell { Text = $"SCHEDA {(char)('A' + i)}:" });
        return sheet;
    }

    static int SchedeOn(PrintPage page) => page.Sheet.Cells.Count(c =>
        page.Range.Contains(c.Key.Row, c.Key.Col) && c.Value.Text.TrimStart().StartsWith("SCHEDA", StringComparison.OrdinalIgnoreCase));

    static void AssertCoversPrintAreaInOrder(Sheet sheet, List<PrintPage> pages)
    {
        Assert.Equal(sheet.PrintArea.Row1, pages[0].Range.Row1);
        Assert.Equal(sheet.PrintArea.Row2, pages[^1].Range.Row2);
        for (int i = 1; i < pages.Count; i++) Assert.Equal(pages[i - 1].Range.Row2 + 1, pages[i].Range.Row1);
        Assert.All(pages, p => Assert.Equal((sheet.PrintArea.Col1, sheet.PrintArea.Col2), (p.Range.Col1, p.Range.Col2)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    public void Schede_AreGroupedTwoPerPage(int schede, int expectedPages)
    {
        var sheet = WithSchede(schede);
        SheetRenderer.PrepareReadablePrint(sheet);
        var pages = SheetRenderer.Paginate(sheet);

        Assert.Equal(expectedPages, pages.Count);
        Assert.All(pages, p => Assert.InRange(SchedeOn(p), 1, 2));
        Assert.All(pages, AssertFitsOnPaper);
        AssertCoversPrintAreaInOrder(sheet, pages);
    }

    [Fact]
    public void Schede_HeaderStaysWithFirstPage_AndLaterPagesStartAtAScheda()
    {
        var sheet = WithSchede(3);
        SheetRenderer.PrepareReadablePrint(sheet);
        var pages = SheetRenderer.Paginate(sheet);

        Assert.Equal(0, pages[0].Range.Row1);
        Assert.Equal("SCHEDA C:", sheet.GetCell(pages[1].Range.Row1, 0)!.Text);
    }

    [Fact]
    public void Schede_AllPagesUseTheSameScale()
    {
        var sheet = WithSchede(3);
        SheetRenderer.PrepareReadablePrint(sheet);
        Assert.Single(SheetRenderer.Paginate(sheet).Select(p => p.Scale).Distinct());
    }

    [Fact]
    public void Schede_CustomLimit_IsApplied()
    {
        var sheet = WithSchede(3);
        SheetRenderer.PrepareReadablePrint(sheet, blocksPerPage: 1);
        Assert.Equal(3, SheetRenderer.Paginate(sheet).Count);
    }

    [Fact]
    public void Schede_LabelIsCaseInsensitive_ButNotMatchedInsideOtherText()
    {
        var sheet = WithSchede(3);
        sheet.SetCell(14, 0, new SheetCell { Text = "  scheda b" });
        sheet.SetCell(5, 1, new SheetCell { Text = "VEDI SCHEDA A" });
        SheetRenderer.PrepareReadablePrint(sheet);
        Assert.Equal(2, SheetRenderer.Paginate(sheet).Count);
    }

    // --- testi che non devono essere tagliati -------------------------------------------

    static Sheet WithMergedNote(int cols, string text, bool bold = false)
    {
        var sheet = Synthetic(10, cols, colWidth: 80);
        var format = new CellFormat { FontSize = Units.FromPt(11), Bold = bold, HAlign = HAlign.Center };
        sheet.SetCell(2, 0, new SheetCell { Text = text, Format = format });
        for (int c = 1; c < cols; c++) sheet.SetCell(2, c, new SheetCell { Format = format });
        sheet.AddMerge(new CellRange(2, 0, 2, cols - 1));
        return sheet;
    }

    [Fact]
    public void LongNoteInMergedRow_IsShrunkToFit_InsteadOfBeingCut()
    {
        // Come "ALL'INIZIO SEMPRE: FOAM ROLLER, …": riga unita larga quanto la tabella,
        // ci sta alla dimensione originale ma non ingrandita del 25%.
        const string text = "ALL'INIZIO SEMPRE: FOAM ROLLER, MOBILITA ANCHE, GAMBE, SPALLE, CAT AND COW";
        var sheet = WithMergedNote(7, text, bold: true);
        double room = 7 * 80 - 2 * CellFormat.Empty.PaddingX;
        double original = TextWidth(text, sheet.GetCell(2, 0)!.Format);
        Assert.True(original < room && original * 1.25 > room, $"dati di prova non validi ({original:0} su {room:0})");

        SheetRenderer.PrepareReadablePrint(sheet);
        var prepared = sheet.GetCell(2, 0)!;

        Assert.False(prepared.Format.Wrap);
        Assert.True(prepared.Format.FontSize >= Units.FromPt(11), "mai più piccola dell'originale");
        Assert.True(TextWidth(text, prepared.Format) <= room, "la nota viene ancora tagliata");
    }

    [Fact]
    public void NoteTooLongEvenAtOriginalSize_WrapsAndRowGrows()
    {
        var sheet = WithMergedNote(3, "ALL'INIZIO SEMPRE: FOAM ROLLER, MOBILITA ANCHE, GAMBE, SPALLE, CAT AND COW 1X10, COBRA POSITION 1X10");
        double rowBefore = sheet.RowHeight(2);

        SheetRenderer.PrepareReadablePrint(sheet);

        Assert.True(sheet.GetCell(2, 0)!.Format.Wrap);
        Assert.True(sheet.RowHeight(2) > rowBefore * 1.4, "la riga non si è alzata per le righe di testo in più");
        Assert.True(sheet.RowHeight(2) + 0.01 >= SheetRenderer.MeasureRowHeight(sheet, 2));
    }

    [Fact]
    public void TextOverflowingIntoEmptyNeighbours_IsLeftAlone()
    {
        // Testo lungo allineato a sinistra con celle vuote accanto: sconfina come in Calc, nessuna riduzione.
        var sheet = Synthetic(5, 6);
        var format = new CellFormat { FontSize = Units.FromPt(10) };
        sheet.SetCell(1, 0, new SheetCell { Text = "NOTA: forza e mobilita ogni giorno", Format = format });
        for (int c = 1; c < 6; c++) sheet.SetCell(1, c, new SheetCell { Format = format });

        SheetRenderer.PrepareReadablePrint(sheet);
        var prepared = sheet.GetCell(1, 0)!;

        Assert.False(prepared.Format.Wrap);
        Assert.Equal(Units.FromPt(12.5), prepared.Format.FontSize, 6);
    }

    static double TextWidth(string text, CellFormat format) =>
        new System.Windows.Media.FormattedText(text.Replace("\r", ""), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily(format.FontFamily),
                format.Italic ? FontStyles.Italic : FontStyles.Normal, format.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            format.FontSize, System.Windows.Media.Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;

    // --- documento per la stampante ------------------------------------------------------

    [Fact]
    public void CreateDocument_HasOnePagePerSheet() => RunSta(() =>
    {
        var sheets = new[] { Template(), Synthetic(200, 6) };
        foreach (var s in sheets) SheetRenderer.PrepareReadablePrint(s);
        var pages = sheets.SelectMany(SheetRenderer.Paginate).ToList();

        var document = SheetRenderer.CreateDocument(pages, Units.A4);
        Assert.Equal(pages.Count, document.Pages.Count);
        Assert.True(pages.Count >= 2);
    });

    /// <summary>I documenti WPF (FixedDocument, FixedPage) richiedono un thread STA.</summary>
    static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new Exception(error.Message, error);
    }
}
