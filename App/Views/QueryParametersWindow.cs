using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Navifox.App.Views;

/// <summary>Valore di un parametro della query (:nome o ${nome}); Raw = inserito come SQL senza apici.</summary>
public sealed record QueryParameterValue(string Value, bool Raw);

/// <summary>
/// Chiede i valori dei parametri prima di eseguire la query. I numeri restano numeri, NULL resta NULL,
/// il resto diventa testo tra apici; «SQL» inserisce il valore così com'è (es. NOW(), una sottoquery).
/// </summary>
public sealed class QueryParametersWindow : Window
{
    private readonly List<(string Name, TextBox Value, CheckBox Raw)> _fields = [];

    public QueryParametersWindow() : this([], new Dictionary<string, QueryParameterValue>()) { }

    public QueryParametersWindow(IReadOnlyList<string> names, IReadOnlyDictionary<string, QueryParameterValue> previous)
    {
        Title = "Parametri della query";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var list = new StackPanel { Spacing = 8, Margin = new Thickness(16, 12) };
        list.Children.Add(new TextBlock
        {
            Text = "Numeri e NULL vengono inseriti così come sono, il resto come testo tra apici. Spunta «SQL» per inserire un'espressione (es. NOW()).",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 4)
        });
        foreach (var name in names)
        {
            previous.TryGetValue(name, out var old);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*,Auto"), ColumnSpacing = 10 };
            var label = new TextBlock { Text = ":" + name, VerticalAlignment = VerticalAlignment.Center, FontFamily = new Avalonia.Media.FontFamily("Cascadia Mono,Consolas,monospace"), TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
            var value = new TextBox { Text = old?.Value ?? string.Empty, Watermark = "valore" };
            var raw = new CheckBox { Content = "SQL", IsChecked = old?.Raw == true, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(raw, "Inserisci il valore come espressione SQL, senza apici");
            Grid.SetColumn(value, 1);
            Grid.SetColumn(raw, 2);
            row.Children.Add(label);
            row.Children.Add(value);
            row.Children.Add(raw);
            list.Children.Add(row);
            _fields.Add((name, value, raw));
            value.KeyDown += (_, args) => { if (args.Key == Key.Enter) { Confirm(); args.Handled = true; } };
        }
        var run = new Button { Content = "Esegui", Classes = { "dialog-primary" }, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Annulla", Classes = { "small-muted" }, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        run.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => Close(false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 4, 16, 14) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(run);
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = list });
        Content = root;
        KeyDown += (_, args) => { if (args.Key == Key.Escape) Close(false); };
        Opened += (_, _) => _fields.FirstOrDefault().Value?.Focus();
    }

    public Dictionary<string, QueryParameterValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    private void Confirm()
    {
        foreach (var (name, value, raw) in _fields) Values[name] = new QueryParameterValue(value.Text ?? string.Empty, raw.IsChecked == true);
        Close(true);
    }
}
