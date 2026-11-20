using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CinnatiWorkout.Views;

/// <summary>Piccola finestra di input con uno o più campi di testo.</summary>
public sealed class PromptWindow : Window
{
    readonly List<TextBox> _boxes = [];
    readonly List<RadioButton> _choices = [];

    PromptWindow(Window owner, string title, string icon, string okText, (string Label, string Value)[] fields,
        (string Label, string[] Options, int Selected)? choice = null)
    {
        Owner = owner;
        Icon = owner.Icon;
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("SurfaceBrush");
        Foreground = (Brush)FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var glyph = new TextBlock
        {
            Text = icon,
            Style = (Style)FindResource("Icon"),
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
        var badge = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Child = glyph };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        header.Children.Add(badge);
        header.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("PageTitle"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) });
        panel.Children.Add(header);

        foreach (var (label, value) in fields)
        {
            panel.Children.Add(new TextBlock { Text = label.ToUpper(Skinfold.Num.Culture), Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 12, 0, 5) });
            var box = new TextBox { Text = value };
            _boxes.Add(box);
            panel.Children.Add(box);
        }

        if (choice is { } c)
        {
            panel.Children.Add(new TextBlock { Text = c.Label.ToUpper(Skinfold.Num.Culture), Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 14, 0, 5) });
            var segments = new StackPanel { Orientation = Orientation.Horizontal };
            var group = $"choice{GetHashCode()}";
            for (int i = 0; i < c.Options.Length; i++)
            {
                var option = new RadioButton { Content = c.Options[i], GroupName = group, IsChecked = i == c.Selected, Style = (Style)FindResource("Segment") };
                _choices.Add(option);
                segments.Children.Add(option);
            }
            panel.Children.Add(new Border { Style = (Style)FindResource("SegmentGroup"), Child = segments });
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 100, Style = (Style)FindResource("PrimaryButton") };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(new Button { Content = "Annulla", IsCancel = true, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) });
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => { _boxes[0].Focus(); _boxes[0].SelectAll(); };
    }

    public static string[]? Ask(Window owner, string title, string icon, params (string Label, string Value)[] fields)
    {
        var window = new PromptWindow(owner, title, icon, "Crea", fields);
        return window.ShowDialog() == true ? window._boxes.Select(b => b.Text.Trim()).ToArray() : null;
    }

    /// <summary>Campi di testo più una scelta a segmenti; restituisce i testi e l'indice dell'opzione scelta.</summary>
    public static (string[] Values, int Choice)? AskWithChoice(Window owner, string title, string icon,
        (string Label, string[] Options, int Selected) choice, params (string Label, string Value)[] fields)
    {
        var window = new PromptWindow(owner, title, icon, "Crea", fields, choice);
        if (window.ShowDialog() != true) return null;
        return (window._boxes.Select(b => b.Text.Trim()).ToArray(), window._choices.FindIndex(r => r.IsChecked == true));
    }

    /// <summary>Un solo campo, con testo personalizzato sul pulsante di conferma.</summary>
    public static string? AskOne(Window owner, string title, string icon, string okText, string label, string value)
    {
        var window = new PromptWindow(owner, title, icon, okText, [(label, value)]);
        return window.ShowDialog() == true ? window._boxes[0].Text.Trim() : null;
    }
}
