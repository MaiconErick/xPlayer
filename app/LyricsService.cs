using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace xPlayer;

public sealed class LyricsResult
{
    public ParsedLyrics? Lyrics { get; set; }
    public List<LyricCandidate> Candidates { get; } = [];
    public string Origin { get; set; } = "";
    public string? Error { get; set; }
}

/// <summary>Finds lyrics: next to the file, embedded in its tags, in the local cache, then online (all ported sources in parallel).</summary>
public sealed class LyricsService
{
    public IReadOnlyList<ILyricsSource> Sources => sources;

    readonly ILyricsSource[] sources =
        [new LrclibSource(), new NeteaseSource(), new KugouSource(), new QQMusicSource(), new MusixmatchSource(), new MelonSource()];

    // tie-break when two candidates score the same: earlier = more trusted
    static readonly string[] Priority = ["lrclib", "NetEase", "Kugou", "QQ Music", "Musixmatch", "Melon"];

    static string CacheDir
    {
        get { var d = Path.Combine(AppSettings.Dir, "lyrics"); Directory.CreateDirectory(d); return d; }
    }

    static string CacheKey(Track t)
    {
        var raw = $"{Fmt.Norm(t.Artist)}|{Fmt.Norm(t.Title)}|{(int)Math.Round(t.Seconds / 4)}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)))[..20];
    }

    // ---------------------------------------------------------------------------- local

    public LyricsResult? LoadLocal(Track t)
    {
        ParsedLyrics? plainOnly = null;
        string plainOrigin = "";

        foreach (var ext in new[] { ".lrc", ".txt" })
        {
            var p = Path.ChangeExtension(t.Path, ext);
            if (!File.Exists(p)) continue;
            try
            {
                var parsed = LrcParser.Parse(ReadText(p), "file");
                if (parsed.Lines.Count == 0) continue;
                if (parsed.Synced) return new LyricsResult { Lyrics = parsed, Origin = "file " + Path.GetFileName(p) };
                plainOnly ??= parsed; plainOrigin = "file " + Path.GetFileName(p);
            }
            catch (Exception e) { Log.Error("sidecar lyrics", e); }
        }

        try
        {
            var (synced, plain) = ReadEmbedded(t.Path);
            if (synced != null) return new LyricsResult { Lyrics = LrcParser.Parse(synced, "embedded"), Origin = "embedded in file" };
            if (plainOnly == null && plain != null) { plainOnly = LrcParser.Parse(plain, "embedded"); plainOrigin = "embedded in file"; }
        }
        catch (Exception e) { Log.Error("embedded lyrics", e); }

        var cached = LoadCache(t);
        if (cached != null)
        {
            if (cached.Synced) return new LyricsResult { Lyrics = cached, Origin = "cache · " + cached.Source };
            if (plainOnly == null) { plainOnly = cached; plainOrigin = "cache · " + cached.Source; }
        }
        return plainOnly == null ? null : new LyricsResult { Lyrics = plainOnly, Origin = plainOrigin };
    }

