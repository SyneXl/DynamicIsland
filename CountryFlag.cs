using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

static class CountryFlags
{
    public readonly record struct LabelInfo(string? Code, string Label);
    static readonly Dictionary<string, BitmapSource?> Images = new(StringComparer.Ordinal);

    public static LabelInfo Parse(string name)
    {
        string text = name.Trim();
        int start = 0;
        while (start < text.Length && (char.IsWhiteSpace(text[start]) || text[start] is '[' or '(' or '{')) start++;
        if (start >= text.Length) return new(null, text);
        string? code = null; int length = 0;
        if (Rune.TryGetRuneAt(text, start, out var first) && first.Value is >= 0x1F1E6 and <= 0x1F1FF
            && start + first.Utf16SequenceLength < text.Length
            && Rune.TryGetRuneAt(text, start + first.Utf16SequenceLength, out var second) && second.Value is >= 0x1F1E6 and <= 0x1F1FF)
        {
            code = string.Concat((char)('A' + first.Value - 0x1F1E6), (char)('A' + second.Value - 0x1F1E6));
            length = first.Utf16SequenceLength + second.Utf16SequenceLength;
        }
        else if (start + 2 <= text.Length && text[start] is >= 'A' and <= 'Z' && text[start + 1] is >= 'A' and <= 'Z'
            && (start + 2 == text.Length || char.IsWhiteSpace(text[start + 2]) || text[start + 2] is ']' or ')' or '}' or ':' or '-' or '|' or '–' or '—'))
        { code = text.Substring(start, 2); length = 2; }
        if (code == "UK") code = "GB";
        if (code == null || !Known(code)) return new(null, text);
        string label = text[(start + length)..].TrimStart(' ', '\t', ']', ')', '}', ':', '-', '|', '–', '—');
        return new(code, label.Length == 0 ? code : label);
    }

    internal static bool Known(string code) => Codes.Contains(code);
    // Asset whitelist: only bundled, two-letter identifiers can form pack URIs.
    internal static readonly HashSet<string> Codes = new(
        "AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BV BW BY BZ CA CC CD CF CG CH CI CK CL CM CN CO CR CU CV CW CX CY CZ DE DJ DK DM DO DZ EC EE EG EH ER ES ET EU FI FJ FK FM FO FR GA GB GD GE GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY HK HM HN HR HT HU ID IE IL IM IN IO IQ IR IS IT JE JM JO JP KE KG KH KI KM KN KP KR KW KY KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT MU MV MW MX MY MZ NA NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG PH PK PL PM PN PR PS PT PW PY QA RE RO RS RU RW SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS ST SV SX SY SZ TC TD TF TG TH TJ TK TL TM TN TO TR TT TV TW TZ UA UG UM US UY UZ VA VC VE VG VI VN VU WF WS XK YE YT ZA ZM ZW".Split(' '), StringComparer.Ordinal);

    internal static BitmapSource? Image(string? code)
    {
        if (code == null || !Known(code)) return null;
        if (Images.TryGetValue(code, out var cached)) return cached;
        try
        {
            var bitmap = new BitmapImage(new Uri("pack://application:,,,/DynamicIsland;component/Assets/Flags/" + code.ToLowerInvariant() + ".png"));
            bitmap.Freeze(); return Images[code] = bitmap;
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.FileFormatException) { return Images[code] = null; }
    }
}

public sealed class CountryFlag : FrameworkElement
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(nameof(Code), typeof(string), typeof(CountryFlag),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public string? Code { get => (string?)GetValue(CodeProperty); set => SetValue(CodeProperty, value); }
    BitmapSource? _image;
    static readonly Pen Edge = new(new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), .6);
    public CountryFlag() { Visibility = Visibility.Collapsed; IsHitTestVisible = false; }
    static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var flag = (CountryFlag)d; flag._image = CountryFlags.Image((string?)e.NewValue);
        flag.Visibility = flag._image == null ? Visibility.Collapsed : Visibility.Visible;
        string? name = flag.Code;
        try { if (name != null) name = new RegionInfo(name).DisplayName; } catch (ArgumentException) { }
        System.Windows.Automation.AutomationProperties.SetName(flag, name ?? "");
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (_image == null || ActualWidth <= 0 || ActualHeight <= 0) return;
        double width = Math.Min(ActualWidth, ActualHeight * _image.PixelWidth / _image.PixelHeight);
        double height = Math.Min(ActualHeight, ActualWidth * _image.PixelHeight / _image.PixelWidth);
        var box = new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height);
        dc.DrawRoundedRectangle(new ImageBrush(_image) { Stretch = Stretch.Fill }, Edge, box, 1.5, 1.5);
    }
}
