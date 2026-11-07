using CommunityToolkit.Mvvm.ComponentModel;

namespace Navifox.App.ViewModels;

/// <summary>Base comune per le schede dell'area di lavoro (Oggetti, tabelle, query…).</summary>
public abstract class WorkspaceTabViewModel : ObservableObject
{
    public abstract string TabTitle { get; }
    public virtual string TabGlyph => "table";
    public virtual bool CanClose => true;
    /// <summary>Chiamato alla chiusura della scheda (es. per annullare una transazione aperta).</summary>
    public virtual ValueTask CloseAsync() => ValueTask.CompletedTask;
}
