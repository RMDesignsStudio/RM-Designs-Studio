using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace Swapper.App;

/// <summary>
/// Text with letter-spacing (WPF has none): wide-tracked caps in the RM Designs style. Font, size, weight and
/// colour are inherited like a TextBlock's; <see cref="Tracking"/> is extra space after each letter, in em.
/// </summary>
public sealed class Tracked : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(Tracked),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackingProperty = DependencyProperty.Register(nameof(Tracking), typeof(double), typeof(Tracked),
        new FrameworkPropertyMetadata(0.14, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UpperProperty = DependencyProperty.Register(nameof(Upper), typeof(bool), typeof(Tracked),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(Tracked),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(Tracked),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(Tracked),
        new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(Tracked),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double Tracking { get => (double)GetValue(TrackingProperty); set => SetValue(TrackingProperty, value); }
    public bool Upper { get => (bool)GetValue(UpperProperty); set => SetValue(UpperProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    string Shown => Upper ? (Text ?? "").ToUpperInvariant() : Text ?? "";

    IEnumerable<(FormattedText Glyph, double X)> Layout()
    {
        var tf = new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip, x = 0;
        foreach (var ch in Shown)
        {
            var ft = new FormattedText(ch.ToString(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, FontSize, Foreground, dip);
            yield return (ft, x);
            x += ft.WidthIncludingTrailingWhitespace + Tracking * FontSize;
        }
    }

    protected override Size MeasureOverride(Size available)
    {
        double w = 0, h = 0;
        foreach (var (g, x) in Layout()) { w = x + g.WidthIncludingTrailingWhitespace; h = Math.Max(h, g.Height); }
        if (h == 0) h = FontSize * 1.2;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        foreach (var (g, x) in Layout()) dc.DrawText(g, new Point(x, 0));
    }
}


/// <summary>Team colours from the RM Designs team strip, keyed by a car token (wheels and chassis: <c>force_india</c>,
/// <c>sauber@2025</c>) or a suit set's team slot (<c>forceindia</c>, <c>renault</c>, F2 teams).</summary>
public static class TeamColors
{
    static readonly Dictionary<string, (string Fill, bool DarkText)> Map = new()
    {
        // cars (game team folders)
        ["fom_car"] = ("#FFD966", true),
        ["force_india"] = ("#006C63", false),
        ["gm_cadillac"] = ("#FFFFFF", true),
        ["lotus"] = ("#0185CA", false),
        ["toro_rosso"] = ("#1B4ED5", false),
        ["sauber@2025"] = ("#52E252", true),
        // suit sets (character files' team slots)
        ["forceindia"] = ("#006C63", false),
        ["cadillac"] = ("#FFFFFF", true),
        ["renault"] = ("#0185CA", false),
        ["tororosso"] = ("#1B4ED5", false),
        ["kicksauber"] = ("#52E252", true),
        ["konnersport"] = ("#1E1E1E", false),
        ["zenith"] = ("#0A0A0A", false),   // APXGP (the F1 movie's team, "zenith" in the game files): black, gold lettering
        // both
        ["ferrari"] = ("#EC2128", false),
        ["haas"] = ("#EFEFEF", true),
        ["mclaren"] = ("#EB862B", true),
        ["mercedes"] = ("#01B1AA", false),
        ["redbull"] = ("#142CCA", false),
        ["sauber"] = ("#FF4200", false),
        ["williams"] = ("#0240FF", false),
        // engine sounds: the power unit maker's colour (whichever car the sound comes from)
        ["engine:Ferrari"] = ("#EC2128", false),
        ["engine:Mercedes"] = ("#01B1AA", false),
        ["engine:Honda"] = ("#C30D23", false),
        ["engine:Honda RBPT"] = ("#C30D23", false),
        ["engine:RBPT Ford"] = ("#003478", false),
        ["engine:Audi"] = ("#FF4200", false),
        ["engine:Renault"] = ("#FFD100", true),
        ["engine:FOM"] = ("#FFD966", true),
        // F2 (team brand colours, approximate)
        ["aixracing"] = ("#8A1538", false),
        ["artgrandprix"] = ("#3A3A3A", false),
        ["camposracing"] = ("#E10600", false),
        ["dams"] = ("#003DA5", false),
        ["hitech"] = ("#C0C0C0", true),
        ["invictaracing"] = ("#D4AF37", true),
        ["mpmotorsport"] = ("#FF6A13", true),
        ["prema"] = ("#B5121B", false),
        ["rodin"] = ("#00A651", false),
        ["tridentmotorsport"] = ("#0A2342", false),
        ["vanamersfoort"] = ("#F7941D", true),
        ["phmracing"] = ("#E4002B", false),
    };

    static readonly BrushConverter Bc = new();
    static Brush B(string hex) { var b = (Brush)Bc.ConvertFromString(hex)!; b.Freeze(); return b; }

    static (string Fill, bool DarkText)? Find(string id) =>
        Map.TryGetValue(id, out var t) ? t : Map.TryGetValue(GameData.ParseDonor(id).Token, out t) ? t : null;

    public static Brush Fill(string id) => B(Find(id)?.Fill ?? "#4300AD");
    /// <summary>Teams whose lettering is neither black nor white.</summary>
    static readonly Dictionary<string, string> Lettering = new() { ["zenith"] = "#D4A93A" };

    public static Brush Text(string id) => B(Lettering.TryGetValue(id, out var hex) ? hex : Find(id)?.DarkText == true ? "#111111" : "#F9F9F9");
}

/// <summary>Team key -> its fill (parameter "text": its text colour).</summary>
public sealed class TeamBrush : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string tok ? (parameter as string == "text" ? TeamColors.Text(tok) : TeamColors.Fill(tok)) : Brushes.Transparent;
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
