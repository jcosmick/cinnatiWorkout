using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CinnatiWorkout.Views;

/// <summary>
/// Sostituto di <see cref="MessageBox"/> con la grafica dell'app: stessi parametri e risultati,
/// più testi personalizzabili per i pulsanti e la variante "pericolosa" (pulsante rosso) per le eliminazioni.
/// </summary>
public sealed class MessageDialog : Window
{
    MessageBoxResult _result;

    MessageDialog(Window? owner, string message, string title, MessageBoxButton buttons, MessageBoxImage image,
        MessageBoxResult defaultResult, string? yesText, string? noText, string? cancelText, bool danger)
    {
        if (owner is { IsLoaded: true })
        {
            Owner = owner;
            Icon = owner.Icon;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = Owner == null;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        // Risultato quando si chiude con la X o con Esc.
        _result = buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel,
        };

        var (glyph, softKey, strongKey) = image switch
        {
            MessageBoxImage.Error => ("", "DangerSoftBrush", "DangerBrush"),
            MessageBoxImage.Warning => ("", "WarningSoftBrush", "WarningTextBrush"),
            MessageBoxImage.Question => ("", "AccentSoftBrush", "AccentTextBrush"),
            _ => ("", "AccentSoftBrush", "AccentTextBrush"),
        };
        if (danger) (softKey, strongKey) = ("DangerSoftBrush", "DangerBrush");

        var root = new DockPanel { Margin = new Thickness(28, 24, 28, 24), LastChildFill = true };

        // Pulsanti
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        DockPanel.SetDock(bar, Dock.Bottom);
        var defs = buttons switch
        {
            MessageBoxButton.OK => new[] { (MessageBoxResult.OK, "OK") },
            MessageBoxButton.OKCancel => [(MessageBoxResult.Cancel, cancelText ?? "Annulla"), (MessageBoxResult.OK, yesText ?? "OK")],
            MessageBoxButton.YesNo => [(MessageBoxResult.No, noText ?? "No"), (MessageBoxResult.Yes, yesText ?? "Sì")],
            _ => [(MessageBoxResult.Cancel, cancelText ?? "Annulla"), (MessageBoxResult.No, noText ?? "No"), (MessageBoxResult.Yes, yesText ?? "Sì")],
        };
        var primary = defs[^1].Item1;
        if (defaultResult == MessageBoxResult.None || defs.All(d => d.Item1 != defaultResult)) defaultResult = primary;

        Button? focus = null;
        foreach (var (result, text) in defs)
        {
            var button = new Button { Content = text, MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
            if (result == primary)
                button.Style = (Style)FindResource(danger ? "DangerButton" : "PrimaryButton");
            if (result == defaultResult) { button.IsDefault = true; focus = button; }
            if (result == _result) button.IsCancel = true;
            var r = result;
            button.Click += (_, _) => { _result = r; Close(); };
            bar.Children.Add(button);
        }
        root.Children.Add(bar);

        // Icona + titolo + messaggio
        var icon = new TextBlock { Text = glyph, Style = (Style)FindResource("Icon"), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, strongKey);
        var badge = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(12), Child = icon, VerticalAlignment = VerticalAlignment.Top };
        badge.SetResourceReference(Border.BackgroundProperty, softKey);
        DockPanel.SetDock(badge, Dock.Left);
        root.Children.Add(badge);

        var texts = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        texts.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("PageTitle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 8) });
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        body.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        texts.Children.Add(body);
        root.Children.Add(texts);

        Content = root;

        // Ctrl+C copia il testo, come nelle finestre di Windows.
        InputBindings.Add(new KeyBinding(new CopyCommand($"{title}\n\n{message}"), Key.C, ModifierKeys.Control));

        Loaded += (_, _) =>
        {
            focus?.Focus();
            (image switch
            {
                MessageBoxImage.Error => SystemSounds.Hand,
                MessageBoxImage.Warning => SystemSounds.Exclamation,
                MessageBoxImage.Question => SystemSounds.Question,
                MessageBoxImage.Information => SystemSounds.Asterisk,
                _ => null,
            })?.Play();
        };
    }

    public static MessageBoxResult Show(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.Information,
        MessageBoxResult defaultResult = MessageBoxResult.None,
        string? yesText = null, string? noText = null, string? cancelText = null, bool danger = false)
    {
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        var dialog = new MessageDialog(owner, message, title, buttons, image, defaultResult, yesText, noText, cancelText, danger);
        dialog.ShowDialog();
        return dialog._result;
    }

    sealed class CopyCommand(string text) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
            try { Clipboard.SetText(text); } catch { /* appunti occupati: pazienza */ }
        }
    }
}
