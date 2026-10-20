using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace xPlayer;

public interface ILyricsSource
{
    string Name { get; }
    Task<List<LyricCandidate>> FindAsync(LyricQuery q, CancellationToken ct);
}

public static class Http
{
    public static readonly HttpClient Client = Create();

    static HttpClient Create()
    {
        var h = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("xPlayer/1.0 (https://github.com/maiconerick/xplayer)");
        return c;
    }

    /// <summary>Request with retries on 429/5xx and network errors (ported from embed_lyrics.pyw). Real 4xx are returned as-is.</summary>
    public static async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct, int retries = 2)
    {
        Exception? last = null;
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                var resp = await Client.SendAsync(make(), HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                {
                    last = new HttpRequestException($"HTTP {(int)resp.StatusCode}");
                    resp.Dispose();
                }
                else return resp;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { last = e; }
            if (attempt < retries) await Task.Delay(TimeSpan.FromSeconds(0.6 * (1 << attempt)), ct).ConfigureAwait(false);
        }
        throw last ?? new HttpRequestException("request failed");
    }

    public static async Task<string?> GetStringAsync(string url, CancellationToken ct, Action<HttpRequestMessage>? tweak = null, int retries = 2)
    {
        using var resp = await SendAsync(() => { var r = new HttpRequestMessage(HttpMethod.Get, url); tweak?.Invoke(r); return r; }, ct, retries).ConfigureAwait(false);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
    }

    public static async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct, Action<HttpRequestMessage>? tweak = null, int retries = 2)
    {
        var s = await GetStringAsync(url, ct, tweak, retries).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(s) ? null : JsonNode.Parse(s);
    }

    public static string Query(params (string K, object? V)[] ps) =>
        string.Join("&", ps.Where(p => p.V != null).Select(p => $"{Uri.EscapeDataString(p.K)}={Uri.EscapeDataString(Convert.ToString(p.V, System.Globalization.CultureInfo.InvariantCulture) ?? "")}"));
}

static class Js
{
    public static string S(this JsonNode? n, string key) => n?[key]?.ToString() ?? "";
    public static JsonNode? At(this JsonNode? n, params string[] path)
    {
        foreach (var p in path) { n = n?[p]; if (n == null) return null; }
        return n;
    }
}

// ------------------------------------------------------------------------------------------ lrclib

public sealed class LrclibSource : ILyricsSource
{
    public string Name => "lrclib";
    const string Base = "https://lrclib.net/api";

    public async Task<List<LyricCandidate>> FindAsync(LyricQuery q, CancellationToken ct)
    {
        var found = new List<LyricCandidate>();
        if (string.IsNullOrWhiteSpace(q.Title)) return found;

        // 1) exact match (artist + title + album + duration): the most trustworthy hit
        if (!string.IsNullOrWhiteSpace(q.Artist))
        {
            var url = $"{Base}/get?" + Http.Query(("artist_name", q.Artist), ("track_name", q.Title), ("album_name", q.Album), ("duration", (int)Math.Round(q.Seconds)));
            try
            {
                var node = await Http.GetJsonAsync(url, ct).ConfigureAwait(false);
                var c = From(node, exact: true);
                if (c != null) found.Add(c);
            }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Error("lrclib get", e); }
        }
        if (found.Any(c => c.HasSynced)) return found;

        // 2) search fallbacks
        foreach (var url in new[]
        {
            $"{Base}/search?" + Http.Query(("artist_name", q.Artist), ("track_name", q.Title)),
            $"{Base}/search?" + Http.Query(("q", $"{q.Artist} {q.Title}".Trim())),
        })
        {
            try
            {
                var arr = await Http.GetJsonAsync(url, ct).ConfigureAwait(false) as JsonArray;
                if (arr == null) continue;
                foreach (var item in arr.Take(6))
                {
                    var c = From(item, exact: false);
                    if (c != null && !found.Any(f => f.Synced == c.Synced && f.Plain == c.Plain)) found.Add(c);
                }
                if (found.Count > 0) break;
            }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Error("lrclib search", e); }
        }
        return found;
    }

    LyricCandidate? From(JsonNode? n, bool exact)
    {
        if (n == null) return null;
        var synced = n.S("syncedLyrics");
        var plain = n.S("plainLyrics");
        if (synced.Length == 0 && plain.Length == 0) return null;
        double.TryParse(n.S("duration"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dur);
        return new LyricCandidate
        {
            Source = Name, Title = n.S("trackName"), Artist = n.S("artistName"), Album = n.S("albumName"), Seconds = dur,
            Synced = synced.Length > 0 ? synced : null, Plain = plain.Length > 0 ? plain : null, Exact = exact,
        };
    }
}

