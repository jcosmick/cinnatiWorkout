using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace CinnatiWorkout.Skinfold;

public sealed class FoldRow(string name, int row) : Observable
{
    string _m1 = "", _m2 = "", _m3 = "";

    public string Name { get; } = name;
    internal int Row { get; } = row;
    internal Action? Changed { get; set; }

    public string M1 { get => _m1; set => Update(ref _m1, value); }
    public string M2 { get => _m2; set => Update(ref _m2, value); }
    public string M3 { get => _m3; set => Update(ref _m3, value); }

    /// <summary>Come nel foglio: (1ª + 2ª + 3ª) / 3, le misure vuote valgono 0.</summary>
    public double? Average
    {
        get
        {
            double?[] values = [Num.Parse(M1), Num.Parse(M2), Num.Parse(M3)];
            return values.Any(v => v.HasValue) ? values.Sum(v => v ?? 0) / 3 : null;
        }
    }

    internal void Load(string m1, string m2, string m3) => (_m1, _m2, _m3) = (m1, m2, m3);

    void Update(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value ?? "", name)) return;
        Raise(nameof(Average));
        Changed?.Invoke();
    }
}

public sealed class CircItem(string name, string address) : Observable
{
    string _value = "";

    public string Name { get; } = name;
    internal string Address { get; } = address;
    internal Action? Changed { get; set; }

    public string Value
    {
        get => _value;
        set { if (Set(ref _value, value ?? "")) Changed?.Invoke(); }
    }

    internal void Load(string value) => _value = value;
}

/// <summary>Una rilevazione = un foglio "P.n" del file pliche.</summary>
public sealed class Measurement : Observable
{
    DateTime? _date;
    string _age = "", _weight = "", _note = "";

    public required string SheetName { get; init; }
    public required SkinfoldLayout Layout { get; init; }
    public required IReadOnlyList<FoldRow> Folds { get; init; }
    /// <summary>Prima le 5 circonferenze della prima riga (Sx), poi le 5 della seconda (Dx), come nel foglio.</summary>
    public required IReadOnlyList<CircItem> Circumferences { get; init; }
    internal Action? Changed { get; set; }

    public DateTime? Date { get => _date; set { if (Set(ref _date, value)) { Raise(nameof(DateText)); Raise(nameof(IsEmpty)); Changed?.Invoke(); } } }
    public string Age { get => _age; set { if (Set(ref _age, value ?? "")) Recalculate(); } }
    public string Weight { get => _weight; set { if (Set(ref _weight, value ?? "")) Recalculate(); } }
    public string Note { get => _note; set { if (Set(ref _note, value ?? "")) Changed?.Invoke(); } }

    public string DisplayName => SheetName.StartsWith("P.") ? $"Rilevazione {SheetName[2..]}" : SheetName;
    public string DateText => Date is { } d ? d.ToString("d MMMM yyyy", Num.Culture) : "Da compilare";
    public bool IsEmpty => Date == null && Total == null && Num.Parse(Weight) == null;

    public double? Total => Folds.Any(f => f.Average.HasValue) ? Folds.Sum(f => f.Average ?? 0) : null;

    /// <summary>Formula del foglio (Jackson-Pollock uomo o donna + Siri).</summary>
    public double? FatPercent => Total is { } s && Num.Parse(Age) is { } age ? Layout.FatPercent(s, age) : null;

    public double? FatKg => Num.Parse(Weight) is { } w && FatPercent is { } p ? w * p / 100 : null;
    public double? LeanKg => Num.Parse(Weight) is { } w && FatKg is { } fat ? w - fat : null;
    public double? WeightKg => Num.Parse(Weight);

    internal void Load(DateTime? date, string age, string weight, string note) =>
        (_date, _age, _weight, _note) = (date, age, weight, note);

    internal void Recalculate()
    {
        Raise(nameof(Total));
        Raise(nameof(FatPercent));
        Raise(nameof(FatKg));
        Raise(nameof(LeanKg));
        Raise(nameof(WeightKg));
        Raise(nameof(IsEmpty));
        Changed?.Invoke();
    }
}

public sealed class SkinfoldWorkbook : Observable, IDisposable
{
    static readonly Regex MeasurementSheet = new(@"^P\.(\d+)$");
    static readonly string[] CircColumns = ["C", "D", "E", "F", "G"];

    readonly XLWorkbook _workbook;
    string _athlete = "";
    bool _isDirty;

    public string FilePath { get; }
    public SkinfoldLayout Layout { get; }
    public bool IsFemale => Layout.IsFemale;
    public IReadOnlyList<Measurement> Measurements { get; }

    public string Athlete
    {
        get => _athlete;
        set { if (Set(ref _athlete, value ?? "")) MarkDirty(); }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set { if (Set(ref _isDirty, value)) DirtyChanged?.Invoke(this, EventArgs.Empty); }
    }

    public event EventHandler? DirtyChanged;

