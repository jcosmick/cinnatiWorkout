using System.IO;

namespace CinnatiWorkout;

public static class Templates
{
    public const string Workout = "Templates.scheda-allenamento.ods";
    public const string SkinfoldMale = "Templates.pliche-uomo.xlsx";
    public const string SkinfoldFemale = "Templates.pliche-donna.xlsx";

    public static void CopyTo(string resourceName, string targetPath)
    {
        using var source = typeof(Templates).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Modello {resourceName} non trovato.");
        using var target = File.Create(targetPath);
        source.CopyTo(target);
    }
}
