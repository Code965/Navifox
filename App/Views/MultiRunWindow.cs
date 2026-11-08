using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Navifox.App.Services;
using Navifox.App.ViewModels;
using Navifox.Core;
using Navifox.Core.Models;

namespace Navifox.App.Views;

/// <summary>
/// Esegue la stessa query (solo lettura) su più connessioni o database e mostra i risultati affiancati,
/// con le righe diverse dal primo risultato evidenziate. Utile per controllare Sviluppo, Test e Produzione.
/// </summary>
public sealed class MultiRunWindow : Window
{
    private sealed class Target
    {
        public required ConnectionProfile Profile { get; init; }
        public CheckBox Check { get; } = new() { VerticalAlignment = VerticalAlignment.Center };
        public ComboBox Database { get; } = new() { MinWidth = 200, PlaceholderText = "(predefinito)", FontSize = 12.5 };
        public ObservableCollection<string> Databases { get; } = [];
        /// <summary>Database da preselezionare (quello della scheda per la connessione corrente).</summary>
        public string? Preferred { get; set; }
        public Task? Loading { get; set; }
    }

    private readonly IDatabaseAccessService _access;
    private readonly string _sql;
    private readonly List<Target> _targets = [];
    private readonly Grid _results = new();
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _run = new() { Content = "Esegui e confronta", Classes = { "dialog-primary" }, MinWidth = 140, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, IsVisible = false, Width = 120, Height = 4, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };

    public MultiRunWindow() : this(null!, [], null!, null, string.Empty) { }

