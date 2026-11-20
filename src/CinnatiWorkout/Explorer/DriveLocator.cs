using System.IO;
using System.Runtime.InteropServices;

namespace CinnatiWorkout.Explorer;

/// <summary>
/// Trova l'ID su Google Drive di un file della cartella sincronizzata da Drive per desktop,
/// leggendo (in sola lettura) l'elenco locale che Drive per desktop tiene in
/// %LOCALAPPDATA%\Google\DriveFS\&lt;account&gt;\metadata_sqlite_db.
/// È un formato interno di Google: se cambia, la ricerca semplicemente non trova nulla
/// e l'app ripiega sulla ricerca per nome su drive.google.com.
/// </summary>
public static class DriveLocator
{
    /// <summary>Indirizzo web per aprire il file su Google Drive, o null se non trovato.</summary>
    public static string? WebUrl(string path)
    {
        var id = FindId(path);
        if (id == null) return null;
        // I .xlsx si aprono direttamente in Google Fogli (modifica file Office);
        // gli altri formati nell'anteprima di Drive, che ha il suo pulsante di stampa.
        return Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? $"https://docs.google.com/spreadsheets/d/{id}/edit"
            : $"https://drive.google.com/file/d/{id}/view";
    }

    /// <summary>Ricerca per nome su drive.google.com, quando l'ID non è disponibile.</summary>
    public static string SearchUrl(string path) =>
        "https://drive.google.com/drive/search?q=" + Uri.EscapeDataString(Path.GetFileName(path));

    public static string? FindId(string path)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "DriveFS");
        if (!Directory.Exists(root)) return null;

        // Cartelle sopra il file, dalla più vicina: servono a distinguere file con lo stesso nome.
        var folders = new List<string>();
        for (var dir = Path.GetDirectoryName(path); !string.IsNullOrEmpty(dir) && Path.GetFileName(dir).Length > 0; dir = Path.GetDirectoryName(dir))
            folders.Add(Path.GetFileName(dir));

        (string Id, int Score)? best = null;
        foreach (var account in Directory.EnumerateDirectories(root))
        {
            var db = Path.Combine(account, "metadata_sqlite_db");
            if (!File.Exists(db)) continue;
            try
            {
                foreach (var candidate in Candidates(db, Path.GetFileName(path), folders))
                    if (best == null || candidate.Score > best.Value.Score) best = candidate;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                // Database non leggibile (bloccato o formato cambiato): si prova con gli altri account.
            }
        }
        // Gli ID locali (file non ancora caricati) non sono validi sul web.
        return best is { } b && !b.Id.StartsWith("local", StringComparison.OrdinalIgnoreCase) ? b.Id : null;
    }

    /// <summary>File con quel nome, con punteggio = quante cartelle superiori coincidono col percorso.</summary>
    static List<(string Id, int Score)> Candidates(string dbPath, string fileName, List<string> folders)
    {
        var result = new List<(string, int)>();
        using var db = new Sqlite(dbPath);
        var items = db.Query("SELECT id, stable_id FROM items WHERE local_title = ?1 AND trashed = 0 AND is_folder = 0", fileName);
        foreach (var row in items)
        {
            int score = 0;
            var stableId = row[1];
            foreach (var folder in folders)
            {
                var parent = db.Query(
                    "SELECT p.stable_id, p.local_title FROM stable_parents sp JOIN items p ON p.stable_id = sp.parent_stable_id WHERE sp.item_stable_id = ?1",
                    stableId).FirstOrDefault();
                if (parent == null || !string.Equals(parent[1], folder, StringComparison.OrdinalIgnoreCase)) break;
                score++;
                stableId = parent[0];
            }
            result.Add((row[0], score));
        }
        return result;
    }

    /// <summary>Accesso minimo in sola lettura a SQLite tramite la libreria inclusa in Windows (winsqlite3.dll).</summary>
    sealed class Sqlite : IDisposable
    {
        const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101, SQLITE_OPEN_READONLY = 1;
        const string Lib = "winsqlite3.dll";

        [DllImport(Lib, CharSet = CharSet.Ansi)] static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Lib)] static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Lib)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Lib, CharSet = CharSet.Unicode)] static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport(Lib, CharSet = CharSet.Unicode)] static extern int sqlite3_bind_text16(IntPtr stmt, int index, string value, int bytes, IntPtr destructor);
        [DllImport(Lib)] static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Lib)] static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport(Lib)] static extern IntPtr sqlite3_column_text16(IntPtr stmt, int col);
        [DllImport(Lib)] static extern int sqlite3_finalize(IntPtr stmt);

        static readonly IntPtr Transient = new(-1);
        readonly IntPtr _db;

        public Sqlite(string path)
        {
            var utf8 = System.Text.Encoding.UTF8.GetBytes(path + "\0");
            if (sqlite3_open_v2(utf8, out _db, SQLITE_OPEN_READONLY, IntPtr.Zero) != SQLITE_OK)
            {
                sqlite3_close_v2(_db);
                throw new InvalidOperationException("Database di Drive non apribile.");
            }
            sqlite3_busy_timeout(_db, 2000);
        }

        public List<string[]> Query(string sql, string parameter)
        {
            if (sqlite3_prepare16_v2(_db, sql, -1, out var stmt, IntPtr.Zero) != SQLITE_OK)
                throw new InvalidOperationException("Struttura del database di Drive non riconosciuta.");
            try
            {
                sqlite3_bind_text16(stmt, 1, parameter, -1, Transient);
                var rows = new List<string[]>();
                int rc;
                while ((rc = sqlite3_step(stmt)) == SQLITE_ROW)
                {
                    var row = new string[sqlite3_column_count(stmt)];
                    for (int i = 0; i < row.Length; i++) row[i] = Marshal.PtrToStringUni(sqlite3_column_text16(stmt, i)) ?? "";
                    rows.Add(row);
                }
                if (rc != SQLITE_DONE) throw new InvalidOperationException("Lettura del database di Drive non riuscita.");
                return rows;
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
        }

        public void Dispose() => sqlite3_close_v2(_db);
    }
}
