using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using CinnatiWorkout.Views;

namespace CinnatiWorkout;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var italian = CultureInfo.GetCultureInfo("it-IT");
        CultureInfo.DefaultThreadCurrentCulture = italian;
        CultureInfo.DefaultThreadCurrentUICulture = italian;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(italian.IetfLanguageTag)));

        // Un clic su una cella modificabile entra subito in modifica, come in un foglio di calcolo.
        EventManager.RegisterClassHandler(typeof(DataGridCell), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(BeginEditOnClick));
        // Quando il focus esce da una tabella (es. clic su una cella di un'altra scheda),
        // la cella in modifica viene confermata e torna allo stile normale.
        EventManager.RegisterClassHandler(typeof(DataGrid), UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((sender, _) =>
        {
            if (sender is DataGrid { IsKeyboardFocusWithin: false } grid) grid.CommitEdit(DataGridEditingUnit.Row, true);
        }));
        // Navigazione da tastiera (Tab/frecce): la cella che riceve il focus entra in modifica.
        EventManager.RegisterClassHandler(typeof(DataGridCell), UIElement.GotFocusEvent, new RoutedEventHandler((sender, args) =>
        {
            if (_clicking || args.OriginalSource is not DataGridCell { IsEditing: false, IsReadOnly: false } cell) return;
            var grid = FindAncestor<DataGrid>(cell);
            grid?.BeginEdit(args);
        }));

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            // Durante la chiusura non si mostrano finestre: il processo deve solo terminare.
            if (!_exiting) MessageDialog.Show(null, args.Exception.Message, "Errore", MessageBoxButton.OK, MessageBoxImage.Error);
            // Se non resta nessuna finestra visibile (errore all'avvio, prima della finestra principale)
            // nessuno chiuderebbe più l'app: con ShutdownMode="OnExplicitShutdown" il processo
            // resterebbe vivo in background senza finestre.
            if (_exiting || !Windows.OfType<Window>().Any(w => w.IsVisible)) Shutdown(1);
        };

        try
        {
            if (!ShowMainWindow()) Shutdown();
        }
        catch (Exception ex)
        {
            // Errore all'avvio: si avvisa e si chiude, invece di lasciare un processo senza finestre.
            MessageDialog.Show(null, ex.Message, "Errore", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Apre la finestra principale (dopo la configurazione iniziale, se serve). false = l'utente ha rinunciato.</summary>
    bool ShowMainWindow()
    {
        var settings = AppSettings.Load();
        ThemeManager.Apply(settings.AccentColor);
        // Senza cartella principale valida la finestra principale si apre direttamente sulle impostazioni (primo avvio).

        var main = new MainWindow(settings);
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        // Chiusa la finestra principale l'app termina sempre, qualunque finestra sia ancora aperta
        // e qualunque sia lo ShutdownMode in quel momento.
        main.Closed += (_, _) => Shutdown();
        main.Show();
        return true;
    }

    static bool _exiting;

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        base.OnExit(e);
        StartExitWatchdog();
    }

    /// <summary>
    /// Rete di sicurezza: se dopo la chiusura dell'interfaccia il processo è ancora vivo
    /// (thread in primo piano, componenti COM/shell caricati dalle finestre di sistema, ecc.),
    /// dopo qualche secondo viene terminato. Tutti i salvataggi sono sincroni e già conclusi a questo punto.
    /// </summary>
    static void StartExitWatchdog()
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(3));
            try { Environment.Exit(Environment.ExitCode); }
            catch { System.Diagnostics.Process.GetCurrentProcess().Kill(); }
        })
        {
            IsBackground = true, // non tiene in vita il processo se la chiusura normale va a buon fine
            Name = "Exit watchdog",
        };
        watchdog.Start();
    }

    static bool _clicking;

    static void BeginEditOnClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridCell { IsEditing: false, IsReadOnly: false } cell) return;
        if (FindAncestor<DataGrid>(cell) is not { } grid) return;

        // Chiudendo la modifica, la cella precedente riprende il focus: senza questo flag
        // il gestore GotFocus la rimetterebbe subito in modifica.
        _clicking = true;
        try
        {
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            grid.CurrentCell = new DataGridCellInfo(cell);
            if (grid.SelectionUnit == DataGridSelectionUnit.FullRow) grid.SelectedItem = cell.DataContext;
            else cell.IsSelected = true;
            // Passando l'evento del mouse, il cursore viene posizionato dove si è cliccato.
            if (grid.BeginEdit(e)) e.Handled = true;
        }
        finally
        {
            _clicking = false;
        }
    }

    static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null && node is not T) node = VisualTreeHelper.GetParent(node);
        return node as T;
    }
}
