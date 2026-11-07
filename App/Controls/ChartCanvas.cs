using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Navifox.App.Controls;

/// <summary>Punto di un grafico: etichetta e valore.</summary>
public sealed record ChartPoint(string Label, double Value);

/// <summary>
/// Grafico semplice disegnato a mano (barre, linee, area, torta) per i risultati delle query e le serie del Server Monitor.
/// Colori della palette Navifox; segue il tema chiaro/scuro.
/// </summary>
public sealed class ChartCanvas : Control
{
    public static readonly StyledProperty<IReadOnlyList<ChartPoint>?> PointsProperty = AvaloniaProperty.Register<ChartCanvas, IReadOnlyList<ChartPoint>?>(nameof(Points));
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<ChartCanvas, string>(nameof(Kind), "Barre");
    public static readonly StyledProperty<bool> CompactProperty = AvaloniaProperty.Register<ChartCanvas, bool>(nameof(Compact));
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<ChartCanvas, IBrush?>(nameof(Stroke));

    private static readonly Color[] Palette =
    [
        Color.Parse("#E8590C"), Color.Parse("#0090FF"), Color.Parse("#30A46C"), Color.Parse("#F5B400"), Color.Parse("#6E56CF"),
        Color.Parse("#E5484D"), Color.Parse("#12A594"), Color.Parse("#8B8D98"), Color.Parse("#D6409F"), Color.Parse("#8E4EC6"),
    ];

    static ChartCanvas() => AffectsRender<ChartCanvas>(PointsProperty, KindProperty, CompactProperty, StrokeProperty);

    public IReadOnlyList<ChartPoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    /// <summary>Barre, Linee, Area, Torta.</summary>
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    /// <summary>Senza assi né etichette (mini-grafico del Monitor).</summary>
    public bool Compact { get => GetValue(CompactProperty); set => SetValue(CompactProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    private bool Dark => ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
    private IBrush TextBrush => new SolidColorBrush(Dark ? Color.Parse("#C9CDD2") : Color.Parse("#4A4540"));
    private IBrush GridBrush => new SolidColorBrush(Dark ? Color.Parse("#33FFFFFF") : Color.Parse("#22000000"));

    public override void Render(DrawingContext context)
    {
        var points = Points;
        var bounds = new Rect(Bounds.Size);
        if (points is null || points.Count == 0 || bounds.Width < 20 || bounds.Height < 20)
        {
            if (!Compact) DrawText(context, "Nessun dato da mostrare: scegli una colonna di etichette e una numerica.", new Point(12, 12), 12);
            return;
        }
        if (Kind == "Torta") DrawPie(context, points, bounds);
        else DrawCartesian(context, points, bounds);
    }

    private void DrawText(DrawingContext context, string text, Point at, double size, IBrush? brush = null, TextAlignment align = TextAlignment.Left, double maxWidth = double.PositiveInfinity)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush ?? TextBrush)
        {
            MaxTextWidth = double.IsInfinity(maxWidth) ? 0 : maxWidth, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = double.IsInfinity(maxWidth) ? TextAlignment.Left : align,
        };
        var x = align switch { TextAlignment.Center => at.X - (double.IsInfinity(maxWidth) ? formatted.Width : maxWidth) / 2, TextAlignment.Right => at.X - formatted.Width, _ => at.X };
        context.DrawText(formatted, new Point(x, at.Y));
    }

