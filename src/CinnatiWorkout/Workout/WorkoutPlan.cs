using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Xml.Linq;
using CinnatiWorkout.Ods;

namespace CinnatiWorkout.Workout;

/// <summary>Una riga esercizio: un valore per ogni colonna d'intestazione della scheda.</summary>
public sealed class ExerciseRow : Observable
{
    readonly string[] _values;

    internal XElement? Element { get; set; }
    internal Action? Changed { get; set; }

    public ExerciseRow(int columns) => _values = Enumerable.Repeat("", columns).ToArray();

    [IndexerName("Item")]
    public string this[int index]
    {
        get => _values[index];
        set
        {
            value ??= "";
            if (_values[index] == value) return;
            _values[index] = value;
            Raise(Binding.IndexerName);
            Changed?.Invoke();
        }
    }

    public int Count => _values.Length;

    internal void Load(int index, string value) => _values[index] = value;
}

/// <summary>Un blocco "SCHEDA X:" con intestazione e righe esercizio.</summary>
public sealed class WorkoutBlock : Observable
{
    string _name = "";

    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) Changed?.Invoke(); }
    }

    public required IReadOnlyList<string> Headers { get; init; }
    internal IReadOnlyList<int> Columns { get; init; } = [];
    internal int LabelColumn { get; init; }
    internal XElement? LabelRow { get; set; }
    internal XElement? HeaderRow { get; set; }
    internal List<XElement> SavedRowElements { get; } = [];
    internal Action? Changed { get; set; }

    public ObservableCollection<ExerciseRow> Rows { get; } = [];
}

public sealed class WorkoutPlan : Observable
{
    static readonly Regex BlockLabel = new(@"^\s*SCHEDA\b", RegexOptions.IgnoreCase);

    readonly OdsDocument _doc;
    readonly List<WorkoutBlock> _removed = [];
    XElement? _titleRow, _noteRow;
    XElement? _templateLabel, _templateHeader, _templateRow;
    string _title = "", _note = "";
    bool _isDirty;

    public string FilePath => _doc.FilePath;
    public ObservableCollection<WorkoutBlock> Blocks { get; } = [];
    public bool CanAddBlock => _templateHeader != null;

    /// <summary>Intestazione grande sopra le schede (es. "GIADA OTTOBRE-NOVEMBRE 2026"); non mostrata nell'editor.</summary>
    public string Title
    {
        get => _title;
        set { if (Set(ref _title, value ?? "")) MarkDirty(); }
    }

    /// <summary>Nota generale della scheda (riga evidenziata tra intestazione e prima scheda).</summary>
    public string Note
    {
        get => _note;
        set { if (Set(ref _note, value ?? "")) MarkDirty(); }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set { if (Set(ref _isDirty, value)) DirtyChanged?.Invoke(this, EventArgs.Empty); }
    }

    public event EventHandler? DirtyChanged;

    WorkoutPlan(OdsDocument doc) => _doc = doc;

    public static WorkoutPlan Load(string path)
    {
        var plan = new WorkoutPlan(OdsDocument.Load(path));
        plan.Parse();
        return plan;
    }

    public WorkoutBlock AddBlock()
    {
        var model = Blocks.FirstOrDefault() ?? throw new InvalidOperationException("Nessuna scheda da usare come modello.");
        var letter = (char)('A' + Blocks.Count);
        var block = new WorkoutBlock
        {
            Name = $"SCHEDA {letter}:",
            Headers = model.Headers,
            Columns = model.Columns,
            LabelColumn = model.LabelColumn,
        };
        for (int i = 0; i < 6; i++) block.Rows.Add(new ExerciseRow(block.Headers.Count));
        Track(block);
        Blocks.Add(block);
        MarkDirty();
        return block;
    }

    public void RemoveBlock(WorkoutBlock block)
    {
        if (!Blocks.Remove(block)) return;
        if (block.LabelRow != null) _removed.Add(block);
        MarkDirty();
    }

    /// <summary>Svuota tutti i valori mantenendo struttura e numero di righe (per un nuovo file).</summary>
    public void ClearAll()
    {
        Note = "";
        foreach (var block in Blocks)
            foreach (var row in block.Rows)
                for (int i = 0; i < row.Count; i++) row[i] = "";
    }

