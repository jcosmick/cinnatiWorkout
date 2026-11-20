using ClosedXML.Excel;

namespace CinnatiWorkout.Skinfold;

/// <summary>
/// Struttura del foglio pliche: i modelli uomo (7 pliche) e donna (5 pliche) hanno
/// celle in posizioni diverse e formule diverse per la % di massa grassa.
/// </summary>
public sealed class SkinfoldLayout
{
    public required bool IsFemale { get; init; }
    public required string Template { get; init; }
    public required string Label { get; init; }
    public required string FormulaDescription { get; init; }
    /// <summary>Righe delle pliche (misure in E–G, media in H).</summary>
    public required int[] FoldRows { get; init; }
    /// <summary>Coppie (riga etichette, riga valori) delle circonferenze, colonne C–G.</summary>
    public required (int Labels, int Values)[] CircRows { get; init; }
    public required string NoteCell { get; init; }
    /// <summary>Cella con la formula della % di massa grassa (usata per riconoscere il modello).</summary>
    public required string PercentCell { get; init; }
    /// <summary>% massa grassa da somma delle pliche ed età, identica alla formula del foglio.</summary>
    public required Func<double, double, double> FatPercent { get; init; }

    /// <summary>Jackson-Pollock 7 pliche uomo + Siri.</summary>
    public static readonly SkinfoldLayout Male = new()
    {
        IsFemale = false,
        Template = Templates.SkinfoldMale,
        Label = "Uomo",
        FormulaDescription = "Calcolo Jackson-Pollock a 7 pliche (uomo). Servono età e pliche; per i kg anche il peso.",
        FoldRows = [12, 13, 14, 15, 16, 17, 18],
        CircRows = [(25, 26), (28, 29)],
        NoteCell = "B32",
        PercentCell = "H21",
        FatPercent = (s, age) => 495 / (1.112 - 0.00043499 * s + 0.00000055 * s * s - 0.00028826 * age) - 450,
    };

    /// <summary>
    /// Jackson-Pollock donna + Siri: il foglio stima la somma a 7 pliche dalle 5 misurate (somma / 5 × 7).
    /// </summary>
    public static readonly SkinfoldLayout Female = new()
    {
        IsFemale = true,
        Template = Templates.SkinfoldFemale,
        Label = "Donna",
        FormulaDescription = "Calcolo Jackson-Pollock donna a 5 pliche (somma riportata a 7). Servono età e pliche; per i kg anche il peso.",
        FoldRows = [12, 13, 14, 15, 16],
        CircRows = [(23, 24), (26, 27)],
        NoteCell = "B30",
        PercentCell = "H19",
        FatPercent = (s, age) =>
        {
            var s7 = s / 5 * 7;
            return 495 / (1.097 - 0.00046971 * s7 + 0.00000056 * s7 * s7 - 0.00012828 * age) - 450;
        },
    };

    public SkinfoldLayout Other => IsFemale ? Male : Female;

    /// <summary>Riconosce il modello dalla posizione della cella "Totale" (G19 uomo, G17 donna).</summary>
    public static SkinfoldLayout Detect(IXLWorksheet firstSheet)
    {
        static bool IsTotal(IXLCell cell) => cell.Value.IsText && cell.Value.GetText().Trim().Equals("Totale", StringComparison.OrdinalIgnoreCase);
        if (IsTotal(firstSheet.Cell("G17")) && !IsTotal(firstSheet.Cell("G19"))) return Female;
        if (firstSheet.Cell("H19").FormulaA1.Contains("1.097")) return Female;
        return Male;
    }
}