    static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch { return Encoding.GetEncoding(1252).GetString(bytes); }
    }

    /// <summary>USLT/SYLT/LYRICS tags. embed_lyrics.pyw stores synced LRC in the USLT with an empty description and the plain text in "plain".</summary>
    static (string? Synced, string? Plain) ReadEmbedded(string path)
    {
        using var f = TagLib.File.Create(path);
        string? synced = null, plain = null;
        if (f.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
        {
            foreach (var fr in id3.GetFrames<TagLib.Id3v2.UnsynchronisedLyricsFrame>())
            {
                var text = fr.Text;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (LrcParser.LooksSynced(text)) synced ??= text;
                else plain ??= text;
            }
            if (synced == null)
            {
                foreach (var sy in id3.GetFrames<TagLib.Id3v2.SynchronisedLyricsFrame>())
                {
                    var sb = new StringBuilder();
                    foreach (var line in sy.Text.OrderBy(x => x.Time))
                        sb.Append(LrcParser.FormatStamp(line.Time / 1000.0)).Append(' ').Append(line.Text.Trim()).Append('\n');
                    if (sb.Length > 0) { synced = sb.ToString(); break; }
                }
            }
        }
        var generic = f.Tag.Lyrics;
        if (!string.IsNullOrWhiteSpace(generic))
        {
            if (LrcParser.LooksSynced(generic)) synced ??= generic;
            else plain ??= generic;
        }
        return (synced, plain);
    }

    // ---------------------------------------------------------------------------- cache

    sealed record CacheEntry(string Source, string? Synced, string? Plain, string Title, string Artist);

    ParsedLyrics? LoadCache(Track t)
    {
        try
        {
            var p = Path.Combine(CacheDir, CacheKey(t) + ".json");
            if (!File.Exists(p)) return null;
            var e = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(p));
            var text = CleanCredits(e?.Synced ?? e?.Plain);
            return text == null ? null : LrcParser.Parse(text, e!.Source);
        }
        catch { return null; }
    }

    public static void Remember(Track t, LyricCandidate c)
    {
        try
        {
            var entry = new CacheEntry(c.Source, c.Synced, c.Plain, c.Title, c.Artist);
            File.WriteAllText(Path.Combine(CacheDir, CacheKey(t) + ".json"), JsonSerializer.Serialize(entry));
            var none = Path.Combine(CacheDir, CacheKey(t) + ".none");
            if (File.Exists(none)) File.Delete(none);
        }
        catch (Exception e) { Log.Error("lyrics cache write", e); }
    }

    bool RecentlyMissed(Track t)
    {
        var p = Path.Combine(CacheDir, CacheKey(t) + ".none");
        return File.Exists(p) && DateTime.UtcNow - File.GetLastWriteTimeUtc(p) < TimeSpan.FromHours(24);
    }

    void MarkMissed(Track t)
    {
        try { File.WriteAllText(Path.Combine(CacheDir, CacheKey(t) + ".none"), ""); } catch { /* ignore */ }
    }

    // ---------------------------------------------------------------------------- online

    public async Task<LyricsResult> FetchOnlineAsync(Track t, CancellationToken ct, IProgress<string>? progress = null, bool force = false)
    {
        var result = new LyricsResult();
        if (!force && RecentlyMissed(t)) { result.Error = "No lyrics found (checked recently — press Search again to retry)"; return result; }

        var variants = LyricRules.TitleVariants(string.IsNullOrWhiteSpace(t.Title) ? Path.GetFileNameWithoutExtension(t.Path) : t.Title);
        var all = new List<LyricCandidate>();
        for (int i = 0; i < variants.Count && !ct.IsCancellationRequested; i++)
        {
            var q = new LyricQuery(variants[i], t.Artist, t.Album, t.Seconds, t.IsKpop);
            progress?.Report(variants.Count > 1 ? $"Searching “{variants[i]}” ({i + 1}/{variants.Count})…" : "Searching lyric sources…");

            var tasks = sources.Select(s => RunSource(s, q, ct)).ToList();
            foreach (var done in await Task.WhenAll(tasks).ConfigureAwait(false))
                all.AddRange(done.Select(c => new LyricCandidate
                {
                    Source = c.Source, Title = c.Title, Artist = c.Artist, Album = c.Album, Seconds = c.Seconds, Exact = c.Exact,
                    Synced = CleanCredits(c.Synced), Plain = CleanCredits(c.Plain),
                }));

            foreach (var c in all) Score(c, q, t);
            var good = all.Where(c => Acceptable(c, t)).ToList();
            if (good.Any(c => c.HasSynced)) break;
            if (i > 0 && good.Count > 0) break;
        }
        ct.ThrowIfCancellationRequested();

        var seen = new HashSet<string>();
        var accepted = all.Where(c => Acceptable(c, t)).Where(c => seen.Add((c.Synced ?? c.Plain ?? "").Trim()))
            .OrderByDescending(c => c.HasSynced).ThenByDescending(c => c.Score)
            .ThenBy(c => Array.IndexOf(Priority, c.Source)).ToList();
        result.Candidates.AddRange(accepted);

        var best = PickBest(accepted, t);
        if (best == null) { MarkMissed(t); result.Error = "No lyrics found on any source"; return result; }
        result.Lyrics = Parse(best);
        result.Origin = best.Source;
        Remember(t, best);
        return result;
    }

    public static ParsedLyrics Parse(LyricCandidate c) => LrcParser.Parse(c.Synced ?? c.Plain, c.Source);

    static LyricCandidate? PickBest(List<LyricCandidate> accepted, Track t)
    {
        var synced = accepted.FirstOrDefault(c => c.HasSynced);
        if (synced != null) return synced;
        var plains = accepted.Where(c => !string.IsNullOrWhiteSpace(c.Plain)).ToList();
        if (t.IsKpop)                                           // like embed_lyrics.pyw: prefer a Hangul text for K-pop
            return plains.FirstOrDefault(c => LyricRules.HasHangul(c.Plain)) ?? plains.FirstOrDefault();
        return plains.FirstOrDefault();
    }

    async Task<List<LyricCandidate>> RunSource(ILyricsSource s, LyricQuery q, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(14));
        try { return await s.FindAsync(q, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { Log.Info($"lyrics: {s.Name} timed out"); }
        catch (Exception e) { Log.Error($"lyrics: {s.Name} failed", e); }
        return [];
    }

    static readonly System.Text.RegularExpressions.Regex Credit = new(
        @"^\s*(作词|作詞|作曲|编曲|編曲|词|曲|制作人|制作|监制|演唱|歌手|混音|母带|录音|吉他|贝斯|鼓|和声|策划|出品|OP|SP|Lyrics|Lyricist|Written by|Composer|Composed by|Arranged by|Arranger|Producer|Produced by|Music|Words)\s*[:：]",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex StampOnly = new(@"^(\[[^\]]*\])+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Drops "作词 : ..." / "Composer : ..." credit lines that Chinese sources put at the top of the lyric.</summary>
    internal static string? CleanCredits(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        int head = 0;
        lines = lines.Where((l, i) =>
        {
            var body = StampOnly.Replace(l.Trim(), "").Trim();
            bool credit = i < 14 && Credit.IsMatch(body);
            if (credit) head++;
            return !credit;
        }).ToList();
        return head == 0 ? text : string.Join("\n", lines);
    }

    static void Score(LyricCandidate c, LyricQuery q, Track t)
    {
        if (c.Source == "Melon") { c.Score = 0.6; return; }       // searched by Korean title on a Korean site: titles are not comparable
        double title = Math.Max(Similarity.Of(c.Title, q.Title), Similarity.Of(c.Title, t.Title));
        double artist = string.IsNullOrWhiteSpace(q.Artist) || string.IsNullOrWhiteSpace(c.Artist) ? 0.5 : Similarity.Of(c.Artist, q.Artist);
        double bonus = 0;
        if (c.Seconds > 0 && q.Seconds > 0)
        {
            double diff = Math.Abs(c.Seconds - q.Seconds);
            bonus = diff <= 3 ? 0.1 : diff > 15 ? -0.25 : 0;
        }
        double penalty = 0;
        bool wantsInst = q.Title.Contains("inst", StringComparison.OrdinalIgnoreCase) || q.Title.Contains("karaoke", StringComparison.OrdinalIgnoreCase);
        if (!wantsInst && (c.Title.Contains("inst", StringComparison.OrdinalIgnoreCase) || c.Title.Contains("karaoke", StringComparison.OrdinalIgnoreCase)
            || c.Title.Contains("伴奏"))) penalty = 0.3;
        c.Score = 0.6 * title + 0.3 * artist + bonus + (c.Exact ? 0.15 : 0) - penalty;
    }

    static bool Acceptable(LyricCandidate c, Track t)
    {
        if (c.Score < 0.55 && !c.Exact) return false;
        return LyricRules.Accept(c.Synced ?? c.Plain, t.IsKpop);
    }

    // ---------------------------------------------------------------------------- saving to the file

    /// <summary>Embeds like embed_lyrics.pyw (mp3: USLT with the synced LRC, plus a "plain" USLT). Other formats get a .lrc/.txt next to the file.</summary>
    public static string SaveToFile(Track t, ParsedLyrics lyrics, bool allowEmbed = true)
    {
        var synced = lyrics.Synced ? LrcParser.NormalizeSpacing(lyrics.RawSynced) : null;
        var plain = lyrics.Synced ? LrcParser.Strip(lyrics.RawSynced) : lyrics.RawPlain;

        if (allowEmbed && t.Path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            TagLib.Id3v2.Tag.DefaultVersion = 3;
            TagLib.Id3v2.Tag.ForceDefaultVersion = true;
            using var f = TagLib.File.Create(t.Path);
            var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true);
            id3.RemoveFrames("USLT");
            if (synced != null)
                id3.AddFrame(new TagLib.Id3v2.UnsynchronisedLyricsFrame("", "und", TagLib.StringType.UTF8) { Text = synced });
            if (!string.IsNullOrWhiteSpace(plain))
                id3.AddFrame(new TagLib.Id3v2.UnsynchronisedLyricsFrame("plain", "und", TagLib.StringType.UTF8) { Text = plain });
            f.Save();
            return "Embedded in the file";
        }

        var path = Path.ChangeExtension(t.Path, lyrics.Synced ? ".lrc" : ".txt");
        File.WriteAllText(path, synced ?? plain, new UTF8Encoding(false));
        return "Saved " + Path.GetFileName(path);
    }
}
