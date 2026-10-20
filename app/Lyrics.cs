using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace xPlayer;

public sealed class LyricLine
{
    public double Time { get; init; }                   // seconds, -1 = unsynced
    public string Text { get; set; } = "";
    public string Translation { get; set; } = "";
}

public sealed class ParsedLyrics
{
    public bool Synced { get; init; }
    public List<LyricLine> Lines { get; init; } = [];
    public string Source { get; init; } = "";
    public string RawSynced { get; init; } = "";        // original LRC text (for saving)
    public string RawPlain { get; init; } = "";

    /// <summary>Index of the line that should be highlighted at this playback time (-1 before the first line).</summary>
    public int IndexAt(double seconds)
    {
        if (!Synced || Lines.Count == 0) return -1;
        int lo = 0, hi = Lines.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Lines[mid].Time <= seconds) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }
}

public static class LrcParser
{
    static readonly Regex Stamp = new(@"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    static readonly Regex WordStamp = new(@"<\d{1,3}:\d{2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);
    static readonly Regex Meta = new(@"^\[(ti|ar|al|au|by|re|ve|length|id|la|language|hash|total|sign|qq|offset|tool|kana|lang):[^\]]*\]\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Offset = new(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public static bool LooksSynced(string? text) => !string.IsNullOrEmpty(text) && Stamp.Matches(text).Count >= 3;

    public static ParsedLyrics Parse(string? text, string source)
    {
        text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim('﻿');
        if (!LooksSynced(text))
        {
            var plain = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !Meta.IsMatch(l)).ToList();
            return new ParsedLyrics
            {
                Synced = false, Source = source, RawPlain = string.Join("\n", plain),
                Lines = plain.Select(l => new LyricLine { Time = -1, Text = l }).ToList(),
            };
        }

        double offset = 0;
        var om = Offset.Match(text);
        if (om.Success) offset = int.Parse(om.Groups[1].Value, CultureInfo.InvariantCulture) / 1000.0;

        var lines = new List<LyricLine>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || Meta.IsMatch(line)) continue;
            var stamps = Stamp.Matches(line);
            if (stamps.Count == 0) continue;
            var body = WordStamp.Replace(Stamp.Replace(line, ""), "").Trim();
            foreach (Match m in stamps)
            {
                double t = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                         + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)
                         + (m.Groups[3].Success ? FractionOf(m.Groups[3].Value) : 0);
                lines.Add(new LyricLine { Time = Math.Max(0, t - offset), Text = body });
            }
        }

        // stable sort, then fold same-timestamp duplicates (original + translation) into one line
        var sorted = lines.Select((l, i) => (l, i)).OrderBy(x => x.l.Time).ThenBy(x => x.i).Select(x => x.l).ToList();
        var merged = new List<LyricLine>();
        foreach (var l in sorted)
        {
            if (merged.Count > 0 && Math.Abs(merged[^1].Time - l.Time) < 0.005)
            {
                if (l.Text.Length > 0 && l.Text != merged[^1].Text)
                    merged[^1].Translation = merged[^1].Translation.Length == 0 ? l.Text : merged[^1].Translation + " " + l.Text;
            }
            else merged.Add(l);
        }
        return new ParsedLyrics { Synced = true, Source = source, Lines = merged, RawSynced = text };
    }

    static double FractionOf(string digits) => int.Parse(digits, CultureInfo.InvariantCulture) / Math.Pow(10, digits.Length);

    public static string Strip(string lrc)
    {
        var lines = new List<string>();
        foreach (var raw in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            var l = raw.Trim();
            if (l.Length == 0 || Meta.IsMatch(l)) continue;
            l = WordStamp.Replace(Stamp.Replace(l, ""), "").Trim();
            if (l.Length > 0) lines.Add(l);
        }
        return string.Join("\n", lines);
    }

    /// <summary>Some sources omit the space after the timestamp; Musicolet then fails to treat the USLT as synced.</summary>
    public static string NormalizeSpacing(string lrc) => Regex.Replace(lrc, @"^(\[\d{1,2}:\d{2}(?:[.:]\d{1,3})?\])(?=\S)", "$1 ", RegexOptions.Multiline);

    public static string FormatStamp(double seconds)
    {
        int cs = (int)Math.Round(Math.Max(0, seconds) * 100);
        return $"[{cs / 6000:00}:{cs / 100 % 60:00}.{cs % 100:00}]";
    }
}

