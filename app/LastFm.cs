using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace xPlayer;

public class LastFmException(string message) : Exception(message);
public sealed class NetworkException(string message) : LastFmException(message);     // offline / timeout / temporary: retry
public sealed class AuthException(string message) : LastFmException(message);        // bad credentials: retrying is pointless
public sealed class ApiException(int code, string message) : LastFmException($"Last.fm error {code}: {message}") { public int Code { get; } = code; }

public sealed class ScrobbleRow
{
    public long Id { get; set; }
    public long Ts { get; set; }
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string Album { get; set; } = "";
    public string Status { get; set; } = "queued";     // queued -> sent -> confirmed | ignored | failed | unverified
    public string Detail { get; set; } = "";
    public int Attempts { get; set; }
    public int? Plays { get; set; }
    public string Corrected { get; set; } = "";
    public double NextTry { get; set; }

    public DateTime When => DateTimeOffset.FromUnixTimeSeconds(Ts).LocalDateTime;
}

// ------------------------------------------------------------------------------------------ HTTP client

public sealed class LastFmClient(LastFmSettings cfg, Action saveSettings)
{
    static string Md5(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    static readonly HashSet<int> Retryable = [8, 11, 16, 29];
    static readonly HashSet<int> Auth = [4, 9, 10, 14, 15, 26];

    public bool Configured => !string.IsNullOrEmpty(cfg.ApiKey) && !string.IsNullOrEmpty(cfg.ApiSecret) && !string.IsNullOrEmpty(cfg.Username) && !string.IsNullOrEmpty(cfg.PasswordHash);

    string Sign(SortedDictionary<string, string> p) =>
        Md5(string.Concat(p.Where(kv => kv.Key is not "format" and not "callback").Select(kv => kv.Key + kv.Value)) + cfg.ApiSecret);

    async Task<JsonNode> CallAsync(string method, Dictionary<string, string>? args, bool signed, bool auth, bool post, CancellationToken ct, bool retryAuth = true)
    {
        var p = new SortedDictionary<string, string>(args ?? []) { ["method"] = method, ["api_key"] = cfg.ApiKey };
        if (auth) p["sk"] = await SessionKeyAsync(ct).ConfigureAwait(false);
        if (signed || auth) p["api_sig"] = Sign(p);
        p["format"] = "json";

        HttpResponseMessage resp;
        try
        {
            if (post)
                resp = await Http.Client.PostAsync(cfg.ApiUrl, new FormUrlEncodedContent(p), ct).ConfigureAwait(false);
            else
                resp = await Http.Client.GetAsync(cfg.ApiUrl + "?" + string.Join("&", p.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new NetworkException(e.GetType().Name + ": " + e.Message);
        }

        using (resp)
        {
            JsonNode? data;
            try { data = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)); }
            catch (System.Text.Json.JsonException) { throw new NetworkException($"HTTP {(int)resp.StatusCode}: unreadable response"); }
            if (data?["error"] != null)
            {
                int code = (int)data["error"]!;
                var msg = data["message"]?.ToString() ?? "";
                if (code == 9 && auth && retryAuth)
                {
                    cfg.SessionKey = ""; saveSettings();           // expired session: log in again once
                    return await CallAsync(method, args, signed, auth, post, ct, false).ConfigureAwait(false);
                }
                if (Retryable.Contains(code)) throw new NetworkException($"Last.fm {code}: {msg}");
                if (Auth.Contains(code)) throw new AuthException($"Last.fm {code}: {msg}");
                throw new ApiException(code, msg);
            }
            if ((int)resp.StatusCode >= 500) throw new NetworkException($"HTTP {(int)resp.StatusCode}");
            return data ?? throw new NetworkException("empty response");
        }
    }

    async Task<string> SessionKeyAsync(CancellationToken ct)
    {
        if (cfg.SessionKey.Length > 0) return cfg.SessionKey;
        if (!Configured) throw new AuthException("Last.fm account is not set up (Settings → Last.fm)");
        AuthException? last = null;
        foreach (var name in new[] { cfg.Username, cfg.Username.ToLowerInvariant() }.Distinct())      // the docs are ambiguous about username case
        {
            try
            {
                var d = await CallAsync("auth.getMobileSession", new() { ["username"] = name, ["authToken"] = Md5(name + cfg.PasswordHash) }, true, false, true, ct).ConfigureAwait(false);
                cfg.SessionKey = d["session"]!["key"]!.ToString();
                saveSettings();
                return cfg.SessionKey;
            }
            catch (AuthException e) { last = e; }
        }
        throw last!;
    }

    public Task EnsureSessionAsync(CancellationToken ct) => SessionKeyAsync(ct);

    public async Task<List<(bool Accepted, string Message, string Corrected)>> ScrobbleAsync(List<ScrobbleRow> rows, CancellationToken ct)
    {
        var p = new Dictionary<string, string>();
        for (int i = 0; i < rows.Count; i++)
        {
            p[$"artist[{i}]"] = rows[i].Artist; p[$"track[{i}]"] = rows[i].Title; p[$"timestamp[{i}]"] = rows[i].Ts.ToString();
            if (rows[i].Album.Length > 0) p[$"album[{i}]"] = rows[i].Album;
            p[$"chosenByUser[{i}]"] = "1";
        }
        var d = await CallAsync("track.scrobble", p, true, true, true, ct).ConfigureAwait(false);
        var node = d["scrobbles"]?["scrobble"];
        var items = node is JsonArray arr ? arr.ToList() : node != null ? [node] : [];
        if (items.Count != rows.Count) throw new ApiException(0, $"expected {rows.Count} results, got {items.Count}");
        return items.Select(it =>
        {
            int code = int.TryParse(it?["ignoredMessage"]?["code"]?.ToString(), out var c) ? c : 0;
            var tr = it?["track"];
            var corrected = tr?["corrected"]?.ToString() == "1" ? tr?["#text"]?.ToString() ?? "" : "";
            var msg = it?["ignoredMessage"]?["#text"]?.ToString() ?? "";
            return (code == 0, msg.Length > 0 ? msg : $"ignored (code {code})", corrected);
        }).ToList();
    }

    public async Task NowPlayingAsync(Track t, CancellationToken ct)
    {
        var p = new Dictionary<string, string> { ["artist"] = t.Artist, ["track"] = t.Title };
        if (t.Album.Length > 0) p["album"] = t.Album;
        if (t.Seconds > 0) p["duration"] = ((int)t.Seconds).ToString();
        await CallAsync("track.updateNowPlaying", p, true, true, true, ct).ConfigureAwait(false);
    }

    /// <summary>Timestamps of the scrobbles in [from, to]: how we make sure a scrobble really landed in the history.</summary>
    public async Task<HashSet<long>> RecentAsync(long from, long to, CancellationToken ct)
    {
        var d = await CallAsync("user.getRecentTracks", new() { ["user"] = cfg.Username, ["from"] = from.ToString(), ["to"] = to.ToString(), ["limit"] = "200" }, false, false, false, ct).ConfigureAwait(false);
        var node = d["recenttracks"]?["track"];
        var items = node is JsonArray arr ? arr.ToList() : node != null ? [node] : [];
        var set = new HashSet<long>();
        foreach (var it in items)
        {
            if (it?["@attr"]?["nowplaying"] != null) continue;
            if (long.TryParse(it?["date"]?["uts"]?.ToString(), out var uts)) set.Add(uts);
        }
        return set;
    }

    public async Task<int?> TrackPlaysAsync(string artist, string title, CancellationToken ct)
    {
        var d = await CallAsync("track.getInfo", new() { ["artist"] = artist, ["track"] = title, ["username"] = cfg.Username, ["autocorrect"] = "1" }, false, false, false, ct).ConfigureAwait(false);
        return int.TryParse(d["track"]?["userplaycount"]?.ToString(), out var n) ? n : null;
    }

    public async Task<int> AccountPlaysAsync(CancellationToken ct)
    {
        var d = await CallAsync("user.getInfo", new() { ["user"] = cfg.Username }, false, false, false, ct).ConfigureAwait(false);
        return int.TryParse(d["user"]?["playcount"]?.ToString(), out var n) ? n : 0;
    }
}

// ------------------------------------------------------------------------------------------ store

public sealed class ScrobbleStore
{
    readonly SqliteConnection db;
    readonly object gate = new();
    public int Version { get; private set; }

