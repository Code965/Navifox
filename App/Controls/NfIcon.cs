using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Navifox.App.Controls;

/// <summary>
/// Icona lineare Navifox: tracciato 24×24 disegnato a contorno (stile «line icons»),
/// scalato alla dimensione del controllo e colorato con il Foreground ereditato.
/// </summary>
public sealed class NfIcon : Control
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<NfIcon, string?>(nameof(Kind));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<NfIcon>();
    public static readonly StyledProperty<double> StrokeWidthProperty = AvaloniaProperty.Register<NfIcon, double>(nameof(StrokeWidth), 2);
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<NfIcon, IBrush?>(nameof(Fill));

    static NfIcon()
    {
        AffectsRender<NfIcon>(KindProperty, ForegroundProperty, StrokeWidthProperty, FillProperty);
        WidthProperty.OverrideDefaultValue<NfIcon>(16);
        HeightProperty.OverrideDefaultValue<NfIcon>(16);
        VerticalAlignmentProperty.OverrideDefaultValue<NfIcon>(Avalonia.Layout.VerticalAlignment.Center);
    }

    public string? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeWidth { get => GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public override void Render(DrawingContext context)
    {
        var geometry = NfIcons.Get(Kind);
        if (geometry is null || Foreground is null) return;
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var scale = size / 24d;
        var offset = new Point((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            var pen = new Pen(Foreground, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(Fill, pen, geometry);
        }
    }
}

/// <summary>Raccolta di icone lineari 24×24 (geometrie in sintassi SVG path).</summary>
public static class NfIcons
{
    private static readonly Dictionary<string, Geometry?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> Paths = new(StringComparer.OrdinalIgnoreCase)
    {
        // struttura / albero
        ["folder"] = "M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z",
        ["folder-open"] = "M6 14l1.5-2.9A2 2 0 0 1 9.24 10H20a2 2 0 0 1 1.94 2.5l-1.54 6a2 2 0 0 1-1.95 1.5H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3.9a2 2 0 0 1 1.69 .9l.81 1.2a2 2 0 0 0 1.67 .9H18a2 2 0 0 1 2 2v2",
        ["server"] = "M4 2h16a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2Z M4 14h16a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2v-4a2 2 0 0 1 2-2Z M6 6h.01 M6 18h.01",
        ["database"] = "M3 5a9 3 0 1 0 18 0a9 3 0 1 0-18 0 M3 5v14a9 3 0 0 0 18 0V5 M3 12a9 3 0 0 0 18 0",
        ["layers"] = "M12.83 2.18a2 2 0 0 0-1.66 0L2.6 6.08a1 1 0 0 0 0 1.83l8.58 3.91a2 2 0 0 0 1.66 0l8.58-3.9a1 1 0 0 0 0-1.83Z M2 12a1 1 0 0 0 .58 .91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 12 M2 17a1 1 0 0 0 .58 .91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 17",
        ["table"] = "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Z M3 9h18 M9 3v18",
        ["table-grid"] = "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Z M3 9h18 M3 15h18 M12 3v18",
        ["eye"] = "M2.06 12.35a1 1 0 0 1 0-.7 10.75 10.75 0 0 1 19.88 0 1 1 0 0 1 0 .7 10.75 10.75 0 0 1-19.88 0 M9 12a3 3 0 1 0 6 0a3 3 0 1 0-6 0",
        ["function"] = "M17 4h-2.5a3 3 0 0 0-3 3v10a3 3 0 0 1-3 3H6 M8 11h8",
        ["clock"] = "M2 12a10 10 0 1 0 20 0a10 10 0 1 0-20 0 M12 6v6l4 2",
        ["zap"] = "M4 14a1 1 0 0 1-.78-1.63l9.9-10.2a0.5 0.5 0 0 1 .86 .46l-1.92 6.02A1 1 0 0 0 13 10h7a1 1 0 0 1 .78 1.63l-9.9 10.2a0.5 0.5 0 0 1-.86-.46l1.92-6.02A1 1 0 0 0 11 14Z",
        ["hash"] = "M4 9h16 M4 15h16 M10 3 8 21 M16 3l-2 18",
        ["users"] = "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 M5 7a4 4 0 1 0 8 0a4 4 0 1 0-8 0 M22 21v-2a4 4 0 0 0-3-3.87 M16 3.13a4 4 0 0 1 0 7.75",
        ["bookmark"] = "M19 21l-7-4-7 4V5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2Z",
        ["archive"] = "M3 3h18a1 1 0 0 1 1 1v3a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1Z M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8 M10 12h4",
        ["workflow"] = "M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8 M21 3v5h-5 M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16 M8 16H3v5",
        ["network"] = "M16 16h6v6h-6Z M2 16h6v6H2Z M9 2h6v6H9Z M5 16v-3a1 1 0 0 1 1-1h12a1 1 0 0 1 1 1v3 M12 12V8",
        ["ellipsis"] = "M11 12a1 1 0 1 0 2 0a1 1 0 1 0-2 0 M18 12a1 1 0 1 0 2 0a1 1 0 1 0-2 0 M4 12a1 1 0 1 0 2 0a1 1 0 1 0-2 0",
        ["file-code"] = "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z M14 2v4a2 2 0 0 0 2 2h4 M10 12.5 8 15l2 2.5 M14 12.5l2 2.5-2 2.5",
        ["file-text"] = "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z M14 2v4a2 2 0 0 0 2 2h4 M10 9H8 M16 13H8 M16 17H8",
        ["terminal"] = "M4 17l6-6-6-6 M12 19h8",
        ["list"] = "M3 12h.01 M3 18h.01 M3 6h.01 M8 12h13 M8 18h13 M8 6h13",
        // azioni
        ["plus"] = "M5 12h14 M12 5v14",
        ["column"] = "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Z M12 3v18",
        ["keyword"] = "M4 7V4h16v3 M9 20h6 M12 4v16",
        ["link"] = "M10 13a5 5 0 0 0 7.54 .54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71 M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71",
        ["zoom-in"] = "M3 11a8 8 0 1 0 16 0a8 8 0 1 0-16 0 M21 21l-4.3-4.3 M11 8v6 M8 11h6",
        ["zoom-out"] = "M3 11a8 8 0 1 0 16 0a8 8 0 1 0-16 0 M21 21l-4.3-4.3 M8 11h6",
        ["maximize"] = "M8 3H5a2 2 0 0 0-2 2v3 M21 8V5a2 2 0 0 0-2-2h-3 M3 16v3a2 2 0 0 0 2 2h3 M16 21h3a2 2 0 0 0 2-2v-3",
        ["image"] = "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Z M7 9a2 2 0 1 0 4 0a2 2 0 1 0-4 0 M21 15l-3.09-3.09a2 2 0 0 0-2.82 0L6 21",
        ["activity"] = "M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a0.25 0.25 0 0 1-.48 0L9.24 2.18a0.25 0.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2",
        ["history"] = "M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8 M3 3v5h5 M12 7v5l4 2",
        ["transfer"] = "M8 3 4 7l4 4 M4 7h16 M16 21l4-4-4-4 M20 17H4",
        ["sync"] = "M21 12a9 9 0 0 0-9-9 9.75 9.75 0 0 0-6.74 2.74L3 8 M3 3v5h5 M3 12a9 9 0 0 0 9 9 9.75 9.75 0 0 0 6.74-2.74L21 16 M16 16h5v5",
        ["sliders"] = "M21 4h-7 M10 4H3 M21 12h-9 M8 12H3 M21 20h-5 M12 20H3 M14 2v4 M8 10v4 M16 18v4",
        ["calendar-clock"] = "M21 7.5V6a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h3.5 M16 2v4 M8 2v4 M3 10h5 M17.5 17.5 16 16.3V14 M10 16a6 6 0 1 0 12 0a6 6 0 1 0-12 0",
        ["skull"] = "M9 12a1 1 0 1 0 2 0a1 1 0 1 0-2 0 M13 12a1 1 0 1 0 2 0a1 1 0 1 0-2 0 M8 20v2h8v-2 M12.5 17l-.5-1-.5 1h1z M16 20a2 2 0 0 0 1.56-3.25 8 8 0 1 0-11.12 0A2 2 0 0 0 8 20",
        ["settings"] = "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43 .25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73 .73l-.22 .38a2 2 0 0 0 .73 2.73l.15 .1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15 .09a2 2 0 0 0-.73 2.73l.22 .38a2 2 0 0 0 2.73 .73l.15-.08a2 2 0 0 1 2 0l.43 .25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15 .08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15 .08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2Z M9 12a3 3 0 1 0 6 0a3 3 0 1 0-6 0",
        ["info"] = "M2 12a10 10 0 1 0 20 0a10 10 0 1 0-20 0 M12 16v-4 M12 8h.01",
        ["code"] = "M16 18l6-6-6-6 M8 6l-6 6 6 6",
        ["panel-left"] = "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Z M9 3v18",
        ["panel-right"] = "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Z M15 3v18",
        ["copy"] = "M10 8h10a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H10a2 2 0 0 1-2-2V10a2 2 0 0 1 2-2Z M4 16c-1.1 0-2-.9-2-2V4c0-1.1 .9-2 2-2h10c1.1 0 2 .9 2 2",
        ["minus"] = "M5 12h14",
        ["check"] = "M20 6 9 17l-5-5",
        ["x"] = "M18 6 6 18 M6 6l12 12",
        ["refresh"] = "M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8 M21 3v5h-5 M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16 M8 16H3v5",
        ["stop"] = "M6 6h12v12H6Z",
        ["play"] = "M6 3l14 9-14 9Z",
        ["search"] = "M3 11a8 8 0 1 0 16 0a8 8 0 1 0-16 0 M21 21l-4.3-4.3",
        ["filter"] = "M22 3H2l8 9.46V19l4 2v-8.54Z",
        ["sort"] = "M3 16l4 4 4-4 M7 20V4 M11 4h10 M11 8h7 M11 12h4",
        ["import"] = "M12 3v12 M7 10l5 5 5-5 M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4",
        ["export"] = "M12 15V3 M7 8l5-5 5 5 M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4",
        ["transaction"] = "M3 5a9 3 0 1 0 18 0a9 3 0 1 0-18 0 M3 5v14a9 3 0 0 0 9 3 M21 5v6 M3 12a9 3 0 0 0 9 3 M17 14l-2 4h4l-2 4",
        ["undo"] = "M3 7v6h6 M21 17a9 9 0 0 0-9-9 9 9 0 0 0-6 2.3L3 13",
        ["arrow-up"] = "M5 12l7-7 7 7 M12 19V5",
        ["arrow-down"] = "M12 5v14 M19 12l-7 7-7-7",
        ["chevron-down"] = "M6 9l6 6 6-6",
        ["chevron-left"] = "M15 18l-6-6 6-6",
        ["chevron-right"] = "M9 18l6-6-6-6",
        ["chevrons-left"] = "M11 17l-5-5 5-5 M18 17l-5-5 5-5",
        ["chevrons-right"] = "M6 17l5-5-5-5 M13 17l5-5-5-5",
        ["power"] = "M12 2v10 M18.4 6.6a9 9 0 1 1-12.77 .04",
        ["unplug"] = "M19 5l3-3 M2 22l3-3 M6.3 20.3a2.4 2.4 0 0 0 3.4 0L12 18l-6-6-2.3 2.3a2.4 2.4 0 0 0 0 3.4Z M7.5 13.5 10 11 M10.5 16.5 13 14 M12 6l6 6 2.3-2.3a2.4 2.4 0 0 0 0-3.4l-2.6-2.6a2.4 2.4 0 0 0-3.4 0Z",
        ["pencil"] = "M21.17 6.81a1 1 0 0 0-3.99-3.99L3.84 16.17a2 2 0 0 0-.5 .83l-1.32 4.35a0.5 0.5 0 0 0 .62 .62l4.35-1.32a2 2 0 0 0 .83-.5Z M15 5l4 4",
        ["trash"] = "M3 6h18 M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6 M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2",
        ["save"] = "M15.2 3a2 2 0 0 1 1.4.6l3.8 3.8a2 2 0 0 1 .6 1.4V19a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M17 21v-7a1 1 0 0 0-1-1H8a1 1 0 0 0-1 1v7 M7 3v4a1 1 0 0 0 1 1h7",
        ["book"] = "M4 19.5v-15A2.5 2.5 0 0 1 6.5 2H19a1 1 0 0 1 1 1v18a1 1 0 0 1-1 1H6.5a1 1 0 0 1 0-5H20",
        ["keyboard"] = "M4 6h16a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2z M7 10h.01 M11 10h.01 M15 10h.01 M7 14h10",
        ["bug"] = "M8 2l1.88 1.88 M14.12 3.88L16 2 M9 7.13v-1a3 3 0 1 1 6 0v1 M12 20c-3.3 0-6-2.7-6-6v-3a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v3c0 3.3-2.7 6-6 6 M12 20v-9 M6.53 9C4.6 8.8 3 7.1 3 5 M6 13H2 M3 21c0-2.1 1.7-3.9 3.8-4 M20.97 5c0 2.1-1.6 3.8-3.5 4 M22 13h-4 M17.2 17c2.1.1 3.8 1.9 3.8 4",
        ["key"] = "M2.59 17.41A2 2 0 0 0 2 18.83V21a1 1 0 0 0 1 1h3a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h1a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h.17a2 2 0 0 0 1.41-.59l.82-.81a6.5 6.5 0 1 0-4-4Z M16 7.5a0.5 0.5 0 1 0 1 0a0.5 0.5 0 1 0-1 0",
        ["printer"] = "M6 18H4a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2 M6 9V3a1 1 0 0 1 1-1h10a1 1 0 0 1 1 1v6 M7 14h10v8H7Z",
        ["download"] = "M12 15V3 M7 10l5 5 5-5 M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4",
        ["upload"] = "M12 3v12 M17 8l-5-5-5 5 M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4",
        ["sun-moon"] = "M12 8a2.83 2.83 0 0 0 4 4 4 4 0 1 1-4-4 M12 2v2 M12 20v2 M4.9 4.9l1.4 1.4 M17.7 17.7l1.4 1.4 M2 12h2 M20 12h2 M6.3 17.7l-1.4 1.4 M19.1 4.9l-1.4 1.4",
        ["group"] = "M8 3H7a2 2 0 0 0-2 2v5a2 2 0 0 1-2 2 2 2 0 0 1 2 2v5a2 2 0 0 0 2 2h1 M16 21h1a2 2 0 0 0 2-2v-5a2 2 0 0 1 2-2 2 2 0 0 1-2-2V5a2 2 0 0 0-2-2h-1 M12 9v6 M9 12h6",
    };

    public static Geometry? Get(string? kind)
    {
        if (string.IsNullOrEmpty(kind)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(kind, out var cached)) return cached;
            Geometry? geometry = null;
            if (Paths.TryGetValue(kind, out var data))
            {
                try { geometry = Geometry.Parse(data); }
                catch (Exception) { geometry = null; }
            }
            Cache[kind] = geometry;
            return geometry;
        }
    }

    public static bool Exists(string? kind) => kind is not null && Paths.ContainsKey(kind);
}
