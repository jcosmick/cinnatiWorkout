using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace CinnatiWorkout.Views;

public partial class SettingsWindow : Window
{
    readonly AppSettings _settings;
    readonly string _originalAccent;
    string _accent;
    bool _updatingHex;

    public SettingsWindow(AppSettings settings, bool firstRun)
    {
        InitializeComponent();
        _settings = settings;
        _originalAccent = _accent = settings.AccentColor;
        RootBox.Text = settings.RootPath ?? (firstRun ? AppSettings.DetectGoogleDrive() ?? "" : "");
        if (firstRun)
        {
            Title = "Cinnati Workout – primo avvio";
            HeaderText.Text = "Configura Cinnati Workout";
            IntroText.Text = "Primo avvio: scegli la cartella principale su Google Drive in cui si trovano schede e pliche. " +
                             "Potrai cambiarla in seguito dalle Impostazioni.";
        }

        foreach (var (name, hex) in ThemeManager.Presets)
        {
            var swatch = new Button
            {
                Style = (Style)FindResource("Swatch"),
                Background = new SolidColorBrush(ThemeManager.TryParse(hex)!.Value),
                ToolTip = name,
                DataContext = hex,
            };
            swatch.Click += (_, _) => SelectAccent(hex);
            SwatchPanel.Children.Add(swatch);
        }
        SelectAccent(_accent);
    }

    // --- colore -------------------------------------------------------------------------

    /// <summary>Applica subito il colore all'app (anteprima) e aggiorna selettore e campo esadecimale.</summary>
    void SelectAccent(string hex, bool fromHexBox = false)
    {
        var color = ThemeManager.TryParse(hex);
        if (color == null) return;
        _accent = ThemeManager.ToHex(color.Value);
        ThemeManager.Apply(_accent);

        foreach (var swatch in SwatchPanel.Children.OfType<Button>())
            swatch.Tag = string.Equals((string)swatch.DataContext, _accent, StringComparison.OrdinalIgnoreCase) ? "selected" : null;
        CustomPreview.Background = new SolidColorBrush(color.Value);
        HexError.Visibility = Visibility.Collapsed;

        if (!fromHexBox)
        {
            _updatingHex = true;
            HexBox.Text = _accent;
            _updatingHex = false;
        }
    }

    void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingHex) return;
        if (ThemeManager.TryParse(HexBox.Text) != null) SelectAccent(HexBox.Text, fromHexBox: true);
        else HexError.Visibility = HexBox.Text.Trim().Length >= 6 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ResetColor_Click(object sender, RoutedEventArgs e) => SelectAccent(ThemeManager.DefaultAccent);

    protected override void OnClosing(CancelEventArgs e)
    {
        // Chiusura senza salvare: si torna al colore di prima.
        if (DialogResult != true) ThemeManager.Apply(_originalAccent);
        base.OnClosing(e);
    }

    // --- cartella -----------------------------------------------------------------------

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Cartella principale" };
        if (Directory.Exists(RootBox.Text)) dialog.InitialDirectory = RootBox.Text;
        if (dialog.ShowDialog(this) == true) RootBox.Text = dialog.FolderName;
    }

    void Detect_Click(object sender, RoutedEventArgs e)
    {
        var path = AppSettings.DetectGoogleDrive();
        if (path != null) RootBox.Text = path;
        else MessageDialog.Show(this, "Cartella di Google Drive non trovata. Verifica che Google Drive per desktop sia installato e avviato, oppure usa \"Sfoglia…\".",
            "Google Drive", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var path = RootBox.Text.Trim();
        if (!Directory.Exists(path))
        {
            MessageDialog.Show(this, "La cartella indicata non esiste.", "Impostazioni", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.RootPath = path;
        _settings.AccentColor = _accent;
        _settings.Save();
        DialogResult = true;
    }
}
