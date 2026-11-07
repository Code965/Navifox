using Avalonia.Controls;
using Avalonia.Media;

namespace MosaicDB.App.Views;

public sealed class MessageDialogModel
{
    public required string Title { get; init; }
    public required string Message { get; init; }
    public string ConfirmText { get; init; } = "OK";
    public string? CancelText { get; init; }
    public bool IsDanger { get; init; }
    public bool IsError { get; init; }
    public bool HasCancel => CancelText is not null;
    public string Glyph => IsError || IsDanger ? "!" : HasCancel ? "?" : "i";
    public IBrush IconBackground => new SolidColorBrush(Color.Parse(IsError || IsDanger ? "#C2410C" : "#E8590C"));
}

public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
        ConfirmButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
    }
}
