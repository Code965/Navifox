using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Navifox.App.Views;

/// <summary>Propone l'invio di una segnalazione d'errore: mostra il testo esatto che verrà inviato (anonimo).</summary>
public sealed class CrashReportWindow : Window
{
    public CrashReportWindow() : this(string.Empty, string.Empty) { }

    public CrashReportWindow(string intro, string report)
    {
        Title = "Segnala un errore";
        Width = 720;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var header = new StackPanel { Spacing = 6, Margin = new Thickness(16, 14, 16, 8) };
        header.Children.Add(new TextBlock { Text = intro, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(new TextBlock
        {
            Text = "Puoi inviare questa segnalazione agli sviluppatori: contiene solo versione, sistema e dettagli tecnici dell'errore. " +
                   "Nomi utente, cartelle personali, password, indirizzi e valori sono già oscurati. «Segnala» apre GitHub con il testo già compilato: " +
                   "lo rivedi e lo invii tu (serve un account GitHub). Niente viene inviato automaticamente.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Opacity = 0.8
        });
        var text = new TextBox
        {
            Text = report, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"), FontSize = 11.5, Margin = new Thickness(16, 0)
        };
        var send = new Button { Content = "Segnala su GitHub", Classes = { "dialog-primary" }, MinWidth = 140, HorizontalContentAlignment = HorizontalAlignment.Center };
        var copy = new Button { Content = "Copia il testo", Classes = { "small-muted" }, MinWidth = 110, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "Non inviare", Classes = { "small-muted" }, MinWidth = 110, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        send.Click += (_, _) => Close(true);
        close.Click += (_, _) => Close(false);
        copy.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(report);
            copy.Content = "Copiato";
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 10, 16, 14) };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        buttons.Children.Add(send);
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(buttons);
        root.Children.Add(text);
        Content = root;
    }
}