    public void Save()
    {
        foreach (var block in _removed) RemoveFromSheet(block);
        _removed.Clear();

        if (_titleRow != null) _doc.SetText(_titleRow, 0, Title);
        SaveNote();

        XElement? previousEnd = null;
        foreach (var block in Blocks)
        {
            if (block.LabelRow == null || block.HeaderRow == null) InsertBlockRows(block, previousEnd);
            _doc.SetText(block.LabelRow!, block.LabelColumn, block.Name);

            foreach (var element in block.SavedRowElements.Except(block.Rows.Select(r => r.Element)).ToList())
                element!.Remove();

            var last = block.HeaderRow!;
            foreach (var row in block.Rows)
            {
                if (row.Element == null)
                {
                    row.Element = _doc.CloneRowAfter(last, _templateRow ?? block.HeaderRow!, clearText: true);
                }
                else if (last.ElementsAfterSelf().FirstOrDefault() != row.Element)
                {
                    row.Element.Remove();
                    last.AddAfterSelf(row.Element);
                }
                for (int i = 0; i < block.Columns.Count; i++) _doc.SetText(row.Element, block.Columns[i], row[i]);
                last = row.Element;
            }

            block.SavedRowElements.Clear();
            block.SavedRowElements.AddRange(block.Rows.Select(r => r.Element!));
            previousEnd = last;
        }

        // Il file salvato (quello che Drive sincronizza) prende lo stesso layout della stampa dall'app.
        var parts = WorkoutPrintLayout.Apply(_doc, WorkoutPrintLayout.LogoSource?.Invoke());
        _doc.Save(parts);
        RefreshTemplates();
        IsDirty = false;
    }

    /// <summary>
    /// Le schede aggiunte dopo un salvataggio copiano le righe com'erano nel file appena scritto
    /// (testi già ingranditi), non come erano all'apertura.
    /// </summary>
    void RefreshTemplates()
    {
        var model = Blocks.FirstOrDefault(b => b.LabelRow != null && b.HeaderRow != null);
        if (model == null) return;
        _templateLabel = new XElement(model.LabelRow!);
        _templateHeader = new XElement(model.HeaderRow!);
        if (model.SavedRowElements.Count > 0) _templateRow = new XElement(model.SavedRowElements[0]);
    }

    void InsertBlockRows(WorkoutBlock block, XElement? previousEnd)
    {
        var anchor = previousEnd ?? _noteRow ?? _titleRow ?? _doc.Rows.First();
        anchor = _doc.InsertBlankRowAfter(anchor);
        anchor = _doc.InsertBlankRowAfter(anchor);
        block.LabelRow = _doc.CloneRowAfter(anchor, _templateLabel!, clearText: true);
        block.HeaderRow = _doc.CloneRowAfter(block.LabelRow, _templateHeader!, clearText: false);
    }

    /// <summary>
    /// Scrive la nota generale. Se il file non ha ancora la riga della nota, la crea sotto
    /// l'intestazione con lo stesso aspetto della stampa (riga grigio chiaro unita su tutte le colonne).
    /// </summary>
    void SaveNote()
    {
        if (_noteRow != null)
        {
            _doc.SetText(_noteRow, 0, Note);
            return;
        }
        if (Note.Trim().Length == 0) return;

        // Intestazione, una riga vuota, la nota, poi lo spazio prima della prima scheda.
        var anchor = _titleRow ?? Blocks.FirstOrDefault()?.LabelRow?.ElementsBeforeSelf().LastOrDefault() ?? _doc.Rows.First();
        if (_titleRow != null && anchor.ElementsAfterSelf().FirstOrDefault() is { } spacer && _doc.IsEmpty(spacer)
            && spacer != Blocks.FirstOrDefault()?.LabelRow)
            anchor = spacer;
        else
            anchor = _doc.InsertBlankRowAfter(anchor);
        _noteRow = _doc.InsertBlankRowAfter(anchor);

        int span = _titleRow != null ? Math.Max(_doc.ColumnSpan(_titleRow, 0), 1) : 12;
        if (span == 1) span = 12;
        _doc.SetCellStyle(_noteRow, 0, _doc.EnsureCellStyle(WorkoutPrintLayout.NoteStyleName, NoteStyle()));
        _doc.MergeAcross(_noteRow, 0, span);
        _doc.SetText(_noteRow, 0, Note);
    }

