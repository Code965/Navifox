using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Navifox.Core.Models;

namespace Navifox.Core.DataGen;

/// <summary>Tipo di valore da generare per una colonna.</summary>
public enum FakeKind
{
    Skip, Null, Sequence, ForeignKey,
    FirstName, LastName, FullName, Email, Phone, FiscalCode, VatNumber, Iban, Username, Password,
    Street, City, Province, Region, PostalCode, Country, Company, Url,
    Integer, Decimal, Price, Boolean, Date, DateTime, BirthDate, Time, Uuid, Text, Enum, Json
}

/// <summary>Colonna da generare: struttura, tipo di dato scelto e valori ammessi (chiavi esterne, ENUM).</summary>
public sealed class GeneratorColumn(DesignColumn column, FakeKind kind)
{
    public DesignColumn Column { get; } = column;
    public FakeKind Kind { get; set; } = kind;
    /// <summary>Valori esistenti della tabella referenziata (chiave esterna).</summary>
    public IReadOnlyList<object?> ForeignValues { get; set; } = [];
    public string? ForeignGroup { get; set; }
    public IReadOnlyList<IReadOnlyList<object?>> ForeignRows { get; set; } = [];
    public int ForeignIndex { get; set; }
    /// <summary>Primo valore della sequenza (chiave primaria non automatica): MAX(colonna) + 1.</summary>
    public long SequenceStart { get; set; } = 1;
    /// <summary>Indice univoco: i testi ricevono un suffisso numerico per non ripetersi.</summary>
    public bool IsUnique { get; set; }
}

/// <summary>Etichette in italiano dei generatori (per l'interfaccia).</summary>
public static class FakeKinds
{
    public static readonly IReadOnlyList<(FakeKind Kind, string Label)> All =
    [
        (FakeKind.Skip, "— Non inserire (default/automatico)"), (FakeKind.Null, "NULL"), (FakeKind.Sequence, "Sequenza (1, 2, 3…)"),
        (FakeKind.ForeignKey, "Chiave esterna (valori esistenti)"), (FakeKind.FirstName, "Nome"), (FakeKind.LastName, "Cognome"),
        (FakeKind.FullName, "Nome e cognome"), (FakeKind.Email, "Email"), (FakeKind.Phone, "Telefono"), (FakeKind.FiscalCode, "Codice fiscale"),
        (FakeKind.VatNumber, "Partita IVA"), (FakeKind.Iban, "IBAN (di prova)"), (FakeKind.Username, "Nome utente"), (FakeKind.Password, "Password (hash fittizio)"),
        (FakeKind.Street, "Indirizzo"), (FakeKind.City, "Città"), (FakeKind.Province, "Provincia"), (FakeKind.Region, "Regione"),
        (FakeKind.PostalCode, "CAP"), (FakeKind.Country, "Nazione"), (FakeKind.Company, "Azienda"), (FakeKind.Url, "Sito web"),
        (FakeKind.Integer, "Numero intero"), (FakeKind.Decimal, "Numero decimale"), (FakeKind.Price, "Importo"), (FakeKind.Boolean, "Sì/No"),
        (FakeKind.Date, "Data"), (FakeKind.DateTime, "Data e ora"), (FakeKind.BirthDate, "Data di nascita"), (FakeKind.Time, "Ora"),
        (FakeKind.Uuid, "UUID"), (FakeKind.Text, "Testo"), (FakeKind.Enum, "Valore dell'elenco (ENUM)"), (FakeKind.Json, "JSON")
    ];

    public static string Label(FakeKind kind) => All.First(k => k.Kind == kind).Label;

    /// <summary>Dati personali o identificativi: vengono sostituiti dall'anonimizzazione.</summary>
    public static bool IsSensitive(FakeKind kind) => kind is FakeKind.FirstName or FakeKind.LastName or FakeKind.FullName or FakeKind.Email
        or FakeKind.Phone or FakeKind.FiscalCode or FakeKind.VatNumber or FakeKind.Iban or FakeKind.Street or FakeKind.Username
        or FakeKind.Password or FakeKind.BirthDate or FakeKind.Company;
}

