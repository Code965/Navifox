using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Navifox.App.Views;

/// <summary>Domanda con più risposte (es. Salva / Non salvare / Annulla). <see cref="Choice"/> = indice del pulsante, -1 se chiusa.</summary>
public sealed class ChoiceDialog : Window
{
    public int Choice { get; private set; } = -1;

    public ChoiceDialog() : this("", "", ["OK"]) { }

    /// <param name="buttons">Testi dei pulsanti; il primo è quello principale, l'ultimo quello di annullamento (Esc).</param>
    public ChoiceDialog(string title, string message, IReadOnlyList<string> buttons)
    {
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 18, 20, 14), FontSize = 13.5 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(20, 4, 20, 16) };
        // Ordine visivo: annulla a sinistra, poi le secondarie, la principale a destra.
        var order = new List<int> { buttons.Count - 1 };
        for (var i = buttons.Count - 2; i >= 1; i--) order.Add(i);
        if (buttons.Count > 1) order.Add(0);
        Button? primary = null;
        foreach (var index in order)
        {
            var button = new Button
            {
                Content = buttons[index], MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center,
                Classes = { index == 0 ? "dialog-primary" : "small-muted" }
            };
            var chosen = index;
            button.Click += (_, _) => { Choice = chosen; Close(true); };
            row.Children.Add(button);
            if (index == 0) primary = button;
        }
        var root = new StackPanel();
        root.Children.Add(text);
        root.Children.Add(row);
        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Choice = buttons.Count - 1; Close(false); }
            else if (e.Key == Key.Enter) { Choice = 0; Close(true); }
        };
        Opened += (_, _) => primary?.Focus();
    }
}
