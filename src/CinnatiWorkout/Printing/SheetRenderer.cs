using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CinnatiWorkout.Skinfold;

namespace CinnatiWorkout.Printing;

/// <summary>Una pagina stampata: una porzione (righe × colonne) dell'area di stampa di un foglio, con la sua scala.</summary>
public sealed record PrintPage(Sheet Sheet, CellRange Range, double Scale)
{
    public Size Paper => Sheet.Page.Paper;
}

/// <summary>Divide i fogli in pagine e le disegna come farebbe il foglio di calcolo (celle, bordi, testo, immagini).</summary>
public static class SheetRenderer
{
    static CultureInfo Culture => Num.Culture;

    // --- impaginazione -------------------------------------------------------------------

    public static List<PrintPage> Paginate(Sheet sheet)
    {
        var area = sheet.PrintArea;
        var page = sheet.Page;
        double availW = Math.Max(10, page.Paper.Width - page.Margins.Left - page.Margins.Right);
        double availH = Math.Max(10, page.Paper.Height - page.Margins.Top - page.Margins.Bottom);
        double contentW = sheet.ColumnX(area.Col2 + 1) - sheet.ColumnX(area.Col1);

        // La stampa occupa sempre tutta la larghezza utile tra i margini: le colonne vengono ingrandite o ridotte
        // per riempire esattamente le pagine in larghezza (una, salvo "adatta a N pagine in larghezza" nel file).
        // L'altezza segue la stessa scala e, se serve, continua sulle pagine successive.
        if (page.FitToOnePage)
        {
            // Ogni gruppo di schede su un solo foglio, tutti con la stessa scala: la più grande con cui
            // il gruppo più alto ci sta sia in larghezza sia in altezza.
            var chunks = PageChunks(sheet);
            double tallest = chunks.Max(c => sheet.RowY(c.Row2 + 1) - sheet.RowY(c.Row1));
            double fit = Math.Min(contentW > 0 ? availW / contentW : page.Scale, tallest > 0 ? availH / tallest : page.Scale);
            fit = Math.Clamp(fit, 0.1, 4);
            return chunks.Select(c => new PrintPage(sheet, c, fit)).ToList();
        }

        int pagesWide = Math.Max(1, page.FitWide);
        double scale = contentW > 0 ? availW * pagesWide / contentW : page.Scale;
        scale = Math.Clamp(scale, 0.1, 4);
        // Su più pagine in larghezza la divisione per colonne intere può non bastare: si riduce finché ci sta.
        var columns = Split(area.Col1, area.Col2, availW / scale, sheet.ColumnWidth, sheet.ColumnBreaks);
        while (pagesWide > 1 && columns.Count > pagesWide && scale > 0.1)
        {
            scale *= 0.98;
            columns = Split(area.Col1, area.Col2, availW / scale, sheet.ColumnWidth, sheet.ColumnBreaks);
        }
        var rows = Split(area.Row1, area.Row2, availH / scale, sheet.RowHeight, sheet.RowBreaks);

        var pages = new List<PrintPage>();
        if (page.DownThenOver)
        {
            foreach (var (c1, c2) in columns)
                foreach (var (r1, r2) in rows)
                    pages.Add(new PrintPage(sheet, new CellRange(r1, c1, r2, c2), scale));
        }
        else
        {
            foreach (var (r1, r2) in rows)
                foreach (var (c1, c2) in columns)
                    pages.Add(new PrintPage(sheet, new CellRange(r1, c1, r2, c2), scale));
        }
        return pages;
    }

    /// <summary>
    /// Righe dell'area di stampa da mettere su ciascun foglio: gruppi di al massimo <see cref="PageSetup.BlocksPerPage"/>
    /// schede (una scheda inizia alla riga con "SCHEDA …"). L'intestazione sta con il primo gruppo,
    /// quel che segue l'ultima scheda con l'ultimo. Senza schede riconosciute: tutta l'area su un foglio.
    /// </summary>
    public static List<CellRange> PageChunks(Sheet sheet)
    {
        var area = sheet.PrintArea;
        int perPage = sheet.Page.BlocksPerPage;
        var starts = perPage > 0 ? BlockStartRows(sheet) : [];
        if (starts.Count <= perPage || perPage <= 0) return [area];

        var chunks = new List<CellRange>();
        for (int i = 0; i < starts.Count; i += perPage)
        {
            int first = i == 0 ? area.Row1 : starts[i];
            int last = i + perPage < starts.Count ? starts[i + perPage] - 1 : area.Row2;
            chunks.Add(new CellRange(first, area.Col1, last, area.Col2));
        }
        return chunks;
    }

