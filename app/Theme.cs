using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace xPlayer;

/// <summary>Halloween palette, matching xVideoWatcher's Spook theme.</summary>
public static class Theme
{
    public static readonly Color Night = C(0x0B0612), Crypt = C(0x150C21), Crypt2 = C(0x1F1230), Crypt3 = C(0x2B1A40);
    public static readonly Color Pumpkin = C(0xFF7A18), PumpkinDeep = C(0xC24E00), Witch = C(0x8B3DFF), WitchDeep = C(0x4B1C99);
    public static readonly Color Slime = C(0x7CFF4F), Blood = C(0xFF2E4D), Bone = C(0xF2E8D5), Moon = C(0xFFD54A);
    public static readonly Color Mist = C(0x9A8BB0), Fog = C(0x5E4F75);

    public static readonly Brush PumpkinBrush = B(Pumpkin), BoneBrush = B(Bone), MistBrush = B(Mist), SlimeBrush = B(Slime);
    public static readonly Brush BloodBrush = B(Blood), EdgeBrush = B(C(0x3A2552)), DeletedEdge = B(C(0xC8243F));
    public static readonly Brush DeletedText = B(C(0xFF7A8C));
    public static readonly Brush NoteBubble = B(C(0x2A2410)), NoteEdge = B(C(0x9C8530)), NoteComposer = B(C(0x2E2712));
    public static readonly Brush SelectEdge = B(Moon);
    public static readonly Brush InBubble = B(C(0x22143A)), InBubbleEdge = B(C(0x33204F));
    public static readonly Brush OutBubble = Grad(C(0x5A2A0C), C(0x43200B)), OutBubbleEdge = B(C(0x7A3A10));

    static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    static Brush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static Brush Grad(Color a, Color b)
    {
        var g = new LinearGradientBrush(a, b, 90);
        g.Freeze();
        return g;
    }
}

/// <summary>Pixel-art sprites (ported from xVideoWatcher). '.' = transparent.</summary>
public static class Sprites
{
    static readonly Dictionary<char, uint> palette = new()
    {
        ['o'] = 0xFFFF7A18, ['O'] = 0xFFC24E00, ['d'] = 0xFF7A2E00, ['y'] = 0xFFFFD54A, ['Y'] = 0xFFFFF3B0,
        ['g'] = 0xFF5BD44A, ['G'] = 0xFF2E7D32, ['w'] = 0xFFF4F0FF, ['s'] = 0xFFB9A8E0, ['k'] = 0xFF140A1C,
        ['p'] = 0xFF8B3DFF, ['P'] = 0xFF4B1C99, ['r'] = 0xFFFF2E4D, ['R'] = 0xFF9E0F2A, ['b'] = 0xFF7A4FB0,
        ['B'] = 0xFF3E2366, ['e'] = 0xFFFF2E4D, ['c'] = 0xFF7CFF4F, ['C'] = 0xFF2FA81C, ['m'] = 0xFFFFF7D6,
        ['M'] = 0xFFE8D9A0, ['n'] = 0xFF6E6E80, ['N'] = 0xFF3F3F4F, ['l'] = 0xFFA0A0B8, ['h'] = 0xFF5A3A1A,
    };

