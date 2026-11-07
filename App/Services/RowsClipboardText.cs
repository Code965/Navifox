using Navifox.Core;

namespace Navifox.App.Services;

public static class RowsClipboardText
{
    /// <summary>TSV da Excel o da un altro client. Senza intestazioni l'abbinamento per posizione va confermato.</summary>
    public static GridRowsClipboard Parse(string text, IReadOnlyList<string> targetColumns, int startColumn = 0)
    {
        if (string.IsNullOrEmpty(text)) throw new FormatException("Gli appunti non contengono righe.");
        using var reader = new StringReader(text);
        var records = DataImporter.ParseDelimited(reader, '\t', '"').ToArray();
        if (records.Length == 0) throw new FormatException("Gli appunti non contengono righe.");
        var first = records[0];
        var header = first.All(n => !string.IsNullOrWhiteSpace(n)) && first.Distinct(StringComparer.OrdinalIgnoreCase).Count() == first.Length &&
            first.All(n => targetColumns.Contains(n, StringComparer.OrdinalIgnoreCase));
        var data = header ? records.Skip(1).ToArray() : records;
        if (data.Length == 0) throw new FormatException("Gli appunti contengono solo le intestazioni.");
        if (data.Any(row => row.Length != first.Length)) throw new FormatException("Le righe degli appunti hanno numeri di colonne diversi. Usa testo separato da tabulazioni con eventuali campi racchiusi tra virgolette.");
        var columns = header ? first : targetColumns.Skip(Math.Max(0, startColumn)).Take(first.Length).ToArray();
        if (columns.Length != first.Length) throw new FormatException("I dati contengono più colonne di quelle disponibili dalla cella selezionata. Seleziona la prima colonna o includi le intestazioni.");
        return new GridRowsClipboard(columns, data.Select(row => row.Select(value => value == "\\N" ? ClipboardCell.From(null) : ClipboardCell.From(value)).ToArray()).ToArray(),
            header ? "Testo con intestazioni" : "Testo senza intestazioni", ByPosition: !header);
    }
}