    static readonly System.Text.RegularExpressions.Regex BlockLabel =
        new(@"^\s*SCHEDA\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Righe (nell'area di stampa) in cui inizia una scheda: quelle con una cella che comincia con "SCHEDA".</summary>
    static List<int> BlockStartRows(Sheet sheet)
    {
        var area = sheet.PrintArea;
        return sheet.Cells
            .Where(c => area.Contains(c.Key.Row, c.Key.Col) && sheet.RowHeight(c.Key.Row) > 0 && BlockLabel.IsMatch(c.Value.Text))
            .Select(c => c.Key.Row)
            .Distinct()
            .Order()
            .ToList();
    }

    /// <summary>
    /// Prepara una scheda allenamento per una stampa più leggibile: al massimo <paramref name="blocksPerPage"/> schede
    /// per foglio, ogni gruppo intero sul suo foglio; margini ridotti, testi più grandi (le righe si allungano se il
    /// testo non ci sta più) e, se avanza spazio in altezza, righe allungate per riempire la pagina.
    /// </summary>
    public static void PrepareReadablePrint(Sheet sheet, double textScale = 1.25, double maxRowStretch = 1.35, int blocksPerPage = 2)
    {
        var area = sheet.PrintArea;
        var page = sheet.Page;
        page.FitToOnePage = true;
        page.BlocksPerPage = blocksPerPage;
        double margin = Units.FromCm(1);
        page.Margins = new Thickness(Math.Min(page.Margins.Left, margin), Math.Min(page.Margins.Top, margin),
            Math.Min(page.Margins.Right, margin), Math.Min(page.Margins.Bottom, margin));

        // Testi più grandi (stessa proporzione tra titoli e celle).
        var scaled = new Dictionary<CellFormat, CellFormat>();
        foreach (var (key, cell) in sheet.Cells.ToList())
        {
            if (!scaled.TryGetValue(cell.Format, out var format))
                scaled[cell.Format] = format = cell.Format with { FontSize = cell.Format.FontSize * textScale };
            sheet.SetCell(key.Row, key.Col, new SheetCell { Text = cell.Text, IsNumber = cell.IsNumber, IsBoolean = cell.IsBoolean, Format = format });
        }

        // Un testo su una riga sola che, ingrandito, non sta più nello spazio a disposizione verrebbe tagliato:
        // si riduce quanto basta (non sotto la dimensione originale), altrimenti va a capo.
        foreach (var (key, cell) in sheet.Cells.ToList())
        {
            var format = cell.Format;
            if (cell.Text.Length == 0 || format.Wrap || format.ShrinkToFit || !area.Contains(key.Row, key.Col)) continue;
            if (sheet.MergeAt(key.Row, key.Col) is { } m && (m.Row1 != key.Row || m.Col1 != key.Col)) continue;
            double room = TextRoom(sheet, key.Row, key.Col, cell);
            double width = BuildText(cell.Text, format, format.FontSize).WidthIncludingTrailingWhitespace;
            if (width <= room) continue;
            double fitting = format.FontSize * room / width * 0.98;
            format = fitting >= format.FontSize / textScale
                ? format with { FontSize = fitting }
                : format with { Wrap = true };
            sheet.SetCell(key.Row, key.Col, new SheetCell { Text = cell.Text, IsNumber = cell.IsNumber, IsBoolean = cell.IsBoolean, Format = format });
        }

        // Righe abbastanza alte per il testo ingrandito.
        var heights = new List<double>();
        for (int r = 0; r <= area.Row2; r++) heights.Add(sheet.RowHeight(r));
        for (int r = area.Row1; r <= area.Row2; r++)
            if (heights[r] > 0) heights[r] = Math.Max(heights[r], MeasureRowHeight(sheet, r));
        sheet.SetRowHeights(heights);

        // Se la pagina è limitata dalla larghezza, l'altezza che avanza (sul foglio più pieno) allunga le righe in proporzione.
        double availW = page.Paper.Width - page.Margins.Left - page.Margins.Right;
        double availH = page.Paper.Height - page.Margins.Top - page.Margins.Bottom;
        double contentW = sheet.ColumnX(area.Col2 + 1) - sheet.ColumnX(area.Col1);
        double tallest = PageChunks(sheet).Max(c => sheet.RowY(c.Row2 + 1) - sheet.RowY(c.Row1));
        if (contentW <= 0 || tallest <= 0) return;
        double stretch = Math.Min(maxRowStretch, availH / tallest / (availW / contentW) * 0.995);
        if (stretch <= 1.01) return;
        for (int r = area.Row1; r <= area.Row2; r++) heights[r] *= stretch;
        sheet.SetRowHeights(heights);
    }

    /// <summary>
    /// Larghezza disponibile per il testo di una cella su una riga sola: la cella (o l'unione) meno i margini interni,
    /// più le celle vuote accanto in cui il testo può sconfinare (non per i numeri), entro l'area di stampa.
    /// </summary>
    static double TextRoom(Sheet sheet, int row, int col, SheetCell cell)
    {
        var area = sheet.PrintArea;
        var merge = sheet.MergeAt(row, col) ?? new CellRange(row, col, row, col);
        var format = cell.Format;
        double own = sheet.ColumnX(merge.Col2 + 1) - sheet.ColumnX(merge.Col1) - 2 * format.PaddingX - format.Indent;
        if (cell.IsNumber || merge.Row1 != merge.Row2) return own;

        bool Free(int c) => sheet.MergeAt(row, c) == null && (sheet.GetCell(row, c)?.Text.Length ?? 0) == 0;
        double right = 0, left = 0;
        for (int c = merge.Col2 + 1; c <= area.Col2 && Free(c); c++) right += sheet.ColumnWidth(c);
        for (int c = merge.Col1 - 1; c >= area.Col1 && Free(c); c--) left += sheet.ColumnWidth(c);
        return EffectiveAlign(cell) switch
        {
            HAlign.Center => own + 2 * Math.Min(left, right),
            HAlign.Right => own + left,
            HAlign.Justify => own,
            _ => own + right,
        };
    }

    static List<(int First, int Last)> Split(int first, int last, double available, Func<int, double> size, HashSet<int> breaks)
    {
        var result = new List<(int, int)>();
        int start = first;
        double used = 0;
        for (int i = first; i <= last; i++)
        {
            double s = size(i);
            if (i > start && (used + s > available + 0.01 || breaks.Contains(i)))
            {
                result.Add((start, i - 1));
                start = i;
                used = 0;
            }
            used += s;
        }
        result.Add((start, last));
        return result;
    }

    /// <summary>
    /// Documento da mandare alla stampante: una pagina per foglio di carta delle dimensioni indicate
    /// (quelle scelte nella finestra di stampa); le pagine con orientamento diverso vengono ruotate.
    /// </summary>
    public static FixedDocument CreateDocument(IEnumerable<PrintPage> pages, Size paper)
    {
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = paper;
        foreach (var page in pages)
        {
            var fixedPage = new FixedPage { Width = paper.Width, Height = paper.Height, Background = Brushes.White };
            fixedPage.Children.Add(new PrintPageElement(page, paper));
            var content = new PageContent();
            ((IAddChild)content).AddChild(fixedPage);
            document.Pages.Add(content);
        }
        return document;
    }

    // --- disegno -------------------------------------------------------------------------

    /// <summary>Disegna la pagina intera (carta bianca compresa) nelle coordinate della carta, origine in alto a sinistra.</summary>
    public static void DrawPage(DrawingContext dc, PrintPage page)
    {
        var sheet = page.Sheet;
        var setup = sheet.Page;
        var range = page.Range;
        dc.DrawRectangle(Brushes.White, null, new Rect(page.Paper));

        double availW = setup.Paper.Width - setup.Margins.Left - setup.Margins.Right;
        var region = sheet.CellRect(range);
        // Il contenuto parte sempre dal margine superiore e scende verso il basso (mai centrato in verticale).
        double x = setup.Margins.Left, y = setup.Margins.Top;
        if (setup.CenterHorizontally) x += Math.Max(0, (availW - region.Width * page.Scale) / 2);

        dc.PushTransform(new TranslateTransform(x, y));
        dc.PushTransform(new ScaleTransform(page.Scale, page.Scale));
        dc.PushTransform(new TranslateTransform(-region.X, -region.Y));
        // Un piccolo margine lascia visibile per intero lo spessore dei bordi esterni.
        var clip = region;
        clip.Inflate(2, 2);
        dc.PushClip(new RectangleGeometry(clip));

        DrawBackgrounds(dc, sheet, range);
        DrawTexts(dc, sheet, range, region);
        DrawBorders(dc, sheet, range);
        dc.PushClip(new RectangleGeometry(region));
        foreach (var picture in sheet.Pictures)
            if (picture.Bounds.IntersectsWith(region) && picture.Bounds.Width > 0 && picture.Bounds.Height > 0)
                dc.DrawImage(picture.Image, picture.Bounds);
        if (sheet.Logo != null && range.Row1 == sheet.PrintArea.Row1 && range.Col1 == sheet.PrintArea.Col1)
            DrawLogo(dc, sheet, sheet.Logo);
        dc.Pop();

        dc.Pop();
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    /// <summary>
    /// Logo nell'angolo in alto a sinistra dell'area di stampa, nello spazio libero prima dell'intestazione:
    /// in larghezza fino alla prima cella scritta della prima riga, in altezza fino alla prima cella scritta
    /// della prima colonna (nella scheda: colonne A–B accanto a "… PERSONAL TRAINER", righe 1–5).
    /// L'immagine mantiene le proporzioni ed è allineata in alto a sinistra.
    /// </summary>
    static void DrawLogo(DrawingContext dc, Sheet sheet, BitmapSource logo)
    {
        var area = sheet.PrintArea;
        bool HasText(int r, int c) => (sheet.GetCell(r, c)?.Text.Length ?? 0) > 0
                                      || (sheet.MergeAt(r, c) is { } m && (sheet.GetCell(m.Row1, m.Col1)?.Text.Length ?? 0) > 0);

        double left = sheet.ColumnX(area.Col1), top = sheet.RowY(area.Row1);
        int col = area.Col1;
        while (col <= area.Col2 && !HasText(area.Row1, col)) col++;
        int row = area.Row1;
        while (row <= area.Row2 && !HasText(row, area.Col1)) row++;

        // Spazio libero assente o eccessivo (foglio diverso dal modello): riquadro di misura fissa.
        double maxW = Units.FromCm(6), maxH = Units.FromCm(3);
        double width = col > area.Col1 && col <= area.Col2 ? sheet.ColumnX(col) - left : Units.FromCm(4);
        double height = row > area.Row1 && row <= area.Row2 ? sheet.RowY(row) - top : Units.FromCm(2.5);
        double pad = Units.FromCm(0.1);
        var box = new Rect(left + pad, top + pad, Math.Max(1, Math.Min(width, maxW) - 2 * pad), Math.Max(1, Math.Min(height, maxH) - 2 * pad));

        double ratio = logo.Height > 0 ? logo.Width / logo.Height : 1;
        double w = box.Width, h = w / ratio;
        if (h > box.Height) { h = box.Height; w = h * ratio; }
        dc.DrawImage(logo, new Rect(box.X, box.Y, w, h));
    }

    /// <summary>Celle della pagina più le unioni che vi sporgono (la prima cella dell'unione può stare fuori pagina).</summary>
    static IEnumerable<(CellRange Area, int Row, int Col)> Anchors(Sheet sheet, CellRange range)
    {
        var seenMerges = new HashSet<CellRange>();
        for (int r = range.Row1; r <= range.Row2; r++)
        {
            if (sheet.RowHeight(r) <= 0) continue;
            for (int c = range.Col1; c <= range.Col2; c++)
            {
                if (sheet.ColumnWidth(c) <= 0) continue;
                if (sheet.MergeAt(r, c) is { } merge)
                {
                    if (seenMerges.Add(merge)) yield return (merge, merge.Row1, merge.Col1);
                }
                else yield return (new CellRange(r, c, r, c), r, c);
            }
        }
    }

    static void DrawBackgrounds(DrawingContext dc, Sheet sheet, CellRange range)
    {
        foreach (var (area, row, col) in Anchors(sheet, range))
            if (sheet.FormatAt(row, col).Background is { } color && color.A > 0)
                dc.DrawRectangle(new SolidColorBrush(color), null, sheet.CellRect(area));
    }

    static void DrawTexts(DrawingContext dc, Sheet sheet, CellRange range, Rect pageRegion)
    {
        foreach (var (area, row, col) in Anchors(sheet, range))
        {
            var cell = sheet.GetCell(row, col);
            if (cell == null || cell.Text.Length == 0) continue;
            var format = cell.Format;
            var rect = sheet.CellRect(area);
            var align = EffectiveAlign(cell);

            double padX = format.PaddingX, padY = format.PaddingY;
            double innerW = Math.Max(1, rect.Width - 2 * padX - format.Indent);
            var text = BuildText(cell.Text, format, format.FontSize);

            if (format.Wrap || align == HAlign.Justify)
            {
                text.MaxTextWidth = innerW;
                text.TextAlignment = ToTextAlignment(align);
            }
            else if (format.ShrinkToFit && text.Width > innerW)
            {
                text = BuildText(cell.Text, format, format.FontSize * innerW / text.Width);
            }

            // Senza "a capo" il testo più largo della cella sconfina nelle celle vuote accanto (come Excel/Calc);
            // per i numeri no, vengono tagliati.
            var clip = rect;
            double width = text.MaxTextWidth > 0 && text.MaxTextWidth < 1e6 ? text.MaxTextWidth : text.WidthIncludingTrailingWhitespace;
            double left = rect.Left + padX + (align == HAlign.Right ? 0 : format.Indent);
            double right = rect.Right - padX - (align == HAlign.Right ? format.Indent : 0);
            double tx = align switch
            {
                HAlign.Center => (left + right) / 2 - width / 2,
                HAlign.Right => right - width,
                _ => left,
            };
            if (!format.Wrap && !cell.IsNumber && area.Row1 == area.Row2 && width > right - left)
                clip = Overflow(sheet, area, align, rect, tx, tx + width);
            if (!format.Wrap && text.MaxTextWidth <= 0 && text.Text.Contains('\n'))
            {
                // Più righe senza "a capo": ogni riga allineata come la cella.
                text.MaxTextWidth = Math.Max(width, 1);
                text.TextAlignment = ToTextAlignment(align);
            }

            double ty = format.VAlign switch
            {
                VAlign.Top => rect.Top + padY,
                VAlign.Center => rect.Top + (rect.Height - text.Height) / 2,
                _ => rect.Bottom - padY - text.Height,
            };
            if (format.Wrap || align == HAlign.Justify) tx = left;

            clip.Intersect(pageRegion);
            if (clip.IsEmpty) continue;
            dc.PushClip(new RectangleGeometry(clip));
            dc.DrawText(text, new Point(tx, ty));
            dc.Pop();
        }
    }

    /// <summary>Area in cui può sconfinare il testo: si allarga sulle celle vuote (e non unite) della stessa riga.</summary>
    static Rect Overflow(Sheet sheet, CellRange area, HAlign align, Rect rect, double textLeft, double textRight)
    {
        int row = area.Row1;
        bool Free(int c) => c >= 0 && sheet.MergeAt(row, c) == null && (sheet.GetCell(row, c)?.Text.Length ?? 0) == 0;
        double left = rect.Left, right = rect.Right;
        if (align != HAlign.Right)
            for (int c = area.Col2 + 1; right < textRight && Free(c) && c < area.Col2 + 64; c++) right = sheet.ColumnX(c + 1);
        if (align is HAlign.Right or HAlign.Center)
            for (int c = area.Col1 - 1; left > textLeft && Free(c); c--) left = sheet.ColumnX(c);
        return new Rect(new Point(left, rect.Top), new Point(right, rect.Bottom));
    }

    static void DrawBorders(DrawingContext dc, Sheet sheet, CellRange range)
    {
        // Si raccolgono i bordi per lato di griglia: tra due celle confinanti vince il più marcato (come Excel);
        // poi i tratti uguali e contigui diventano una linea sola, così non restano giunture visibili.
        // Linea orizzontale: (indice della riga di griglia, colonna); verticale: (indice della colonna di griglia, riga).
        var horizontal = new Dictionary<(int Line, int Index), BorderLine>();
        var vertical = new Dictionary<(int Line, int Index), BorderLine>();
        static void Put(Dictionary<(int, int), BorderLine> edges, (int, int) key, BorderLine line)
        {
            if (!edges.TryGetValue(key, out var current) || Weight(line) > Weight(current)) edges[key] = line;
        }

        // Dentro un'unione restano solo i bordi sul perimetro.
        // Le celle subito fuori pagina contribuiscono i bordi che toccano la pagina.
        for (int r = range.Row1 - 1; r <= range.Row2 + 1; r++)
        {
            if (r < 0 || sheet.RowHeight(r) <= 0) continue;
            for (int c = range.Col1 - 1; c <= range.Col2 + 1; c++)
            {
                if (c < 0 || sheet.ColumnWidth(c) <= 0) continue;
                bool insideRows = r >= range.Row1 && r <= range.Row2, insideCols = c >= range.Col1 && c <= range.Col2;
                if (!insideRows && !insideCols) continue;
                var format = sheet.FormatAt(r, c);
                if (format.Left == null && format.Top == null && format.Right == null && format.Bottom == null) continue;

                var merge = sheet.MergeAt(r, c);
                bool onLeft = merge is not { } m1 || c == m1.Col1, onRight = merge is not { } m2 || c == m2.Col2;
                bool onTop = merge is not { } m3 || r == m3.Row1, onBottom = merge is not { } m4 || r == m4.Row2;

                // Fuori pagina conta solo il lato rivolto verso la pagina.
                if (format.Left is { } left && onLeft && insideRows && c >= range.Col1 && c <= range.Col2 + 1)
                    Put(vertical, (c, r), left);
                if (format.Right is { } right && onRight && insideRows && c >= range.Col1 - 1 && c <= range.Col2)
                    Put(vertical, (c + 1, r), right);
                if (format.Top is { } top && onTop && insideCols && r >= range.Row1 && r <= range.Row2 + 1)
                    Put(horizontal, (r, c), top);
                if (format.Bottom is { } bottom && onBottom && insideCols && r >= range.Row1 - 1 && r <= range.Row2)
                    Put(horizontal, (r + 1, c), bottom);
            }
        }

        foreach (var (line, from, to, border) in Runs(horizontal))
            Line(dc, border, new Point(sheet.ColumnX(from), sheet.RowY(line)), new Point(sheet.ColumnX(to + 1), sheet.RowY(line)));
        foreach (var (line, from, to, border) in Runs(vertical))
            Line(dc, border, new Point(sheet.ColumnX(line), sheet.RowY(from)), new Point(sheet.ColumnX(line), sheet.RowY(to + 1)));
    }

    /// <summary>Tratti consecutivi con lo stesso bordo sulla stessa linea di griglia, uniti.</summary>
    static IEnumerable<(int Line, int From, int To, BorderLine Border)> Runs(Dictionary<(int Line, int Index), BorderLine> edges)
    {
        foreach (var group in edges.GroupBy(e => e.Key.Line))
        {
            var sorted = group.OrderBy(e => e.Key.Index).ToList();
            int start = sorted[0].Key.Index, end = start;
            var border = sorted[0].Value;
            for (int i = 1; i < sorted.Count; i++)
            {
                var (key, value) = (sorted[i].Key, sorted[i].Value);
                if (key.Index == end + 1 && value == border) { end = key.Index; continue; }
                yield return (group.Key, start, end, border);
                (start, end, border) = (key.Index, key.Index, value);
            }
            yield return (group.Key, start, end, border);
        }
    }

    static double Weight(BorderLine line) => line.Width + (line.Kind == LineKind.Double ? 0.5 : 0) + (line.Kind == LineKind.Solid ? 0.1 : 0);

    static void Line(DrawingContext dc, BorderLine border, Point a, Point b)
    {
        var brush = new SolidColorBrush(border.Color);
        brush.Freeze();
        if (border.Kind == LineKind.Double)
        {
            // Due linee sottili separate da uno spazio, centrate sulla linea della griglia.
            double w = Math.Max(0.5, border.Width / 3), offset = border.Width / 2 - w / 2;
            bool horizontal = Math.Abs(a.Y - b.Y) < 0.01;
            var shift = horizontal ? new Vector(0, offset) : new Vector(offset, 0);
            var thin = new Pen(brush, w) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square };
            dc.DrawLine(thin, a - shift, b - shift);
            dc.DrawLine(thin, a + shift, b + shift);
            return;
        }
        var pen = new Pen(brush, border.Width);
        switch (border.Kind)
        {
            case LineKind.Dashed:
                pen.DashStyle = new DashStyle([3, 2], 0);
                break;
            case LineKind.Dotted:
                pen.DashStyle = new DashStyle([1, 1], 0);
                break;
            default:
                // Le estremità quadrate chiudono gli angoli tra bordi spessi.
                pen.StartLineCap = pen.EndLineCap = PenLineCap.Square;
                break;
        }
        dc.DrawLine(pen, a, b);
    }

    // --- testo ---------------------------------------------------------------------------

    static HAlign EffectiveAlign(SheetCell cell) => cell.Format.HAlign != HAlign.General ? cell.Format.HAlign
        : cell.IsBoolean ? HAlign.Center
        : cell.IsNumber ? HAlign.Right
        : HAlign.Left;

    static TextAlignment ToTextAlignment(HAlign align) => align switch
    {
        HAlign.Center => TextAlignment.Center,
        HAlign.Right => TextAlignment.Right,
        HAlign.Justify => TextAlignment.Justify,
        _ => TextAlignment.Left,
    };

    static FormattedText BuildText(string text, CellFormat format, double size)
    {
        var typeface = new Typeface(new FontFamily(format.FontFamily),
            format.Italic ? FontStyles.Italic : FontStyles.Normal,
            format.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);
        var brush = new SolidColorBrush(format.Foreground);
        brush.Freeze();
        var formatted = new FormattedText(text.Replace("\r", ""), Culture, FlowDirection.LeftToRight, typeface, Math.Max(1, size), brush, 1.0);
        if (format.Underline || format.Strike)
        {
            var decorations = new TextDecorationCollection();
            if (format.Underline) decorations.Add(TextDecorations.Underline);
            if (format.Strike) decorations.Add(TextDecorations.Strikethrough);
            formatted.SetTextDecorations(decorations);
        }
        return formatted;
    }

    /// <summary>
    /// Altezza minima di una riga perché il testo (anche andato a capo) ci stia per intero;
    /// con <paramref name="wrappedOnly"/> conta solo le celle con "testo a capo".
    /// </summary>
    public static double MeasureRowHeight(Sheet sheet, int row, bool wrappedOnly = false)
    {
        double needed = 0;
        foreach (var (key, cell) in sheet.Cells)
        {
            if (key.Row != row || cell.Text.Length == 0 || (wrappedOnly && !cell.Format.Wrap)) continue;
            var merge = sheet.MergeAt(row, key.Col);
            if (merge is { } m && (m.Row1 != m.Row2 || m.Col1 != key.Col)) continue;
            var format = cell.Format;
            var area = merge ?? new CellRange(row, key.Col, row, key.Col);
            double width = sheet.ColumnX(area.Col2 + 1) - sheet.ColumnX(area.Col1) - 2 * format.PaddingX - format.Indent;
            var text = BuildText(cell.Text, format, format.FontSize);
            if (format.Wrap) text.MaxTextWidth = Math.Max(1, width);
            needed = Math.Max(needed, text.Height + 2 * format.PaddingY);
        }
        return needed;
    }
}

/// <summary>
/// Elemento che disegna una pagina su un foglio di carta di dimensioni date: se l'orientamento non coincide
/// la ruota di 90°, se la carta è diversa la adatta (centrata in orizzontale, in alto in verticale). Usato sia per l'anteprima sia per la stampa.
/// </summary>
public sealed class PrintPageElement : FrameworkElement
{
    readonly PrintPage _page;
    readonly Size _paper;

