using System.Text.RegularExpressions;
using Avalonia.Controls;

namespace Navifox.App.Views;

/// <summary>Scelta dell'utente nella finestra «Aggiornamento disponibile».</summary>
public enum UpdateChoice
{
    Later,
    Update,
    Skip,
}

public sealed class UpdateDialogModel
{
    public required string NewVersion { get; init; }
    public required string CurrentVersion { get; init; }
    /// <summary>Note di rilascio in Markdown (dal pacchetto della release); possono mancare.</summary>
    public string? NotesMarkdown { get; init; }

    public string Title => $"È disponibile Navifox {NewVersion}";
    public string CurrentLabel => $"v{CurrentVersion} attuale";
    public string NewLabel => $"v{NewVersion}";
    public string Notes => string.IsNullOrWhiteSpace(NotesMarkdown) ? "Miglioramenti e correzioni." : ToPlainText(NotesMarkdown);

    /// <summary>Markdown semplice → testo leggibile: titoli, grassetti, corsivi e codice senza simboli, elenchi con il pallino.</summary>
    public static string ToPlainText(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            var text = line.TrimEnd();
            text = Regex.Replace(text, @"^\s{0,3}#{1,6}\s*", string.Empty);
            text = Regex.Replace(text, @"^(\s*)[-*+]\s+", "$1•  ");
            text = Regex.Replace(text, @"\*\*(.+?)\*\*|__(.+?)__", "$1$2");
            text = Regex.Replace(text, @"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])", "$1");
            text = Regex.Replace(text, @"`([^`]+)`", "$1");
            text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
            return text;
        });
        // Le note su una sola riga con «;» (come quelle passate a release.ps1) diventano un elenco.
        var joined = string.Join("\n", lines).Trim();
        if (!joined.Contains('\n') && joined.Count(c => c == ';') >= 2)
        {
            var parts = joined.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var head = parts[0].Contains(':') ? parts[0][..(parts[0].IndexOf(':') + 1)] : null;
            if (head is not null) parts[0] = parts[0][(head.Length)..].Trim();
            joined = (head is null ? string.Empty : head + "\n") + string.Join("\n", parts.Select(p => "•  " + char.ToUpper(p[0]) + p[1..].TrimEnd('.') + "."));
        }
        return Regex.Replace(joined, @"\n{3,}", "\n\n");
    }
}

public partial class UpdateDialog : Window
{
    public UpdateDialog()
    {
        InitializeComponent();
        UpdateButton.Click += (_, _) => Close(UpdateChoice.Update);
        LaterButton.Click += (_, _) => Close(UpdateChoice.Later);
        SkipButton.Click += (_, _) => Close(UpdateChoice.Skip);
    }
}
