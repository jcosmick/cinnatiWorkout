using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CinnatiWorkout.Skinfold;
using CinnatiWorkout.Workout;

namespace CinnatiWorkout.Views;

public partial class WorkoutEditor : UserControl, IDocumentEditor
{
    static readonly Regex BlockLetter = new(@"SCHEDA\s+(\w+)", RegexOptions.IgnoreCase);

    readonly WorkoutPlan _plan;

    public string FilePath => _plan.FilePath;
    public bool IsDirty => _plan.IsDirty;
    public event EventHandler? DirtyChanged;

    public WorkoutEditor(WorkoutPlan plan)
    {
        InitializeComponent();
        _plan = plan;
        DataContext = plan;
        AddBlockButton.IsEnabled = plan.CanAddBlock;
        plan.DirtyChanged += (_, _) => DirtyChanged?.Invoke(this, EventArgs.Empty);
        plan.Blocks.CollectionChanged += (_, _) => BuildBlocks();
        BuildBlocks();
    }

    public void Save()
    {
        CommitEdits();
        _plan.Save();
    }

    void BuildBlocks()
    {
        BlocksPanel.Children.Clear();
        foreach (var block in _plan.Blocks) BlocksPanel.Children.Add(BuildBlock(block));
        UpdateCounts();
    }

    void UpdateCounts()
    {
        int blocks = _plan.Blocks.Count;
        int exercises = _plan.Blocks.Sum(b => b.Rows.Count(r => !string.IsNullOrWhiteSpace(r[0])));
        BlockCountText.Text = blocks == 1 ? "1 scheda" : $"{blocks} schede";
        ExerciseCountText.Text = exercises == 1 ? "1 esercizio" : $"{exercises} esercizi";
    }