    public PrintPageElement(PrintPage page, Size? paper = null)
    {
        _page = page;
        _paper = paper ?? page.Paper;
        Width = _paper.Width;
        Height = _paper.Height;
        SnapsToDevicePixels = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = _page.Paper;
        bool rotate = (size.Width > size.Height) != (_paper.Width > _paper.Height) && Math.Abs(size.Width - size.Height) > 1;
        var oriented = rotate ? new Size(size.Height, size.Width) : size;
        double scale = Math.Min(_paper.Width / oriented.Width, _paper.Height / oriented.Height);
        if (Math.Abs(scale - 1) < 0.01) scale = 1;

        dc.DrawRectangle(Brushes.White, null, new Rect(_paper));
        // Centrata in orizzontale, ma ancorata in alto: lo spazio che avanza resta in fondo al foglio.
        dc.PushTransform(new TranslateTransform((_paper.Width - oriented.Width * scale) / 2, 0));
        dc.PushTransform(new ScaleTransform(scale, scale));
        if (rotate)
        {
            // Rotazione oraria di 90°: (x, y) → (altezza − y, x).
            dc.PushTransform(new TranslateTransform(size.Height, 0));
            dc.PushTransform(new RotateTransform(90));
        }
        SheetRenderer.DrawPage(dc, _page);
        if (rotate) { dc.Pop(); dc.Pop(); }
        dc.Pop();
        dc.Pop();
    }
}