    static readonly Dictionary<string, string[]> rows = new()
    {
        ["pumpkin"] =
        [
            ".......GG.......", "......GgG.......", "...OOOoGGoOOO...", "..OoooOooOoooO..", ".OoooOooooOoooO.",
            ".OooyyOooOyyooO.", "OooyyyyOOyyyyooO", "OoooooOooOoooooO", "OoooooOyyOoooooO", "OoyooOooooOooyoO",
            "OoyyyyyyyyyyyyoO", ".OoyyOyyyyOyyoO.", ".OoooOOyyOOoooO.", "..OOooOooOooOO..", "....OOOOOOOO....",
        ],
        ["ghost"] =
        [
            ".....wwwww......", "...wwwwwwwws....", "..wwwwwwwwwss...", ".wwwwwwwwwwwss..", ".wwkkwwwkkwwss..",
            ".wwkkwwwkkwwss..", "wwwwwwwwwwwwwss.", "wwwwwwkkkwwwwss.", "wwwwwkkkkkwwwss.", "wwwwwwkkkwwwwss.",
            "wwwwwwwwwwwwwss.", "wwwwwwwwwwwwwss.", "wwwwwwwwwwwwwss.", "ww.wwww.wwww.ss.", "w...ww...ww...s.",
        ],
        ["bat1"] =
        [
            "B..............B", "BB............BB", "BbB....bb....BbB", "BbbB..bBBb..BbbB", "BbbbBbBBBBbBbbbB",
            ".BbbbBBeBeBBbbB.", "..BBbBBBBBBbBB..", "....B.BBBB.B....", ".......BB.......",
        ],
        ["bat2"] =
        [
            "................", "................", ".......bb.......", "......bBBb......", ".....BBBBBB.....",
            "..BBBBBeBeBBBB..", ".BbbbBBBBBBbbbB.", "BbbB.B.BB.B.BbbB", "BB......BB....BB",
        ],
        ["skull"] =
        [
            "....mmmmmm....", "..mmmmmmmmmm..", ".mmmmmmmmmmmM.", ".mmmmmmmmmmmM.", "mmkkkmmmmkkkmM", "mmkkkmmmmkkkmM",
            "mmkkkmmmmkkkmM", ".mmmmmkkmmmmM.", "..mmmmkkmmmM..", "...mmmmmmmM...", "...mkmkmkmM...", "...mmmmmmmM...",
            "....MMMMMM....",
        ],
        ["spider"] =
        [
            ".......l.......", ".......l.......", ".......l.......", ".......l.......", "..P...PPP...P..",
            "...P.PpppP.P...", "P...PppppppP..P", ".PPPPpepepPPPP.", "....PpppppP....", ".PPPPPpppPPPPP.",
            "P...PPpppPP...P", "...P.PPPPP.P...", "..P...PPP...P..",
        ],
        ["candy"] = ["r.r..........r.r", ".rRr.rrrrrr.rRr.", "..rrrrwrrwrrrr..", "..rrrwrrwrrwrr..", ".rRr.rrrrrr.rRr.", "r.r..........r.r"],
        ["cauldron"] =
        [
            "...c..c...c...", "..c.cccc.c.c..", ".CcccccccccccC", "NNNNNNNNNNNNNN", ".nnNnnnnnnnnN.", "nnlnnnnnnnnnnN",
            "nlnnnnnnnnnnnN", "nnnnnnnnnnnnnN", ".nnnnnnnnnnnN.", "..nnnnnnnnnN..", "..NN......NN..",
        ],
        ["moon"] =
        [
            "....YYYYY....", "..YYYYYYYYY..", ".YYYYyYYYYYY.", ".YYYyyYYYYYYY", "YYYYYYYYYYYYy", "YYYYYYYYyYYYy",
            "YYYYYYYyyYYYy", "YYyYYYYYYYYYy", ".YyyYYYYYYYy.", ".YYYYYYYYYyy.", "..YYYYYYYyy..", "....YYyyy....",
        ],
        ["tomb"] =
        [
            "....nnnnnn....", "..nnllllllnn..", ".nlllllllllln.", ".nllllnnllllnN", "nllllllnllllnN", "nllllnnnnnllnN",
            "nllllllnllllnN", "nllllllnllllnN", "nlllllllllllnN", "nllllllllllnnN", "nlllllllllllnN", "GgGgGGgGGgGgGG",
        ],
        ["hat"] =
        [
            ".........PP.....", "........PpP.....", ".......PppP.....", "......PpppP.....", ".....PppppP.....",
            "....PpppppP.....", "....PppppppP....", "...PyyyyyyyyP...", "...PPPPPPPPPP...", "PPPPppppppppPPPP",
            ".PPPPPPPPPPPPPP.",
        ],
    };

    static readonly Dictionary<string, BitmapSource> cache = new();

    public static BitmapSource Get(string id)
    {
        if (cache.TryGetValue(id, out var bmp)) return bmp;
        var r = rows[id];
        int w = r.Max(x => x.Length), h = r.Length;
        var px = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < r[y].Length; x++)
                if (palette.TryGetValue(r[y][x], out var c)) px[y * w + x] = c;
        bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return cache[id] = bmp;
    }

    /// <summary>A crisp (nearest-neighbour) Image of the sprite at the given height.</summary>
    public static Image Make(string id, double height)
    {
        var src = Get(id);
        var img = new Image
        {
            Source = src,
            Height = height,
            Width = height * src.PixelWidth / src.PixelHeight,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        return img;
    }
}

/// <summary>XAML-usable sprite: &lt;local:Sprite Id="pumpkin" Height="32"/&gt;</summary>
public sealed class Sprite : Image
{
    public static readonly DependencyProperty IdProperty = DependencyProperty.Register(
        nameof(Id), typeof(string), typeof(Sprite), new PropertyMetadata(null, (d, e) => ((Sprite)d).Refresh()));

    public string? Id { get => (string?)GetValue(IdProperty); set => SetValue(IdProperty, value); }

    public Sprite()
    {
        Stretch = Stretch.Uniform;
        IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    void Refresh() => Source = Id == null ? null : Sprites.Get(Id);
}
