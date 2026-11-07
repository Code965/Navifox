using System.Windows.Input;

namespace Navifox.App.ViewModels;

/// <summary>Voce di menu contestuale indipendente dalla UI (la vista la trasforma in MenuItem).</summary>
public sealed record ContextAction(
    string Header,
    ICommand? Command = null,
    object? Parameter = null,
    string? Glyph = null,
    bool IsEnabled = true,
    IReadOnlyList<ContextAction>? Children = null,
    string? ToolTip = null)
{
    public static ContextAction Separator { get; } = new("-");
    public bool IsSeparator => Header == "-";
}

public sealed record DumpRequest(TreeNodeViewModel Node, bool IncludeData);
