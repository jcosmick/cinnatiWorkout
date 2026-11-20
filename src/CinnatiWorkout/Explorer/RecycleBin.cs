using System.IO;
using System.Runtime.InteropServices;

namespace CinnatiWorkout.Explorer;

/// <summary>Elimina file e cartelle spostandoli nel Cestino di Windows (come Esplora file).</summary>
public static class RecycleBin
{
    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400, FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>
    /// true se eliminato, false se l'utente ha annullato (Windows chiede conferma solo quando
    /// l'unità non ha un Cestino e l'eliminazione sarebbe definitiva).
    /// </summary>
    public static bool Delete(string path, IntPtr owner)
    {
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = FO_DELETE,
            pFrom = path + "\0", // l'elenco termina con un doppio carattere nullo
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        var result = SHFileOperation(ref op);
        if (op.fAnyOperationsAborted) return false;
        if (result != 0) throw new IOException($"Windows ha restituito l'errore 0x{result:X}.");
        return !File.Exists(path) && !Directory.Exists(path);
    }
}
