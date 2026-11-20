using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CinnatiWorkout;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>Contratto comune agli editor mostrati nella finestra principale.</summary>
public interface IDocumentEditor
{
    string FilePath { get; }
    bool IsDirty { get; }
    event EventHandler? DirtyChanged;
    void Save();
}
