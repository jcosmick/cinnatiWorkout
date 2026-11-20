using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CinnatiWorkout.Explorer;
using CinnatiWorkout.Skinfold;
using CinnatiWorkout.Workout;

namespace CinnatiWorkout.Views;

public partial class MainWindow : Window
{
    readonly AppSettings _settings;
    FileNode? _root;
    IDocumentEditor? _editor;
    string? _openPath;
    bool _suppressSelection;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        LoadRoot();
    }

    void LoadRoot()
    {
        _root = new FileNode(_settings.RootPath!, true, null, Path.GetFileName(_settings.RootPath!.TrimEnd('\\')) is { Length: > 0 } n ? n : _settings.RootPath);
        _root.IsExpanded = true;
        FolderTree.ItemsSource = new[] { _root };
        RootPathText.Text = _settings.RootPath;
        RootPathText.ToolTip = _settings.RootPath;
        UpdateStatus();
    }

    // --- apertura file -------------------------------------------------------------------

    void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressSelection) return;
        if (e.NewValue is FileNode { IsFolder: false, IsPlaceholder: false } node && node.FullPath != _openPath)
            OpenFile(node.FullPath);
    }

    void OpenFile(string path)
    {
        if (!ConfirmDiscard()) return;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            _editor = CreateEditor(path);
            _openPath = path;
            _editor.DirtyChanged += (_, _) => UpdateStatus();
            if (_editor is SkinfoldEditor skinfold)
                skinfold.Converted += (_, label) => ShowToast($"Modello cambiato in \"{label}\" e file salvato");
            EditorHost.Content = _editor;
        }
        catch (Exception ex)
        {
            _editor = null;
            _openPath = path;
            EditorHost.Content = UnsupportedPanel(path, ex.Message);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        UpdateStatus();
    }

    static IDocumentEditor CreateEditor(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".ods":
                var plan = WorkoutPlan.Load(path);
                if (plan.Blocks.Count == 0)
                    throw new NotSupportedException("Il file non sembra una scheda di allenamento: nessuna riga \"SCHEDA …:\" trovata.");
                return new WorkoutEditor(plan);
            case ".xlsx":
                return new SkinfoldEditor(SkinfoldWorkbook.Load(path));
            default:
                throw new NotSupportedException("Formato non supportato.");
        }
    }

    UIElement UnsupportedPanel(string path, string message)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "\uE7BA", Style = (Style)FindResource("Icon"), FontSize = 26, Foreground = (Brush)FindResource("WarningTextBrush"), HorizontalAlignment = HorizontalAlignment.Left });
        panel.Children.Add(new TextBlock { Text = "Impossibile aprire il file in Cinnati Workout", Style = (Style)FindResource("CardTitle"), FontSize = 16, Margin = new Thickness(0, 12, 0, 6) });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("MutedBrush") });
        return new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(28),
            Child = panel,
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>Chiede cosa fare delle modifiche non salvate. false = l'utente ha annullato.</summary>
    bool ConfirmDiscard()
    {
        if (_editor is not { IsDirty: true }) return true;
        var answer = MessageDialog.Show(this, $"Salvare le modifiche a \"{Path.GetFileName(_editor.FilePath)}\"?", "Modifiche non salvate",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, yesText: "Salva", noText: "Non salvare");
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return TrySave();
        return true;
    }

    bool TrySave()
    {
        if (_editor == null) return true;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            _editor.Save();
            ShowToast($"Salvato alle {DateTime.Now:HH:mm}");
            return true;
        }
        catch (IOException ex)
        {
            MessageDialog.Show(this, $"Impossibile salvare: {ex.Message}\n\nIl file potrebbe essere aperto in un altro programma (Excel, LibreOffice).",
                "Errore di salvataggio", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            UpdateTitle();
        }
    }

    void UpdateStatus()
    {
        bool hasFile = _openPath != null;
        TopBar.Visibility = hasFile ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasFile ? Visibility.Collapsed : Visibility.Visible;
        if (hasFile)
        {
            bool workout = FileNode.KindOf(_openPath!) == "workout";
            FileIcon.Text = workout ? "\uF0E3" : "\uEB05";
            FileIcon.SetResourceReference(TextBlock.ForegroundProperty, workout ? "AccentTextBrush" : "SkinfoldBrush");
            FileIconBox.SetResourceReference(Border.BackgroundProperty, workout ? "AccentSoftBrush" : "SkinfoldSoftBrush");
            FileNameText.Text = Path.GetFileNameWithoutExtension(_openPath);
            FilePathText.Text = RelativeFolder(_openPath!);
        }
        UpdateTitle();
    }

    void UpdateTitle()
    {
        bool dirty = _editor?.IsDirty == true;
        SaveButton.IsEnabled = _editor != null;
        DirtyBadge.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        Title = _openPath == null
            ? "Cinnati Workout"
            : $"{(dirty ? "● " : "")}{Path.GetFileName(_openPath)} – Cinnati Workout";
    }

    string RelativeFolder(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var relative = Path.GetRelativePath(_settings.RootPath!, folder);
        var rootName = Path.GetFileName(_settings.RootPath!.TrimEnd('\\'));
        return relative == "." ? rootName : $"{rootName}  ›  {relative.Replace("\\", "  ›  ")}";
    }

    void ShowToast(string message)
    {
        ToastText.Text = message;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2200))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2600))));
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    // --- comandi barra strumenti -------------------------------------------------------

    void Save_Executed(object sender, ExecutedRoutedEventArgs e) => TrySave();

    void Print_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _editor != null;

    /// <summary>
    /// Stampa il file così com'è: lo salva e lo apre su Google Drive nel browser
    /// (Google Fogli per .xlsx, anteprima di Drive per .ods), dove si stampa con Ctrl+P.
    /// </summary>
    void Print_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_editor == null || _openPath == null) return;
        bool justSaved = false;
        if (_editor.IsDirty)
        {
            var answer = MessageDialog.Show(this,
                "Per stampare il file così com'è va prima salvato. Salvare le modifiche?\n\n" +
                "Scegliendo No si stampa l'ultima versione salvata.",
                "Stampa", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, yesText: "Salva e stampa", noText: "Non salvare");
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                if (!TrySave()) return;
                justSaved = true;
            }
        }

        Mouse.OverrideCursor = Cursors.Wait;
        string? url;
        try
        {
            url = DriveLocator.WebUrl(_openPath);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (url == null)
        {
            if (MessageDialog.Show(this,
                    "Non trovo questo file su Google Drive: forse non è ancora stato caricato (file appena creato) " +
                    "oppure la cartella non è sincronizzata da Drive per desktop.\n\n" +
                    "Aprire la ricerca del file su drive.google.com?",
                    "Stampa", MessageBoxButton.YesNo, MessageBoxImage.Information, yesText: "Apri ricerca", noText: "Annulla") != MessageBoxResult.Yes)
                return;
            url = DriveLocator.SearchUrl(_openPath);
        }

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        ShowToast(justSaved
            ? "Aperto su Google Drive: attendi la sincronizzazione, poi stampa con Ctrl+P"
            : "Aperto su Google Drive: stampa con Ctrl+P");
    }

    void NewWorkout_Click(object sender, RoutedEventArgs e)
    {
        var values = PromptWindow.Ask(this, "Nuova scheda allenamento", "\uF0E3",
            ("Nome del file (es. MARIO ROSSI NOVEMBRE-DICEMBRE 2026)", ""));
        if (values == null || !TryTargetPath(values[0], ".ods", out var path)) return;
        if (!ConfirmDiscard()) return;

        Templates.CopyTo(Templates.Workout, path);
        var plan = WorkoutPlan.Load(path);
        plan.ClearAll();
        // L'intestazione grande del foglio riprende il nome del file (come "GIADA OTTOBRE-NOVEMBRE 2026").
        plan.Title = Path.GetFileNameWithoutExtension(path).ToUpper(Num.Culture);
        plan.Save();
        AfterCreate(path);
    }

    void NewSkinfold_Click(object sender, RoutedEventArgs e)
    {
        var answer = PromptWindow.AskWithChoice(this, "Nuova scheda pliche", "\uEB05",
            ("Modello", ["Uomo", "Donna"], 0),
            ("Nome del file (es. Pliche Mario Rossi)", ""),
            ("Nome atleta", ""));
        if (answer is not ({ } values, var choice) || !TryTargetPath(values[0], ".xlsx", out var path)) return;
        if (!ConfirmDiscard()) return;

        Templates.CopyTo(choice == 1 ? Templates.SkinfoldFemale : Templates.SkinfoldMale, path);
        using (var workbook = SkinfoldWorkbook.Load(path))
        {
            workbook.Athlete = values[1];
            workbook.Save();
        }
        AfterCreate(path);
    }

    void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var values = PromptWindow.Ask(this, "Nuova cartella", "\uED25", ("Nome della cartella", ""));
        if (values == null || !ValidName(values[0])) return;
        var path = Path.Combine(TargetFolder(), values[0]);
        if (Directory.Exists(path))
        {
            MessageDialog.Show(this, "Esiste già una cartella con questo nome.", "Nuova cartella", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Directory.CreateDirectory(path);
        RefreshFolder(Path.GetDirectoryName(path)!);
    }

    void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_openPath != null) DuplicateFile(_openPath);
    }

    void DuplicateFile(string source)
    {
        var ext = Path.GetExtension(source);
        var name = PromptWindow.AskOne(this, "Duplica file", "\uE8C8", "Duplica", "Nome della copia",
            Path.GetFileNameWithoutExtension(source) + " - copia");
        if (name == null || !ValidName(name)) return;
        if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name = name[..^ext.Length];
        var path = Path.Combine(Path.GetDirectoryName(source)!, name + ext);
        if (File.Exists(path))
        {
            MessageDialog.Show(this, "Esiste già un file con questo nome.", "Duplica", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!ConfirmDiscard()) return;
        File.Copy(source, path);
        AfterCreate(path);
    }

    // --- rinomina ----------------------------------------------------------------------

    void RenameOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_openPath != null) Rename(_openPath, isFolder: false);
    }

    void RenameNode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileNode { IsPlaceholder: false } node }) Rename(node.FullPath, node.IsFolder);
    }

    void DuplicateNode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileNode { IsFolder: false, IsPlaceholder: false } node }) DuplicateFile(node.FullPath);
    }

    void RevealNode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileNode { IsPlaceholder: false } node })
            Process.Start("explorer.exe", $"/select,\"{node.FullPath}\"");
    }

    void FolderTree_KeyDown(object sender, KeyEventArgs e)
    {
        if (FolderTree.SelectedItem is not FileNode { IsPlaceholder: false } node) return;
        if (e.Key == Key.F2)
        {
            e.Handled = true;
            Rename(node.FullPath, node.IsFolder);
        }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            Delete(node.FullPath, node.IsFolder);
        }
    }

    bool IsRoot(string path) =>
        string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), _settings.RootPath!.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    /// <summary>true se il percorso è il file aperto o una cartella che lo contiene.</summary>
    bool AffectsOpen(string path, bool isFolder) => _openPath != null && (isFolder
        ? _openPath.StartsWith(path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        : string.Equals(_openPath, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Dopo rinomina o spostamento: aggiorna l'albero, riapre il file aperto dal nuovo percorso e lo evidenzia.</summary>
    void AfterRelocate(string path, string target, bool isFolder, bool affectsOpen, string toast)
    {
        var targetFolder = Path.GetDirectoryName(target)!;
        var sourceFolder = Path.GetDirectoryName(path)!;
        RefreshFolder(targetFolder);
        if (!string.Equals(sourceFolder, targetFolder, StringComparison.OrdinalIgnoreCase)) RefreshFolder(sourceFolder);
        if (affectsOpen)
        {
            var reopened = isFolder ? target + _openPath![path.Length..] : target;
            _editor = null;
            _openPath = null;
            OpenFile(reopened);
        }
        // L'evidenziazione nell'albero segue sempre il file aperto, se c'è.
        var node = RevealPath(_openPath ?? target);
        if (node != null) SelectNode(node);
        ShowToast(toast);
    }

    /// <summary>
    /// Rinomina un file o una cartella. Se riguarda il file aperto (o la cartella che lo contiene),
    /// prima gestisce le modifiche non salvate e poi lo riapre dal nuovo percorso.
    /// </summary>
    void Rename(string path, bool isFolder)
    {
        path = path.TrimEnd(Path.DirectorySeparatorChar);
        if (IsRoot(path))
        {
            MessageDialog.Show(this, "La cartella principale si cambia dalle Impostazioni.", "Rinomina", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ext = isFolder ? "" : Path.GetExtension(path);
        var current = isFolder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var name = PromptWindow.AskOne(this, isFolder ? "Rinomina cartella" : "Rinomina file", "\uE8AC", "Rinomina", "Nuovo nome", current);
        if (name == null || name == current || !ValidName(name)) return;
        if (ext.Length > 0 && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name = name[..^ext.Length];

        var folder = Path.GetDirectoryName(path)!;
        var target = Path.Combine(folder, name + ext);
        bool caseOnly = string.Equals(target, path, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
        {
            MessageDialog.Show(this, "Esiste già un elemento con questo nome nella stessa cartella.", "Rinomina", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool affectsOpen = AffectsOpen(path, isFolder);
        if (affectsOpen && !ConfirmDiscard()) return;

        try
        {
            Move(path, target, isFolder, caseOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Show(this, $"Impossibile rinominare: {ex.Message}\n\nIl file potrebbe essere aperto in un altro programma.",
                "Rinomina", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        AfterRelocate(path, target, isFolder, affectsOpen, $"Rinominato in \"{name + ext}\"");
    }

    /// <summary>Espande l'albero fino al percorso indicato e ne restituisce il nodo.</summary>
    FileNode? RevealPath(string path)
    {
        var node = _root;
        while (node != null && !string.Equals(node.FullPath, path, StringComparison.OrdinalIgnoreCase))
        {
            node.IsExpanded = true;
            node = node.Children.FirstOrDefault(c => !c.IsPlaceholder &&
                (string.Equals(c.FullPath, path, StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith(c.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        }
        return node;
    }

    static void Move(string source, string target, bool isFolder, bool caseOnly)
    {
        // Cambiare solo maiuscole/minuscole richiede un passaggio intermedio.
        if (caseOnly)
        {
            var temp = source + ".rename-" + Guid.NewGuid().ToString("N")[..8];
            Move(source, temp, isFolder, caseOnly: false);
            source = temp;
        }
        if (isFolder) Directory.Move(source, target);
        else File.Move(source, target);
    }

    // --- elimina -----------------------------------------------------------------------

    void DeleteNode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileNode { IsPlaceholder: false } node }) Delete(node.FullPath, node.IsFolder);
    }

    /// <summary>Sposta file o cartella nel Cestino dopo conferma; se era aperto, chiude l'editor.</summary>
    void Delete(string path, bool isFolder)
    {
        path = path.TrimEnd(Path.DirectorySeparatorChar);
        if (IsRoot(path))
        {
            MessageDialog.Show(this, "La cartella principale non si può eliminare da qui.", "Elimina", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = Path.GetFileName(path);
        var message = isFolder ? $"Eliminare la cartella \"{name}\"" : $"Eliminare il file \"{name}\"?";
        if (isFolder)
        {
            int files = 0;
            try { files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            message += files switch
            {
                0 => "?",
                1 => " e il file che contiene?",
                _ => $" e tutto il suo contenuto ({files} file)?",
            };
        }
        bool affectsOpen = AffectsOpen(path, isFolder);
        if (affectsOpen && _editor is { IsDirty: true })
            message += "\n\nIl file aperto ha modifiche non salvate, che andranno perse.";
        message += "\n\nVerrà spostato nel Cestino.";

        if (MessageDialog.Show(this, message, isFolder ? "Elimina cartella" : "Elimina file",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, yesText: "Sposta nel Cestino", noText: "Annulla", danger: true) != MessageBoxResult.Yes)
            return;

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            if (!RecycleBin.Delete(path, new WindowInteropHelper(this).Handle)) return;
        }
        catch (IOException ex)
        {
            MessageDialog.Show(this, $"Impossibile eliminare: {ex.Message}\n\nIl file potrebbe essere aperto in un altro programma.",
                "Elimina", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (affectsOpen)
        {
            _editor = null;
            _openPath = null;
            EditorHost.Content = null;
            UpdateStatus();
        }
        RefreshFolder(Path.GetDirectoryName(path)!);
        ShowToast($"\"{name}\" spostato nel Cestino");
    }

    // --- sposta trascinando -------------------------------------------------------------

    Point _dragStart;
    FileNode? _dragNode;
    FileNode? _dropTarget;
    DispatcherTimer? _expandTimer;

    void FolderTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragNode = null;
        if (e.OriginalSource is not DependencyObject source || FindAncestor<ToggleButton>(source) != null) return;
        // La cartella principale (senza Parent) non si sposta.
        if (FindAncestor<TreeViewItem>(source)?.DataContext is FileNode { IsPlaceholder: false, Parent: not null } node)
        {
            _dragNode = node;
            _dragStart = e.GetPosition(FolderTree);
        }
    }

    void FolderTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragNode == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragNode = null;
            return;
        }
        var delta = e.GetPosition(FolderTree) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var node = _dragNode;
        _dragNode = null;
        try
        {
            DragDrop.DoDragDrop(FolderTree, new DataObject(typeof(FileNode), node), DragDropEffects.Move);
        }
        finally
        {
            SetDropTarget(null);
        }
    }

    void FolderTree_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        AutoScroll(e.GetPosition(FolderTree));
        var folder = DropFolder(e.OriginalSource as DependencyObject);
        bool ok = e.Data.GetData(typeof(FileNode)) is FileNode node && folder != null && CanMoveInto(node, folder);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        SetDropTarget(ok ? folder : null);
    }

    void FolderTree_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave arriva anche passando da un elemento all'altro: si azzera solo uscendo dall'albero.
        var p = e.GetPosition(FolderTree);
        if (p.X < 0 || p.Y < 0 || p.X >= FolderTree.ActualWidth || p.Y >= FolderTree.ActualHeight) SetDropTarget(null);
    }

    void FolderTree_Drop(object sender, DragEventArgs e)
    {
        var folder = _dropTarget;
        SetDropTarget(null);
        if (e.Data.GetData(typeof(FileNode)) is not FileNode node || folder == null) return;
        e.Handled = true;
        // Lo spostamento (con eventuali messaggi) avviene dopo la fine del trascinamento.
        Dispatcher.BeginInvoke(() => MoveInto(node.FullPath, node.IsFolder, folder.FullPath));
    }

    /// <summary>Cartella di destinazione sotto il puntatore: la cartella stessa o quella che contiene il file.</summary>
    FileNode? DropFolder(DependencyObject? source) => FindAncestor<TreeViewItem>(source)?.DataContext switch
    {
        FileNode { IsPlaceholder: true } placeholder => placeholder.Parent,
        FileNode { IsFolder: true } folder => folder,
        FileNode file => file.Parent,
        _ => _root, // spazio vuoto sotto l'albero
    };

    static bool CanMoveInto(FileNode node, FileNode folder)
    {
        var folderPath = folder.FullPath.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(Path.GetDirectoryName(node.FullPath), folderPath, StringComparison.OrdinalIgnoreCase)) return false; // già lì
        if (node.IsFolder && (string.Equals(node.FullPath, folderPath, StringComparison.OrdinalIgnoreCase) ||
                              folderPath.StartsWith(node.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            return false; // dentro sé stessa
        return true;
    }

    void SetDropTarget(FileNode? folder)
    {
        if (folder == _dropTarget) return;
        if (_dropTarget != null) _dropTarget.IsDropTarget = false;
        _dropTarget = folder;
        _expandTimer?.Stop();
        if (folder == null) return;
        folder.IsDropTarget = true;
        // Tenendo il file sopra una cartella chiusa per un attimo, la cartella si apre.
        if (!folder.IsExpanded)
        {
            _expandTimer ??= CreateExpandTimer();
            _expandTimer.Start();
        }
    }

    DispatcherTimer CreateExpandTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_dropTarget != null) _dropTarget.IsExpanded = true;
        };
        return timer;
    }

    void AutoScroll(Point position)
    {
        if (FindDescendant<ScrollViewer>(FolderTree) is not { } scroll) return;
        const double edge = 28;
        if (position.Y < edge) scroll.LineUp();
        else if (position.Y > FolderTree.ActualHeight - edge) scroll.LineDown();
    }

    void MoveInto(string path, bool isFolder, string folder)
    {
        var target = Path.Combine(folder, Path.GetFileName(path));
        var folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        if (File.Exists(target) || Directory.Exists(target))
        {
            MessageDialog.Show(this, $"Nella cartella \"{folderName}\" esiste già un elemento chiamato \"{Path.GetFileName(path)}\".",
                "Sposta", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool affectsOpen = AffectsOpen(path, isFolder);
        if (affectsOpen && !ConfirmDiscard()) return;

        try
        {
            if (isFolder) Directory.Move(path, target);
            else File.Move(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Show(this, $"Impossibile spostare: {ex.Message}\n\nIl file potrebbe essere aperto in un altro programma.",
                "Sposta", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        AfterRelocate(path, target, isFolder, affectsOpen, $"Spostato in \"{folderName}\"");
    }

    static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null && element is not T)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        return element as T;
    }

    static T? FindDescendant<T>(DependencyObject element) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is T found) return found;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _root?.Refresh();
        if (_openPath != null && File.Exists(_openPath) && _editor is not { IsDirty: true })
        {
            var path = _openPath;
            _openPath = null;
            OpenFile(path);
        }
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var previous = _settings.RootPath;
        if (new SettingsWindow(_settings, firstRun: false) { Owner = this }.ShowDialog() != true || previous == _settings.RootPath) return;
        _editor = null;
        _openPath = null;
        EditorHost.Content = null;
        LoadRoot();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
        base.OnClosing(e);
    }

    // --- utilità ---------------------------------------------------------------------

    /// <summary>Cartella in cui creare i nuovi file: quella selezionata, o quella del file selezionato.</summary>
    string TargetFolder() => FolderTree.SelectedItem switch
    {
        FileNode { IsFolder: true } folder => folder.FullPath,
        FileNode { IsPlaceholder: false } file => Path.GetDirectoryName(file.FullPath)!,
        _ => _settings.RootPath!,
    };

    bool TryTargetPath(string name, string extension, out string path)
    {
        path = "";
        if (!ValidName(name)) return false;
        if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name = name[..^extension.Length];
        path = Path.Combine(TargetFolder(), name + extension);
        if (!File.Exists(path)) return true;
        MessageDialog.Show(this, "Esiste già un file con questo nome nella cartella selezionata.", "Nuovo file", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    bool ValidName(string name)
    {
        if (name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) return true;
        MessageDialog.Show(this, "Nome non valido. Non usare i caratteri \\ / : * ? \" < > |", "Nome", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    void AfterCreate(string path)
    {
        _editor = null; // le modifiche pendenti sono già state gestite da ConfirmDiscard
        var folder = RefreshFolder(Path.GetDirectoryName(path)!);
        OpenFile(path);
        var node = folder?.Children.FirstOrDefault(c => c.FullPath == path);
        if (node != null) SelectNode(node);
    }

    FileNode? RefreshFolder(string folderPath)
    {
        var node = FindFolder(_root, folderPath);
        if (node == null) return null;
        node.Refresh();
        node.IsExpanded = true;
        return node;
    }

    static FileNode? FindFolder(FileNode? node, string path)
    {
        if (node == null) return null;
        if (string.Equals(node.FullPath.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return node;
        return node.Children.Where(c => c.IsFolder).Select(c => FindFolder(c, path)).FirstOrDefault(n => n != null);
    }

    void SelectNode(FileNode node)
    {
        _suppressSelection = true;
        try
        {
            FolderTree.UpdateLayout();
            ItemsControl parent = FolderTree;
            var chain = new Stack<FileNode>();
            for (var n = node; n != null; n = n.Parent) chain.Push(n);
            TreeViewItem? item = null;
            foreach (var n in chain)
            {
                item = parent.ItemContainerGenerator.ContainerFromItem(n) as TreeViewItem;
                if (item == null) return;
                item.UpdateLayout();
                parent = item;
            }
            if (item != null)
            {
                item.IsSelected = true;
                item.BringIntoView();
            }
        }
        finally
        {
            _suppressSelection = false;
        }
    }
}