    private static string Short(double value) => Math.Abs(value) switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000:0.#} mld",
        >= 1_000_000 => $"{value / 1_000_000:0.#} mln",
        >= 10_000 => $"{value / 1000:0.#}k",
        _ => value % 1 == 0 ? value.ToString("N0") : value.ToString("0.##"),
    };

    private void DrawCartesian(DrawingContext context, IReadOnlyList<ChartPoint> points, Rect bounds)
    {
        var left = Compact ? 1 : 58.0;
        var bottom = Compact ? 1 : 42.0;
        var top = Compact ? 2 : 14.0;
        var right = Compact ? 1 : 12.0;
        var plot = new Rect(left, top, Math.Max(10, bounds.Width - left - right), Math.Max(10, bounds.Height - top - bottom));
        var max = points.Max(p => p.Value);
        var min = Math.Min(0, points.Min(p => p.Value));
        if (max <= min) max = min + 1;
        double Y(double v) => plot.Bottom - (v - min) / (max - min) * plot.Height;
        var accent = Stroke ?? new SolidColorBrush(Palette[0]);

        if (!Compact)
        {
            var pen = new Pen(GridBrush, 1);
            for (var i = 0; i <= 4; i++)
            {
                var value = min + (max - min) * i / 4;
                var y = Y(value);
                context.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
                DrawText(context, Short(value), new Point(plot.Left - 6, y - 7), 10.5, align: TextAlignment.Right);
            }
        }

        var step = plot.Width / points.Count;
        if (Kind == "Barre")
        {
            var width = Math.Max(1, step * 0.7);
            for (var i = 0; i < points.Count; i++)
            {
                var x = plot.Left + step * i + (step - width) / 2;
                var y = Y(Math.Max(points[i].Value, 0));
                var zero = Y(Math.Max(min, 0));
                var rect = new Rect(x, Math.Min(y, zero), width, Math.Max(1, Math.Abs(zero - y)));
                context.DrawRectangle(accent, null, rect, Compact ? 0 : 2, Compact ? 0 : 2);
            }
        }
        else
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                for (var i = 0; i < points.Count; i++)
                {
                    var p = new Point(plot.Left + step * i + step / 2, Y(points[i].Value));
                    if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
                }
                g.EndFigure(false);
            }
            if (Kind == "Area" || Compact)
            {
                var area = new StreamGeometry();
                using (var g = area.Open())
                {
                    g.BeginFigure(new Point(plot.Left + step / 2, plot.Bottom), true);
                    for (var i = 0; i < points.Count; i++) g.LineTo(new Point(plot.Left + step * i + step / 2, Y(points[i].Value)));
                    g.LineTo(new Point(plot.Left + step * (points.Count - 1) + step / 2, plot.Bottom));
                    g.EndFigure(true);
                }
                var color = (accent as ISolidColorBrush)?.Color ?? Palette[0];
                context.DrawGeometry(new SolidColorBrush(color, 0.18), null, area);
            }
            context.DrawGeometry(null, new Pen(accent, Compact ? 1.5 : 2), geometry);
            if (!Compact && points.Count <= 60)
                for (var i = 0; i < points.Count; i++) context.DrawEllipse(accent, null, new Point(plot.Left + step * i + step / 2, Y(points[i].Value)), 3, 3);
        }

        if (Compact) return;
        // Etichette dell'asse X (al massimo ~12, per non sovrapporle).
        var every = Math.Max(1, (int)Math.Ceiling(points.Count / Math.Max(1, plot.Width / 70)));
        for (var i = 0; i < points.Count; i += every)
            DrawText(context, points[i].Label, new Point(plot.Left + step * i + step / 2, plot.Bottom + 6), 10.5, align: TextAlignment.Center, maxWidth: Math.Max(30, step * every - 4));
        if (points.Count <= 24 && Kind == "Barre")
            for (var i = 0; i < points.Count; i++)
                DrawText(context, Short(points[i].Value), new Point(plot.Left + step * i + step / 2, Y(Math.Max(points[i].Value, 0)) - 15), 10, align: TextAlignment.Center, maxWidth: Math.Max(30, step));
    }

    private void DrawPie(DrawingContext context, IReadOnlyList<ChartPoint> points, Rect bounds)
    {
        var slices = points.Where(p => p.Value > 0).OrderByDescending(p => p.Value).ToList();
        if (slices.Count > 9)
        {
            var others = slices.Skip(9).Sum(p => p.Value);
            slices = slices.Take(9).Append(new ChartPoint("Altri", others)).ToList();
        }
        var total = slices.Sum(p => p.Value);
        if (total <= 0) return;
        var radius = Math.Max(10, Math.Min(bounds.Height - 24, bounds.Width * 0.55) / 2);
        var center = new Point(radius + 20, bounds.Height / 2);
        var angle = -Math.PI / 2;
        for (var i = 0; i < slices.Count; i++)
        {
            var sweep = slices[i].Value / total * Math.PI * 2;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(center, true);
                g.LineTo(new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)));
                g.ArcTo(new Point(center.X + radius * Math.Cos(angle + sweep), center.Y + radius * Math.Sin(angle + sweep)), new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise);
                g.EndFigure(true);
            }
            context.DrawGeometry(new SolidColorBrush(Palette[i % Palette.Length]), new Pen(new SolidColorBrush(Dark ? Colors.Black : Colors.White), 1.5), geometry);
            angle += sweep;
        }
        // Legenda
        var x = center.X + radius + 28;
        var y = Math.Max(8, center.Y - slices.Count * 11);
        for (var i = 0; i < slices.Count; i++)
        {
            context.DrawRectangle(new SolidColorBrush(Palette[i % Palette.Length]), null, new Rect(x, y + 3, 11, 11), 2, 2);
            DrawText(context, $"{slices[i].Label} · {Short(slices[i].Value)} ({slices[i].Value / total:P0})", new Point(x + 17, y), 11.5, maxWidth: Math.Max(60, bounds.Width - x - 24));
            y += 22;
        }
    }
}
