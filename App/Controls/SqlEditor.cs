using System.Text.RegularExpressions;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using MosaicDB.Core.Models;

namespace MosaicDB.App.Controls;

/// <summary>Fonte dei suggerimenti: tabelle e colonne dello schema corrente.</summary>
public interface ISqlCompletionSource
{
    SchemaModel? Schema { get; }
    IReadOnlyList<string> OtherNames { get; }
    /// <summary>Database corrente (per «database.» → tabelle).</summary>
    string? CurrentDatabase => null;
    /// <summary>Schema di un altro database, caricato su richiesta (null se non disponibile).</summary>
    Task<SchemaModel?> GetSchemaForAsync(string database) => Task.FromResult<SchemaModel?>(null);
}

/// <summary>
/// Editor SQL Navifox: evidenziazione sintattica e autocompletamento contestuale
/// (parole chiave, tabelle dopo FROM/JOIN, colonne dopo «tabella.» o «alias.»). Ctrl+Spazio apre i suggerimenti.
/// </summary>
public sealed class SqlEditor : TextEditor
{
    private static readonly string[] Keywords =
    [
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "NULL", "IS", "IN", "LIKE", "ILIKE", "BETWEEN", "EXISTS", "AS", "ON", "USING",
        "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN", "GROUP BY", "ORDER BY", "HAVING", "LIMIT", "OFFSET",
        "DISTINCT", "UNION", "UNION ALL", "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "CREATE TABLE", "ALTER TABLE",
        "DROP TABLE", "TRUNCATE TABLE", "CREATE INDEX", "CREATE VIEW", "PRIMARY KEY", "FOREIGN KEY", "REFERENCES", "DEFAULT",
        "CASE", "WHEN", "THEN", "ELSE", "END", "ASC", "DESC", "WITH", "RETURNING", "EXPLAIN", "SHOW", "DESCRIBE", "BEGIN", "COMMIT", "ROLLBACK",
        "COUNT(*)", "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "NULLIF", "CAST", "CONCAT", "LOWER", "UPPER", "TRIM", "LENGTH",
        "SUBSTRING", "REPLACE", "ROUND", "NOW()", "CURRENT_DATE", "CURRENT_TIMESTAMP", "DATE", "EXTRACT", "GROUP_CONCAT", "STRING_AGG",
    ];

    private static readonly HashSet<string> TableContextWords = new(StringComparer.OrdinalIgnoreCase) { "FROM", "JOIN", "UPDATE", "INTO", "TABLE", "DESCRIBE", "DESC" };

    private static readonly Regex TableReference = new(
        @"\b(?:FROM|JOIN|UPDATE|INTO)\s+(?:[`""\[]?(?<schema>\w+)[`""\]]?\.)?[`""\[]?(?<table>\w+)[`""\]]?(?:\s+(?:AS\s+)?(?<alias>(?!(?:WHERE|ON|JOIN|LEFT|RIGHT|INNER|OUTER|FULL|CROSS|GROUP|ORDER|LIMIT|SET|VALUES|USING|UNION|HAVING)\b)\w+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private CompletionWindow? _completion;

    public SqlEditor()
    {
        FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace");
        FontSize = 13;
        ShowLineNumbers = true;
        WordWrap = false;
        Options.ConvertTabsToSpaces = true;
        Options.IndentationSize = 4;
        Options.HighlightCurrentLine = true;
        Options.EnableHyperlinks = false;
        TextArea.TextEntered += OnTextEntered;
        TextArea.TextEntering += OnTextEntering;
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
        ApplyTheme();
    }

    protected override Type StyleKeyOverride => typeof(TextEditor);

    public ISqlCompletionSource? CompletionSource { get; set; }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ShowCompletion(force: true);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    // ───────────── Autocompletamento ─────────────

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        var c = e.Text[^1];
        if (c == '.' || (char.IsLetter(c) || c == '_') && _completion is null && CurrentWord().Length >= 1) ShowCompletion(force: false);
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        // Un carattere non identificativo conferma il suggerimento selezionato (come negli IDE).
        if (_completion is null || string.IsNullOrEmpty(e.Text)) return;
        var c = e.Text[0];
        if (!char.IsLetterOrDigit(c) && c != '_')
        {
            if (c is ' ' or '\t' or '\n' or '\r') _completion.Close();
            else _completion.CompletionList.RequestInsertion(e);
        }
    }

    private string CurrentWord()
    {
        var offset = CaretOffset;
        var start = offset;
        while (start > 0 && IsIdentifier(Document.GetCharAt(start - 1))) start--;
        return Document.GetText(start, offset - start);
    }

