using System.IO;
using System.Text.Json;

namespace CinnatiWorkout;

public sealed class AppSettings
{
    public string? RootPath { get; set; }

    /// <summary>Colore principale dell'interfaccia in formato #RRGGBB.</summary>
    public string AccentColor { get; set; } = ThemeManager.DefaultAccent;

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinnatiWorkout", "settings.json");

    public bool HasValidRoot => !string.IsNullOrWhiteSpace(RootPath) && Directory.Exists(RootPath);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Impostazioni corrotte: si riparte da zero e l'utente le reinserisce.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Cerca la cartella "Il mio Drive" creata da Google Drive per desktop.</summary>
    public static string? DetectGoogleDrive()
    {
        string[] names = ["Il mio Drive", "My Drive"];
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(drive.RootDirectory.FullName, name);
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Google Drive");
        return Directory.Exists(legacy) ? legacy : null;
    }
}