// ------------------------------------------------------------------------------------------ NetEase (ported from eslyric netease.js)

public sealed class NeteaseSource : ILyricsSource
{
    public string Name => "NetEase";

    static readonly byte[] Key = Encoding.Latin1.GetBytes("rFgB&h#%2?^eDg:Q");
    const string Token = "bf8bfeabb1aa84f9c8c3906c04a04fb864322804c83f5d607e91a04eae463c9436bd1a17ec353cf780b396507a3f7464e8a60f4bbc019437993166e004087dd32d1490298caf655c2353e58daa0bc13cc7d5c198250968580b12c1b8817e3f5c807e650dd04abd3fb8130b7ae43fcc5b";

    static string Eparams(string method, string url, object parameters)
    {
        var json = JsonSerializer.Serialize(new { method, url, @params = parameters });
        using var aes = Aes.Create();
        aes.Key = Key; aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.PKCS7;
        var enc = aes.CreateEncryptor().TransformFinalBlock(Encoding.UTF8.GetBytes(json), 0, Encoding.UTF8.GetByteCount(json));
        return Convert.ToHexString(enc);
    }

    static async Task<JsonNode?> CallAsync(string apiUrl, object parameters, CancellationToken ct)
    {
        var body = "eparams=" + Eparams("POST", apiUrl, parameters);
        using var resp = await Http.SendAsync(() =>
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "https://music.163.com/api/linux/forward")
            { Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded") };
            r.Headers.TryAddWithoutValidation("Referer", "https://music.163.com");
            r.Headers.TryAddWithoutValidation("Cookie", "MUSIC_A=" + Token);
            r.Headers.UserAgent.Clear();
            r.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/60.0.3112.90 Safari/537.36");
            return r;
        }, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    // same keyword cleanup as the original script
    static string Keywords(string s)
    {
        s = s.ToLowerInvariant();
        s = Regex.Replace(s, @"'|·|\$|&|–", "");
        s = Regex.Replace(s, @"\(.*?\)|\[.*?\]|\{.*?\}|（.*?", "");
        s = Regex.Replace(s, @"[\-/:-@\[-`{-~]+", "");
        s = Regex.Replace(s, "[\u2014\u2018\u201c\u2026\u3001\u3002\u300a\u300b\u300e\u300f\u3010\u3011\u30fb\uff01\uff08\uff09\uff0c\uff1a\uff1b\uff1f\uff5e\uffe5]+", "");
        return s.Trim();
    }

    public async Task<List<LyricCandidate>> FindAsync(LyricQuery q, CancellationToken ct)
    {
        var found = new List<LyricCandidate>();
        var search = await CallAsync("https://music.163.com/api/search/get",
            new { s = Keywords(q.Title) + " " + Keywords(q.Artist), type = 1, limit = 10, offset = 0 }, ct).ConfigureAwait(false);
        var songs = search.At("result", "songs") as JsonArray;
        if (songs == null) return found;

        foreach (var song in songs.Take(4))
        {
            ct.ThrowIfCancellationRequested();
            var id = song.S("id");
            if (id.Length == 0) continue;
            var lyric = await CallAsync("https://music.163.com/api/song/lyric?lv=-1&kv=-1&tv=-1", new { id = long.Parse(id) }, ct).ConfigureAwait(false);
            if (lyric == null) continue;
            var lrc = lyric.At("lrc", "lyric")?.ToString() ?? "";
            if (lyric.At("lrc", "version")?.ToString() == "1" && lrc.Length < 20) continue;
            var tr = lyric.At("tlyric", "lyric")?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(lrc)) continue;
            double.TryParse(song.S("duration"), out var ms);
            var artist = (song["artists"] as JsonArray)?.FirstOrDefault()?.S("name") ?? "";
            var text = LrcParser.LooksSynced(lrc) && !string.IsNullOrWhiteSpace(tr) ? lrc + "\n" + tr : lrc;
            found.Add(new LyricCandidate
            {
                Source = Name, Title = song.S("name"), Artist = artist, Album = song.At("album")?.S("name") ?? "", Seconds = ms / 1000,
                Synced = LrcParser.LooksSynced(text) ? text : null, Plain = LrcParser.LooksSynced(text) ? null : text,
            });
        }
        return found;
    }
}

