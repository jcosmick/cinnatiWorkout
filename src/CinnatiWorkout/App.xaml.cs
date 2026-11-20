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
            MessageDialog.Show(null, args.Exception.Message, "Errore", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var settings = AppSettings.Load();
        ThemeManager.Apply(settings.AccentColor);
        if (!settings.HasValidRoot)
        {
            var dialog = new SettingsWindow(settings, firstRun: true);
            if (dialog.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
        }

        var main = new MainWindow(settings);
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
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
