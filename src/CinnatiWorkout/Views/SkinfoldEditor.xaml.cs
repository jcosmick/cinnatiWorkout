using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CinnatiWorkout.Skinfold;

namespace CinnatiWorkout.Views;

public partial class SkinfoldEditor : UserControl, IDocumentEditor
{
    SkinfoldWorkbook _workbook;

    public string FilePath => _workbook.FilePath;
    public bool IsDirty => _workbook.IsDirty;
    public event EventHandler? DirtyChanged;

    /// <summary>Il file è stato riscritto sull'altro modello (uomo/donna) e salvato.</summary>
    public event EventHandler<string>? Converted;

    public SkinfoldEditor(SkinfoldWorkbook workbook)
    {
        InitializeComponent();
        _workbook = workbook;
        Bind();
    }

    void Bind()
    {
        DataContext = _workbook;
        _workbook.DirtyChanged += (_, _) => DirtyChanged?.Invoke(this, EventArgs.Empty);
        MaleOption.IsChecked = !_workbook.IsFemale;
        FemaleOption.IsChecked = _workbook.IsFemale;
        RestoreBanner.Visibility = _workbook.CanRestoreLayout ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Save() => _workbook.Save();

    void GenderOption_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ChangeGender(sender == FemaleOption);
    }

    void GenderOption_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter)) return;
        e.Handled = true;
        ChangeGender(sender == FemaleOption);
    }

    void ChangeGender(bool female)
    {
        if (female == _workbook.IsFemale) return;
        var owner = Window.GetWindow(this)!;
        var target = _workbook.Layout.Other;

        var message = $"Il file verrà riscritto sul modello \"{target.Label}\" ({target.FoldRows.Length} pliche): " +
                      "cambiano formula, immagini e pliche del foglio. Atleta, date, età, peso, circonferenze, note " +
                      "e le pliche presenti in entrambi i modelli vengono mantenuti.";
        var lost = _workbook.LostWhenConverting(target);
        if (lost.Count > 0)
            message += $"\n\nAndranno persi i valori di: {string.Join(", ", lost)}.";
        else if (target.FoldRows.Length > _workbook.Layout.FoldRows.Length)
            message += "\n\nLe pliche in più del nuovo modello saranno da compilare.";
        if (_workbook.IsDirty)
            message += "\n\nAnche le modifiche non salvate verranno salvate.";
        message += "\n\nProcedere?";

        if (MessageDialog.Show(owner, message, $"Passa al modello {target.Label.ToLowerInvariant()}",
                MessageBoxButton.YesNo, lost.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question,
                yesText: "Cambia modello", noText: "Annulla") != MessageBoxResult.Yes)
            return;

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var selected = MeasureList.SelectedIndex;
            _workbook = _workbook.ConvertTo(target);
            Bind();
            MeasureList.SelectedIndex = Math.Max(0, selected);
            DirtyChanged?.Invoke(this, EventArgs.Empty);
            Converted?.Invoke(this, target.Label);
        }
        catch (IOException ex)
        {
            MessageDialog.Show(owner, $"Impossibile cambiare modello: {ex.Message}\n\nIl file potrebbe essere aperto in un altro programma.",
                "Cambia modello", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    void RestoreLayout_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this)!;
        if (MessageDialog.Show(owner,
                $"Il file verrà ricostruito dal modello originale \"Pliche {_workbook.Layout.Label.ToLowerInvariant()}\": tornano logo e immagini corretti, " +
                "e vengono riportati atleta, date, misure e note di tutte le rilevazioni.\n\nProcedere?",
                "Ripristina immagini", MessageBoxButton.YesNo, MessageBoxImage.Question, yesText: "Ripristina", noText: "Annulla") != MessageBoxResult.Yes)
            return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            _workbook.RestoreLayoutFromTemplate();
            RestoreBanner.Visibility = Visibility.Collapsed;
        }
        catch (IOException ex)
        {
            MessageDialog.Show(owner, $"Impossibile ripristinare: {ex.Message}\n\nIl file potrebbe essere aperto in un altro programma.",
                "Ripristina immagini", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }
}
