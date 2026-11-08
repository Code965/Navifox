using System.Text.Json;

namespace Navifox.App.Services;

/// <summary>Snippet personale: nome, scorciatoia (parola + Tab nell'editor) e testo. «$0» indica dove resta il cursore.</summary>
public sealed record UserSnippet(string Name, string Shortcut, string Body);

/// <summary>Snippet personali salvati in snippets.json nella cartella dati di Navifox.</summary>
public static class SnippetStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static List<UserSnippet>? _cache;

    private static string FilePath => Navifox.Core.AppPaths.File("snippets.json");

    public static IReadOnlyList<UserSnippet> All
    {
        get
        {
            if (_cache is not null) return _cache;
            try
            {
                _cache = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<UserSnippet>>(File.ReadAllText(FilePath)) ?? []
                    : [];
            }
            catch (Exception) { _cache = []; }
            return _cache;
        }
    }

    /// <summary>Snippet con questa scorciatoia (maiuscole indifferenti); null se nessuno.</summary>
    public static UserSnippet? Find(string shortcut) =>
        All.FirstOrDefault(s => s.Shortcut.Length > 0 && s.Shortcut.Equals(shortcut, StringComparison.OrdinalIgnoreCase));

    public static void Save(IEnumerable<UserSnippet> snippets)
    {
        var list = snippets
            .Where(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrEmpty(s.Body))
            .Select(s => s with { Name = s.Name.Trim(), Shortcut = new string(s.Shortcut.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray()) })
            .ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, Options));
        File.Move(temp, FilePath, overwrite: true);
        _cache = list;
    }

    public static void Add(UserSnippet snippet) =>
        Save(All.Where(s => !s.Name.Equals(snippet.Name, StringComparison.OrdinalIgnoreCase)).Append(snippet));
}
