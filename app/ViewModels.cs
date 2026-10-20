using System.Windows;
using System.Windows.Media;

namespace xPlayer;

/// <summary>Display wrapper for one scrobble row (glyph/colour/label computed once).</summary>
public sealed class ScrobbleVm
{
    public ScrobbleRow Row { get; }
    public string Title => Row.Title;
    public string Sub => string.IsNullOrEmpty(Row.Album) ? Row.Artist : $"{Row.Artist} · {Row.Album}";
    public string When { get; }
    public string Glyph { get; }
    public Brush Brush { get; }
    public string StatusText { get; }
    public string Plays => Row.Plays is int n ? $"{n}×" : "";

    public ScrobbleVm(ScrobbleRow r)
    {
        Row = r;
        var d = r.When;
        When = d.Date == DateTime.Today ? d.ToString("HH:mm") : d.ToString("MMM d  HH:mm");
        (Glyph, Brush, StatusText) = r.Status switch
        {
            "confirmed" => ("✓", Theme.SlimeBrush, "Confirmed on Last.fm"),
            "sent" => ("↗", B(0x60A5FA), "Sent · verifying…"),
            "queued" => ("…", B(0xFFD54A), r.Detail.Length > 0 ? "Queued · " + r.Detail : "Queued"),
            "ignored" => ("⊘", Theme.BloodBrush, "Ignored · " + r.Detail),
            "failed" => ("✕", Theme.BloodBrush, "Failed · " + r.Detail),
            "unverified" => ("?", B(0xFFD54A), "Unverified · " + r.Detail),
            _ => ("·", Theme.MistBrush, r.Status),
        };
    }

    static Brush B(uint rgb)
    {
        var b = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        b.Freeze();
        return b;
    }
}

/// <summary>One lyric line as shown in the lyrics panel; its look depends on its distance from the line being sung.</summary>
public sealed class LyricLineVm : Observable
{
    public LyricLine Line { get; }
    public string Text => Line.Text.Length > 0 ? Line.Text : "♪";
    public string Translation => Line.Translation;
    bool showTranslation;
    public bool HasTranslation => showTranslation && Line.Translation.Length > 0;
    public bool Seekable { get; }

    public LyricLineVm(LyricLine line, bool seekable, bool showTranslation = false)
    {
        Line = line;
        Seekable = seekable;
        this.showTranslation = showTranslation;
    }

    public void ShowTranslation(bool on)
    {
        if (showTranslation == on) return;
        showTranslation = on;
        Raise(nameof(HasTranslation));
    }

    double fontSize = 20;
    public double FontSize { get => fontSize; set => Set(ref fontSize, value); }

    Brush brush = Theme.MistBrush;
    public Brush Brush { get => brush; set => Set(ref brush, value); }

    double opacity = 0.75;
    public double Opacity { get => opacity; set => Set(ref opacity, value); }

    FontWeight weight = FontWeights.Normal;
    public FontWeight Weight { get => weight; set => Set(ref weight, value); }

    /// <summary>distance 0 = the line being sung, negative = already sung, positive = coming up.</summary>
    public void Apply(int distance, bool synced)
    {
        if (!synced) { FontSize = 18; Brush = Theme.BoneBrush; Opacity = 0.92; Weight = FontWeights.Normal; return; }
        if (distance == 0) { FontSize = 26; Brush = Theme.PumpkinBrush; Opacity = 1; Weight = FontWeights.SemiBold; return; }
        int d = Math.Abs(distance);
        FontSize = 19;
        Brush = distance < 0 ? Theme.MistBrush : Theme.BoneBrush;
        Opacity = Math.Max(0.28, (distance < 0 ? 0.55 : 0.82) - d * 0.09);
        Weight = FontWeights.Normal;
    }
}
