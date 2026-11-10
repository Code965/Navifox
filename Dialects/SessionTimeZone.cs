namespace Navifox.Dialects;

/// <summary>
/// Fuso orario delle sessioni: con l'ora del PC (predefinito) NOW()/CURRENT_TIMESTAMP e le colonne con fuso
/// (TIMESTAMP di MySQL, timestamptz di PostgreSQL) appaiono come l'orologio di Windows anche se il server è in UTC
/// (Supabase, cloud). Con l'ora del server si usano le impostazioni del server, come prima.
/// </summary>
public static class SessionTimeZone
{
    public static bool UseLocal { get; set; } = true;

    /// <summary>Scarto del PC adesso, nel formato di MySQL («+01:00»).</summary>
    public static string MySqlOffset()
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
        return $"{(offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";
    }

    /// <summary>Nome IANA del fuso del PC (es. «Europe/Rome») per PostgreSQL; null se non convertibile.</summary>
    public static string? IanaName()
    {
        var id = TimeZoneInfo.Local.Id;
        if (id.Contains('/')) return id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : null;
    }
}