// ------------------------------------------------------------------------------------------ Kugou (krc, ported from kugou.js + krc.js)

public sealed class KugouSource : ILyricsSource
{
    public string Name => "Kugou";
    static readonly byte[] Xor = [0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69];

    public async Task<List<LyricCandidate>> FindAsync(LyricQuery q, CancellationToken ct)
    {
        var found = new List<LyricCandidate>();
        if (q.Seconds <= 0) return found;       // the search needs the duration, like the original script
        var url = "http://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword=" + Uri.EscapeDataString(q.Artist + "-" + q.Title)
                  + "&duration=" + (long)Math.Round(q.Seconds) * 1000 + "&hash=";
        var node = await Http.GetJsonAsync(url, ct).ConfigureAwait(false);
        var cands = node?["candidates"] as JsonArray;
        if (cands == null) return found;

        foreach (var c in cands.Take(3))
        {
            ct.ThrowIfCancellationRequested();
            var id = c.S("id"); var key = c.S("accesskey");
            if (id.Length == 0 || key.Length == 0) continue;
            var dl = await Http.GetJsonAsync($"http://lyrics.kugou.com/download?ver=1&client=pc&id={id}&accesskey={key}&fmt=krc&charset=utf8", ct).ConfigureAwait(false);
            var content = dl.S("content");
            if (content.Length == 0) continue;
            var lrc = KrcToLrc(Convert.FromBase64String(content));
            if (string.IsNullOrWhiteSpace(lrc)) continue;
            found.Add(new LyricCandidate { Source = Name, Title = c.S("song"), Artist = c.S("singer"), Synced = lrc, Seconds = q.Seconds });
        }
        return found;
    }

    static string? KrcToLrc(byte[] data)
    {
        if (data.Length < 5 || data[0] != 'k' || data[1] != 'r' || data[2] != 'c' || data[3] != '1') return null;
        var x = new byte[data.Length - 4];
        for (int i = 0; i < x.Length; i++) x[i] = (byte)(data[i + 4] ^ Xor[i % Xor.Length]);
        string text;
        try
        {
            using var z = new ZLibStream(new MemoryStream(x), CompressionMode.Decompress);
            using var sr = new StreamReader(z, new UTF8Encoding(false));
            text = sr.ReadToEnd();
        }
        catch { return null; }

        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var m = Regex.Match(raw, @"^\[(\d+),(\d+)\](.*)$");
            if (!m.Success) continue;                                   // meta lines ([ti:], [language:] ...) are not needed
            var start = int.Parse(m.Groups[1].Value) / 1000.0;
            var words = Regex.Replace(m.Groups[3].Value, @"<\d+,\d+,\d+>", "");
            sb.Append(LrcParser.FormatStamp(start)).Append(' ').Append(words.Trim()).Append('\n');
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}

// ------------------------------------------------------------------------------------------ QQ Music (LRC endpoints of qqmusic.js)

public sealed class QQMusicSource : ILyricsSource
{
    public string Name => "QQ Music";

