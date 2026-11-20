using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace CinnatiWorkout;

public sealed class AppSettings
{
    public string? RootPath { get; set; }

    /// <summary>Colore principale dell'interfaccia in formato #RRGGBB.</summary>
    public string AccentColor { get; set; } = ThemeManager.DefaultAccent;

    /// <summary>
    /// Logo stampato in alto a sinistra nelle schede allenamento: copia dell'immagine scelta,
    /// conservata nella cartella dell'app (così resta valida anche se l'originale viene spostato).
    /// </summary>
    public string? LogoPath { get; set; }

    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinnatiWorkout");
    static string FilePath => Path.Combine(Folder, "settings.json");

    /// <summary>Copia l'immagine nella cartella dell'app e la imposta come logo (null = nessun logo).</summary>
    public void SetLogo(string? sourcePath)
    {
        var previous = LogoPath;
        if (sourcePath == null)
        {
            LogoPath = null;
        }
        else
        {
            if (string.Equals(Path.GetFullPath(sourcePath), previous == null ? null : Path.GetFullPath(previous), StringComparison.OrdinalIgnoreCase))
                return;
            Directory.CreateDirectory(Folder);
            // Nome sempre nuovo: l'immagine precedente può essere ancora aperta in memoria.
            var target = Path.Combine(Folder, $"logo-{DateTime.Now:yyyyMMddHHmmss}{Path.GetExtension(sourcePath).ToLowerInvariant()}");
            File.Copy(sourcePath, target, overwrite: true);
            LogoPath = target;
        }
        if (previous != null && previous != LogoPath)
        {
            try { File.Delete(previous); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Il logo pronto per la stampa, o null se non impostato o illeggibile.</summary>
    public BitmapSource? LoadLogo() => LogoPath != null && File.Exists(LogoPath) ? LoadImage(LogoPath) : null;

    public static BitmapSource? LoadImage(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // legge tutto subito e non tiene aperto il file
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

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
