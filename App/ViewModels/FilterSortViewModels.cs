using CommunityToolkit.Mvvm.ComponentModel;
using Navifox.Core;

namespace Navifox.App.ViewModels;

public sealed record FilterOperator(string Label, string Kind, bool NeedsValue = true, bool NeedsSecondValue = false)
{
    public override string ToString() => Label;

    public static IReadOnlyList<FilterOperator> All { get; } =
    [
        new("=", "eq"), new("≠", "ne"), new("<", "lt"), new("≤", "le"), new(">", "gt"), new("≥", "ge"),
        new("contiene", "contains"), new("non contiene", "notcontains"), new("inizia con", "starts"), new("finisce con", "ends"),
        new("è NULL", "null", false), new("non è NULL", "notnull", false), new("è vuoto", "empty", false), new("non è vuoto", "notempty", false),
        new("è compreso tra", "between", true, true), new("è in elenco", "in"), new("SQL personalizzato", "custom"),
    ];
}

/// <summary>Una condizione della barra filtri: [✓] colonna operatore valore.</summary>
public partial class FilterConditionViewModel : ObservableObject
{
    public FilterConditionViewModel(IReadOnlyList<string> columns, string? column = null)
    {
        Columns = columns;
        _column = column ?? columns.FirstOrDefault() ?? string.Empty;
    }

    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<FilterOperator> Operators => FilterOperator.All;
    public IReadOnlyList<string> Connectors { get; } = ["AND", "OR"];

    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private string _column;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsValue), nameof(NeedsSecondValue), nameof(IsCustom), nameof(ShowColumn))]
    private FilterOperator _operator = FilterOperator.All[0];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueLabel))]
    private string _value = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value2Label))]
    private string _value2 = string.Empty;

    /// <summary>Testo del valore nel criterio: «&lt;?&gt;» finché non è inserito.</summary>
    public string ValueLabel => Value.Length == 0 ? "<?>" : Value;
    public string Value2Label => Value2.Length == 0 ? "<?>" : Value2;
    [ObservableProperty] private string _connector = "AND";
    [ObservableProperty] private bool _isFirst;
    [ObservableProperty] private bool _isSelected;

    public bool NeedsValue => Operator.NeedsValue;
    public bool NeedsSecondValue => Operator.NeedsSecondValue;
    public bool IsCustom => Operator.Kind == "custom";
    public bool ShowColumn => !IsCustom;

    public string? BuildSql(IDialectProvider provider)
    {
        if (!IsEnabled) return null;
        if (IsCustom) return string.IsNullOrWhiteSpace(Value) ? null : $"({Value})";
        if (string.IsNullOrEmpty(Column)) return null;
        var column = provider.QuoteIdentifier(Column);
        var mysql = provider.UsesMySqlScriptSyntax;
        var text = provider.Kind switch { Navifox.Core.Models.DatabaseKind.MySql or Navifox.Core.Models.DatabaseKind.Sqlite => column, Navifox.Core.Models.DatabaseKind.SqlServer => $"CAST({column} AS nvarchar(max))", _ => $"CAST({column} AS text)" };
        var like = provider.Kind == Navifox.Core.Models.DatabaseKind.PostgreSql ? "ILIKE" : "LIKE";
        string Literal(string value) => provider.FormatLiteral(value);
        string Pattern(string prefix, string suffix) => Literal(prefix + EscapeLike(Value) + suffix);
        return Operator.Kind switch
        {
            "eq" => $"{column} = {Literal(Value)}",
            "ne" => $"{column} <> {Literal(Value)}",
            "lt" => $"{column} < {Literal(Value)}",
            "le" => $"{column} <= {Literal(Value)}",
            "gt" => $"{column} > {Literal(Value)}",
            "ge" => $"{column} >= {Literal(Value)}",
            "contains" => $"{text} {like} {Pattern("%", "%")}",
            "notcontains" => $"{text} NOT {like} {Pattern("%", "%")}",
            "starts" => $"{text} {like} {Pattern(string.Empty, "%")}",
            "ends" => $"{text} {like} {Pattern("%", string.Empty)}",
            "null" => $"{column} IS NULL",
            "notnull" => $"{column} IS NOT NULL",
            "empty" => $"({column} IS NULL OR {text} = '')",
            "notempty" => $"({column} IS NOT NULL AND {text} <> '')",
            "between" => $"{column} BETWEEN {Literal(Value)} AND {Literal(Value2)}",
            "in" => $"{column} IN ({string.Join(", ", Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(Literal))})",
            _ => null
        };
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

/// <summary>Un criterio di ordinamento: [✓] colonna direzione.</summary>
public partial class SortConditionViewModel : ObservableObject
{
    public SortConditionViewModel(IReadOnlyList<string> columns, string? column = null, bool descending = false)
    {
        Columns = columns;
        _column = column ?? columns.FirstOrDefault() ?? string.Empty;
        _direction = descending ? "Decrescente" : "Crescente";
    }

    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<string> Directions { get; } = ["Crescente", "Decrescente"];

    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private string _column;
    [ObservableProperty] private string _direction;

    public bool IsDescending => Direction == "Decrescente";

    public string? BuildSql(IDialectProvider provider) =>
        IsEnabled && !string.IsNullOrEmpty(Column) ? $"{provider.QuoteIdentifier(Column)} {(IsDescending ? "DESC" : "ASC")}" : null;
}
