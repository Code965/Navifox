using System.Globalization;
using System.Numerics;
using System.Text;

namespace Navifox.Core.DataGen;

/// <summary>Codice fiscale, partita IVA e IBAN italiani con cifre di controllo corrette (per dati di prova).</summary>
public static class ItalianCodes
{
    private const string Months = "ABCDEHLMPRST";
    // Valori delle posizioni dispari (codice fiscale e CIN dell'IBAN): cifre 0-9 e lettere A-Z.
    private static readonly int[] OddValues = [1, 0, 5, 7, 9, 13, 15, 17, 19, 21, 2, 4, 18, 20, 11, 3, 6, 8, 12, 14, 16, 10, 22, 25, 24, 23];

    public static string FiscalCode(string surname, string name, DateTime birth, bool female, string cadastralCode)
    {
        var code = new StringBuilder(16);
        code.Append(SurnamePart(surname)).Append(NamePart(name));
        code.Append((birth.Year % 100).ToString("00", CultureInfo.InvariantCulture));
        code.Append(Months[birth.Month - 1]);
        code.Append((birth.Day + (female ? 40 : 0)).ToString("00", CultureInfo.InvariantCulture));
        code.Append(cadastralCode.ToUpperInvariant());
        code.Append(ControlLetter(code.ToString()));
        return code.ToString();
    }

    /// <summary>Carattere di controllo del codice fiscale (o CIN dell'IBAN) sui caratteri dati.</summary>
    public static char ControlLetter(string text)
    {
        var sum = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = char.ToUpperInvariant(text[i]);
            var index = char.IsDigit(c) ? c - '0' : c - 'A';
            sum += i % 2 == 0 ? OddValues[index] : index;
        }
        return (char)('A' + sum % 26);
    }

    public static bool IsValidFiscalCode(string code) =>
        code.Length == 16 && ControlLetter(code[..15]) == char.ToUpperInvariant(code[15]);

    /// <summary>Partita IVA di 11 cifre: matricola, ufficio provinciale e cifra di controllo (algoritmo di Luhn).</summary>
    public static string VatNumber(Random random)
    {
        var digits = new StringBuilder(11);
        for (var i = 0; i < 7; i++) digits.Append((char)('0' + random.Next(10)));
        digits.Append(random.Next(1, 101).ToString("000", CultureInfo.InvariantCulture));
        digits.Append(VatCheckDigit(digits.ToString()));
        return digits.ToString();
    }

    public static char VatCheckDigit(string first10)
    {
        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            var d = first10[i] - '0';
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return (char)('0' + (10 - sum % 10) % 10);
    }

    public static bool IsValidVatNumber(string vat) =>
        vat.Length == 11 && vat.All(char.IsDigit) && VatCheckDigit(vat[..10]) == vat[10];

    /// <summary>IBAN italiano di prova (27 caratteri) con CIN e cifre di controllo corrette.</summary>
    public static string Iban(Random random)
    {
        var bank = new StringBuilder(22);
        for (var i = 0; i < 10; i++) bank.Append((char)('0' + random.Next(10))); // ABI + CAB
        for (var i = 0; i < 12; i++) bank.Append((char)('0' + random.Next(10))); // conto
        var bban = ControlLetter(bank.ToString()) + bank.ToString();
        return "IT" + IbanCheckDigits("IT", bban) + bban;
    }

    public static string IbanCheckDigits(string country, string bban)
    {
        var rearranged = bban + country + "00";
        var numeric = new StringBuilder();
        foreach (var c in rearranged) numeric.Append(char.IsLetter(c) ? (char.ToUpperInvariant(c) - 'A' + 10).ToString(CultureInfo.InvariantCulture) : c.ToString());
        var mod = (int)(BigInteger.Parse(numeric.ToString(), CultureInfo.InvariantCulture) % 97);
        return (98 - mod).ToString("00", CultureInfo.InvariantCulture);
    }

    public static bool IsValidIban(string iban)
    {
        if (iban.Length < 15) return false;
        var rearranged = iban[4..] + iban[..4];
        var numeric = new StringBuilder();
        foreach (var c in rearranged) numeric.Append(char.IsLetter(c) ? (char.ToUpperInvariant(c) - 'A' + 10).ToString(CultureInfo.InvariantCulture) : c.ToString());
        return BigInteger.Parse(numeric.ToString(), CultureInfo.InvariantCulture) % 97 == 1;
    }

    private static string SurnamePart(string surname)
    {
        var letters = Letters(surname);
        var part = new string(letters.Where(IsConsonant).Concat(letters.Where(c => !IsConsonant(c))).Take(3).ToArray());
        return part.PadRight(3, 'X');
    }

    private static string NamePart(string name)
    {
        var letters = Letters(name);
        var consonants = letters.Where(IsConsonant).ToArray();
        if (consonants.Length >= 4) return $"{consonants[0]}{consonants[2]}{consonants[3]}";
        return new string(consonants.Concat(letters.Where(c => !IsConsonant(c))).Take(3).ToArray()).PadRight(3, 'X');
    }

    private static char[] Letters(string text) =>
        text.Normalize(NormalizationForm.FormD).Where(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z').Select(char.ToUpperInvariant).ToArray();

    private static bool IsConsonant(char c) => "AEIOU".IndexOf(c) < 0;
}
