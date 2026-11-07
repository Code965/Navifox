using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using MosaicDB.App.ViewModels;

namespace MosaicDB.App;

public partial class MainWindow : Window
{
    public MainWindow() : this(App.Services.GetRequiredService<MainWindowViewModel>()) { }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
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
            if (!args.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) return;
            if (args.Key == Avalonia.Input.Key.C) { viewModel.CopyTablesCommand.Execute(viewModel.SelectedNode); args.Handled = true; }
            else if (args.Key == Avalonia.Input.Key.V) { viewModel.PasteTablesCommand.Execute(viewModel.SelectedNode); args.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ConnectionTree.DoubleTapped += (_, args) =>
        {
            if (args.Source is Avalonia.Visual source && source.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is TreeNodeViewModel node)
                viewModel.OpenNodeCommand.Execute(node);
        };
        StartScreenshotIfRequested();
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

    private static List<Control> BuildMenuItems(IEnumerable<ContextAction> actions) => actions.Select(action => action.IsSeparator
        ? (Control)new Separator()
        : new MenuItem
        {
            Header = action.Header,
            Command = action.Command,
            CommandParameter = action.Parameter,
            IsEnabled = action.IsEnabled && (action.Command is not null || action.Children is not null),
            Icon = action.Glyph is null ? null : new MosaicDB.App.Controls.NfIcon { Kind = action.Glyph, Width = 15, Height = 15, Classes = { "menu-icon" } },
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