    public async Task<List<LyricCandidate>> FindAsync(LyricQuery q, CancellationToken ct)
    {
        var found = new List<LyricCandidate>();
        void Ref(HttpRequestMessage r) => r.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com");
        var url = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp?" + Http.Query(("format", "json"), ("n", 8), ("p", 0), ("w", q.Title + " " + q.Artist), ("cr", 1), ("g_tk", 5381));
        var node = await Http.GetJsonAsync(url, ct, Ref, retries: 0).ConfigureAwait(false);   // QQ gates this API now: fail fast instead of slowing the whole search
        var list = node.At("data", "song", "list") as JsonArray;
        if (list == null) return found;

        foreach (var s in list.Take(4))
        {
            ct.ThrowIfCancellationRequested();
            var mid = s.S("songmid");
            if (mid.Length == 0) continue;
            var lurl = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?" + Http.Query(
                ("songmid", mid), ("pcachetime", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("g_tk", 5381), ("loginUin", 0), ("hostUin", 0),
                ("inCharset", "utf8"), ("outCharset", "utf-8"), ("notice", 0), ("platform", "yqq"), ("needNewCode", 1), ("format", "json"));
            var l = await Http.GetJsonAsync(lurl, ct, Ref, retries: 0).ConfigureAwait(false);
            var b64 = l.S("lyric");
            if (b64.Length == 0) continue;
            var text = WebUtility.HtmlDecode(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            var tb64 = l.S("trans");
            if (tb64.Length > 0) text += "\n" + WebUtility.HtmlDecode(Encoding.UTF8.GetString(Convert.FromBase64String(tb64)));
            if (string.IsNullOrWhiteSpace(text)) continue;
            var singer = (s["singer"] as JsonArray)?.FirstOrDefault()?.S("name") ?? "";
            found.Add(new LyricCandidate
            {
                Source = Name, Title = s.S("songname"), Artist = singer, Album = s.S("albumname"),
                Synced = LrcParser.LooksSynced(text) ? text : null, Plain = LrcParser.LooksSynced(text) ? null : text,
            });
        }
        return found;
    }
}

// ------------------------------------------------------------------------------------------ Musixmatch (desktop API, ported from musixmatch.js)

public sealed class MusixmatchSource : ILyricsSource
{
    public string Name => "Musixmatch";
    static string? token;
    static DateTime tokenAt;
    const string Base = "https://apic-desktop.musixmatch.com/ws/1.1/";

    static void Cookie(HttpRequestMessage r) => r.Headers.TryAddWithoutValidation("Cookie", "AWSELBCORS=0; AWSELB=0");

