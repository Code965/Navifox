using CommunityToolkit.Mvvm.ComponentModel;

namespace Navifox.App.ViewModels;

/// <summary>Base comune per le schede dell'area di lavoro (Oggetti, tabelle, query…).</summary>
public abstract class WorkspaceTabViewModel : ObservableObject
{
    public abstract string TabTitle { get; }
    public virtual string TabGlyph => "table";
    public virtual bool CanClose => true;
    /// <summary>Connessione della scheda (per colore e avviso di produzione); null per le schede generali.</summary>
    public virtual Navifox.Core.Models.ConnectionProfile? TabConnection => null;
    /// <summary>Chiamato alla chiusura della scheda (es. per annullare una transazione aperta).</summary>
    public virtual ValueTask CloseAsync() => ValueTask.CompletedTask;
}
