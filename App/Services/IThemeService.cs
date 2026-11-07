namespace MosaicDB.App.Services;

public interface IThemeService
{
    bool IsDark { get; }
    void Toggle();
    /// <summary>"Sistema", "Chiaro" o "Scuro".</summary>
    void Apply(string theme);
}