    static async Task<string?> TokenAsync(CancellationToken ct)
    {
        if (token != null && DateTime.UtcNow - tokenAt < TimeSpan.FromHours(6)) return token;
        var node = await Http.GetJsonAsync(Base + "token.get?" + Http.Query(("user_language", "en"), ("app_id", "web-desktop-app-v1.0"), ("t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())), ct, Cookie).ConfigureAwait(false);
        var t = node.At("message", "body")?.S("user_token") ?? "";
        if (t.Length == 0 || t.StartsWith("UpgradeOnly")) return null;
        tokenAt = DateTime.UtcNow;
        return token = t;
    }

    public async Task<List<LyricCandidate>> FindAsync(LyricQuery q, CancellationToken ct)
    {
        var found = new List<LyricCandidate>();
        var tk = await TokenAsync(ct).ConfigureAwait(false);
        if (tk == null) return found;

        var search = await Http.GetJsonAsync(Base + "track.search?" + Http.Query(
            ("user_language", "en"), ("app_id", "web-desktop-app-v1.0"), ("format", "json"), ("subtitle_format", "lrc"), ("q_track", q.Title),
            ("q_artist", q.Artist), ("q_album", q.Album), ("f_has_lyrics", 1), ("usertoken", tk), ("t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())), ct, Cookie).ConfigureAwait(false);
        var tracks = search.At("message", "body", "track_list") as JsonArray;
        if (tracks == null) return found;

        foreach (var item in tracks.Take(3))
        {
            ct.ThrowIfCancellationRequested();
            var t = item?["track"];
            var id = t.S("commontrack_id");
            if (id == "" || id == "0") continue;
            bool hasSubs = t.S("has_subtitles") == "1";
            string? text = null;
            if (hasSubs)
            {
                var n = await Http.GetJsonAsync(Base + "track.subtitle.get?" + Http.Query(("user_language", "en"), ("app_id", "web-desktop-app-v1.0"), ("subtitle_format", "lrc"), ("commontrack_id", id), ("usertoken", tk)), ct, Cookie).ConfigureAwait(false);
                text = n.At("message", "body", "subtitle")?.S("subtitle_body");
            }
            if (string.IsNullOrWhiteSpace(text) && t.S("has_lyrics") == "1")
            {
                var n = await Http.GetJsonAsync(Base + "track.lyrics.get?" + Http.Query(("user_language", "en"), ("app_id", "web-desktop-app-v1.0"), ("commontrack_id", id), ("usertoken", tk)), ct, Cookie).ConfigureAwait(false);
                text = n.At("message", "body", "lyrics")?.S("lyrics_body");
                if (text != null) text = Regex.Replace(text, @"\n\*{3,}.*$", "", RegexOptions.Singleline).Trim();
            }
            if (string.IsNullOrWhiteSpace(text)) continue;
            double.TryParse(t.S("track_length"), out var len);
            found.Add(new LyricCandidate
            {
                Source = Name, Title = t.S("track_name"), Artist = t.S("artist_name"), Album = t.S("album_name"), Seconds = len,
                Synced = LrcParser.LooksSynced(text) ? text : null, Plain = LrcParser.LooksSynced(text) ? null : text,
            });
        }
        return found;
    }
}

// ------------------------------------------------------------------------------------------ Melon (K-pop plain lyrics, ported from embed_lyrics.pyw)

public sealed class MelonSource : ILyricsSource
{
    public string Name => "Melon";

    public async Task<List<LyricCandidate>> FindAsync(LyricQuery q, CancellationToken ct)
    {
        var found = new List<LyricCandidate>();
        if (!q.IsKpop) return found;           // Korean-only site: pointless for everything else
        var page = await Http.GetStringAsync("https://www.melon.com/search/song/index.htm?q=" + Uri.EscapeDataString($"{q.Artist} {q.Title}".Trim()), ct).ConfigureAwait(false);
        if (page == null) return found;
        var tbody = Regex.Match(page, "<tbody>(.*?)</tbody>", RegexOptions.Singleline);
        if (!tbody.Success) return found;

        foreach (Match row in Regex.Matches(tbody.Groups[1].Value, "<tr[^>]*>.*?</tr>", RegexOptions.Singleline).Take(2))
        {
            ct.ThrowIfCancellationRequested();
            var title = Regex.Match(row.Value, "title=\"([^\"]+) 곡 선택\"");
            var id = Regex.Match(row.Value, "name=\"input_check\"[^>]*value=\"(\\d+)\"");
            if (!title.Success || !id.Success) continue;
            var artist = Regex.Match(row.Value, @"goArtistDetail\('\d+'\)[^>]*>([^<]+)</a>");
            var detail = await Http.GetStringAsync("https://www.melon.com/song/detail.htm?songId=" + id.Groups[1].Value, ct).ConfigureAwait(false);
            if (detail == null) continue;
            var lyric = Regex.Match(detail, "<div class=\"lyric\"\\s+id=\"d_video_summary\">(.*?)</div>", RegexOptions.Singleline);
            if (!lyric.Success) continue;
            var body = Regex.Replace(lyric.Groups[1].Value, "<!--.*?-->", "", RegexOptions.Singleline);
            body = Regex.Replace(body, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            body = Regex.Replace(body, "<[^>]+>", "");
            var lines = WebUtility.HtmlDecode(body).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0) continue;
            found.Add(new LyricCandidate
            {
                Source = Name, Title = WebUtility.HtmlDecode(title.Groups[1].Value), Artist = artist.Success ? WebUtility.HtmlDecode(artist.Groups[1].Value.Trim()) : "",
                Plain = string.Join("\n", lines),
            });
        }
        return found;
    }
}
