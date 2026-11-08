using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using Navifox.App.Services;

namespace Navifox.App.Views;

/// <summary>Gestione degli snippet personali: nome, scorciatoia (parola + Tab nell'editor) e testo.</summary>
public sealed partial class SnippetManagerWindow : Window
{
    /// <summary>Snippet in modifica: i campi del modulo sono legati direttamente alle sue proprietà.</summary>
    public sealed partial class SnippetItem : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Label))]
        private string _name = "Nuovo snippet";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Label))]
        private string _shortcut = string.Empty;

        [ObservableProperty] private string _body = string.Empty;

        public string Label => Shortcut.Trim().Length > 0 ? $"{Name}   ·   {Shortcut.Trim()} + Tab" : Name;
    }

    private readonly ObservableCollection<SnippetItem> _items;
    private readonly ListBox _list = new();
    private readonly Grid _form;

    public SnippetManagerWindow() : this(null) { }

    /// <param name="initialBody">Testo selezionato nell'editor: crea subito un nuovo snippet con questo contenuto.</param>
    public SnippetManagerWindow(string? initialBody)
    {
        Title = "Snippet personali";
        Width = 860;
        Height = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _items = new(SnippetStore.All.Select(s => new SnippetItem { Name = s.Name, Shortcut = s.Shortcut, Body = s.Body }));
        _list.ItemsSource = _items;
        _list.ItemTemplate = new FuncDataTemplate<SnippetItem>((_, _) =>
        {
            var text = new TextBlock { Margin = new Thickness(2, 1), TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
            text.Bind(TextBlock.TextProperty, new Binding(nameof(SnippetItem.Label)));
            return text;
        });

        // Modulo legato all'elemento selezionato (DataContext = SnippetItem).
        TextBox Field(string property, string watermark, bool multiline = false)
        {
            var box = new TextBox { Watermark = watermark };
            if (multiline)
            {
                box.AcceptsReturn = true;
                box.AcceptsTab = true;
                box.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
                box.FontFamily = new Avalonia.Media.FontFamily("Cascadia Mono,Consolas,monospace");
                box.FontSize = 12.5;
                box.VerticalContentAlignment = VerticalAlignment.Top;
            }
            box.Bind(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay });
            return box;
        }
        _form = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*"), Margin = new Thickness(6, 12, 12, 0), RowSpacing = 4 };
        void Add(Control control, int row) { Grid.SetRow(control, row); _form.Children.Add(control); }
        var name = Field(nameof(SnippetItem.Name), "Nome");
        Add(new TextBlock { Text = "Nome", FontSize = 12 }, 0);
        Add(name, 1);
        Add(new TextBlock { Text = "Scorciatoia: scrivila nell'editor e premi Tab (lettere, numeri, _)", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) }, 2);
        Add(Field(nameof(SnippetItem.Shortcut), "es. top"), 3);
        Add(new TextBlock { Text = "Testo  ·  $0 = posizione del cursore dopo l'inserimento", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) }, 4);
        Add(Field(nameof(SnippetItem.Body), "SELECT *\nFROM $0\nLIMIT 100;", multiline: true), 5);
        _form.Bind(DataContextProperty, new Binding(nameof(ListBox.SelectedItem)) { Source = _list });
        _form.Bind(IsEnabledProperty, new Binding(nameof(ListBox.SelectedItem)) { Source = _list, Converter = Avalonia.Data.Converters.ObjectConverters.IsNotNull });

        var add = new Button { Content = "Nuovo", Classes = { "small-muted" } };
        var remove = new Button { Content = "Elimina", Classes = { "small-muted" } };
        add.Click += (_, _) => { AddItem(string.Empty); name.Focus(); name.SelectAll(); };
        remove.Click += (_, _) =>
        {
            if (_list.SelectedItem is not SnippetItem item) return;
            var index = _items.IndexOf(item);
            _items.Remove(item);
            if (_items.Count > 0) _list.SelectedIndex = Math.Min(index, _items.Count - 1);
        };
        var leftButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        leftButtons.Children.Add(add);
        leftButtons.Children.Add(remove);
        var left = new DockPanel { Margin = new Thickness(12, 12, 6, 0) };
        DockPanel.SetDock(leftButtons, Dock.Bottom);
        left.Children.Add(leftButtons);
        left.Children.Add(new Border { BorderBrush = Avalonia.Media.Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = _list });

        var save = new Button { Content = "Salva", Classes = { "dialog-primary" }, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Annulla", Classes = { "small-muted" }, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var error = new TextBlock { Foreground = Avalonia.Media.Brushes.IndianRed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        save.Click += (_, _) =>
        {
            try
            {
                SnippetStore.Save(_items.Select(i => new UserSnippet(i.Name, i.Shortcut, i.Body)));
                Close(true);
            }
            catch (Exception exception) { error.Text = "Salvataggio non riuscito: " + exception.Message; }
        };
        cancel.Click += (_, _) => Close(false);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        bottom.Children.Add(error);
        bottom.Children.Add(cancel);
        bottom.Children.Add(save);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("280,*") };
        body.Children.Add(left);
        Grid.SetColumn(_form, 1);
        body.Children.Add(_form);
        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(body);
        Content = root;

        if (!string.IsNullOrWhiteSpace(initialBody) || _items.Count == 0) AddItem(initialBody ?? string.Empty);
        else _list.SelectedIndex = 0;
        Opened += (_, _) => { name.Focus(); name.SelectAll(); };
    }

    private void AddItem(string body)
    {
        var item = new SnippetItem { Body = body };
        _items.Add(item);
        _list.SelectedItem = item;
    }
}
