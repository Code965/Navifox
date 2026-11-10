using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Metadata;
using Navifox.App.ViewModels;

namespace Navifox.App.Controls;

/// <summary>
/// Modello delle righe dell'albero: quello completo (dialetto, host, etichette) solo per le connessioni,
/// uno leggero per database, categorie e tabelle, che possono essere migliaia.
/// </summary>
public sealed class TreeNodeTemplateSelector : ITreeDataTemplate
{
    /// <summary>Righe delle connessioni.</summary>
    public TreeDataTemplate? Connection { get; set; }

    /// <summary>Tutte le altre righe.</summary>
    [Content]
    public TreeDataTemplate? Node { get; set; }

    private TreeDataTemplate? For(object? item) => item is TreeNodeViewModel { IsConnection: true } ? Connection ?? Node : Node ?? Connection;

    public bool Match(object? data) => data is TreeNodeViewModel;

    public Control? Build(object? param) => For(param)?.Build(param);

    public InstancedBinding? ItemsSelector(object item) => For(item)?.ItemsSelector(item);
}

/// <summary>Pannello dei figli di un nodo dell'albero: virtualizzato per le categorie, normale per il resto.</summary>
public static class TreePanels
{
    private static readonly ITemplate<Panel?> Virtual = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
    private static readonly ITemplate<Panel?> Plain = new FuncTemplate<Panel?>(() => new StackPanel());

    public static readonly Avalonia.Data.Converters.IValueConverter ForChildren =
        new Avalonia.Data.Converters.FuncValueConverter<bool, ITemplate<Panel?>>(virtualize => virtualize ? Virtual : Plain);
}