    private static bool IsIdentifier(char c) => char.IsLetterOrDigit(c) || c == '_';

    private void ShowCompletion(bool force)
    {
        var offset = CaretOffset;
        var wordStart = offset;
        while (wordStart > 0 && IsIdentifier(Document.GetCharAt(wordStart - 1))) wordStart--;
        var prefix = Document.GetText(wordStart, offset - wordStart);
        var items = BuildItems(wordStart);
        if (items.Count == 0)
        {
            // «altrodatabase.» → carica le tabelle di quel database e riapre i suggerimenti
            if (QualifierBefore(wordStart) is { } qualifier && CompletionSource is { } source
                && source.OtherNames.Contains(qualifier, StringComparer.OrdinalIgnoreCase))
                _ = ShowDatabaseTablesAsync(source, qualifier, wordStart);
            return;
        }
        if (!force && prefix.Length > 0 && !items.Any(item => item.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return;

        _completion?.Close();
        var window = new CompletionWindow(TextArea) { StartOffset = wordStart, EndOffset = offset, MinWidth = 300, CloseWhenCaretAtBeginning = true };
        window.Classes.Add("nf-completion");
        foreach (var item in items) window.CompletionList.CompletionData.Add(item);
        window.Closed += (_, _) => _completion = null;
        _completion = window;
        window.Show();
        if (prefix.Length > 0) window.CompletionList.SelectItem(prefix);
    }

    private string? QualifierBefore(int wordStart)
    {
        if (wordStart == 0 || Document.GetCharAt(wordStart - 1) != '.') return null;
        var end = wordStart - 1;
        var start = end;
        while (start > 0 && (IsIdentifier(Document.GetCharAt(start - 1)) || Document.GetCharAt(start - 1) is '`' or '"')) start--;
        return Document.GetText(start, end - start).Trim('`', '"');
    }

    private readonly Dictionary<string, SchemaModel?> _otherSchemas = new(StringComparer.OrdinalIgnoreCase);

    private async Task ShowDatabaseTablesAsync(ISqlCompletionSource source, string database, int wordStart)
    {
        if (!_otherSchemas.TryGetValue(database, out var schema))
        {
            schema = await source.GetSchemaForAsync(database);
            _otherSchemas[database] = schema;
        }
        if (schema is null || CaretOffset < wordStart || QualifierBefore(wordStart) != database) return;
        _completion?.Close();
        var window = new CompletionWindow(TextArea) { StartOffset = wordStart, EndOffset = CaretOffset, MinWidth = 340, CloseWhenCaretAtBeginning = true };
        foreach (var name in schema.Tables.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            window.CompletionList.CompletionData.Add(new SqlCompletionItem(name, database, schema.Views.Contains(name) ? "eye" : "table", 0));
        window.Closed += (_, _) => _completion = null;
        _completion = window;
        window.Show();
    }

    private List<SqlCompletionItem> BuildItems(int wordStart)
    {
        var schema = CompletionSource?.Schema;
        var text = Document.Text;
        var items = new List<SqlCompletionItem>();
        var references = ParseReferences(text);

        // «qualcosa.» → colonne della tabella o dell'alias
        if (wordStart > 0 && Document.GetCharAt(wordStart - 1) == '.')
        {
            var qualifierEnd = wordStart - 1;
            var qualifierStart = qualifierEnd;
            while (qualifierStart > 0 && (IsIdentifier(Document.GetCharAt(qualifierStart - 1)) || Document.GetCharAt(qualifierStart - 1) is '`' or '"')) qualifierStart--;
            var qualifier = Document.GetText(qualifierStart, qualifierEnd - qualifierStart).Trim('`', '"');
            var current = CompletionSource?.CurrentDatabase;
            // «database.» → tabelle del database (quello corrente subito, gli altri su richiesta)
            if (CompletionSource is { } src && src.OtherNames.Contains(qualifier, StringComparer.OrdinalIgnoreCase) && (schema is null || FindTable(schema, qualifier) is null))
            {
                if (schema is not null && string.Equals(qualifier, current, StringComparison.OrdinalIgnoreCase))
                    items.AddRange(schema.Tables.Keys.Select(name => new SqlCompletionItem(name, qualifier, schema.Views.Contains(name) ? "eye" : "table", 0)));
                else if (_otherSchemas.TryGetValue(qualifier, out var other) && other is not null)
                    items.AddRange(other.Tables.Keys.Select(name => new SqlCompletionItem(name, qualifier, other.Views.Contains(name) ? "eye" : "table", 0)));
                return items.OrderBy(item => item.Text, StringComparer.OrdinalIgnoreCase).ToList();
            }
            var table = references.TryGetValue(qualifier, out var resolved) ? resolved : qualifier;
            if (schema is not null && FindTable(schema, table) is { } columns)
            {
                items.AddRange(columns.Select(column => new SqlCompletionItem(column.Name, column.DataType + (column.IsPrimaryKey ? " · PK" : string.Empty),
                    column.IsPrimaryKey ? "key" : "column", 0)));
                return items;
            }
            // schema.tabella
            if (schema is not null) items.AddRange(schema.Tables.Keys.Select(name => new SqlCompletionItem(name, schema.Views.Contains(name) ? "vista" : "tabella", schema.Views.Contains(name) ? "eye" : "table", 0)));
            return items;
        }

        var previousWord = PreviousWord(wordStart);
        var tableContext = TableContextWords.Contains(previousWord) || (previousWord.Length > 0 && text[..wordStart].TrimEnd().EndsWith(',') && LastClauseIsFrom(text[..wordStart]));
        if (schema is not null)
        {
            var tables = schema.Tables.Keys.Select(name => new SqlCompletionItem(name, CompletionSource?.CurrentDatabase ?? (schema.Views.Contains(name) ? "vista" : "tabella"),
                schema.Views.Contains(name) ? "eye" : "table", tableContext ? 0 : 2));
            items.AddRange(tables);
            if (!tableContext)
            {
                var used = references.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var table in used)
                    if (FindTable(schema, table) is { } columns)
                        items.AddRange(columns.Select(column => new SqlCompletionItem(column.Name, $"{table} · {column.DataType}", column.IsPrimaryKey ? "key" : "column", 1)));
                items.AddRange(references.Keys.Where(alias => !used.Contains(alias, StringComparer.OrdinalIgnoreCase))
                    .Select(alias => new SqlCompletionItem(alias, $"alias di {references[alias]}", "table", 1)));
            }
        }
        if (!tableContext)
        {
            items.AddRange(Keywords.Select(keyword => new SqlCompletionItem(keyword, keyword.Contains('(') || keyword is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX" or "COALESCE" or "CAST" ? "funzione" : "parola chiave", "keyword", 3)));
            if (CompletionSource is { } source) items.AddRange(source.OtherNames.Select(name => new SqlCompletionItem(name, "database", "database", 4)));
        }
        return items
            .GroupBy(item => item.Text, StringComparer.OrdinalIgnoreCase).Select(group => group.OrderBy(item => item.Rank).First())
            .OrderBy(item => item.Rank).ThenBy(item => item.Text, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<ColumnInfo>? FindTable(SchemaModel schema, string table) =>
        schema.Tables.TryGetValue(table, out var exact) ? exact
        : schema.Tables.FirstOrDefault(pair => string.Equals(pair.Key, table, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Alias → tabella (e tabella → tabella) dai FROM/JOIN/UPDATE/INTO presenti nel testo.</summary>
    private static Dictionary<string, string> ParseReferences(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TableReference.Matches(text))
        {
            var table = match.Groups["table"].Value;
            map[table] = table;
            if (match.Groups["alias"].Success) map[match.Groups["alias"].Value] = table;
        }
        return map;
    }

    private string PreviousWord(int position)
    {
        var i = position - 1;
        while (i >= 0 && char.IsWhiteSpace(Document.GetCharAt(i))) i--;
        var end = i + 1;
        while (i >= 0 && IsIdentifier(Document.GetCharAt(i))) i--;
        return end > i + 1 ? Document.GetText(i + 1, end - i - 1) : string.Empty;
    }

    private static bool LastClauseIsFrom(string before)
    {
        var match = Regex.Matches(before, @"\b(FROM|WHERE|SELECT|SET|ON|GROUP|ORDER)\b", RegexOptions.IgnoreCase).LastOrDefault();
        return match is not null && match.Value.Equals("FROM", StringComparison.OrdinalIgnoreCase);
    }

    // ───────────── Evidenziazione ─────────────

    private void ApplyTheme()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        SyntaxHighlighting = LoadHighlighting(dark);
        Background = new SolidColorBrush(Color.Parse(dark ? "#1A1D22" : "#FFFFFF"));
        Foreground = new SolidColorBrush(Color.Parse(dark ? "#E9E7E4" : "#1D1B19"));
        LineNumbersForeground = new SolidColorBrush(Color.Parse(dark ? "#5C626B" : "#B5AEA6"));
        TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.Parse(dark ? "#22262C" : "#FAF8F5"));
        TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent);
        TextArea.SelectionBrush = new SolidColorBrush(Color.Parse(dark ? "#5A2E14" : "#FFD9C2"));
        TextArea.SelectionForeground = null;
        TextArea.Caret.CaretBrush = new SolidColorBrush(Color.Parse(dark ? "#FF7A2F" : "#E8590C"));
    }

    private static IHighlightingDefinition LoadHighlighting(bool dark)
    {
        string keyword = dark ? "#FF8A47" : "#C2410C", literal = dark ? "#6FD39A" : "#2E7D4F", number = dark ? "#B896FF" : "#7B4FC8",
            comment = dark ? "#7A8089" : "#9BA1A9", identifier = dark ? "#C9CDD2" : "#3D3934", function = dark ? "#FFB07F" : "#9A3412";
        var words = string.Join("", new[]
        {
            "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "NULL", "IS", "IN", "LIKE", "ILIKE", "BETWEEN", "EXISTS", "AS", "ON", "USING", "JOIN", "INNER", "LEFT", "RIGHT",
            "FULL", "OUTER", "CROSS", "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET", "DISTINCT", "UNION", "ALL", "INSERT", "INTO", "VALUES", "UPDATE", "SET",
            "DELETE", "CREATE", "ALTER", "DROP", "TRUNCATE", "TABLE", "INDEX", "VIEW", "DATABASE", "SCHEMA", "PRIMARY", "FOREIGN", "KEY", "REFERENCES", "DEFAULT",
            "CONSTRAINT", "UNIQUE", "CHECK", "CASE", "WHEN", "THEN", "ELSE", "END", "ASC", "DESC", "WITH", "RETURNING", "EXPLAIN", "SHOW", "DESCRIBE", "BEGIN",
            "COMMIT", "ROLLBACK", "TRANSACTION", "IF", "TRUE", "FALSE", "CASCADE", "ENGINE", "CHARSET", "COLLATE", "AUTO_INCREMENT", "INT", "INTEGER", "BIGINT",
            "VARCHAR", "CHAR", "TEXT", "DATETIME", "TIMESTAMP", "DATE", "DECIMAL", "NUMERIC", "BOOLEAN", "JSON", "JSONB", "SERIAL", "GRANT", "REVOKE", "PROCEDURE", "FUNCTION", "TRIGGER",
        }.Select(word => $"<Word>{word}</Word>"));
        var functions = string.Join("", new[] { "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "NULLIF", "CAST", "CONCAT", "LOWER", "UPPER", "TRIM", "LENGTH", "SUBSTRING",
            "REPLACE", "ROUND", "NOW", "EXTRACT", "GROUP_CONCAT", "STRING_AGG", "IFNULL", "DATE_FORMAT", "TO_CHAR" }.Select(word => $"<Word>{word}</Word>"));
        var xshd = $"""
            <SyntaxDefinition name="NaviFoxSQL" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="{comment}" fontStyle="italic" />
              <Color name="String" foreground="{literal}" />
              <Color name="Identifier" foreground="{identifier}" />
              <Color name="Keyword" foreground="{keyword}" fontWeight="bold" />
              <Color name="Function" foreground="{function}" />
              <Color name="Number" foreground="{number}" />
              <RuleSet ignoreCase="true">
                <Span color="Comment" begin="--" />
                <Span color="Comment" begin="\#" />
                <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
                <Span color="String"><Begin>'</Begin><End>'</End><RuleSet><Span begin="\\" end="." /></RuleSet></Span>
                <Span color="Identifier"><Begin>`</Begin><End>`</End></Span>
                <Span color="Identifier"><Begin>"</Begin><End>"</End></Span>
                <Keywords color="Keyword">{words}</Keywords>
                <Keywords color="Function">{functions}</Keywords>
                <Rule color="Number">\b\d+(\.\d+)?\b</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}

/// <summary>Voce dell'elenco di completamento con icona lineare e descrizione.</summary>
public sealed class SqlCompletionItem(string text, string description, string icon, int rank) : ICompletionData
{
    public string Text { get; } = text;
    public int Rank { get; } = rank;
    public object Description { get; } = description;
    public double Priority => -Rank;
    public Avalonia.Media.IImage? Image => null;

    public object Content { get; } = new StackPanel
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal,
        Spacing = 7,
        Children =
        {
            new NfIcon { Kind = icon, Width = 13, Height = 13, Classes = { "completion-icon" } },
            new TextBlock { Text = text, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
            new TextBlock { Text = description, FontSize = 11, Opacity = 0.55, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) },
        }
    };

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, Text);
}