    public ScrobbleStore(string? dir = null)
    {
        db = new SqliteConnection($"Data Source={Path.Combine(dir ?? AppSettings.Dir, "scrobbles.db")}");
        db.Open();
        Exec("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS scrobbles(
              id INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL, artist TEXT NOT NULL, title TEXT NOT NULL, album TEXT DEFAULT '',
              status TEXT NOT NULL DEFAULT 'queued', detail TEXT DEFAULT '', attempts INTEGER DEFAULT 0, plays INTEGER,
              corrected TEXT DEFAULT '', next_try REAL DEFAULT 0, UNIQUE(ts, artist, title));
            """);
    }

    void Exec(string sql, params (string, object?)[] ps)
    {
        lock (gate)
        {
            using var c = db.CreateCommand();
            c.CommandText = sql;
            foreach (var (k, v) in ps) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
            c.ExecuteNonQuery();
        }
    }

    List<ScrobbleRow> Query(string sql, params (string, object?)[] ps)
    {
        lock (gate)
        {
            using var c = db.CreateCommand();
            c.CommandText = sql;
            foreach (var (k, v) in ps) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
            using var r = c.ExecuteReader();
            var list = new List<ScrobbleRow>();
            while (r.Read())
                list.Add(new ScrobbleRow
                {
                    Id = r.GetInt64(0), Ts = r.GetInt64(1), Artist = r.GetString(2), Title = r.GetString(3), Album = r.GetString(4), Status = r.GetString(5),
                    Detail = r.GetString(6), Attempts = r.GetInt32(7), Plays = r.IsDBNull(8) ? null : r.GetInt32(8), Corrected = r.GetString(9), NextTry = r.GetDouble(10),
                });
            return list;
        }
    }

    const string Cols = "id,ts,artist,title,album,status,detail,attempts,plays,corrected,next_try";

    public bool Add(long ts, string artist, string title, string album)
    {
        lock (gate)
        {
            using var c = db.CreateCommand();
            c.CommandText = "INSERT OR IGNORE INTO scrobbles(ts,artist,title,album) VALUES($t,$a,$ti,$al)";
            c.Parameters.AddWithValue("$t", ts); c.Parameters.AddWithValue("$a", artist); c.Parameters.AddWithValue("$ti", title); c.Parameters.AddWithValue("$al", album);
            var added = c.ExecuteNonQuery() > 0;
            if (added) Version++;
            return added;
        }
    }

    public void Update(long id, string? status = null, string? detail = null, int? attempts = null, double? nextTry = null, int? plays = null, string? corrected = null)
    {
        var sets = new List<string>(); var ps = new List<(string, object?)> { ("$id", id) };
        void S(string col, object? v) { sets.Add($"{col}=$p{sets.Count}"); ps.Add(($"$p{sets.Count - 1}", v)); }
        if (status != null) S("status", status);
        if (detail != null) S("detail", detail);
        if (attempts != null) S("attempts", attempts);
        if (nextTry != null) S("next_try", nextTry);
        if (plays != null) S("plays", plays);
        if (corrected != null) S("corrected", corrected);
        if (sets.Count == 0) return;
        Exec($"UPDATE scrobbles SET {string.Join(",", sets)} WHERE id=$id", ps.ToArray());
        Version++;
    }

    public List<ScrobbleRow> Due(string status, double now, int limit = 50) =>
        Query($"SELECT {Cols} FROM scrobbles WHERE status=$s AND next_try<=$n ORDER BY ts LIMIT $l", ("$s", status), ("$n", now), ("$l", limit));

    public ScrobbleRow? Find(long ts, string artist, string title) =>
        Query($"SELECT {Cols} FROM scrobbles WHERE ts=$t AND artist=$a AND title=$ti", ("$t", ts), ("$a", artist), ("$ti", title)).FirstOrDefault();

    public List<ScrobbleRow> Recent(int limit = 500) => Query($"SELECT {Cols} FROM scrobbles ORDER BY ts DESC, id DESC LIMIT $l", ("$l", limit));

    public void Requeue(IEnumerable<long> ids)
    {
        foreach (var id in ids) Update(id, status: "queued", detail: "", attempts: 0, nextTry: 0);
    }

    public void Reverify(IEnumerable<long> ids)
    {
        foreach (var id in ids) Update(id, status: "sent", detail: "re-verifying", attempts: 0, nextTry: 0);
    }

    public (int Today, int TodayOk, int Confirmed, int Pending, int Problems) Counts()
    {
        long midnight = new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds();
        lock (gate)
        {
            using var c = db.CreateCommand();
            c.CommandText = """
                SELECT SUM(ts>=$m), SUM(ts>=$m AND status='confirmed'), SUM(status='confirmed'),
                       SUM(status IN ('queued','sent')), SUM(status IN ('ignored','failed','unverified')) FROM scrobbles
                """;
            c.Parameters.AddWithValue("$m", midnight);
            using var r = c.ExecuteReader();
            r.Read();
            int V(int i) => r.IsDBNull(i) ? 0 : r.GetInt32(i);
            return (V(0), V(1), V(2), V(3), V(4));
        }
    }
}

// ------------------------------------------------------------------------------------------ engine

/// <summary>
/// Watches playback and scrobbles by the Last.fm rule (track longer than 30 s, played for half of it or 4 minutes).
/// Every scrobble is queued on disk, retried while offline, and finally verified against your Last.fm history.
/// </summary>
public sealed class Scrobbler
{
    readonly AppSettings settings;
    readonly LastFmClient client;
    public ScrobbleStore Store { get; }
    readonly CancellationTokenSource stop = new();
    readonly AutoResetEvent wake = new(false);
    static readonly double[] VerifyDelays = [4, 8, 15, 30, 60, 120, 240];

    public bool Online { get; private set; }
    public string Auth { get; private set; } = "unknown";            // unknown | ok | error | off
    public string AuthDetail { get; private set; } = "";
    public int? AccountPlays { get; private set; }
    public string LastEvent { get; private set; } = "";
    double playsAt;

    // current play
    Track? track;
    long startedTs;
    double listened;
    bool counted;
    double duration;

    public Scrobbler(AppSettings s, string? dataDir = null, Action? save = null)
    {
        settings = s;
        Store = new ScrobbleStore(dataDir);
        client = new LastFmClient(s.LastFm, save ?? s.Save);
        new Thread(Loop) { IsBackground = true, Name = "scrobbler" }.Start();
    }

    public bool Configured => client.Configured && settings.LastFm.Enabled;
    public void Stop() { stop.Cancel(); wake.Set(); }
    public void Wake() => wake.Set();
    public void SettingsChanged() { Auth = "unknown"; AuthDetail = ""; AccountPlays = null; playsAt = 0; wake.Set(); }

    void Note(string text) { LastEvent = text; Log.Info("scrobbler: " + text); }

    /// <summary>Progress of the track that is playing: whether it was already queued, and seconds still to listen.</summary>
    public (bool Counted, double Remaining, bool TooShort) Progress =>
        (counted, Math.Max(0, ThresholdFor(duration) - listened), duration > 0 && duration < 30);

    /// <summary>Seconds of listening needed before a track of this length is scrobbled (setting: fixed seconds, percent, or the Last.fm standard).</summary>
    public double ThresholdFor(double trackSeconds)
    {
        double value = Math.Max(1, settings.ScrobbleValue);
        return settings.ScrobbleMode switch
        {
            "seconds" => Math.Min(value, Math.Max(1, trackSeconds)),
            "percent" => Math.Max(1, trackSeconds * Math.Clamp(value, 1, 100) / 100),
            _ => Math.Min(trackSeconds / 2, 240),
        };
    }

    public ScrobbleRow? CurrentRow() => counted && track != null ? Store.Find(startedTs, track.Artist, track.Title) : null;

    // ---- playback hooks (UI thread)

    public void TrackStarted(Track t, double durationSeconds)
    {
        track = t;
        startedTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        listened = 0;
        counted = false;
        duration = durationSeconds > 0 ? durationSeconds : t.Seconds;
        if (Configured && !string.IsNullOrEmpty(t.Artist))
            _ = Task.Run(async () =>
            {
                try { await client.NowPlayingAsync(t, stop.Token).ConfigureAwait(false); }
                catch (Exception e) when (e is not OperationCanceledException) { Log.Info("now playing: " + e.Message); }
            });
    }

    /// <summary>Called every tick while a track is open; <paramref name="dt"/> is the wall-clock time since the previous call.</summary>
    public void Tick(bool playing, double dt, double durationSeconds)
    {
        if (track == null || counted || !playing) return;
        if (durationSeconds > 0) duration = durationSeconds;
        listened += Math.Min(dt, 2);
        if (duration < 30) return;
        if (listened >= ThresholdFor(duration)) Count();
    }

    void Count()
    {
        counted = true;
        if (track == null || string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title)) return;
        if (Store.Add(startedTs, track.Artist, track.Title, track.Album))
        {
            Note($"queued: {track.Artist} - {track.Title}");
            wake.Set();
        }
    }

    // ---- worker

    async void Loop()
    {
        var ct = stop.Token;
        while (!ct.IsCancellationRequested)
        {
            double delay = 1;
            try { delay = await WorkOnce(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { Log.Error("scrobbler loop", e); delay = 5; }
            wake.WaitOne(TimeSpan.FromSeconds(delay));
        }
    }

    async Task<double> WorkOnce(CancellationToken ct)
    {
        if (!Configured) { Auth = "off"; return 5; }
        if (Auth != "ok")
        {
            try { await client.EnsureSessionAsync(ct).ConfigureAwait(false); Auth = "ok"; AuthDetail = ""; Note($"logged in to Last.fm as {settings.LastFm.Username}"); }
            catch (AuthException e) { Auth = "error"; AuthDetail = e.Message; Note("Last.fm login failed: " + e.Message); return 30; }
            catch (LastFmException e) { Online = false; AuthDetail = e.Message; return 10; }
        }
        try
        {
            await SubmitDue(ct).ConfigureAwait(false);
            await VerifyDue(ct).ConfigureAwait(false);
            await RefreshPlays(ct).ConfigureAwait(false);
            Online = true;
        }
        catch (NetworkException e) { Online = false; Note("offline: " + e.Message); return 5; }
        catch (AuthException e) { Auth = "error"; AuthDetail = e.Message; Note("Last.fm rejected the credentials: " + e.Message); return 30; }
        return Store.Due("queued", Now(), 1).Count > 0 || Store.Due("sent", Now(), 1).Count > 0 ? 1 : 3;
    }

    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    async Task SubmitDue(CancellationToken ct)
    {
        var now = Now();
        var rows = Store.Due("queued", now);
        if (rows.Count == 0) return;
        var maxAge = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 14 * 86400;
        foreach (var old in rows.Where(r => r.Ts < maxAge))
            Store.Update(old.Id, status: "failed", detail: "older than 14 days (Last.fm limit)");
        rows = rows.Where(r => r.Ts >= maxAge).ToList();
        if (rows.Count == 0) return;

        List<(bool Accepted, string Message, string Corrected)> results;
        try { results = await client.ScrobbleAsync(rows, ct).ConfigureAwait(false); }
        catch (NetworkException e)
        {
            foreach (var r in rows)
            {
                int n = r.Attempts + 1;
                Store.Update(r.Id, attempts: n, nextTry: now + Math.Min(300, 5 * Math.Pow(2, Math.Min(n, 6))), detail: "will retry: " + e.Message);
            }
            throw;
        }
        catch (ApiException e)
        {
            foreach (var r in rows)
            {
                int n = r.Attempts + 1;
                if (n >= 6) Store.Update(r.Id, status: "failed", attempts: n, detail: e.Message);
                else Store.Update(r.Id, attempts: n, nextTry: now + 30 * n, detail: "will retry: " + e.Message);
            }
            Note(e.Message);
            return;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            var (ok, msg, corrected) = results[i];
            if (ok) { Store.Update(rows[i].Id, status: "sent", attempts: 0, nextTry: now + 3, corrected: corrected, detail: "accepted, verifying on Last.fm"); Note($"sent: {rows[i].Artist} - {rows[i].Title}"); }
            else { Store.Update(rows[i].Id, status: "ignored", detail: msg); Note($"ignored by Last.fm: {rows[i].Artist} - {rows[i].Title} ({msg})"); }
        }
    }

    async Task VerifyDue(CancellationToken ct)
    {
        var now = Now();
        var rows = Store.Due("sent", now);
        if (rows.Count == 0) return;
        var seen = await client.RecentAsync(rows.Min(r => r.Ts) - 2, rows.Max(r => r.Ts) + 2, ct).ConfigureAwait(false);
        foreach (var r in rows)
        {
            if (seen.Contains(r.Ts) || seen.Contains(r.Ts - 1) || seen.Contains(r.Ts + 1))
            {
                int? plays = null;
                try { plays = await client.TrackPlaysAsync(r.Artist, r.Title, ct).ConfigureAwait(false); } catch (LastFmException) { /* the count is a bonus */ }
                Store.Update(r.Id, status: "confirmed", detail: "", plays: plays);
                Note($"confirmed on Last.fm: {r.Artist} - {r.Title}");
                playsAt = 0;
                continue;
            }
            int n = r.Attempts + 1;
            if (n > VerifyDelays.Length)
            {
                Store.Update(r.Id, status: "unverified", attempts: n, detail: "accepted by Last.fm but not found in your history");
                Note($"could not verify: {r.Artist} - {r.Title}");
            }
            else Store.Update(r.Id, attempts: n, nextTry: now + VerifyDelays[n - 1]);
        }
    }

    async Task RefreshPlays(CancellationToken ct)
    {
        if (Now() - playsAt < 120) return;
        playsAt = Now();
        try { AccountPlays = await client.AccountPlaysAsync(ct).ConfigureAwait(false); } catch (LastFmException) { /* cosmetic */ }
    }
}
