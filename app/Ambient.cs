using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace xPlayer;

/// <summary>
/// Live background animation (same set as the Android app's Ambient.kt). Drawn in OnRender at ~40 fps,
/// only while visible; all instances share one frame callback.
/// </summary>
public sealed class AmbientLayer : FrameworkElement
{
    public static readonly (string Id, string Label)[] Kinds =
    [
        ("off", "Off"), ("fireflies", "🌟 Fireflies"), ("embers", "🔥 Embers"), ("snow", "❄ Snow"), ("rain", "🌧 Rain"),
        ("leaves", "🍂 Autumn leaves"), ("petals", "🌸 Petals"), ("bats", "🦇 Bat swarm"), ("stars", "✨ Starfall"),
        ("wisps", "👻 Ghost wisps"), ("aurora", "🌌 Aurora"), ("bubbles", "🧪 Cauldron bubbles"),
    ];

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(AmbientLayer), new PropertyMetadata("off", (d, _) => ((AmbientLayer)d).Reset()));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    /// <summary>Fraction of the particles to draw (smaller for preview tiles).</summary>
    public double Density { get; set; } = 1;

    /// <summary>Drama dials of this layer (Settings → Themes, per screen): count ×, size ×, and how far down top effects reach (0.25–1).</summary>
    public double Scale { get; set; } = 1.4;
    public double Reach { get; set; } = 0.6;
    double intensity = 1.8;
    public double Intensity { get => intensity; set { if (intensity != value) { intensity = value; Reset(); } } }
    double K => Scale;

    record struct P(double X, double Y, double R, double Speed, double Phase, double Drift, int K);

    P[] ps = [];
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static readonly List<AmbientLayer> live = new();
    static long lastFrame;

    public AmbientLayer()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) => Update();
    }

    public void Reset()
    {
        var n = Kind switch { "rain" => 80, "snow" => 70, "stars" => 90, "bubbles" => 26, "bats" => 9, "aurora" => 4, _ => 30 };
        n = Math.Max(3, (int)(n * Density * Intensity));
        var rnd = new Random(Kind.GetHashCode());
        ps = Enumerable.Range(0, n).Select(_ => new P(rnd.NextDouble(), rnd.NextDouble(), 1.2 + rnd.NextDouble() * 2.6, 0.4 + rnd.NextDouble(),
            rnd.NextDouble() * 6.28, rnd.NextDouble(), rnd.Next(1000))).ToArray();
        Update();
        InvalidateVisual();
    }

    void Update()
    {
        if (IsLoaded && IsVisible && Kind != "off") Start(); else Stop();
    }

    void Start()
    {
        if (live.Contains(this)) return;
        if (live.Count == 0) CompositionTarget.Rendering += Tick;
        live.Add(this);
    }

    void Stop()
    {
        if (!live.Remove(this)) return;
        if (live.Count == 0) CompositionTarget.Rendering -= Tick;
        InvalidateVisual();
    }

    static void Tick(object? sender, EventArgs e)
    {
        var now = clock.ElapsedMilliseconds;
        if (now - lastFrame < 24) return; // ~40 fps is plenty for drifting particles
        lastFrame = now;
        foreach (var a in live.ToArray()) a.InvalidateVisual();
    }

    static double Frac(double v) => v - Math.Floor(v);
    static Color A(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    static readonly Dictionary<(Color, int), Brush> glowCache = new();
    static Brush Glow(Color c, double alpha)
    {
        var key = (c, (int)(alpha * 40));
        if (glowCache.TryGetValue(key, out var b)) return b;
        var g = new RadialGradientBrush(A(c, alpha), A(c, 0));
        g.Freeze();
        if (glowCache.Count > 4000) glowCache.Clear();
        return glowCache[key] = g;
    }

    static readonly Dictionary<Color, SolidColorBrush> solidCache = new();
    static Brush Solid(Color c)
    {
        if (solidCache.TryGetValue(c, out var b)) return b;
        b = new SolidColorBrush(c);
        b.Freeze();
        if (solidCache.Count > 4000) solidCache.Clear();
        return solidCache[c] = b;
    }

    void GlowDot(DrawingContext dc, Point o, double r, Color c, double a, double halo = 5)
    {
        if (a <= 0.01) return;
        dc.DrawEllipse(Glow(c, 0.32 * a), null, o, r * halo, r * halo);
        dc.DrawEllipse(Solid(A(c, 0.85 * a)), null, o, r, r);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Kind == "off" || !live.Contains(this) && !IsVisible) return;
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2) return;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        var t = clock.ElapsedMilliseconds / 1000.0;
        switch (Kind)
        {
            case "fireflies": Fireflies(dc, t, w, h); break;
            case "embers": Embers(dc, t, w, h); break;
            case "snow": Snow(dc, t, w, h); break;
            case "rain": Rain(dc, t, w, h); break;
            case "leaves": Leaves(dc, t, w, h, [Color.FromRgb(0xD9, 0x82, 0x2B), Color.FromRgb(0xB4, 0x44, 0x1C), Color.FromRgb(0xE8, 0xB2, 0x3A), Color.FromRgb(0x8C, 0x5A, 0x2B)], false); break;
            case "petals": Leaves(dc, t, w, h, [Color.FromRgb(0xF7, 0xB6, 0xC8), Color.FromRgb(0xF2, 0x9B, 0xB5), Color.FromRgb(0xFF, 0xD6, 0xE0)], true); break;
            case "bats": Bats(dc, t, w, h); break;
            case "stars": Stars(dc, t, w, h); break;
            case "wisps": Wisps(dc, t, w, h); break;
            case "aurora": Aurora(dc, t, w, h); break;
            case "bubbles": Bubbles(dc, t, w, h); break;
        }
        dc.Pop();
    }

    void Fireflies(DrawingContext dc, double t, double w, double h)
    {
        dc.DrawEllipse(Glow(Theme.Moon, 0.12), null, new Point(w * 0.85, h * 0.08), w * 0.45, w * 0.45);
        for (int k = 0; k < 2; k++)
        {
            var x = Frac(t / (40.0 / (1 + k)) + k * 0.4) * w * 2 - w * 0.5;
            dc.DrawEllipse(Glow(Theme.Mist, 0.07), null, new Point(x, h * (0.78 + 0.1 * k)), w * 0.6, w * 0.3);
        }
        foreach (var f in ps)
        {
            var a = t * 0.16 * f.Speed * 2 * Math.PI + f.Phase;
            var x = Frac(f.X + Math.Sin(a) * 0.05 + t * f.Drift * 0.0015) * w;
            var y = Frac(f.Y + Math.Sin(a * 0.7 + 1) * 0.04) * h;
            GlowDot(dc, new Point(x, y), f.R * K, Theme.Slime, 0.35 + 0.65 * ((Math.Sin(a * 3) + 1) / 2), 6);
        }
    }

    void Embers(DrawingContext dc, double t, double w, double h)
    {
        var mist = new LinearGradientBrush(A(Theme.Witch, 0), A(Theme.Witch, 0.08), 90);
        dc.DrawRectangle(mist, null, new Rect(0, h * 0.6, w, h * 0.4));
        for (int i = 0; i < ps.Length; i++)
        {
            var e = ps[i];
            var rise = Frac(t * e.Speed * 0.05 + e.Y);
            var x = (e.X + Math.Sin(rise * 6 + e.Phase) * 0.03) * w;
            GlowDot(dc, new Point(x, h * (1 - rise)), e.R * 0.8 * K, i % 3 == 0 ? Theme.Moon : Theme.Pumpkin, Math.Sin(rise * Math.PI), 6);
        }
    }

    void Snow(DrawingContext dc, double t, double w, double h)
    {
        foreach (var s in ps)
        {
            var fall = Frac(t * (0.025 + s.Speed * 0.03) + s.Y);
            var x = Frac(s.X + Math.Sin(t * 0.6 * s.Speed + s.Phase) * 0.02 + t * 0.004) * w;
            var depth = 0.5 + s.Drift * 0.5;
            dc.DrawEllipse(Solid(A(Colors.White, 0.55 * depth)), null, new Point(x, fall * h), s.R * depth * 1.1 * K, s.R * depth * 1.1 * K);
        }
    }

    void Rain(DrawingContext dc, double t, double w, double h)
    {
        var len = h * 0.035 * K;
        foreach (var d in ps)
        {
            var fall = Frac(t * (0.45 + d.Speed * 0.35) + d.Y);
            var x = Frac(d.X + fall * 0.04) * w;
            var y = fall * h * 1.05;
            var pen = new Pen(Solid(A(Theme.Mist, 0.18 + 0.18 * d.Drift)), (1.2 + d.Drift) * K) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, new Point(x, y), new Point(x + len * 0.12, y + len));
            if (fall > 0.97) dc.DrawEllipse(null, new Pen(Solid(A(Theme.Mist, 0.15)), 1), new Point(x, h - 4), 6 * (fall - 0.97) / 0.03 + 1, 2);
        }
    }

    void Leaves(DrawingContext dc, double t, double w, double h, Color[] colors, bool petal)
    {
        foreach (var l in ps)
        {
            var fall = Frac(t * (0.03 + l.Speed * 0.025) + l.Y);
            var sway = Math.Sin(t * (0.8 + l.Speed * 0.6) + l.Phase);
            var x = Frac(l.X + sway * 0.06 + t * 0.006) * w;
            var y = fall * h * 1.1 - h * 0.05;
            var s = ((petal ? 5 : 7) + l.R * 2.2) * K;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(0, -s), true, true);
                c.QuadraticBezierTo(new Point(s * 0.9, 0), new Point(0, s), true, false);
                c.QuadraticBezierTo(new Point(-s * 0.9, 0), new Point(0, -s), true, false);
            }
            dc.PushTransform(new TranslateTransform(x, y));
            dc.PushTransform(new RotateTransform(sway * 70 + l.K));
            dc.DrawGeometry(Solid(A(colors[l.K % colors.Length], 0.55)), null, g);
            if (!petal) dc.DrawLine(new Pen(Solid(A(Colors.Black, 0.18)), 1), new Point(0, -s), new Point(0, s));
            dc.Pop();
            dc.Pop();
        }
    }

    void Bats(DrawingContext dc, double t, double w, double h)
    {
        dc.DrawEllipse(Glow(Theme.Moon, 0.16), null, new Point(w * 0.8, h * 0.12), w * 0.3, w * 0.3);
        foreach (var b in ps)
        {
            var p = Frac(t * (0.03 + b.Speed * 0.025) + b.X);
            var x = p * w * 1.3 - w * 0.15;
            var y = h * (0.06 + b.Y * (Reach - 0.06)) + Math.Sin(t * 1.3 + b.Phase) * h * 0.03;
            var flap = Math.Sin(t * (9 + b.Speed * 5) + b.Phase);
            var ww = (10 + b.R * 4) * K;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x - ww, y - flap * ww * 0.6), true, true);
                c.QuadraticBezierTo(new Point(x - ww * 0.5, y - ww * 0.15), new Point(x, y + ww * 0.12), true, false);
                c.QuadraticBezierTo(new Point(x + ww * 0.5, y - ww * 0.15), new Point(x + ww, y - flap * ww * 0.6), true, false);
                c.QuadraticBezierTo(new Point(x + ww * 0.45, y + ww * 0.1), new Point(x, y + ww * 0.35), true, false);
                c.QuadraticBezierTo(new Point(x - ww * 0.45, y + ww * 0.1), new Point(x - ww, y - flap * ww * 0.6), true, false);
            }
            dc.DrawGeometry(Solid(A(Colors.Black, 0.55)), null, g);
        }
    }

    void Stars(DrawingContext dc, double t, double w, double h)
    {
        foreach (var s in ps)
        {
            var tw = 0.3 + 0.7 * ((Math.Sin(t * (1 + s.Speed * 2) + s.Phase) + 1) / 2);
            var r = s.R * 0.55 * K;
            dc.DrawEllipse(Solid(A(Theme.Bone, 0.55 * tw * (0.4 + s.Drift * 0.6))), null, new Point(s.X * w, s.Y * h * Reach), r, r);
        }
        for (int k = 0; k < 2; k++)
        {
            var period = 6 + k * 3.5;
            var ph = Frac((t + k * 2.3) / period);
            if (ph >= 0.18) continue;
            var rnd = new Random((int)((t + k * 2.3) / period) * 31 + k);
            var sx = w * (0.2 + rnd.NextDouble() * 0.8);
            var sy = h * rnd.NextDouble() * Reach * 0.7;
            var q = ph / 0.18;
            var head = new Point(sx - q * w * 0.45, sy + q * w * 0.22);
            var tail = new Point(head.X + w * 0.12, head.Y - w * 0.06);
            var a = Math.Sin(q * Math.PI);
            var brush = new LinearGradientBrush(A(Theme.Moon, 0.9 * a), A(Theme.Moon, 0), head, tail) { MappingMode = BrushMappingMode.Absolute };
            dc.DrawLine(new Pen(brush, 2.2 * K) { StartLineCap = PenLineCap.Round }, head, tail);
            GlowDot(dc, head, 1.8, Theme.Moon, a, 4);
        }
    }

    void Wisps(DrawingContext dc, double t, double w, double h)
    {
        foreach (var s in ps.Take(Math.Max(3, (int)(9 * Intensity))))
        {
            var p = Frac(t * (0.012 + s.Speed * 0.01) + s.X);
            var x = p * w * 1.4 - w * 0.2;
            var y = h * (0.1 + s.Y * Math.Min(0.85, Reach * 1.3)) + Math.Sin(t * 0.7 + s.Phase) * 30;
            var r = (28 + s.R * 12) * K;
            var a = 0.10 + 0.06 * Math.Sin(t + s.Phase);
            for (int i = 4; i >= 0; i--)
            {
                var rr = r * (1 - i * 0.13);
                dc.DrawEllipse(Glow(Theme.Mist, a * (1 - i / 5.0)), null, new Point(x - i * r * 0.55, y + Math.Sin(t * 2 + s.Phase + i) * 6), rr, rr);
            }
            dc.DrawEllipse(Solid(A(Colors.Black, 0.25)), null, new Point(x - 5, y - 3), 2.4, 2.4);
            dc.DrawEllipse(Solid(A(Colors.Black, 0.25)), null, new Point(x + 5, y - 3), 2.4, 2.4);
        }
    }

    static readonly Dictionary<(Color, int), Brush> curtainCache = new();
    static Brush Curtain(Color c, double a)
    {
        var key = (c, (int)(a * 60));
        if (curtainCache.TryGetValue(key, out var b)) return b;
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops = { new GradientStop(A(c, 0), 0), new GradientStop(A(c, a), 0.3), new GradientStop(A(c, a * 0.35), 0.7), new GradientStop(A(c, 0), 1) } };
        g.Freeze();
        if (curtainCache.Count > 2000) curtainCache.Clear();
        return curtainCache[key] = g;
    }

    /// <summary>Aurora: rippling vertical curtains of light hanging from a wavy line.</summary>
    void Aurora(DrawingContext dc, double t, double w, double h)
    {
        Color[] bands = [Theme.Slime, Theme.Witch, Color.FromRgb(0x4F, 0xD8, 0xFF)];
        const int n = 70;
        for (int i = 0; i < bands.Length; i++)
        {
            for (int s = 0; s < n; s++)
            {
                var u = (double)s / n;
                var top = h * (0.04 + i * 0.09 * Reach * 1.6) + Math.Sin(u * 7 + t * (0.3 + i * 0.1) + i * 2) * h * 0.05 + Math.Sin(u * 17 - t * 0.5) * h * 0.012;
                var len = h * (0.16 + 0.09 * Math.Sin(u * 11 + t * 0.7 + i)) * Reach * 2.2 * Math.Min(1.6, K);
                var a = (0.05 + 0.07 * Math.Pow((Math.Sin(u * 9 - t * (0.8 + i * 0.2) + i) + 1) / 2, 2)) * (0.6 + 0.4 * Math.Sin(u * Math.PI)) * Math.Min(1.8, 0.7 + 0.3 * Intensity);
                double x0 = Math.Floor(u * w), x1 = Math.Floor((s + 1.0) / n * w); // whole pixels: no overlapping seams
                dc.DrawRectangle(Curtain(bands[i], a), null, new Rect(x0, top, x1 - x0, len));
            }
        }
    }

    void Bubbles(DrawingContext dc, double t, double w, double h)
    {
        dc.DrawRectangle(new LinearGradientBrush(A(Theme.Slime, 0), A(Theme.Slime, 0.07), 90), null, new Rect(0, h * 0.7, w, h * 0.3));
        foreach (var b in ps)
        {
            var rise = Frac(t * (0.04 + b.Speed * 0.04) + b.Y);
            var x = (b.X + Math.Sin(rise * 9 + b.Phase) * 0.015) * w;
            var y = h * (1 - rise * 0.75);
            var r = (3 + b.R * 3.2 * (0.6 + rise)) * K;
            var pop = rise > 0.93 ? 1 - (rise - 0.93) / 0.07 : 1;
            dc.DrawEllipse(null, new Pen(Solid(A(Theme.Slime, 0.32 * pop)), 1.4), new Point(x, y), r, r);
            dc.DrawEllipse(Solid(A(Colors.White, 0.25 * pop)), null, new Point(x - r * 0.35, y - r * 0.35), r * 0.22, r * 0.22);
        }
    }
}
