using System.Globalization;
using System.Text.RegularExpressions;
using Navifox.Core.Models;

namespace Navifox.Core;

public enum QuickFilterOp { IsNull, NotNull, Equal, NotEqual, Greater, GreaterOrEqual, Less, LessOrEqual, Between, Like, NotLike, Contains }

public sealed record QuickFilterCondition(QuickFilterOp Op, string Value = "", string Value2 = "");

/// <summary>
/// Filtro rapido sotto le intestazioni della griglia. Sintassi:
/// <c>testo</c> (contiene; nelle colonne numeriche: uguale), <c>Roma*</c> / <c>*ma</c> (jolly), <c>=x</c>, <c>!x</c> o <c>&lt;&gt;x</c>,
/// <c>&gt;100</c>, <c>&gt;=100</c>, <c>&lt;5</c>, <c>&lt;=5</c>, <c>10..20</c> (intervallo), <c>null</c>, <c>!null</c>.
/// </summary>
public static class QuickFilter
{
    private static readonly Regex Comparison = new(@"^(>=|<=|<>|!=|>|<|=|!)\s*(.*)$", RegexOptions.Compiled | RegexOptions.Singleline);

    public static QuickFilterCondition? Parse(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Equals("null", StringComparison.OrdinalIgnoreCase) || value.Equals("=null", StringComparison.OrdinalIgnoreCase)) return new(QuickFilterOp.IsNull);
        if (Regex.IsMatch(value, @"^(!|<>|!=)\s*null$", RegexOptions.IgnoreCase)) return new(QuickFilterOp.NotNull);
        var match = Comparison.Match(value);
        if (match.Success)
        {
            var operand = Unquote(match.Groups[2].Value.Trim());
            var op = match.Groups[1].Value;
            if (op is "!" or "<>" or "!=") return operand.Contains('*') ? new(QuickFilterOp.NotLike, operand) : new(QuickFilterOp.NotEqual, operand);
            return new(op switch
            {
                ">=" => QuickFilterOp.GreaterOrEqual,
                "<=" => QuickFilterOp.LessOrEqual,
                ">" => QuickFilterOp.Greater,
                "<" => QuickFilterOp.Less,
                _ => QuickFilterOp.Equal
            }, operand);
        }
        var range = value.IndexOf("..", StringComparison.Ordinal);
        if (range > 0 && range < value.Length - 2)
            return new(QuickFilterOp.Between, Unquote(value[..range].Trim()), Unquote(value[(range + 2)..].Trim()));
        if (value.Contains('*')) return new(QuickFilterOp.Like, Unquote(value));
        return new(QuickFilterOp.Contains, Unquote(value));
    }

    /// <summary>Condizione SQL per una colonna (già quotata). <paramref name="literal"/> formatta i valori testuali.</summary>
    public static string ToSql(QuickFilterCondition condition, string column, bool numeric, DatabaseKind kind, Func<object?, string> literal)
    {
        string Value(string text) => numeric && Number(text) is { } n ? n.ToString(CultureInfo.InvariantCulture) : literal(text);
        string Pattern(string text) => literal(text.Replace('*', '%'));
        var asText = kind switch
        {
            DatabaseKind.PostgreSql => $"CAST({column} AS TEXT)",
            DatabaseKind.SqlServer when numeric => $"CAST({column} AS NVARCHAR(100))",
            _ => column
        };
        var like = kind == DatabaseKind.PostgreSql ? "ILIKE" : "LIKE";
        return condition.Op switch
        {
            QuickFilterOp.IsNull => $"{column} IS NULL",
            QuickFilterOp.NotNull => $"{column} IS NOT NULL",
            QuickFilterOp.Equal => $"{column} = {Value(condition.Value)}",
            QuickFilterOp.NotEqual => $"({column} <> {Value(condition.Value)} OR {column} IS NULL)",
            QuickFilterOp.Greater => $"{column} > {Value(condition.Value)}",
            QuickFilterOp.GreaterOrEqual => $"{column} >= {Value(condition.Value)}",
            QuickFilterOp.Less => $"{column} < {Value(condition.Value)}",
            QuickFilterOp.LessOrEqual => $"{column} <= {Value(condition.Value)}",
            QuickFilterOp.Between => $"{column} BETWEEN {Value(condition.Value)} AND {Value(condition.Value2)}",
            QuickFilterOp.Like => $"{asText} {like} {Pattern(condition.Value)}",
            QuickFilterOp.NotLike => $"({asText} NOT {like} {Pattern(condition.Value)} OR {column} IS NULL)",
            _ when numeric && Number(condition.Value) is { } n => $"{column} = {n.ToString(CultureInfo.InvariantCulture)}",
            _ => $"{asText} {like} {literal("%" + condition.Value + "%")}"
        };
    }

    /// <summary>Confronto lato client (risultati delle query già caricati).</summary>
    public static bool Matches(QuickFilterCondition condition, object? value)
    {
        var isNull = value is null or DBNull;
        switch (condition.Op)
        {
            case QuickFilterOp.IsNull: return isNull;
            case QuickFilterOp.NotNull: return !isNull;
            case QuickFilterOp.NotEqual: return isNull || Compare(value!, condition.Value) != 0;
            case QuickFilterOp.NotLike: return isNull || !Like(Text(value!), condition.Value);
        }
        if (isNull) return false;
        return condition.Op switch
        {
            QuickFilterOp.Equal => Compare(value!, condition.Value) == 0,
            QuickFilterOp.Greater => Compare(value!, condition.Value) > 0,
            QuickFilterOp.GreaterOrEqual => Compare(value!, condition.Value) >= 0,
            QuickFilterOp.Less => Compare(value!, condition.Value) < 0,
            QuickFilterOp.LessOrEqual => Compare(value!, condition.Value) <= 0,
            QuickFilterOp.Between => Compare(value!, condition.Value) >= 0 && Compare(value!, condition.Value2) <= 0,
            QuickFilterOp.Like => Like(Text(value!), condition.Value),
            _ => IsNumber(value!) && Number(condition.Value) is { } n
                ? Number(Text(value!)) == n
                : Text(value!).Contains(condition.Value, StringComparison.CurrentCultureIgnoreCase)
        };
    }

    private static int Compare(object value, string operand)
    {
        if (Number(Text(value)) is { } left && Number(operand) is { } right) return left.CompareTo(right);
        if (value is DateTime date && DateTime.TryParse(operand, CultureInfo.InvariantCulture, DateTimeStyles.None, out var other)) return date.CompareTo(other);
        if (value is DateTime itDate && DateTime.TryParse(operand, CultureInfo.GetCultureInfo("it-IT"), DateTimeStyles.None, out var itOther)) return itDate.CompareTo(itOther);
        return string.Compare(Text(value), operand, StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool Like(string text, string pattern) =>
        Regex.IsMatch(text, "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static bool IsNumber(object value) => value is byte or sbyte or short or ushort or int or uint or long or ulong or decimal or float or double;

    private static string Text(object value) => value switch
    {
        DateTime date => date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    /// <summary>Numero scritto con il punto o con la virgola decimale («12,5»).</summary>
    private static decimal? Number(string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return null;
        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
        if (!value.Contains('.') && decimal.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }

    private static string Unquote(string text) =>
        text.Length >= 2 && (text[0] == '\'' && text[^1] == '\'' || text[0] == '"' && text[^1] == '"') ? text[1..^1] : text;
}
