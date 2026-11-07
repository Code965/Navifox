using CommunityToolkit.Mvvm.ComponentModel;
using MosaicDB.Core;
using MosaicDB.Core.Models;

namespace DbClient.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public string DialectSummary { get; }

    public MainViewModel(IEnumerable<IDialectProvider> dialects)
    {
        DialectSummary = string.Join("  ·  ", dialects.Select(dialect => dialect.Kind.DisplayName()));
    }
}