    static XElement NoteStyle()
    {
        XNamespace style = OdsDocument.StyleNs, fo = OdsDocument.FoNs;
        return new XElement(style + "style",
            new XAttribute(style + "family", "table-cell"),
            new XAttribute(style + "parent-style-name", "Default"),
            new XElement(style + "table-cell-properties",
                new XAttribute(fo + "background-color", "#f2f2f2"),
                new XAttribute(fo + "wrap-option", "wrap"),
                new XAttribute(style + "text-align-source", "fix"),
                new XAttribute(style + "vertical-align", "middle")),
            new XElement(style + "paragraph-properties",
                new XAttribute(fo + "text-align", "center")),
            new XElement(style + "text-properties",
                new XAttribute(fo + "color", "#000000"),
                new XAttribute(style + "font-name", "Arial"),
                new XAttribute(fo + "font-size", "11pt"),
                new XAttribute(fo + "font-weight", "bold")));
    }

    void RemoveFromSheet(WorkoutBlock block)
    {
        var last = block.SavedRowElements.LastOrDefault() ?? block.HeaderRow ?? block.LabelRow!;
        // Toglie anche fino a due righe vuote di separazione successive.
        foreach (var spacer in last.ElementsAfterSelf().Take(2).TakeWhile(_doc.IsEmpty).ToList()) spacer.Remove();
        foreach (var element in block.SavedRowElements) element.Remove();
        block.HeaderRow?.Remove();
        block.LabelRow?.Remove();
    }

    void Parse()
    {
        var rows = _doc.Rows;
        int firstLabel = -1;

        for (int i = 0; i < rows.Count - 1; i++)
        {
            var (labelColumn, label) = _doc.FirstText(rows[i]);
            if (label == null || !BlockLabel.IsMatch(label)) continue;

            var header = rows[i + 1];
            var columns = new List<int>();
            var headers = new List<string>();
            for (int c = 0; c < OdsDocument.MaxColumns; c++)
            {
                var text = _doc.GetText(header, c).Trim();
                if (text.Length == 0) continue;
                columns.Add(c);
                headers.Add(text);
            }
            if (columns.Count == 0) continue;
            if (firstLabel < 0) firstLabel = i;

            var block = new WorkoutBlock
            {
                Name = label,
                Headers = headers,
                Columns = columns,
                LabelColumn = labelColumn,
                LabelRow = rows[i],
                HeaderRow = header,
            };

            // Le righe vuote ma formattate come esercizi (stessa unione di celle o stesso stile)
            // fanno parte della scheda: sono gli spazi lasciati liberi da compilare.
            int headerSpan = _doc.ColumnSpan(header, columns[0]);
            string? rowStyle = null;
            int j = i + 2;
            for (; j < rows.Count; j++)
            {
                var row = rows[j];
                var (_, text) = _doc.FirstText(row);
                if (text != null && BlockLabel.IsMatch(text)) break;
                if (text == null)
                {
                    bool sameSpan = headerSpan > 1 && _doc.ColumnSpan(row, columns[0]) == headerSpan;
                    bool sameStyle = rowStyle != null && _doc.CellStyle(row, columns[0]) == rowStyle;
                    if (!sameSpan && !sameStyle) break;
                }
                rowStyle ??= _doc.CellStyle(row, columns[0]);

                var exercise = new ExerciseRow(columns.Count) { Element = row };
                for (int k = 0; k < columns.Count; k++) exercise.Load(k, _doc.GetText(row, columns[k]));
                block.Rows.Add(exercise);
                block.SavedRowElements.Add(row);
            }

            _templateLabel ??= new XElement(rows[i]);
            _templateHeader ??= new XElement(header);
            if (_templateRow == null && block.SavedRowElements.Count > 0) _templateRow = new XElement(block.SavedRowElements[0]);

            Track(block);
            Blocks.Add(block);
            i = j - 1;
        }

        // Sopra le schede, le righe con testo in colonna A: la prima è l'intestazione
        // (es. "GIADA OTTOBRE-NOVEMBRE 2026"), l'ultima, se diversa, è la nota generale.
        var top = Enumerable.Range(0, Math.Max(firstLabel, 0))
            .Select(i => (Row: rows[i], Text: _doc.FirstText(rows[i])))
            .Where(x => x.Text.Column == 0)
            .ToList();
        if (top.Count > 0)
        {
            _titleRow = top[0].Row;
            _title = top[0].Text.Text!;
        }
        if (top.Count > 1)
        {
            _noteRow = top[^1].Row;
            _note = _doc.GetText(_noteRow, 0);
        }

        Blocks.CollectionChanged += (_, _) => MarkDirty();
    }

    void Track(WorkoutBlock block)
    {
        block.Changed = MarkDirty;
        foreach (var row in block.Rows) row.Changed = MarkDirty;
        block.Rows.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (ExerciseRow row in e.NewItems) row.Changed = MarkDirty;
            MarkDirty();
        };
    }

    void MarkDirty() => IsDirty = true;
}
