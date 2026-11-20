using System.Collections.ObjectModel;
using System.IO;

namespace CinnatiWorkout.Explorer;

/// <summary>Nodo dell'albero cartelle; i figli vengono letti solo quando il nodo viene espanso.</summary>
public sealed class FileNode : Observable
{
    static readonly string[] SupportedExtensions = [".ods", ".xlsx"];

    bool _isExpanded, _loaded, _isDropTarget;

    public string Name { get; }
    public string FullPath { get; }
    public bool IsFolder { get; }
    public bool IsPlaceholder { get; }
    public FileNode? Parent { get; }
    public ObservableCollection<FileNode> Children { get; } = [];

    /// <summary>"folder", "workout" (.ods), "skinfold" (.xlsx) o "placeholder": usato dall'UI per icona e colore.</summary>
    public string Kind => IsPlaceholder ? "placeholder" : IsFolder ? "folder" : KindOf(FullPath);

    public static string KindOf(string path) =>
        Path.GetExtension(path).Equals(".ods", StringComparison.OrdinalIgnoreCase) ? "workout" : "skinfold";

    /// <summary>Evidenziato come destinazione durante il trascinamento.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => Set(ref _isDropTarget, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (Set(ref _isExpanded, value) && value) LoadChildren(); }
    }

    public FileNode(string path, bool isFolder, FileNode? parent, string? displayName = null)
    {
        FullPath = path;
        IsFolder = isFolder;
        Parent = parent;
        Name = displayName ?? Path.GetFileName(path);
        if (isFolder) Children.Add(new FileNode());
    }

    FileNode()
    {
        Name = "…";
        FullPath = "";
        IsPlaceholder = true;
    }

    public static bool IsSupportedFile(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public void Refresh()
    {
        _loaded = false;
        if (IsExpanded) LoadChildren();
        else if (IsFolder)
        {
            Children.Clear();
            Children.Add(new FileNode());
        }
    }

    void LoadChildren()
    {
        if (_loaded || !IsFolder) return;
        _loaded = true;
        Children.Clear();
        try
        {
            var dir = new DirectoryInfo(FullPath);
            foreach (var sub in dir.EnumerateDirectories()
                         .Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden) && !d.Name.StartsWith('.'))
                         .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
                Children.Add(new FileNode(sub.FullName, true, this));

            foreach (var file in dir.EnumerateFiles()
                         .Where(f => IsSupportedFile(f.Name) && !f.Name.StartsWith("~$") && !f.Name.StartsWith(".~lock"))
                         .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
                Children.Add(new FileNode(file.FullName, false, this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cartella non leggibile (permessi o Drive non ancora sincronizzato): la lasciamo vuota.
        }
    }
}