    SkinfoldWorkbook(string path, XLWorkbook workbook, SkinfoldLayout? layout = null)
    {
        FilePath = path;
        _workbook = workbook;

        var sheets = workbook.Worksheets
            .Select(ws => (Sheet: ws, Match: MeasurementSheet.Match(ws.Name)))
            .Where(x => x.Match.Success)
            .OrderBy(x => int.Parse(x.Match.Groups[1].Value))
            .Select(x => x.Sheet)
            .ToList();
        if (sheets.Count == 0)
            throw new NotSupportedException("Il file non sembra una scheda pliche (mancano i fogli P.1, P.2, …).");

        Layout = layout ?? SkinfoldLayout.Detect(sheets[0]);
        _athlete = ReadText(sheets[0].Cell("D5"));
        Measurements = sheets.Select(ReadMeasurement).ToList();
    }

    public static SkinfoldWorkbook Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        return new SkinfoldWorkbook(path, new XLWorkbook(stream));
    }

    /// <summary>
    /// true se le immagini del file sono state rovinate dalle versioni 1.0/1.1 dell'app
    /// e il file è basato sul modello uomo o donna (stessa formula), quindi ripristinabile.
    /// </summary>
    public bool CanRestoreLayout
    {
        get
        {
            try
            {
                var temp = Path.Combine(Path.GetTempPath(), $"cinnati_{Guid.NewGuid():N}.xlsx");
                try
                {
                    Templates.CopyTo(Layout.Template, temp);
                    using var template = new XLWorkbook(temp);
                    var sheet = Measurements[0].SheetName;
                    return XlsxPatcher.CountShapeGroups(FilePath) < XlsxPatcher.CountShapeGroups(temp)
                           && template.Worksheets.Count == _workbook.Worksheets.Count
                           && template.Worksheet(sheet).Cell(Layout.PercentCell).FormulaA1 == _workbook.Worksheet(sheet).Cell(Layout.PercentCell).FormulaA1;
                }
                finally
                {
                    File.Delete(temp);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public void Save() => SaveOnto(baseFile: null);

    /// <summary>Riscrive il file partendo dal modello originale, mantenendo tutti i dati inseriti.</summary>
    public void RestoreLayoutFromTemplate() => SaveOnto(baseFile: Layout.Template);

    /// <summary>
    /// Nomi delle pliche (o rilevazioni) con valori che andrebbero persi passando all'altro modello,
    /// es. pettorale e ascellare da uomo a donna.
    /// </summary>
    public IReadOnlyList<string> LostWhenConverting(SkinfoldLayout target)
    {
        using var probe = LoadTemplate(target);
        var targetFolds = probe.Measurements[0].Folds.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Measurements
            .SelectMany(m => m.Folds)
            .Where(f => !targetFolds.Contains(f.Name) && (f.M1 + f.M2 + f.M3).Trim().Length > 0)
            .Select(f => f.Name)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Riscrive il file sul modello dell'altro sesso (immagini, formule, pliche) riportando atleta,
    /// date, età, peso, pliche con lo stesso nome, circonferenze e note. Salva subito su disco.
    /// </summary>
    public SkinfoldWorkbook ConvertTo(SkinfoldLayout target)
    {
        var converted = LoadTemplate(target, FilePath);
        converted.Athlete = Athlete;
        foreach (var source in Measurements)
        {
            var dest = converted.Measurements.FirstOrDefault(m => m.SheetName == source.SheetName);
            if (dest == null) continue;
            dest.Date = source.Date;
            dest.Age = source.Age;
            dest.Weight = source.Weight;
            dest.Note = source.Note;
            foreach (var fold in source.Folds)
            {
                var match = dest.Folds.FirstOrDefault(f => f.Name.Equals(fold.Name, StringComparison.OrdinalIgnoreCase));
                if (match == null) continue;
                match.M1 = fold.M1;
                match.M2 = fold.M2;
                match.M3 = fold.M3;
            }
            foreach (var circ in source.Circumferences)
            {
                var match = dest.Circumferences.FirstOrDefault(c => c.Name.Equals(circ.Name, StringComparison.OrdinalIgnoreCase));
                if (match != null) match.Value = circ.Value;
            }
        }
        converted.SaveOnto(baseFile: target.Template);
        Dispose();
        return converted;
    }

    /// <summary>Apre il modello incorporato come cartella di lavoro vuota (associata a <paramref name="path"/> se indicato).</summary>
    static SkinfoldWorkbook LoadTemplate(SkinfoldLayout layout, string? path = null)
    {
        using var stream = typeof(Templates).Assembly.GetManifestResourceStream(layout.Template)
            ?? throw new InvalidOperationException($"Modello {layout.Template} non trovato.");
        var memory = new MemoryStream();
        stream.CopyTo(memory);
        memory.Position = 0;
        return new SkinfoldWorkbook(path ?? "", new XLWorkbook(memory), layout);
    }

    public void Dispose() => _workbook.Dispose();

    void SaveOnto(string? baseFile)
    {
        var inputs = new List<(string Sheet, string Address)>();
        void Put(IXLWorksheet ws, string address, XLCellValue value)
        {
            ws.Cell(address).Value = value;
            inputs.Add((ws.Name, address));
        }

        var first = _workbook.Worksheet(Measurements[0].SheetName);
        Put(first, "D5", string.IsNullOrWhiteSpace(Athlete) ? Blank.Value : Athlete.Trim());

        foreach (var m in Measurements)
        {
            var ws = _workbook.Worksheet(m.SheetName);
            Put(ws, "C7", m.Date is { } d ? d : Blank.Value);
            Put(ws, "F7", ToValue(m.Age));
            Put(ws, "H7", ToValue(m.Weight));
            foreach (var fold in m.Folds)
            {
                Put(ws, $"E{fold.Row}", ToValue(fold.M1));
                Put(ws, $"F{fold.Row}", ToValue(fold.M2));
                Put(ws, $"G{fold.Row}", ToValue(fold.M3));
            }
            foreach (var circ in m.Circumferences) Put(ws, circ.Address, ToValue(circ.Value));
            Put(ws, Layout.NoteCell, string.IsNullOrWhiteSpace(m.Note) ? Blank.Value : m.Note);
        }

        // ClosedXML serve solo a calcolare le formule; il file viene modificato cella per cella
        // così immagini e gruppi di forme restano esattamente quelli originali.
        _workbook.RecalculateAllFormulas();

        var temp = Path.Combine(Path.GetTempPath(), $"cinnati_{Guid.NewGuid():N}.xlsx");
        try
        {
            if (baseFile == null) File.Copy(FilePath, temp, overwrite: true);
            else Templates.CopyTo(baseFile, temp);

            using (var patch = new XlsxPatcher(temp))
            {
                foreach (var (sheet, address) in inputs)
                    patch.SetValue(sheet, address, _workbook.Worksheet(sheet).Cell(address).Value);
                foreach (var ws in _workbook.Worksheets)
                    foreach (var cell in ws.CellsUsed(c => c.HasFormula))
                        patch.SetCachedResult(ws.Name, cell.Address.ToString()!, cell.Value);
                patch.ForceFullCalculationOnLoad();
            }
            File.Copy(temp, FilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
        IsDirty = false;
    }

    Measurement ReadMeasurement(IXLWorksheet ws)
    {
        var folds = Layout.FoldRows.Select(r =>
        {
            var name = ReadText(ws.Cell(r, "B"));
            var fold = new FoldRow(name.Length > 0 ? name : $"Plica {r - 11}", r);
            fold.Load(ReadNumber(ws.Cell(r, "E")), ReadNumber(ws.Cell(r, "F")), ReadNumber(ws.Cell(r, "G")));
            return fold;
        }).ToList();

        var circs = new List<CircItem>();
        foreach (var (labelRow, valueRow) in Layout.CircRows)
        {
            foreach (var col in CircColumns)
            {
                var item = new CircItem(ReadText(ws.Cell(labelRow, col)), $"{col}{valueRow}");
                item.Load(ReadNumber(ws.Cell(valueRow, col)));
                circs.Add(item);
            }
        }

        var m = new Measurement { SheetName = ws.Name, Layout = Layout, Folds = folds, Circumferences = circs };
        m.Load(ReadDate(ws.Cell("C7")), ReadNumber(ws.Cell("F7")), ReadNumber(ws.Cell("H7")), ReadText(ws.Cell(Layout.NoteCell)));

        m.Changed = MarkDirty;
        foreach (var fold in folds) fold.Changed = m.Recalculate;
        foreach (var circ in circs) circ.Changed = MarkDirty;
        return m;
    }

    void MarkDirty() => IsDirty = true;

    static string ReadText(IXLCell cell)
    {
        var v = cell.Value;
        if (v.IsText) return v.GetText().Trim();
        if (v.IsNumber) return Num.Format(v.GetNumber());
        return "";
    }

    static string ReadNumber(IXLCell cell)
    {
        var v = cell.Value;
        if (v.IsNumber) return Num.Format(v.GetNumber());
        if (v.IsText) return v.GetText().Trim();
        return "";
    }

    static DateTime? ReadDate(IXLCell cell)
    {
        var v = cell.Value;
        if (v.IsDateTime) return v.GetDateTime();
        if (v.IsNumber) return DateTime.FromOADate(v.GetNumber());
        if (v.IsText && DateTime.TryParse(v.GetText(), Num.Culture, DateTimeStyles.None, out var d)) return d;
        return null;
    }

    static XLCellValue ToValue(string text)
    {
        if (Num.Parse(text) is { } number) return number;
        return string.IsNullOrWhiteSpace(text) ? Blank.Value : text.Trim();
    }
}

/// <summary>Numeri in formato italiano (virgola decimale), accettando anche il punto.</summary>
public static class Num
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("it-IT");

    public static double? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = text.Trim().Replace('.', ',');
        return double.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Culture, out var value)
            ? value
            : null;
    }

    public static string Format(double value) => value.ToString("0.##", Culture);
}