/// <summary>Sceglie il generatore più adatto dal nome e dal tipo della colonna.</summary>
public static class ColumnGuesser
{
    private static readonly (string Pattern, FakeKind Kind)[] ByName =
    [
        (@"^(e_?mail|posta|mail)|e_?mail$|_mail$|pec$", FakeKind.Email),
        (@"codice_?fiscale|^cod_?fisc|^c_?f$|fiscal_?code|tax_?code|^cf_|_cf$", FakeKind.FiscalCode),
        (@"partita_?iva|^p_?iva$|^piva|vat", FakeKind.VatNumber),
        (@"iban", FakeKind.Iban),
        (@"tel|phone|cell|mobile|fax|cellulare", FakeKind.Phone),
        (@"^(nome_?)?cognome|last_?name|surname|family_?name", FakeKind.LastName),
        (@"^(nome|first_?name|given_?name|firstname)$|^nome_|_nome$", FakeKind.FirstName),
        (@"full_?name|nominativo|^name$|display_?name|intestatario|contatto", FakeKind.FullName),
        (@"username|user_?name|login|utente|nickname", FakeKind.Username),
        (@"password|passwd|pwd|hash", FakeKind.Password),
        (@"ragione_?sociale|azienda|company|ditta|societa|fornitore|vendor", FakeKind.Company),
        (@"indirizzo|address|street|^via$|strada", FakeKind.Street),
        (@"^cap$|zip|postal|postcode|codice_?postale", FakeKind.PostalCode),
        (@"provincia|province|^prov$|state", FakeKind.Province),
        (@"regione|region", FakeKind.Region),
        (@"citta|città|city|comune|localita|town", FakeKind.City),
        (@"nazione|country|paese|stato_?estero", FakeKind.Country),
        (@"url|sito|website|web|link", FakeKind.Url),
        (@"nascita|birth|dob|compleanno", FakeKind.BirthDate),
        (@"prezzo|price|importo|amount|totale|total|costo|cost|imponibile|saldo|balance|stipendio|salary", FakeKind.Price),
    ];

    public static FakeKind Guess(DesignColumn column, bool isForeignKey)
    {
        if (column.IsVirtual || column.AutoIncrement || column.Expression is not null) return FakeKind.Skip;
        if (isForeignKey) return FakeKind.ForeignKey;
        var type = column.Type.ToLowerInvariant();
        if (type.StartsWith("enum", StringComparison.Ordinal)) return FakeKind.Enum;
        if (column.KeyOrder > 0 && IsIntegerType(type)) return FakeKind.Sequence;
        if (type is "uuid" or "uniqueidentifier") return FakeKind.Uuid;
        if (type is "json" or "jsonb") return FakeKind.Json;
        if (type is "bool" or "boolean" or "bit" || type == "tinyint" && column.Length == 1) return FakeKind.Boolean;
        var name = column.Name.ToLowerInvariant();
        var textual = IsTextType(type);
        foreach (var (pattern, kind) in ByName)
        {
            if (!Regex.IsMatch(name, pattern)) continue;
            if (kind == FakeKind.BirthDate && !IsDateType(type)) continue;
            if (kind == FakeKind.Price && !IsNumericType(type)) continue;
            if (kind is not (FakeKind.BirthDate or FakeKind.Price) && !textual) continue;
            return kind;
        }
        if (IsDateType(type)) return type is "date" ? FakeKind.Date : type.StartsWith("time", StringComparison.Ordinal) && !type.StartsWith("timestamp", StringComparison.Ordinal) ? FakeKind.Time : FakeKind.DateTime;
        if (IsIntegerType(type)) return FakeKind.Integer;
        if (IsNumericType(type)) return FakeKind.Decimal;
        if (textual) return FakeKind.Text;
        if (!column.NotNull || column.Default is not null) return FakeKind.Skip;
        return FakeKind.Text;
    }

    public static bool IsIntegerType(string type) => type is "int" or "integer" or "bigint" or "smallint" or "tinyint" or "mediumint" or "int2" or "int4" or "int8" or "serial" or "bigserial" or "smallserial";
    public static bool IsNumericType(string type) => IsIntegerType(type) || type is "decimal" or "numeric" or "float" or "double" or "real" or "money" or "smallmoney" or "double precision" or "float4" or "float8";
    public static bool IsDateType(string type) => type.Contains("date") || type.StartsWith("time", StringComparison.Ordinal) || type == "year";
    public static bool IsTextType(string type) => type.Contains("char") || type.Contains("text") || type is "string" or "clob" or "citext" or "sysname" || type.StartsWith("enum", StringComparison.Ordinal) || type.StartsWith("set", StringComparison.Ordinal);
}