    UIElement BuildBlock(WorkoutBlock block)
    {
        // Altezza riga automatica: le note lunghe vanno a capo e restano interamente visibili.
        var grid = new DataGrid { ItemsSource = block.Rows, Tag = block, RowHeight = double.NaN, MinRowHeight = 38 };
        for (int i = 0; i < block.Headers.Count; i++)
        {
            var header = block.Headers[i];
            bool isName = i == 0;
            bool isNote = header.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase);
            bool isShort = header is "REC" or "KG";
            // Le colonne con testi più lunghi (es. "TEST 6 + 2X4") ricevono più spazio.
            int longest = block.Rows.Select(r => r[i].Length).Append(header.Length).Max();
            double weight = Math.Clamp(longest / 6.5, 1, 2.2);
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header.ToUpper(Num.Culture),
                Binding = new Binding($"[{i}]") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = isName ? new DataGridLength(2.1, DataGridLengthUnitType.Star)
                      : isShort ? new DataGridLength(68)
                      : new DataGridLength(isNote ? Math.Max(weight, 1.1) : weight, DataGridLengthUnitType.Star),
                MinWidth = isName ? 170 : isNote ? 80 : Math.Max(60, longest * 7.5 + 24),
                ElementStyle = (Style)FindResource(isName ? "CellTextWrapStrong" : "CellTextWrap"),
                EditingElementStyle = (Style)FindResource("CellEditorWrap"),
            });
        }
        grid.Columns.Add(new DataGridTemplateColumn
        {
            CellTemplate = (DataTemplate)FindResource("RowActions"),
            Width = new DataGridLength(90),
            IsReadOnly = true,
        });
        // La griglia non ha uno scorrimento proprio: la rotellina scorre la pagina.
        grid.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta / 3.0);
        };

        // Intestazione della card: lettera, nome modificabile, conteggio, azioni.
        var letter = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var badge = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(10),
            Child = letter,
        };
        letter.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
        badge.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");

        var nameBox = new TextBox { Style = (Style)FindResource("InlineTitleBox"), MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
        nameBox.SetBinding(TextBox.TextProperty, new Binding(nameof(WorkoutBlock.Name)) { Source = block, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        var count = new TextBlock { Style = (Style)FindResource("Muted") };
        var titles = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(nameBox);
        titles.Children.Add(count);

        void Refresh()
        {
            var match = BlockLetter.Match(block.Name);
            letter.Text = match.Success ? match.Groups[1].Value.ToUpper(Num.Culture) : block.Name.Trim().FirstOrDefault().ToString();
            int n = block.Rows.Count(r => !string.IsNullOrWhiteSpace(r[0]));
            count.Text = n == 1 ? "1 esercizio" : $"{n} esercizi";
            UpdateCounts();
        }
        block.PropertyChanged += (_, _) => Refresh();
        block.Rows.CollectionChanged += (_, _) => Refresh();
        foreach (var row in block.Rows) row.PropertyChanged += (_, _) => Refresh();
        block.Rows.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (ExerciseRow row in e.NewItems) row.PropertyChanged += (_, _) => Refresh();
        };
        Refresh();

        var deleteBlock = new Button { Style = (Style)FindResource("DangerIconButton"), Tag = "", ToolTip = "Elimina scheda" };
        deleteBlock.Click += (_, _) => RemoveBlock(block);

        var headerBar = new Grid { Margin = new Thickness(20, 16, 14, 14) };
        headerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerBar.Children.Add(badge);
        Grid.SetColumn(titles, 1);
        headerBar.Children.Add(titles);
        Grid.SetColumn(deleteBlock, 2);
        deleteBlock.VerticalAlignment = VerticalAlignment.Top;
        headerBar.Children.Add(deleteBlock);

        var addRow = new Button
        {
            Style = (Style)FindResource("AccentGhostButton"),
            Tag = "",
            Content = "Aggiungi esercizio",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(10, 8, 10, 10),
        };
        addRow.Click += (_, _) => AddRow(block, grid);

        var body = new StackPanel();
        body.Children.Add(headerBar);
        body.Children.Add(grid);
        body.Children.Add(addRow);

        return new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 20),
            Child = body,
        };
    }

    static void AddRow(WorkoutBlock block, DataGrid grid)
    {
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        var row = new ExerciseRow(block.Headers.Count);
        int index = grid.SelectedIndex >= 0 ? grid.SelectedIndex + 1 : block.Rows.Count;
        block.Rows.Insert(index, row);
        grid.SelectedItem = row;
        grid.UpdateLayout();
        grid.ScrollIntoView(row);
        grid.CurrentCell = new DataGridCellInfo(row, grid.Columns[0]);
        grid.Focus();
        grid.BeginEdit();
    }

    // --- azioni di riga (dal template RowActions) ----------------------------------------

    void MoveUp_Click(object sender, RoutedEventArgs e) => MoveRow(sender, -1);
    void MoveDown_Click(object sender, RoutedEventArgs e) => MoveRow(sender, +1);

    void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (RowContext(sender) is not (DataGrid grid, WorkoutBlock block, ExerciseRow row)) return;
        grid.CancelEdit();
        block.Rows.Remove(row);
    }

    static void MoveRow(object sender, int delta)
    {
        if (RowContext(sender) is not (DataGrid grid, WorkoutBlock block, ExerciseRow row)) return;
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        int from = block.Rows.IndexOf(row), to = from + delta;
        if (to < 0 || to >= block.Rows.Count) return;
        block.Rows.Move(from, to);
        grid.SelectedItem = row;
    }

    static (DataGrid Grid, WorkoutBlock Block, ExerciseRow Row)? RowContext(object sender)
    {
        if (sender is not FrameworkElement { DataContext: ExerciseRow row } element) return null;
        DependencyObject? node = element;
        while (node != null && node is not DataGrid) node = VisualTreeHelper.GetParent(node);
        return node is DataGrid { Tag: WorkoutBlock block } grid ? (grid, block, row) : null;
    }

    // --- schede ----------------------------------------------------------------------------

    void RemoveBlock(WorkoutBlock block)
    {
        if (MessageDialog.Show(Window.GetWindow(this)!, $"Eliminare \"{block.Name}\" con tutti i suoi esercizi?", "Elimina scheda",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, yesText: "Elimina", noText: "Annulla", danger: true) == MessageBoxResult.Yes)
            _plan.RemoveBlock(block);
    }

    void AddBlock_Click(object sender, RoutedEventArgs e)
    {
        _plan.AddBlock();
        Dispatcher.InvokeAsync(() => Scroller.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void CommitEdits()
    {
        foreach (var grid in BlocksPanel.Children.OfType<Border>().Select(b => b.Child).OfType<StackPanel>().SelectMany(p => p.Children.OfType<DataGrid>()))
            grid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) CommitEdits();
        base.OnPreviewKeyDown(e);
    }
}
