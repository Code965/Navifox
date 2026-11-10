using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Navifox.Core;

namespace Navifox.App.Services;

/// <summary>
/// Lingua dell'interfaccia. I testi sono scritti in italiano; con l'inglese attivo un dizionario
/// (Assets/i18n/en.json, risorsa incorporata) li traduce mentre vengono mostrati: testi, pulsanti, menu,
/// suggerimenti, titoli delle finestre, segnaposto. I dati (celle della griglia, editor SQL, nomi di tabelle) non si toccano.
/// </summary>
public static class Localization
{
    public const string Italian = "it";
    public const string English = "en";

    private static Translator? _translator;
    [ThreadStatic] private static bool _applying;

    /// <summary>Lingua in uso: "it" o "en".</summary>
    public static string Language { get; private set; } = Italian;

    public static bool IsEnglish => Language == English;

    /// <summary>Lingua da usare: quella scelta nelle preferenze, altrimenti quella di Windows (italiano → italiano, il resto → inglese).</summary>
    public static string Resolve(string? setting) => setting switch
    {
        Italian or English => setting,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? Italian : English
    };

    /// <summary>Testo tradotto nella lingua attiva (o l'originale).</summary>
    public static string T(string text) => _translator?.Translate(text) ?? text;

    /// <summary>Da chiamare all'avvio, prima di creare le finestre.</summary>
    public static void Initialize(string? setting)
    {
        Language = Resolve(setting);
        if (Language == Italian) return;
        using var stream = typeof(Localization).Assembly.GetManifestResourceStream($"Navifox.i18n.{Language}.json");
        if (stream is null) { Language = Italian; return; }
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        _translator = new Translator(entries);
        Hook();
    }

    /// <summary>Diagnostica (NAVIFOX_I18N_MISSING=file): testi mostrati senza traduzione, scritti nel file all'uscita.</summary>
    public static HashSet<string>? Missing { get; private set; }

    public static void SaveMissing()
    {
        if (Missing is null || Environment.GetEnvironmentVariable("NAVIFOX_I18N_MISSING") is not { } path) return;
        lock (Missing) File.WriteAllText(path, JsonSerializer.Serialize(Missing.OrderBy(t => t).ToArray(), new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    private static void Hook()
    {
        if (Environment.GetEnvironmentVariable("NAVIFOX_I18N_MISSING") is not null) Missing = [];
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((block, e) =>
        {
            if (!block.Classes.Contains("no-tr")) Apply(block, TextBlock.TextProperty, e.NewValue);
        });
        ContentControl.ContentProperty.Changed.AddClassHandler<ContentControl>((control, e) => { if (!control.Classes.Contains("no-tr")) Apply(control, ContentControl.ContentProperty, e.NewValue); });
        HeaderedContentControl.HeaderProperty.Changed.AddClassHandler<HeaderedContentControl>((control, e) => Apply(control, HeaderedContentControl.HeaderProperty, e.NewValue));
        HeaderedSelectingItemsControl.HeaderProperty.Changed.AddClassHandler<HeaderedSelectingItemsControl>((control, e) => Apply(control, HeaderedSelectingItemsControl.HeaderProperty, e.NewValue));
        HeaderedItemsControl.HeaderProperty.Changed.AddClassHandler<HeaderedItemsControl>((control, e) => Apply(control, HeaderedItemsControl.HeaderProperty, e.NewValue));
        ToolTip.TipProperty.Changed.AddClassHandler<Control>((control, e) => Apply(control, ToolTip.TipProperty, e.NewValue));
        TextBox.WatermarkProperty.Changed.AddClassHandler<TextBox>((box, e) => Apply(box, TextBox.WatermarkProperty, e.NewValue));
        ComboBox.PlaceholderTextProperty.Changed.AddClassHandler<ComboBox>((box, e) => Apply(box, ComboBox.PlaceholderTextProperty, e.NewValue));
        Window.TitleProperty.Changed.AddClassHandler<Window>((window, e) => Apply(window, Window.TitleProperty, e.NewValue));
        DataGridColumn.HeaderProperty.Changed.AddClassHandler<DataGridColumn>((column, e) => Apply(column, DataGridColumn.HeaderProperty, e.NewValue));
    }

    /// <summary>Sostituisce il testo con la traduzione mantenendo eventuali binding (SetCurrentValue).</summary>
    private static void Apply(AvaloniaObject target, AvaloniaProperty property, object? value)
    {
        if (_applying || value is not string text) return;
        if (_translator?.Translate(text) is not { } translated || translated == text)
        {
            if (Missing is not null && text.Length > 1 && System.Text.RegularExpressions.Regex.IsMatch(text, "[A-Za-zÀ-ú]{2}")) lock (Missing) Missing.Add(text);
            return;
        }
        _applying = true;
        try { target.SetCurrentValue(property, translated); }
        finally { _applying = false; }
    }
}
