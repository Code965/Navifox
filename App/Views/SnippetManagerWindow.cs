using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Navifox.App.Services;

namespace Navifox.App.Views;

/// <summary>Gestione degli snippet personali: nome, scorciatoia (parola + Tab nell'editor) e testo.</summary>
public sealed class SnippetManagerWindow : Window
{
    private sealed class Item
    {
        public string Name { get; set; } = "Nuovo snippet";
        public string Shortcut { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public override string ToString() => Shortcut.Length > 0 ? $"{Name}   ·   {Shortcut} ⇥" : Name;
    }

    private readonly ObservableCollection<Item> _items;
    private readonly ListBox _list = new() { MinWidth = 220 };
    private readonly TextBox _name = new() { Watermark = "Nome" };
    private readonly TextBox _shortcut = new() { Watermark = "es. sel (poi Tab nell'editor)" };
    private readonly TextBox _body = new()
    {
        AcceptsReturn = true, AcceptsTab = true, TextWrapping = Avalonia.Media.TextWrapping.NoWrap, MinHeight = 220,
        FontFamily = new Avalonia.Media.FontFamily("Cascadia Mono,Consolas,monospace"), FontSize = 12.5,
        Watermark = "SELECT *\nFROM $0\nLIMIT 100;"
    };
    private bool _loading;

    public SnippetManagerWindow() : this(null) { }

    /// <param name="initialBody">Testo selezionato nell'editor: crea subito un nuovo snippet con questo contenuto.</param>
    public SnippetManagerWindow(string? initialBody)
    {
        Title = "Snippet personali";
        Width = 820;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _items = new(SnippetStore.All.Select(s => new Item { Name = s.Name, Shortcut = s.Shortcut, Body = s.Body }));
        _list.ItemsSource = _items;
        _list.ItemTemplate = new FuncDataTemplate<Item>((item, _) => new TextBlock { Text = item?.ToString(), Margin = new Thickness(2, 1) }, supportsRecycling: false);
        _list.SelectionChanged += (_, _) => LoadSelected();
        _name.TextChanged += (_, _) => Store(i => i.Name = _name.Text ?? string.Empty);
        _shortcut.TextChanged += (_, _) => Store(i => i.Shortcut = _shortcut.Text ?? string.Empty);
        _body.TextChanged += (_, _) => Store(i => i.Body = _body.Text ?? string.Empty);

        var add = new Button { Content = "Nuovo", Classes = { "small-muted" } };
        var remove = new Button { Content = "Elimina", Classes = { "small-muted" } };
        add.Click += (_, _) => AddItem(string.Empty);
        remove.Click += (_, _) =>
        {
            if (_list.SelectedItem is not Item item) return;
            var index = _items.IndexOf(item);
            _items.Remove(item);
            _list.SelectedIndex = Math.Min(index, _items.Count - 1);
        };
        var leftButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        leftButtons.Children.Add(add);
        leftButtons.Children.Add(remove);
        var left = new DockPanel { Margin = new Thickness(12, 12, 6, 0) };
        DockPanel.SetDock(leftButtons, Dock.Bottom);
        left.Children.Add(leftButtons);
        left.Children.Add(new Border { BorderBrush = Avalonia.Media.Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Opacity = 0.95, Child = _list });

        var form = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*,Auto"), Margin = new Thickness(6, 12, 12, 0), RowSpacing = 4 };
        void Add(Control control, int row) { Grid.SetRow(control, row); form.Children.Add(control); }
        Add(new TextBlock { Text = "Nome", FontSize = 12 }, 0);
        Add(_name, 1);
        Add(new TextBlock { Text = "Scorciatoia: scrivila nell'editor e premi Tab", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) }, 2);
        Add(_shortcut, 3);
        Add(new TextBlock { Text = "Testo  ·  $0 = posizione del cursore dopo l'inserimento", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) }, 4);
        Add(_body, 5);

        var save = new Button { Content = "Salva", Classes = { "dialog-primary" }, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Annulla", Classes = { "small-muted" }, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) =>
        {
            SnippetStore.Save(_items.Select(i => new UserSnippet(i.Name, i.Shortcut, i.Body)));
            Close(true);
        };
        cancel.Click += (_, _) => Close(false);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        bottom.Children.Add(cancel);
        bottom.Children.Add(save);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*") };
        body.Children.Add(left);
        Grid.SetColumn(form, 1);
        body.Children.Add(form);
        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(body);
        Content = root;

        if (!string.IsNullOrWhiteSpace(initialBody)) AddItem(initialBody);
        else if (_items.Count > 0) _list.SelectedIndex = 0;
        else AddItem(string.Empty);
    }

    private void AddItem(string body)
    {
        var item = new Item { Body = body };
        _items.Add(item);
        _list.SelectedItem = item;
        _name.Focus();
        _name.SelectAll();
    }

    private void LoadSelected()
    {
        var item = _list.SelectedItem as Item;
        _loading = true;
        _name.Text = item?.Name ?? string.Empty;
        _shortcut.Text = item?.Shortcut ?? string.Empty;
        _body.Text = item?.Body ?? string.Empty;
        _name.IsEnabled = _shortcut.IsEnabled = _body.IsEnabled = item is not null;
        _loading = false;
    }

    private void Store(Action<Item> update)
    {
        if (_loading || _list.SelectedItem is not Item item) return;
        update(item);
        // Aggiorna l'etichetta nell'elenco (nome e scorciatoia).
        var index = _items.IndexOf(item);
        _loading = true;
        _items[index] = item;
        _list.SelectedIndex = index;
        _loading = false;
    }
}
