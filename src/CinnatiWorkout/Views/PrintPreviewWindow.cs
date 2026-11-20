using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using CinnatiWorkout.Printing;

namespace CinnatiWorkout.Views;

/// <summary>
/// Anteprima di stampa disegnata dall'app (nessun programma o servizio esterno):
/// mostra le pagine come usciranno, permette di scegliere i fogli e stampa con la finestra di Windows.
/// </summary>
public sealed class PrintPreviewWindow : Window
{
    readonly string _documentName;
    readonly IReadOnlyList<Sheet> _sheets;
    readonly List<CheckBox> _sheetChecks = [];
    readonly StackPanel _pagesPanel = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 24) };
    readonly ScrollViewer _scroller;
    readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly Button _printButton;
    List<PrintPage> _pages = [];

    public bool Printed { get; private set; }

    PrintPreviewWindow(Window owner, string documentName, IReadOnlyList<Sheet> sheets, ICollection<string> selected)
    {
        Owner = owner;
        Icon = owner.Icon;
        Title = $"Stampa – {documentName}";
        _documentName = documentName;
        _sheets = sheets;
        Width = Math.Min(1100, SystemParameters.WorkArea.Width * 0.9);
        Height = SystemParameters.WorkArea.Height * 0.9;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var root = new DockPanel();

        // --- barra superiore: titolo, riepilogo pagine, pulsanti
        var bar = new DockPanel { Margin = new Thickness(24, 16, 24, 16), LastChildFill = true };
        var barBorder = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        barBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        barBorder.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        DockPanel.SetDock(barBorder, Dock.Top);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        var cancel = new Button { Content = "Annulla", MinWidth = 100, IsCancel = true };
        cancel.Click += (_, _) => Close();
        _printButton = new Button { Content = "Stampa…", Tag = "", MinWidth = 120, Margin = new Thickness(8, 0, 0, 0), IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        _printButton.Click += (_, _) => Print();
        buttons.Children.Add(cancel);
        buttons.Children.Add(_printButton);
        bar.Children.Add(buttons);

        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock { Text = "Anteprima di stampa", Style = (Style)FindResource("PageTitle") });
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _summary.Margin = new Thickness(0, 2, 0, 0);
        heading.Children.Add(_summary);
        bar.Children.Add(heading);
        root.Children.Add(barBorder);

        // --- elenco fogli (solo se il file ne ha più d'uno)
        if (sheets.Count > 1)
        {
            var list = new StackPanel { Margin = new Thickness(20, 20, 20, 20) };
            list.Children.Add(new TextBlock { Text = "FOGLI DA STAMPARE", Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 0, 0, 10) });
            foreach (var sheet in sheets)
            {
                var check = new CheckBox
                {
                    Content = Label(sheet.Name),
                    Tag = sheet,
                    IsChecked = selected.Contains(sheet.Name),
                    Margin = new Thickness(0, 4, 0, 4),
                    Cursor = Cursors.Hand,
                };
                check.SetResourceReference(ForegroundProperty, "TextBrush");
                check.Checked += (_, _) => Rebuild();
                check.Unchecked += (_, _) => Rebuild();
                _sheetChecks.Add(check);
                list.Children.Add(check);
            }
            var side = new Border
            {
                Width = 220,
                BorderThickness = new Thickness(0, 0, 1, 0),
                Child = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            };
            side.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            side.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            DockPanel.SetDock(side, Dock.Left);
            root.Children.Add(side);
        }

        // --- pagine
        _scroller = new ScrollViewer
        {
            Content = _pagesPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false,
        };
        _scroller.SizeChanged += (_, _) => UpdateZoom();
        _scroller.Loaded += (_, _) => UpdateZoom();
        root.Children.Add(_scroller);

        Content = root;
        InputBindings.Add(new KeyBinding(new RelayCommand(Print), Key.P, ModifierKeys.Control));
        Rebuild();
    }

    /// <summary>Mostra l'anteprima; true se è stato inviato qualcosa alla stampante.</summary>
    public static bool Show(Window owner, string documentName, IReadOnlyList<Sheet> sheets, ICollection<string> selected)
    {
        var window = new PrintPreviewWindow(owner, documentName, sheets, selected);
        window.ShowDialog();
        return window.Printed;
    }

    static string Label(string sheetName) =>
        sheetName.StartsWith("P.") && int.TryParse(sheetName[2..], out var n) ? $"Rilevazione {n}" : sheetName;

    IEnumerable<Sheet> SelectedSheets =>
        _sheetChecks.Count == 0 ? _sheets : _sheetChecks.Where(c => c.IsChecked == true).Select(c => (Sheet)c.Tag);

    void Rebuild()
    {
        _pages = SelectedSheets.SelectMany(SheetRenderer.Paginate).ToList();
        _pagesPanel.Children.Clear();

        for (int i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            var paper = new Border
            {
                Background = Brushes.White,
                Child = new PrintPageElement(page),
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.18, Direction = 270 },
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var caption = new TextBlock
            {
                Text = $"Pagina {i + 1} di {_pages.Count}" + (_sheets.Count > 1 ? $"  ·  {Label(page.Sheet.Name)}" : ""),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 24),
                FontSize = 12,
            };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            _pagesPanel.Children.Add(paper);
            _pagesPanel.Children.Add(caption);
        }

        if (_pages.Count == 0)
        {
            var empty = new TextBlock { Text = "Seleziona almeno un foglio da stampare.", Margin = new Thickness(0, 60, 0, 0) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            _pagesPanel.Children.Add(empty);
        }

        _printButton.IsEnabled = _pages.Count > 0;
        bool mixed = _pages.Select(p => p.Sheet.Page.IsLandscape).Distinct().Count() > 1;
        _summary.Text = _pages.Count switch
        {
            0 => "Nessuna pagina",
            1 => $"1 pagina  ·  {PaperName(_pages[0].Paper)}",
            var n => $"{n} pagine  ·  {PaperName(_pages[0].Paper, withOrientation: !mixed)}",
        };
        UpdateZoom();
    }

    /// <summary>Adatta le pagine alla larghezza dell'area di anteprima (lasciando spazio alla barra di scorrimento).</summary>
    void UpdateZoom()
    {
        if (_pages.Count == 0 || _scroller.ActualWidth <= 0) return;
        double widest = _pages.Max(p => p.Paper.Width);
        double available = _scroller.ActualWidth - SystemParameters.VerticalScrollBarWidth - 64;
        double zoom = Math.Clamp(available / widest, 0.3, 1.25);
        _pagesPanel.LayoutTransform = new ScaleTransform(zoom, zoom);
    }

    static string PaperName(Size paper, bool withOrientation = true)
    {
        double w = Math.Min(paper.Width, paper.Height), h = Math.Max(paper.Width, paper.Height);
        string name = Near(w, Units.A4.Width) && Near(h, Units.A4.Height) ? "A4"
            : Near(w, Units.FromInch(8.5)) && Near(h, Units.FromInch(11)) ? "Letter"
            : Near(w, Units.FromMm(297)) && Near(h, Units.FromMm(420)) ? "A3"
            : Near(w, Units.FromMm(148)) && Near(h, Units.FromMm(210)) ? "A5"
            : $"{w / 96 * 25.4:0} × {h / 96 * 25.4:0} mm";
        return withOrientation ? $"{name} {(paper.Width > paper.Height ? "orizzontale" : "verticale")}" : name;
    }

    static bool Near(double a, double b) => Math.Abs(a - b) < 4;

    void Print()
    {
        if (_pages.Count == 0) return;

        var dialog = new PrintDialog
        {
            UserPageRangeEnabled = true,
            MinPage = 1,
            MaxPage = (uint)_pages.Count,
            PageRange = new PageRange(1, _pages.Count),
        };
        try
        {
            // Carta e orientamento proposti come nel file; le pagine con orientamento diverso vengono ruotate.
            var paper = _pages[0].Paper;
            dialog.PrintTicket.PageMediaSize = MediaSize(paper);
            dialog.PrintTicket.PageOrientation = _pages.Count(p => p.Sheet.Page.IsLandscape) * 2 > _pages.Count
                ? PageOrientation.Landscape
                : PageOrientation.Portrait;
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException)
        {
            // Nessuna stampante predefinita: la finestra di Windows lo segnalerà.
        }

        if (dialog.ShowDialog() != true) return;

        var selected = _pages;
        if (dialog.PageRangeSelection == PageRangeSelection.UserPages)
        {
            int from = Math.Max(1, dialog.PageRange.PageFrom), to = Math.Min(_pages.Count, Math.Max(from, dialog.PageRange.PageTo));
            selected = _pages.Skip(from - 1).Take(to - from + 1).ToList();
        }

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var document = SheetRenderer.CreateDocument(selected, new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight));
            dialog.PrintDocument(document.DocumentPaginator, _documentName);
            Printed = true;
            Close();
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException or System.IO.IOException)
        {
            MessageDialog.Show(this, $"Stampa non riuscita: {ex.Message}", "Stampa", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    static PageMediaSize MediaSize(Size paper)
    {
        double w = Math.Min(paper.Width, paper.Height), h = Math.Max(paper.Width, paper.Height);
        if (Near(w, Units.A4.Width) && Near(h, Units.A4.Height)) return new PageMediaSize(PageMediaSizeName.ISOA4);
        if (Near(w, Units.FromInch(8.5)) && Near(h, Units.FromInch(11))) return new PageMediaSize(PageMediaSizeName.NorthAmericaLetter);
        if (Near(w, Units.FromMm(297)) && Near(h, Units.FromMm(420))) return new PageMediaSize(PageMediaSizeName.ISOA3);
        if (Near(w, Units.FromMm(148)) && Near(h, Units.FromMm(210))) return new PageMediaSize(PageMediaSizeName.ISOA5);
        return new PageMediaSize(w, h);
    }

    sealed class RelayCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
