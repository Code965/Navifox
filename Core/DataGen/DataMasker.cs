using System.Security.Cryptography;
using System.Text;
using Navifox.Core.Models;

namespace Navifox.Core.DataGen;

/// <summary>
/// Mascheramento dei campi riconosciuti durante la copia. Lo stesso valore mantiene il sostituto nella stessa copia;
/// un nuovo sale per trasferimento evita una corrispondenza stabile tra esportazioni. NULL e chiavi escluse restano invariati.
/// Il riconoscimento per nome/tipo non garantisce l'anonimizzazione completa del contenuto.
/// </summary>
public sealed class DataMasker
{
    private readonly (int Index, GeneratorColumn Column)[] _masked;
    private readonly byte[] _salt;
    private readonly Dictionary<(string Column, string Original), object?> _values = new();
    private readonly Dictionary<string, Dictionary<string, string>> _used = new(StringComparer.Ordinal);

    private DataMasker((int, GeneratorColumn)[] masked, byte[] salt) { _masked = masked; _salt = salt.ToArray(); }

    /// <summary>Colonne riconosciute come sensibili (nome → tipo di dato finto).</summary>
    public IReadOnlyList<(string Column, FakeKind Kind)> MaskedColumns => _masked.Select(m => (m.Column.Column.Name, m.Column.Kind)).ToArray();

    /// <summary>Null se nessuna colonna della tabella è sensibile.</summary>
    public static DataMasker? For(IReadOnlyList<ColumnInfo> columns, IEnumerable<string>? excludedColumns = null,
        IReadOnlyList<DesignColumn>? definitions = null, IEnumerable<string>? uniqueColumns = null, byte[]? salt = null)
    {
        var masked = new List<(int, GeneratorColumn)>();
        var excluded = (excludedColumns ?? []).ToHashSet(StringComparer.Ordinal);
        var unique = (uniqueColumns ?? []).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++)
        {
            var info = columns[i];
            if (info.IsPrimaryKey || info.IsAutoIncrement || info.IsGenerated || excluded.Contains(info.Name)) continue;
            var design = definitions?.FirstOrDefault(c => c.Name == info.Name) ?? new DesignColumn(info.Name, info.Name, BaseType(info.DataType), Length(info.DataType), NotNull: !info.IsNullable);
            var kind = ColumnGuesser.Guess(design, isForeignKey: false);
            if (FakeKinds.IsSensitive(kind)) masked.Add((i, new GeneratorColumn(design, kind) { IsUnique = unique.Contains(info.Name) }));
        }
        return masked.Count == 0 ? null : new DataMasker([.. masked], salt ?? RandomNumberGenerator.GetBytes(32));
    }

    public IReadOnlyList<object?> Mask(IReadOnlyList<object?> row)
    {
        var result = row.ToArray();
        foreach (var (index, column) in _masked)
        {
            if (index >= result.Length || result[index] is null or DBNull) continue;
            var original = result[index] is DateTime date ? date.ToString("O") : Convert.ToString(result[index], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            if (!_values.TryGetValue((column.Column.Name, original), out var value))
            {
                for (var attempt = 0; ; attempt++)
                {
                    if (attempt == 1000) throw new InvalidOperationException($"Non è possibile mascherare altri valori univoci per «{column.Column.Name}».");
                    var key = attempt == 0 ? original : original + "#" + attempt;
                    // La regola resta identica tra tabelle durante la stessa copia, senza memorizzare il valore originale nel risultato.
                    var config = new GeneratorColumn(column.Column, column.Kind);
                    value = new DataGenerator([config], Seed(column.Kind, key)) { RandomNulls = false }.Next()[0];
                    if (column.IsUnique && value is string text && column.Kind is not (FakeKind.FiscalCode or FakeKind.VatNumber or FakeKind.Iban or FakeKind.Phone or FakeKind.Password))
                        value = UniqueText(text, column, key);
                    if (!column.IsUnique || value is null) break;
                    if (!_used.TryGetValue(column.Column.Name, out var used)) _used[column.Column.Name] = used = new(StringComparer.Ordinal);
                    var representation = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    if (used.TryAdd(representation, original)) break;
                }
                _values[(column.Column.Name, original)] = value;
            }
            // Il tipo resta quello della colonna d'origine (es. data di nascita come DateTime).
            result[index] = result[index] is DateTime && value is DateTime ? value : value is DateTime d ? d.ToString("yyyy-MM-dd") : value;
        }
        return result;
    }

    private string UniqueText(string value, GeneratorColumn column, string original)
    {
        var token = Convert.ToHexString(HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(original))).ToLowerInvariant();
        var max = column.Column.Length ?? 100;
        if (column.Kind == FakeKind.Email)
        {
            var domain = max >= 14 ? "@example.test" : "@e.test";
            if (max <= domain.Length) throw new InvalidOperationException("Colonna email troppo corta per il mascheramento.");
            return token[..Math.Min(token.Length, max - domain.Length)] + domain;
        }
        var size = Math.Min(12, Math.Max(1, max - 2));
        var prefix = Math.Min(value.Length, Math.Max(0, max - size - 1));
        return (prefix > 0 ? value[..prefix] + "-" : "") + token[..size];
    }

    private int Seed(FakeKind kind, string value)
    {
        var hash = HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes($"navifox-mask/{kind}/{value}"));
        return BitConverter.ToInt32(hash, 0) & int.MaxValue;
    }

    private static string BaseType(string dataType)
    {
        var type = dataType.Trim().ToLowerInvariant();
        var paren = type.IndexOf('(');
        if (paren > 0 && !type.StartsWith("enum", StringComparison.Ordinal)) type = type[..paren];
        return type.Replace(" unsigned", string.Empty).Trim();
    }

    private static int? Length(string dataType)
    {
        var match = System.Text.RegularExpressions.Regex.Match(dataType, @"\((\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var length) ? length : null;
    }
}