    public MultiRunWindow(IDatabaseAccessService access, IReadOnlyList<ConnectionProfile> profiles, ConnectionProfile current, string? currentDatabase, string sql)
    {
        _access = access;
        _sql = sql;
        Title = "Esegui su più database e confronta";
        Width = 1240;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var sqlPreview = new TextBlock
        {
            Text = string.Join(' ', sql.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)),
            FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.8
        };
        var list = new StackPanel { Spacing = 4 };
        foreach (var profile in profiles.Where(p => current is null || p.Kind == current.Kind).OrderBy(p => p.Id == current?.Id ? 0 : 1).ThenBy(p => p.Name))
        {
            var target = new Target { Profile = profile };
            target.Database.ItemsSource = target.Databases;
            var label = new TextBlock
            {
                Text = profile.Name + (string.IsNullOrEmpty(profile.Environment) ? string.Empty : $"   [{profile.Environment}]"),
                VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, MinWidth = 260
            };
            var host = new TextBlock { Text = profile.Kind == DatabaseKind.Sqlite ? profile.Host : $"{profile.Host}:{profile.EffectivePort}", VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5, Opacity = 0.65 };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(target.Check);
            row.Children.Add(label);
            row.Children.Add(target.Database);
            row.Children.Add(host);
            list.Children.Add(row);
            _targets.Add(target);
            target.Check.IsCheckedChanged += async (_, _) => { if (target.Check.IsChecked == true) await LoadDatabasesAsync(target); };
            target.Database.DropDownOpened += async (_, _) => await LoadDatabasesAsync(target);
            if (profile.Id == current?.Id)
            {
                target.Preferred = currentDatabase;
                target.Check.IsChecked = true;
            }
        }
        var targetsBox = new Border
        {
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8), MaxHeight = 210,
            Child = new ScrollViewer { Content = list }
        };
        var hint = new TextBlock
        {
            Text = "Scegli le connessioni (stesso tipo di database) e il database di ciascuna. Si eseguono solo query di lettura; il primo risultato è il riferimento e le righe diverse sono evidenziate.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75
        };
        _run.Click += async (_, _) => await RunAsync();
        var actions = new DockPanel { LastChildFill = true };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        right.Children.Add(_progress);
        right.Children.Add(_run);
        DockPanel.SetDock(right, Dock.Right);
        actions.Children.Add(right);
        actions.Children.Add(_summary);

        var top = new StackPanel { Spacing = 8, Margin = new Thickness(14, 12, 14, 8) };
        top.Children.Add(sqlPreview);
        top.Children.Add(hint);
        top.Children.Add(targetsBox);
        top.Children.Add(actions);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(new Border { Margin = new Thickness(14, 0, 14, 14), Child = _results });
        Content = root;
    }

    private Task LoadDatabasesAsync(Target target) => target.Loading ??= LoadDatabasesCoreAsync(target);

    private async Task LoadDatabasesCoreAsync(Target target)
    {
        var preferred = target.Preferred;
        try
        {
            var databases = await _access.ListDatabasesAsync(target.Profile, CancellationToken.None);
            foreach (var database in databases) target.Databases.Add(database);
            var wish = preferred ?? (_targets.FirstOrDefault(t => t != target && t.Database.SelectedItem is string)?.Database.SelectedItem as string) ?? target.Profile.DefaultDatabase;
            target.Database.SelectedItem = target.Databases.FirstOrDefault(d => string.Equals(d, wish, StringComparison.OrdinalIgnoreCase))
                ?? target.Databases.FirstOrDefault(d => string.Equals(d, target.Profile.DefaultDatabase, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error)
        {
            target.Loading = null;
            ToolTip.SetTip(target.Database, error.Message);
            target.Database.PlaceholderText = "connessione non riuscita";
        }
    }

    private async Task RunAsync()
    {
        var chosen = _targets.Where(t => t.Check.IsChecked == true).ToArray();
        if (chosen.Length == 0) { _summary.Text = "Scegli almeno una connessione."; return; }
        if (SqlSafety.Analyze(_sql, chosen[0].Profile.Kind == DatabaseKind.MySql).Any(r => r.Risk != SqlRisk.Read))
        {
            _summary.Text = "Il confronto esegue solo query di lettura (SELECT, SHOW…): togli le istruzioni che modificano dati o struttura.";
            _summary.Foreground = Brushes.IndianRed;
            return;
        }
        _summary.ClearValue(TextBlock.ForegroundProperty);
        _run.IsEnabled = false;
        _progress.IsVisible = true;
        _summary.Text = $"Esecuzione su {chosen.Length} database…";
        var maxRows = Math.Max(1, AppSettings.Current.QueryMaxRows);
        var tasks = chosen.Select(async target =>
        {
            var database = target.Database.SelectedItem as string;
            var timer = Stopwatch.StartNew();
            try
            {
                var results = await _access.ExecuteBatchAsync(target.Profile, database, _sql, maxRows, CancellationToken.None);
                var error = results.FirstOrDefault(r => r.Error is not null)?.Error;
                var last = results.LastOrDefault(r => r.Result?.HasResultSet == true)?.Result;
                return (target, database, result: error is null ? last : null, error: error?.Message ?? (last is null ? "Nessun risultato" : null), timer.Elapsed);
            }
            catch (Exception error) { return (target, database, result: (QueryResult?)null, error: (string?)error.Message, timer.Elapsed); }
        }).ToArray();
        var outcomes = await Task.WhenAll(tasks);
        _run.IsEnabled = true;
        _progress.IsVisible = false;
        Show(outcomes.Select(o => (o.target.Profile, o.database, o.result, o.error, o.Elapsed)).ToArray());
    }

    private void Show(IReadOnlyList<(ConnectionProfile Profile, string? Database, QueryResult? Result, string? Error, TimeSpan Elapsed)> outcomes)
    {
        var differences = ResultComparer.Compare(outcomes.Select(o => o.Result).ToArray());
        _results.Children.Clear();
        _results.ColumnDefinitions.Clear();
        for (var i = 0; i < outcomes.Count; i++)
        {
            _results.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star) { MinWidth = 220 });
            var (profile, database, result, error, elapsed) = outcomes[i];
            var diff = differences[i];
            var status = error is not null ? $"Errore: {error}"
                : i == 0 ? $"Riferimento · {result!.Rows.Count:N0} righe · {elapsed.TotalMilliseconds:N0} ms"
                : !diff.SameColumns ? $"Colonne diverse · {result!.Rows.Count:N0} righe"
                : diff.OnlyHere == 0 && diff.Missing == 0 ? $"Identico · {result!.Rows.Count:N0} righe · {elapsed.TotalMilliseconds:N0} ms"
                : $"{diff.OnlyHere:N0} righe diverse · {diff.Missing:N0} mancanti · {result!.Rows.Count:N0} righe";
            var same = error is null && (i == 0 || diff.SameColumns && diff.OnlyHere == 0 && diff.Missing == 0);
            var header = new StackPanel { Spacing = 2, Margin = new Thickness(8, 6) };
            header.Children.Add(new TextBlock { Text = $"{profile.Name} · {database ?? "(predefinito)"}", FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            header.Children.Add(new TextBlock { Text = status, FontSize = 12, Foreground = same ? Brushes.SeaGreen : Brushes.IndianRed, TextWrapping = TextWrapping.Wrap });
            var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserSortColumns = false };
            grid.Classes.Add("nf-grid");
            if (result is not null)
            {
                ResultGridBuilder.Rebuild(grid, result.Columns);
                var rows = result.Rows.Select(r => new TableRowViewModel(r)).ToArray();
                var different = diff.DifferentRows;
                var index = rows.Select((row, n) => (row, n)).ToDictionary(p => p.row, p => p.n);
                grid.LoadingRow += (_, e) => e.Row.Classes.Set("diff-row", e.Row.DataContext is TableRowViewModel row && index.TryGetValue(row, out var n) && different.Contains(n));
                grid.ItemsSource = rows;
            }
            var panel = new DockPanel { Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0) };
            var headerBorder = new Border { Child = header, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0, 0, 0, 1) };
            DockPanel.SetDock(headerBorder, Dock.Top);
            panel.Children.Add(headerBorder);
            panel.Children.Add(grid);
            var card = new Border { BorderBrush = same ? Brushes.SeaGreen : Brushes.IndianRed, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = panel, Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0) };
            Grid.SetColumn(card, i);
            _results.Children.Add(card);
        }
        var failed = outcomes.Count(o => o.Error is not null);
        _summary.Text = failed > 0 ? $"{failed} esecuzioni non riuscite." :
            ResultComparer.AllEqual(differences) ? "Risultati identici su tutti i database." :
            "Ci sono differenze: le righe evidenziate non compaiono nel risultato di riferimento (il primo).";
    }
}
