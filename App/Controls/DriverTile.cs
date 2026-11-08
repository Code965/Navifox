using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Navifox.App.ViewModels;

namespace Navifox.App.Controls;

/// <summary>
/// Riquadro del driver: il logo del database (in chiaro, con il colore del marchio) quando disponibile,
/// altrimenti la sigla su fondo colorato.
/// </summary>
public sealed class DriverTile : Border
{
    public static readonly StyledProperty<DriverInfo?> DriverProperty = AvaloniaProperty.Register<DriverTile, DriverInfo?>(nameof(Driver));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<DriverTile, double>(nameof(Size), 58);

    private static readonly IBrush LightTile = new SolidColorBrush(Color.Parse("#F7F5F2"));
    private static readonly IBrush DarkTile = new SolidColorBrush(Color.Parse("#23272E"));

    public DriverInfo? Driver { get => GetValue(DriverProperty); set => SetValue(DriverProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public DriverTile()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Update();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DriverProperty || change.Property == SizeProperty) Update();
    }

    private void Update()
    {
        var size = Size;
        Width = Height = size;
        CornerRadius = new CornerRadius(size * 0.27);
        var driver = Driver;
        if (driver?.Logo is { } logo)
        {
            var color = Color.Parse(logo.Hex);
            // Marchi molto chiari (es. giallo): riquadro scuro, così il logo resta leggibile.
            var light = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255 > 0.72;
            Background = light ? DarkTile : LightTile;
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
            BorderThickness = new Thickness(1);
            Child = new Viewbox
            {
                Width = size * 0.6, Height = size * 0.6,
                Child = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(logo.PathData), Fill = new SolidColorBrush(color), Width = 24, Height = 24 }
            };
            ToolTip.SetTip(this, null);
            return;
        }
        Background = driver?.BadgeBrush ?? Brushes.Gray;
        BorderThickness = new Thickness(0);
        Child = new TextBlock
        {
            Text = driver?.Monogram ?? "?", Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = size * 0.34,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
    }
}