/// <summary>What we ask the lyric sources about.</summary>
public sealed record LyricQuery(string Title, string Artist, string Album, double Seconds, bool IsKpop)
{
    public static LyricQuery Of(Track t) => new(t.Title, t.Artist, t.Album, t.Seconds, t.IsKpop);
}

public sealed class LyricCandidate
{
    public string Source { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public double Seconds { get; init; }
    public string? Synced { get; init; }
    public string? Plain { get; init; }
    public double Score { get; set; }
    public bool Exact { get; init; }

    public bool HasSynced => !string.IsNullOrWhiteSpace(Synced);
    public string Label => $"{Source} · {(HasSynced ? "synced" : "plain")} · {Artist} - {Title}";
}

public static class Similarity
{
    /// <summary>0..1 how alike two titles/artists are, tolerant to punctuation, brackets and any script (bigram Dice).</summary>
    public static double Of(string? a, string? b)
    {
        a = Strip(a); b = Strip(b);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        if (a.Contains(b) || b.Contains(a)) return 0.88;
        if (a.Length < 2 || b.Length < 2) return 0;
        var ba = Bigrams(a); var bb = Bigrams(b);
        int hit = 0;
        foreach (var kv in ba) if (bb.TryGetValue(kv.Key, out var n)) hit += Math.Min(kv.Value, n);
        return 2.0 * hit / (a.Length - 1 + b.Length - 1);
    }

    static string Strip(string? s)
    {
        if (s == null) return "";
        s = Regex.Replace(s, @"\(.*?\)|\[.*?\]|\{.*?\}", "");           // (feat. x), [Remastered]
        return Fmt.Norm(s);
    }

    static Dictionary<string, int> Bigrams(string s)
    {
        var d = new Dictionary<string, int>();
        for (int i = 0; i < s.Length - 1; i++)
        {
            var k = s.Substring(i, 2);
            d[k] = d.GetValueOrDefault(k) + 1;
        }
        return d;
    }
}

public static class LyricRules
{
    static readonly Regex Hangul = new(@"[가-힣ᄀ-ᇿ㄰-㆏]", RegexOptions.Compiled);
    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
        { "the", "and", "you", "i", "to", "a", "of", "in", "me", "my", "it", "is", "that", "on", "your", "we", "for", "with", "be", "but", "this", "so", "all", "just", "like", "love", "baby", "oh", "do", "know" };

    public static bool HasHangul(string? s) => !string.IsNullOrEmpty(s) && Hangul.IsMatch(s);

    static bool LooksEnglish(string text)
    {
        var words = Regex.Matches(text.ToLowerInvariant(), @"[a-z']+").Select(m => m.Value).ToList();
        if (words.Count == 0) return false;
        int hits = words.Count(Stop.Contains);
        return words.Count < 20 ? hits >= 3 : hits >= 8 && hits / (double)words.Count >= 0.09;
    }

    /// <summary>Ported from embed_lyrics.pyw: K-pop sources are often romanized junk, so demand Hangul or real English.</summary>
    public static bool Accept(string? text, bool isKpop)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (isKpop) return HasHangul(text) || LooksEnglish(text);
        return text.Count(char.IsLetter) >= 3;
    }

    static readonly Regex TitleParen = new(@"^(?<main>.*?)\s*[\(\[](?<alt>[^\)\]]+)[\)\]]\s*$", RegexOptions.Compiled);
    static readonly string[] ModifierPrefixes = ["feat", "ft.", "ft ", "with ", "remix", "inst", "instrumental", "ver", "version", "live", "acoustic", "remaster", "prod", "cover", "mix", "edit"];

    /// <summary>"계절이 너로 시작돼 (You're my season)" -> full title, Korean part, English part. Modifiers like (feat. X) only add the bare title.</summary>
    public static List<string> TitleVariants(string title)
    {
        var list = new List<string> { title };
        var m = TitleParen.Match(title);
        if (m.Success)
        {
            var main = m.Groups["main"].Value.Trim();
            var alt = m.Groups["alt"].Value.Trim();
            bool modifier = ModifierPrefixes.Any(p => alt.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (main.Length > 0) list.Add(main);
            if (alt.Length > 0 && !modifier) list.Add(alt);
        }
        return list.Where(s => s.Length > 0).Distinct().ToList();
    }
}
