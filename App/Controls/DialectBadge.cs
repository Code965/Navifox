using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Navifox.App.ViewModels;
using Navifox.Core.Models;

namespace Navifox.App.Controls;

/// <summary>Identità visiva di una connessione: monogramma, colore e nome del dialetto.</summary>
public static class DialectVisuals
{
    /// <summary>"#RRGGBB" → pennello; vuoto → trasparente.</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter ColorBrushConverter =
        new Avalonia.Data.Converters.FuncValueConverter<string?, Avalonia.Media.IBrush>(value =>
            string.IsNullOrEmpty(value) || !Avalonia.Media.Color.TryParse(value, out var color) ? Avalonia.Media.Brushes.Transparent : new Avalonia.Media.SolidColorBrush(color));

    public static readonly Avalonia.Data.Converters.IValueConverter ColorNameConverter =
        new Avalonia.Data.Converters.FuncValueConverter<string?, string>(value => value switch
        {
            "#E5484D" => "Rosso", "#F76B15" => "Arancione", "#F5B400" => "Giallo", "#30A46C" => "Verde", "#12A594" => "Verde acqua",
            "#0090FF" => "Blu", "#6E56CF" => "Viola", "#8B8D98" => "Grigio", _ => "Nessun colore"
        });

    public static readonly Avalonia.Data.Converters.IValueConverter HasColorConverter =
        new Avalonia.Data.Converters.FuncValueConverter<ConnectionProfile?, bool>(profile => !string.IsNullOrEmpty(profile?.Color));

    public static readonly Avalonia.Data.Converters.IValueConverter ProfileColorConverter =
        new Avalonia.Data.Converters.FuncValueConverter<ConnectionProfile?, Avalonia.Media.IBrush>(profile =>
            string.IsNullOrEmpty(profile?.Color) || !Avalonia.Media.Color.TryParse(profile.Color, out var color) ? Avalonia.Media.Brushes.Transparent : new Avalonia.Media.SolidColorBrush(color));

    public static readonly Avalonia.Data.Converters.IValueConverter IsProductionConverter =
        new Avalonia.Data.Converters.FuncValueConverter<ConnectionProfile?, bool>(profile => profile?.IsProduction == true);

    public static readonly Avalonia.Data.Converters.IValueConverter IsReadOnlyConverter =
        new Avalonia.Data.Converters.FuncValueConverter<ConnectionProfile?, bool>(profile => profile?.ReadOnly == true);

    public static readonly Avalonia.Data.Converters.IValueConverter HasEnvironmentConverter =
        new Avalonia.Data.Converters.FuncValueConverter<ConnectionProfile?, bool>(profile => !string.IsNullOrEmpty(profile?.Environment));

    public static readonly Avalonia.Data.Converters.IValueConverter EnvironmentLabelConverter =
        new Avalonia.Data.Converters.FuncValueConverter<ConnectionProfile?, string>(profile => profile?.Environment switch
        {
            "Produzione" => "PROD", "Test" => "TEST", "Sviluppo" => "DEV", _ => string.Empty
        });

    public static DriverInfo? DriverOf(ConnectionProfile? profile) => profile is null
        ? null
        : DriverCatalog.Drivers.FirstOrDefault(d => d.Kind == profile.Kind && string.Equals(d.Name, profile.Driver, StringComparison.OrdinalIgnoreCase))
          ?? DriverCatalog.Drivers.First(d => d.Kind == profile.Kind);

    public static string DialectName(ConnectionProfile? profile) => DriverOf(profile)?.Name ?? string.Empty;

    /// <summary>«MySQL · localhost:3306» (+ database iniziale se presente).</summary>
    public static string Summary(ConnectionProfile? profile) => profile is null
        ? string.Empty
        : $"{DialectName(profile)} · {profile.Endpoint}{(string.IsNullOrWhiteSpace(profile.DefaultDatabase) ? string.Empty : " · " + profile.DefaultDatabase)}";

    public static readonly IValueConverter SummaryConverter = new FuncValueConverter<ConnectionProfile?, string>(Summary);
    public static string Endpoint(ConnectionProfile? profile) => profile is null ? string.Empty : profile.Endpoint;
    public static readonly IValueConverter EndpointConverter = new FuncValueConverter<ConnectionProfile?, string>(Endpoint);
    public static readonly IValueConverter NameConverter = new FuncValueConverter<ConnectionProfile?, string>(DialectName);
}

/// <summary>
/// Badge del dialetto: quadrato colorato con monogramma (My, Pg, Ma…) oppure, con <see cref="ShowName"/>,
/// pillola con il nome («MySQL», «PostgreSQL»). Stessi colori del catalogo driver.
/// </summary>
public sealed class DialectBadge : Border
{
    public static readonly StyledProperty<ConnectionProfile?> ProfileProperty = AvaloniaProperty.Register<DialectBadge, ConnectionProfile?>(nameof(Profile));
    public static readonly StyledProperty<bool> ShowNameProperty = AvaloniaProperty.Register<DialectBadge, bool>(nameof(ShowName));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<DialectBadge, double>(nameof(Size), 18);

    private readonly TextBlock _text = new()
    {
        Foreground = Brushes.White,
        FontWeight = FontWeight.Bold,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public DialectBadge()
    {
        Child = _text;
        VerticalAlignment = VerticalAlignment.Center;
        Update();
    }

    public ConnectionProfile? Profile { get => GetValue(ProfileProperty); set => SetValue(ProfileProperty, value); }
    public bool ShowName { get => GetValue(ShowNameProperty); set => SetValue(ShowNameProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ProfileProperty || change.Property == ShowNameProperty || change.Property == SizeProperty) Update();
    }

    private void Update()
    {
        var driver = DialectVisuals.DriverOf(Profile);
        if (driver is null)
        {
            _text.Text = string.Empty;
            Background = Brushes.Transparent;
            return;
        }
        Background = driver.BadgeBrush;
        ToolTip.SetTip(this, DialectVisuals.Summary(Profile));
        if (ShowName)
        {
            _text.Text = driver.Name;
            _text.FontSize = 10;
            _text.FontWeight = FontWeight.SemiBold;
            Padding = new Thickness(6, 1);
            CornerRadius = new CornerRadius(8);
            Width = double.NaN;
            Height = double.NaN;
        }
        else
        {
            _text.Text = driver.Monogram;
            _text.FontSize = Math.Max(8, Size * 0.48);
            _text.FontWeight = FontWeight.Bold;
            Padding = new Thickness(0);
            CornerRadius = new CornerRadius(Size * 0.28);
            Width = Size;
            Height = Size;
        }
    }
}