/// <summary>
/// Genera righe realistiche (italiane) per una tabella: nome, cognome, email e codice fiscale della stessa riga sono coerenti
/// tra loro, le chiavi esterne usano valori esistenti, i limiti di lunghezza e precisione sono rispettati.
/// </summary>
public sealed class DataGenerator(IReadOnlyList<GeneratorColumn> columns, int? seed = null)
{
    private readonly Random _random = seed is { } s ? new Random(s) : new Random();
    private long _row;
    private readonly Dictionary<GeneratorColumn, HashSet<object>> _uniqueValues = new();
    private readonly Dictionary<string, IReadOnlyList<object?>> _foreignRows = new();

    public IReadOnlyList<GeneratorColumn> InsertedColumns { get; } = columns.Where(c => c.Kind != FakeKind.Skip).ToArray();

    /// <summary>Qualche NULL casuale nelle colonne facoltative (disattivato per l'anonimizzazione).</summary>
    public bool RandomNulls { get; init; } = true;

    public IEnumerable<object?[]> Generate(int count)
    {
        for (var i = 0; i < count; i++) yield return Next();
    }

    public object?[] Next()
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            _foreignRows.Clear();
            var person = new Lazy<Person>(() => Person.Random(_random));
            var place = new Lazy<ItalianPlace>(() => Pick(ItalianData.Places));
            var values = InsertedColumns.Select(column => Value(column, _row, person, place)).ToArray();
            if (InsertedColumns.Select((c, i) => (c, i)).Any(p => p.c.IsUnique && values[p.i] is { } value && _uniqueValues.TryGetValue(p.c, out var used) && used.Contains(value))) continue;
            for (var i = 0; i < InsertedColumns.Count; i++)
                if (InsertedColumns[i].IsUnique && values[i] is { } value)
                {
                    if (!_uniqueValues.TryGetValue(InsertedColumns[i], out var used)) _uniqueValues[InsertedColumns[i]] = used = [];
                    used.Add(value);
                }
            _row++;
            return values;
        }
        throw new InvalidOperationException("Impossibile generare altri valori univoci con le opzioni scelte. Riduci le righe o cambia il generatore della colonna univoca.");
    }

    private object? Value(GeneratorColumn column, long row, Lazy<Person> person, Lazy<ItalianPlace> place)
    {
        var c = column.Column;
        // Colonne facoltative: qualche NULL qua e là, tranne per i dati che caratterizzano la riga.
        if (RandomNulls && !c.NotNull && c.KeyOrder == 0 && column.Kind is FakeKind.Text or FakeKind.Phone or FakeKind.Url or FakeKind.Date or FakeKind.DateTime
            && _random.NextDouble() < 0.08) return null;
        var value = Raw(column, row, person.Value, place);
        if (value is string text)
        {
            if (column.IsUnique && column.Kind is FakeKind.Text or FakeKind.Username or FakeKind.Email) text = Unique(text, row, column.Kind, c.Length);
            if (c.Length is { } max and > 0 && text.Length > max)
            {
                // Email troppo lunga: si accorcia la parte prima della @ (tenendo la fine, con l'eventuale suffisso univoco).
                var at = column.Kind == FakeKind.Email ? text.IndexOf('@') : -1;
                var room = max - (text.Length - at);
                if (at > 0 && room >= 1) text = text[(at - room)..at].TrimStart('.', '_') + text[at..];
                else if (column.Kind == FakeKind.Email && max >= 8) text = text[..Math.Min(max - 7, at > 0 ? at : text.Length)] + "@e.test";
                else if (column.Kind == FakeKind.Email) throw new InvalidOperationException($"La colonna «{c.Name}» è troppo corta per un indirizzo email di prova.");
                else text = text[..max];
            }
            return text;
        }
        return value;
    }

    private object? Raw(GeneratorColumn column, long row, Person person, Lazy<ItalianPlace> place)
    {
        var c = column.Column;
        var type = c.Type.ToLowerInvariant();
        switch (column.Kind)
        {
            case FakeKind.Null: return null;
            case FakeKind.Sequence: return Number(type, checked(column.SequenceStart + row));
            case FakeKind.ForeignKey:
                if (column.ForeignGroup is { } group && column.ForeignRows.Count > 0)
                {
                    if (!_foreignRows.TryGetValue(group, out var tuple)) _foreignRows[group] = tuple = Pick(column.ForeignRows);
                    return tuple[column.ForeignIndex];
                }
                return column.ForeignValues.Count == 0 ? null : column.ForeignValues[_random.Next(column.ForeignValues.Count)];
            case FakeKind.FirstName: return person.FirstName;
            case FakeKind.LastName: return person.LastName;
            case FakeKind.FullName: return $"{person.FirstName} {person.LastName}";
            case FakeKind.Email: return person.Email(_random);
            case FakeKind.Phone: return Phone(c.Length);
            case FakeKind.FiscalCode: return ItalianCodes.FiscalCode(person.LastName, person.FirstName, person.Birth, person.Female, person.Place.CadastralCode);
            case FakeKind.VatNumber: return ItalianCodes.VatNumber(_random);
            case FakeKind.Iban: return ItalianCodes.Iban(_random);
            case FakeKind.Username: return $"{Ascii(person.FirstName)}.{Ascii(person.LastName)}{_random.Next(1, 100)}".ToLowerInvariant();
            case FakeKind.Password: return "$2b$10$" + RandomString(53, "./ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789");
            case FakeKind.Street: return $"{Pick(ItalianData.Streets)}, {_random.Next(1, 200)}";
            case FakeKind.City: return place.Value.City;
            case FakeKind.Province: return place.Value.Province;
            case FakeKind.Region: return place.Value.Region;
            case FakeKind.PostalCode: return ClientNumberOrText(type, place.Value.PostalCode);
            case FakeKind.Country: return c.Length is > 0 and <= 3 ? "IT" : "Italia";
            case FakeKind.Company: return $"{Pick(ItalianData.Surnames)} {Pick(ItalianData.CompanyWords)} {Pick(ItalianData.CompanySuffixes)}";
            case FakeKind.Url: return $"https://www.{Ascii(Pick(ItalianData.Surnames)).ToLowerInvariant()}{_random.Next(1, 999)}.example.test";
            case FakeKind.Integer: return Integer(c, type);
            case FakeKind.Decimal: return DecimalValue(c, 0, 10_000);
            case FakeKind.Price: return IsInteger(type) ? Number(type, _random.Next(1, 1000)) : DecimalValue(c, 1, 2_000, 2);
            case FakeKind.Boolean: return type is "bit" or "tinyint" or "int" or "smallint" ? Number(type, _random.Next(2)) : _random.Next(2) == 1;
            case FakeKind.Date: return DateBetween(new DateTime(2018, 1, 1), DateTime.Today).Date;
            case FakeKind.DateTime: return DateBetween(new DateTime(2018, 1, 1), DateTime.Today.AddDays(1)) is var d ? new DateTime(d.Year, d.Month, d.Day, d.Hour, d.Minute, d.Second) : default;
            case FakeKind.BirthDate: return type is "date" ? person.Birth.Date : person.Birth;
            case FakeKind.Time: return new TimeSpan(_random.Next(7, 20), _random.Next(0, 4) * 15, 0).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            case FakeKind.Uuid: return GuidFromRandom().ToString();
            case FakeKind.Enum: return EnumValues(c.Type) is { Length: > 0 } values ? Pick(values) : null;
            case FakeKind.Json: return $"{{\"nota\": \"{Sentence(3)}\", \"valore\": {_random.Next(1, 100)}}}";
            default: return TextValue(c);
        }
    }

    private object Integer(DesignColumn column, string type)
    {
        var name = column.Name.ToLowerInvariant();
        long value = name switch
        {
            _ when Regex.IsMatch(name, "eta|età|^age") => _random.Next(18, 90),
            _ when Regex.IsMatch(name, "anno|year") => _random.Next(2000, DateTime.Today.Year + 1),
            _ when Regex.IsMatch(name, "quant|qty|pezzi|numero|count") => _random.Next(1, 100),
            _ when Regex.IsMatch(name, "ordine|order|sort|posizione|priority|priorit") => _random.Next(1, 20),
            _ => type switch
            {
                "tinyint" => _random.Next(0, column.Unsigned ? 255 : 127),
                "smallint" or "int2" => _random.Next(1, 30_000),
                _ => _random.Next(1, 100_000)
            }
        };
        if (type == "tinyint") value = Math.Min(value, column.Unsigned ? 255 : 127);
        return Number(type, value);
    }

    private decimal DecimalValue(DesignColumn column, decimal min, decimal max, int? fixedScale = null)
    {
        var scale = column.Decimals ?? fixedScale ?? 2;
        if (column.Length is { } precision and > 0)
        {
            var digits = Math.Max(0, precision - scale);
            var quantum = 1m / (decimal)Math.Pow(10, Math.Clamp(scale, 0, 28));
            if (digits < 8) max = Math.Min(max, Math.Max(0, (decimal)Math.Pow(10, digits) - quantum));
            if (min > max) min = 0;
        }
        var value = min + (decimal)_random.NextDouble() * (max - min);
        return Math.Round(value, Math.Clamp(scale, 0, 6));
    }

    private string TextValue(DesignColumn column)
    {
        var type = column.Type.ToLowerInvariant();
        var max = column.Length is > 0 ? column.Length.Value : type.Contains("text") || type == "clob" ? 200 : 40;
        if (type is "char" or "nchar" && column.Length is > 0 and <= 12) return RandomString(column.Length.Value, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
        if (max <= 3) return RandomString(max, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
        var words = Math.Clamp(max / 8, 1, type.Contains("text") ? 25 : 6);
        var sentence = Sentence(_random.Next(1, words + 1));
        return sentence.Length > max ? sentence[..max].TrimEnd() : sentence;
    }

    private string Sentence(int words)
    {
        var text = string.Join(' ', Enumerable.Range(0, words).Select(_ => Pick(ItalianData.Words)));
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private string Phone(int? length)
    {
        var prefix = Pick(new[] { "320", "324", "328", "329", "331", "333", "338", "339", "340", "345", "347", "348", "349", "351", "366", "380", "389", "392" });
        var number = $"{prefix}{_random.Next(1_000_000, 9_999_999)}";
        if (length is null or >= 16) return $"+39 {number[..3]} {number[3..6]} {number[6..]}";
        if (length >= 13) return "+39" + number;
        return number;
    }

    private static string Unique(string text, long row, FakeKind kind, int? maxLength)
    {
        var suffix = (row + 1).ToString(CultureInfo.InvariantCulture);
        if (kind == FakeKind.Email && text.IndexOf('@') is var at and > 0) return $"{text[..at]}.{suffix}{text[at..]}";
        if (maxLength is { } max && text.Length + suffix.Length + 1 > max)
        {
            if (suffix.Length > max) throw new InvalidOperationException("La colonna è troppo corta per altri valori univoci.");
            return text[..Math.Max(0, max - suffix.Length)] + suffix;
        }
        return $"{text}-{suffix}";
    }

    private DateTime DateBetween(DateTime from, DateTime to) => from.AddSeconds(_random.NextDouble() * (to - from).TotalSeconds);

    private T Pick<T>(IReadOnlyList<T> items) => items[_random.Next(items.Count)];

    private string RandomString(int length, string alphabet)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = alphabet[_random.Next(alphabet.Length)];
        return new string(chars);
    }

    private Guid GuidFromRandom()
    {
        var bytes = new byte[16];
        _random.NextBytes(bytes);
        bytes[7] = (byte)(bytes[7] & 0x0F | 0x40);
        bytes[8] = (byte)(bytes[8] & 0x3F | 0x80);
        return new Guid(bytes);
    }

    private static bool IsInteger(string type) => ColumnGuesser.IsIntegerType(type);

    /// <summary>Numero nel tipo .NET adatto alla colonna (long per gli interi, decimal per gli altri).</summary>
    private static object Number(string type, long value) => IsInteger(type) || type is "bit" ? (object)value : (decimal)value;

    private static object ClientNumberOrText(string type, string digits) => ColumnGuesser.IsNumericType(type) ? long.Parse(digits, CultureInfo.InvariantCulture) : digits;

    /// <summary>enum('a','b','c') → a, b, c.</summary>
    public static string[] EnumValues(string type)
    {
        var open = type.IndexOf('(');
        if (open < 0) return [];
        return Regex.Matches(type[open..], @"'((?:[^']|'')*)'").Select(m => m.Groups[1].Value.Replace("''", "'")).ToArray();
    }

    internal static string Ascii(string text) =>
        new(text.Normalize(NormalizationForm.FormD).Where(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z').ToArray());

    /// <summary>Persona inventata: i dati della stessa riga (nome, email, codice fiscale…) sono coerenti.</summary>
    internal sealed record Person(string FirstName, string LastName, bool Female, DateTime Birth, ItalianPlace Place)
    {
        public static Person Random(Random random)
        {
            var female = random.Next(2) == 0;
            var names = female ? ItalianData.FemaleNames : ItalianData.MaleNames;
            var birth = new DateTime(1950, 1, 1).AddDays(random.Next(0, 365 * 55));
            return new Person(names[random.Next(names.Length)], ItalianData.Surnames[random.Next(ItalianData.Surnames.Length)], female, birth,
                ItalianData.Places[random.Next(ItalianData.Places.Length)]);
        }

        public string Email(Random random) =>
            $"{Ascii(FirstName)}.{Ascii(LastName)}{(random.Next(3) == 0 ? random.Next(1, 99).ToString(CultureInfo.InvariantCulture) : string.Empty)}@{ItalianData.EmailDomains[random.Next(ItalianData.EmailDomains.Length)]}".ToLowerInvariant();
    }
}
