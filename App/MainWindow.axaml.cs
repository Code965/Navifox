using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Navifox.App.ViewModels;

namespace Navifox.App;

public partial class MainWindow : Window
{
    public MainWindow() : this(App.Services.GetRequiredService<MainWindowViewModel>()) { }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // Avvisi del Server Monitor (connessioni oltre soglia, query lente): notifica in basso a destra.
        var notifications = new Avalonia.Controls.Notifications.WindowNotificationManager(this)
        { Position = Avalonia.Controls.Notifications.NotificationPosition.BottomRight, MaxItems = 4 };
        void OnMonitorAlert(string title, string message) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            notifications.Show(new Avalonia.Controls.Notifications.Notification(title, message, Avalonia.Controls.Notifications.NotificationType.Warning, TimeSpan.FromSeconds(15))));
        ServerMonitorViewModel.AlertRaised += OnMonitorAlert;
        Closed += (_, _) => ServerMonitorViewModel.AlertRaised -= OnMonitorAlert;
        Opened += async (_, _) => await viewModel.InitializeAsync();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.IsTreeVisible)) SetPanelVisible(0, 1, viewModel.IsTreeVisible, ref _treeWidth);
            else if (args.PropertyName == nameof(MainWindowViewModel.IsInfoVisible)) SetPanelVisible(4, 3, viewModel.IsInfoVisible, ref _infoWidth);
        };
        ConnectionTree.ContextRequested += (_, args) =>
        {
            if (args.Source is not Avalonia.Visual source || source.FindAncestorOfType<TreeViewItem>(includeSelf: true) is not { DataContext: TreeNodeViewModel node } item) return;
            if (node.IsPlaceholder) return;
            if (viewModel.SelectedNode != node) viewModel.SelectedNode = node;
            var actions = viewModel.GetContextActions(node);
            if (actions.Count == 0) return;
            var menu = new ContextMenu { ItemsSource = BuildMenuItems(actions) };
            menu.Open(item);
            args.Handled = true;
        };
        ConnectionTree.AddHandler(KeyDownEvent, (_, args) =>
        {
            // Canc sulla connessione: elimina (con conferma) · F2: modifica connessione / rinomina cartella.
            // Alt+Su / Alt+Giù: riordina connessioni e cartelle.
            if (args.KeyModifiers == Avalonia.Input.KeyModifiers.Alt && args.Key is Avalonia.Input.Key.Up or Avalonia.Input.Key.Down
                && viewModel.SelectedNode is { Kind: TreeNodeKind.Connection or TreeNodeKind.Group } moving)
            {
                var key = moving.Kind == TreeNodeKind.Group ? MainWindowViewModel.FolderKey(moving.FolderPath!) : MainWindowViewModel.ConnectionKey(moving.Profile!.Id);
                viewModel.ReorderCommand.Execute(new ReorderRequest(key, args.Key == Avalonia.Input.Key.Up ? -1 : 1));
                args.Handled = true;
                return;
            }
            if (args.KeyModifiers == Avalonia.Input.KeyModifiers.None && viewModel.SelectedNode is { } selected)
            {
                if (args.Key == Avalonia.Input.Key.F5)
                {
                    // F5 aggiorna il nodo selezionato (connessione aperta, database, categoria); sulle tabelle la loro categoria.
                    var target = selected.Kind is TreeNodeKind.Table or TreeNodeKind.DbObject ? selected.Parent : selected;
                    if (target is { Kind: not TreeNodeKind.Group } && (target.Kind != TreeNodeKind.Connection || target.IsLoaded)) viewModel.RefreshNodeCommand.Execute(target);
                    args.Handled = true;
                }
                else if (args.Key == Avalonia.Input.Key.Delete && selected.Kind == TreeNodeKind.Connection) { viewModel.DeleteSelectedConnectionCommand.Execute(null); args.Handled = true; }
                else if (args.Key == Avalonia.Input.Key.F2 && selected.Kind == TreeNodeKind.Connection) { viewModel.RenameConnectionCommand.Execute(selected); args.Handled = true; }
                else if (args.Key == Avalonia.Input.Key.F2 && selected.Kind == TreeNodeKind.Group) { viewModel.RenameFolderCommand.Execute(selected.FolderPath); args.Handled = true; }
                if (args.Handled) return;
            }
            if (!args.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) return;
            if (args.Key == Avalonia.Input.Key.C) { viewModel.CopyTablesCommand.Execute(viewModel.SelectedNode); args.Handled = true; }
            else if (args.Key == Avalonia.Input.Key.V) { viewModel.PasteTablesCommand.Execute(viewModel.SelectedNode); args.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SetupConnectionDragDrop(viewModel);
        SetupPalette(viewModel);
        // Tasto destro su una scheda: chiudi/sposta/duplica. Clic centrale: chiudi.
        WorkspaceTabs.ContextRequested += (_, args) =>
        {
            if ((args.Source as Visual)?.FindAncestorOfType<TabItem>(includeSelf: true) is not { DataContext: WorkspaceTabViewModel tab } item) return;
            new ContextMenu { ItemsSource = BuildMenuItems(viewModel.GetTabActions(tab)) }.Open(item);
            args.Handled = true;
        };
        WorkspaceTabs.AddHandler(PointerReleasedEvent, (_, args) =>
        {
            if (args.InitialPressMouseButton == Avalonia.Input.MouseButton.Middle
                && (args.Source as Visual)?.FindAncestorOfType<TabItem>(includeSelf: true) is { DataContext: WorkspaceTabViewModel { CanClose: true } tab })
            {
                viewModel.CloseTabCommand.Execute(tab);
                args.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // File › Apri recenti: l'elenco si ricostruisce a ogni apertura del sottomenu.
        RecentMenu.ItemsSource = new object[] { new MenuItem { Header = "(nessun file recente)", IsEnabled = false } };
        // Preferiti: aggiungi l'elemento corrente, poi l'elenco (clic = apri), poi rimozione.
        FavoritesMenu.ItemsSource = new object[] { new MenuItem { Header = "(nessun preferito)", IsEnabled = false } };
        FavoritesMenu.SubmenuOpened += (_, _) =>
        {
            var items = new List<object> { new MenuItem { Header = "Aggiungi ai preferiti", Command = viewModel.AddFavoriteCommand, CommandParameter = viewModel.SelectedTab is TableTabViewModel or QueryTabViewModel ? null : viewModel.SelectedNode, Icon = new Controls.NfIcon { Kind = "star", Width = 14, Height = 14 } }, new Separator() };
            var favorites = viewModel.Favorites;
            if (favorites.Count == 0) items.Add(new MenuItem { Header = "(nessun preferito: tasto destro su una tabella › Aggiungi ai preferiti)", IsEnabled = false });
            foreach (var favorite in favorites)
                items.Add(new MenuItem { Header = $"{favorite.Title}  ·  {favorite.Subtitle}", Command = viewModel.OpenFavoriteCommand, CommandParameter = favorite.Key, Icon = new Controls.NfIcon { Kind = favorite.Glyph, Width = 14, Height = 14 } });
            if (favorites.Count > 0)
            {
                var remove = new MenuItem { Header = "Rimuovi dai preferiti" };
                remove.ItemsSource = favorites.Select(f => new MenuItem { Header = f.Title, Command = viewModel.RemoveFavoriteCommand, CommandParameter = f.Key }).ToArray();
                items.Add(new Separator());
                items.Add(remove);
            }
            FavoritesMenu.ItemsSource = items;
        };
        Closing += (_, _) => viewModel.SaveSession();
        RecentMenu.SubmenuOpened += (_, _) =>
        {
            var items = new List<object>();
            foreach (var path in viewModel.RecentFiles)
                items.Add(new MenuItem { Header = System.IO.Path.GetFileName(path), Command = viewModel.OpenRecentCommand, CommandParameter = path, [ToolTip.TipProperty] = path });
            if (items.Count == 0) items.Add(new MenuItem { Header = "(nessun file recente)", IsEnabled = false });
            else
            {
                items.Add(new Separator());
                items.Add(new MenuItem { Header = "Svuota elenco", Command = viewModel.ClearRecentCommand });
            }
            RecentMenu.ItemsSource = items;
        };
        ConnectionTree.Tapped += (_, args) =>
        {
            if (args.Source is Avalonia.Visual source && source.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is TreeNodeViewModel node)
                viewModel.ReactivateNode(node);
        };
        ConnectionTree.DoubleTapped += (_, args) =>
        {
            if (args.Source is Avalonia.Visual source && source.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is TreeNodeViewModel node)
                viewModel.OpenNodeCommand.Execute(node);
        };
        StartScreenshotIfRequested();
    }

    /// <summary>Barra comandi: focus sulla ricerca, frecce/Invio/Esc, clic fuori per chiudere.</summary>
    private void SetupPalette(MainWindowViewModel viewModel)
    {
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.IsPaletteOpen) && viewModel.IsPaletteOpen)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => PaletteBox.Focus(), Avalonia.Threading.DispatcherPriority.Input);
        };
        PaletteBox.AddHandler(KeyDownEvent, (_, args) =>
        {
            switch (args.Key)
            {
                case Avalonia.Input.Key.Down: viewModel.MovePaletteSelection(1); args.Handled = true; break;
                case Avalonia.Input.Key.Up: viewModel.MovePaletteSelection(-1); args.Handled = true; break;
                case Avalonia.Input.Key.Enter: _ = viewModel.RunPaletteItemCommand.ExecuteAsync(null); args.Handled = true; break;
                case Avalonia.Input.Key.Escape: viewModel.ClosePaletteCommand.Execute(null); args.Handled = true; break;
            }
            if (args.Handled && viewModel.SelectedPaletteItem is { } item) PaletteList.ScrollIntoView(item);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        PaletteList.DoubleTapped += (_, _) => _ = viewModel.RunPaletteItemCommand.ExecuteAsync(null);
        PaletteList.AddHandler(PointerReleasedEvent, (_, args) =>
        {
            if ((args.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is PaletteItem item)
                _ = viewModel.RunPaletteItemCommand.ExecuteAsync(item);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        PaletteOverlay.PointerPressed += (_, args) =>
        {
            if (ReferenceEquals(args.Source, PaletteOverlay)) viewModel.ClosePaletteCommand.Execute(null);
        };
    }

    private const string ConnectionFormat = "application/x-navifox-connection";
    private Avalonia.Point? _dragStart;
    private TreeNodeViewModel? _dragNode;

    /// <summary>
    /// Trascinamento nell'albero: connessioni e cartelle si spostano prima/dopo un altro elemento (bordo superiore/inferiore della riga)
    /// oppure dentro una cartella (centro della riga). Nello spazio vuoto vanno in fondo alla radice.
    /// </summary>
    private void SetupConnectionDragDrop(MainWindowViewModel viewModel)
    {
        Avalonia.Input.DragDrop.SetAllowDrop(ConnectionTree, true);
        ConnectionTree.AddHandler(PointerPressedEvent, (_, args) =>
        {
            _dragNode = null;
            if (!args.GetCurrentPoint(ConnectionTree).Properties.IsLeftButtonPressed) return;
            if ((args.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is TreeNodeViewModel { Kind: TreeNodeKind.Connection or TreeNodeKind.Group } node)
            {
                _dragNode = node;
                _dragStart = args.GetPosition(ConnectionTree);
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ConnectionTree.AddHandler(PointerMovedEvent, async (_, args) =>
        {
            if (_dragNode is not { } node || _dragStart is not { } start || !args.GetCurrentPoint(ConnectionTree).Properties.IsLeftButtonPressed) return;
            var position = args.GetPosition(ConnectionTree);
            if (Math.Abs(position.X - start.X) + Math.Abs(position.Y - start.Y) < 8) return;
            _dragNode = null;
            _dragStart = null;
            var key = node.Kind == TreeNodeKind.Group ? MainWindowViewModel.FolderKey(node.FolderPath!) : MainWindowViewModel.ConnectionKey(node.Profile!.Id);
#pragma warning disable CS0618 // DataObject: API classica, ancora supportata in Avalonia 11
            var data = new Avalonia.Input.DataObject();
            data.Set(ConnectionFormat, key);
            await Avalonia.Input.DragDrop.DoDragDrop(args, data, Avalonia.Input.DragDropEffects.Move);
#pragma warning restore CS0618
            ClearDropHint();
        });
        ConnectionTree.AddHandler(PointerReleasedEvent, (_, _) => { _dragNode = null; _dragStart = null; }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ConnectionTree.AddHandler(Avalonia.Input.DragDrop.DragOverEvent, (_, args) =>
        {
#pragma warning disable CS0618
            if (!args.Data.Contains(ConnectionFormat)) { args.DragEffects = Avalonia.Input.DragDropEffects.None; return; }
#pragma warning restore CS0618
            args.DragEffects = Avalonia.Input.DragDropEffects.Move;
            var (item, node, where) = DropTarget(args);
            ShowDropHint(item, node, where);
        });
        ConnectionTree.AddHandler(Avalonia.Input.DragDrop.DragLeaveEvent, (_, _) => ClearDropHint());
        ConnectionTree.AddHandler(Avalonia.Input.DragDrop.DropEvent, async (_, args) =>
        {
            ClearDropHint();
#pragma warning disable CS0618
            if (args.Data.Get(ConnectionFormat) is not string key) return;
#pragma warning restore CS0618
            var (_, node, where) = DropTarget(args);
            await viewModel.DropAsync(key, node, where);
        });
        TreeNodeViewModel? ImportTarget(object? source) => (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as TreeNodeViewModel;
        Services.ImportFileDrop.Attach(ConnectionTree,
            source => ImportTarget(source) is { Profile.ReadOnly: false } target && (target.Kind == TreeNodeKind.Table && target.Category != "Viste" || target.Kind == TreeNodeKind.Category && target.Category == "Tabelle" || target.Kind is TreeNodeKind.Database or TreeNodeKind.Schema),
            (source, path) => viewModel.OpenImportWizardAsync(ImportTarget(source), path), message => viewModel.StatusMessage = message);
    }

    /// <summary>Elemento sotto il puntatore e posizione (sopra, dentro, sotto) in base all'altezza nella riga.</summary>
    private (TreeViewItem? Item, TreeNodeViewModel? Node, DropPosition Where) DropTarget(Avalonia.Input.DragEventArgs args)
    {
        var item = (args.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (item?.DataContext is not TreeNodeViewModel node) return (null, null, DropPosition.Inside);
        // Database, tabelle…: valgono come la loro connessione.
        while (node.Kind is not (TreeNodeKind.Connection or TreeNodeKind.Group) && node.Parent is { } parent)
        {
            node = parent;
            item = item?.FindAncestorOfType<TreeViewItem>();
        }
        if (item is null) return (null, null, DropPosition.Inside);
        var header = item.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "PART_Header")?.Bounds.Height is > 0 and var h ? h : 28;
        var y = args.GetPosition(item).Y;
        var where = node.Kind == TreeNodeKind.Group
            ? y < header * 0.28 ? DropPosition.Before : y > header * 0.72 ? DropPosition.After : DropPosition.Inside
            : y < header / 2 ? DropPosition.Before : DropPosition.After;
        return (item, node, where);
    }

    private TreeViewItem? _hintItem;

    /// <summary>Segno visivo durante il trascinamento: linea sopra/sotto la riga oppure cartella evidenziata.</summary>
    private void ShowDropHint(TreeViewItem? item, TreeNodeViewModel? node, DropPosition where)
    {
        if (_hintItem != item) ClearDropHint();
        _hintItem = item;
        if (item is null) return;
        item.Classes.Set("drop-before", where == DropPosition.Before);
        item.Classes.Set("drop-after", where == DropPosition.After);
        item.Classes.Set("drop-inside", where == DropPosition.Inside);
    }

    private void ClearDropHint()
    {
        if (_hintItem is null) return;
        _hintItem.Classes.Remove("drop-before");
        _hintItem.Classes.Remove("drop-after");
        _hintItem.Classes.Remove("drop-inside");
        _hintItem = null;
    }

    private GridLength _treeWidth = new(280);
    private GridLength _infoWidth = new(300);

    /// <summary>Nasconde o mostra una colonna laterale ricordandone la larghezza.</summary>
    private void SetPanelVisible(int column, int splitter, bool visible, ref GridLength saved)
    {
        var columns = MainGrid.ColumnDefinitions;
        if (visible)
        {
            columns[column].Width = saved.Value > 40 ? saved : new GridLength(column == 0 ? 280 : 300);
            columns[splitter].Width = new GridLength(6);
        }
        else
        {
            if (columns[column].Width.Value > 40) saved = columns[column].Width;
            columns[column].Width = new GridLength(0);
            columns[splitter].Width = new GridLength(0);
        }
    }

    internal static List<Control> BuildMenuItems(IEnumerable<ContextAction> actions) => actions.Select(action => action.IsSeparator
        ? (Control)new Separator()
        : new MenuItem
        {
            Header = action.Header,
            Command = action.Command,
            CommandParameter = action.Parameter,
            IsEnabled = action.IsEnabled && (action.Command is not null || action.Children is not null),
            Icon = action.Glyph is null ? null : new Navifox.App.Controls.NfIcon { Kind = action.Glyph, Width = 15, Height = 15, Classes = { "menu-icon" } },
            ItemsSource = action.Children is null ? null : BuildMenuItems(action.Children),
            [ToolTip.TipProperty] = action.ToolTip
        }).ToList();

    private void StartScreenshotIfRequested()
    {
        var arguments = Environment.GetCommandLineArgs();
        var optionIndex = Array.IndexOf(arguments, "--screenshot");
        if (optionIndex < 0 || optionIndex + 1 >= arguments.Length) return;
        var output = Path.GetFullPath(arguments[optionIndex + 1]);
        var frameCount = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            if (++frameCount < 120) return;
            timer.Stop();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height)), new Vector(96, 96));
                bitmap.Render(this);
                bitmap.Save(output);
                Close();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Screenshot non salvato: {exception.Message}");
                Environment.ExitCode = 1;
                Close();
            }
        };
        timer.Start();
    }
}
