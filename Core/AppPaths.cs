namespace Navifox.Core;

/// <summary>
/// Cartella dei dati di Navifox (%APPDATA%\Navifox): connessioni, impostazioni, cronologia, automazioni.
/// Al primo avvio dopo la rinomina copia i file dalla vecchia cartella %APPDATA%\MosaicDB, che resta come copia di sicurezza.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> Directory = new(Prepare, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string DataDirectory => Directory.Value;

    public static string File(string name) => Path.Combine(DataDirectory, name);

    private static string Prepare()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var current = Path.Combine(root, "Navifox");
        var legacy = Path.Combine(root, "MosaicDB");
        try
        {
            if (!System.IO.Directory.Exists(current) && System.IO.Directory.Exists(legacy))
            {
                System.IO.Directory.CreateDirectory(current);
                foreach (var file in System.IO.Directory.GetFiles(legacy))
                    System.IO.File.Copy(file, Path.Combine(current, Path.GetFileName(file)), overwrite: false);
            }
            System.IO.Directory.CreateDirectory(current);
        }
        catch (IOException)
        {
            // Se la copia non riesce si continua con la vecchia cartella, così nessun dato va perso.
            if (System.IO.Directory.Exists(legacy) && !System.IO.Directory.Exists(current)) return legacy;
        }
        catch (UnauthorizedAccessException)
        {
            if (System.IO.Directory.Exists(legacy) && !System.IO.Directory.Exists(current)) return legacy;
        }
        return current;
    }
}
