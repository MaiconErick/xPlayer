using System.Net;
using System.Text;
using System.Text.Json;

namespace xPlayer;

/// <summary>
/// `xPlayer.exe --scrobble-test out.txt` runs the real scrobbler against an in-process fake Last.fm
/// (isolated database and settings, your account is never touched): rule, offline retry, ignored, verification.
/// </summary>
public static class ScrobbleSelfTest
{
    static readonly List<(long Ts, string Artist, string Title)> Accepted = [];
    static int recentCalls, scrobbleCalls, failScrobbles;

    public static string Run()
    {
        var log = new StringBuilder();
        void Say(string s) => log.AppendLine(s);

        var listener = new HttpListener();
        int port = 40000 + Random.Shared.Next(2000);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(() => Serve(listener));

        var dir = Path.Combine(Path.GetTempPath(), "xplayer_scrobble_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var settings = new AppSettings();
        settings.LastFm.ApiKey = "key"; settings.LastFm.ApiSecret = "secret"; settings.LastFm.Username = "Tester";
        settings.LastFm.PasswordHash = "0123456789abcdef0123456789abcdef"; settings.LastFm.ApiUrl = $"http://127.0.0.1:{port}/";

        var s = new Scrobbler(settings, dir, save: () => { });
        // the threshold setting: default is 5 s "instant"; percent and Last.fm-standard modes must compute correctly
        string Th(string mode, double value, double dur) { settings.ScrobbleMode = mode; settings.ScrobbleValue = value; return s.ThresholdFor(dur).ToString("0.#"); }
        Say($"threshold default mode '{new AppSettings().ScrobbleMode}' value {new AppSettings().ScrobbleValue}");
        bool thresholdsOk = Th("seconds", 5, 200) == "5" && Th("seconds", 5, 3) == "3" && Th("percent", 25, 200) == "50"
                            && Th("standard", 0, 600) == "240" && Th("standard", 0, 100) == "50";
        Say($"thresholds (5s/200s=5, 5s/3s=3, 25%/200s=50, std/600s=240, std/100s=50): {(thresholdsOk ? "ok" : "WRONG")}");
        settings.ScrobbleMode = "seconds"; settings.ScrobbleValue = 5;
        failScrobbles = 1;                                                   // first submit hits an "offline" error and must be retried

        var good = new Track { Path = "a.mp3", Artist = "Good Artist", Title = "Good Song", Album = "Album", Seconds = 40 };
        var bad = new Track { Path = "b.mp3", Artist = "Ignored Artist", Title = "Bad Song", Album = "Album", Seconds = 40 };
        var tiny = new Track { Path = "c.mp3", Artist = "Tiny", Title = "Jingle", Seconds = 12 };

        Play(s, good, 40);
        Play(s, bad, 40);
        Play(s, tiny, 12);

        var progress = s.Progress;
        Say($"too-short track flagged: {progress.TooShort}");

        bool ok = Wait(() => s.Store.Recent().Count(r => r.Status is "confirmed" or "ignored") >= 2, 90);
        var rows = s.Store.Recent();
        foreach (var r in rows) Say($"row: {r.Artist} - {r.Title} => {r.Status} {r.Detail} plays={r.Plays} attempts={r.Attempts}");
        var good1 = rows.FirstOrDefault(r => r.Artist == "Good Artist");
        var bad1 = rows.FirstOrDefault(r => r.Artist == "Ignored Artist");
        bool pass = ok && thresholdsOk
            && good1?.Status == "confirmed" && good1.Plays == 7
            && bad1?.Status == "ignored"
            && rows.All(r => r.Artist != "Tiny")
            && scrobbleCalls >= 2;
        Say($"scrobble calls: {scrobbleCalls} (>=2 means the offline failure was retried), recent calls: {recentCalls}");
        Say(pass ? "SCROBBLER SELF-TEST PASSED" : "SCROBBLER SELF-TEST FAILED");
        s.Stop();
        listener.Stop();
        return log.ToString();
    }

    static void Play(Scrobbler s, Track t, double duration)
    {
        s.TrackStarted(t, duration);
        for (int i = 0; i < 30; i++) s.Tick(playing: true, dt: 1, duration);        // 30 s of listening
        Thread.Sleep(50);
    }

    static bool Wait(Func<bool> cond, int seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { if (cond()) return true; Thread.Sleep(200); }
        return false;
    }

    static void Serve(HttpListener l)
    {
        while (l.IsListening)
        {
            HttpListenerContext c;
            try { c = l.GetContext(); } catch { return; }
            try { Handle(c); } catch { /* the test reports what it needs */ }
        }
    }

    static void Handle(HttpListenerContext c)
    {
        var q = new Dictionary<string, string>();
        foreach (var kv in (c.Request.Url!.Query.TrimStart('?')).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = kv.Split('=', 2);
            q[WebUtility.UrlDecode(p[0])] = p.Length > 1 ? WebUtility.UrlDecode(p[1]) : "";
        }
        if (c.Request.HttpMethod == "POST")
        {
            using var r = new StreamReader(c.Request.InputStream);
            foreach (var kv in r.ReadToEnd().Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = kv.Split('=', 2);
                q[WebUtility.UrlDecode(p[0])] = p.Length > 1 ? WebUtility.UrlDecode(p[1]) : "";
            }
        }

        object body;
        int status = 200;
        switch (q.GetValueOrDefault("method"))
        {
            case "auth.getMobileSession":
                body = new { session = new { name = "Tester", key = "SK", subscriber = 0 } };
                break;
            case "track.updateNowPlaying":
                body = new { nowplaying = new { } };
                break;
            case "track.scrobble":
                Interlocked.Increment(ref scrobbleCalls);
                if (Interlocked.Decrement(ref failScrobbles) >= 0) { status = 503; body = new { error = 11, message = "Service offline" }; break; }
                var items = new List<object>();
                for (int i = 0; q.ContainsKey($"artist[{i}]"); i++)
                {
                    bool ign = q[$"artist[{i}]"].StartsWith("Ignored");
                    if (!ign) lock (Accepted) Accepted.Add((long.Parse(q[$"timestamp[{i}]"]), q[$"artist[{i}]"], q[$"track[{i}]"]));
                    items.Add(new { track = new { corrected = "0", text = q[$"track[{i}]"] }, ignoredMessage = new { code = ign ? "1" : "0", text = ign ? "Artist ignored" : "" } });
                }
                body = new { scrobbles = new { scrobble = items } };
                break;
            case "user.getRecentTracks":
                int n = Interlocked.Increment(ref recentCalls);
                var tracks = new List<object> { new Dictionary<string, object> { ["name"] = "Now", ["@attr"] = new { nowplaying = "true" } } };
                if (n >= 3)                                                   // Last.fm shows new scrobbles with a delay
                    lock (Accepted) foreach (var a in Accepted) tracks.Add(new { name = a.Title, date = new { uts = a.Ts.ToString() } });
                body = new { recenttracks = new { track = tracks } };
                break;
            case "track.getInfo":
                body = new { track = new { userplaycount = "7" } };
                break;
            case "user.getInfo":
                body = new { user = new { playcount = "1234" } };
                break;
            default:
                body = new { error = 3, message = "Invalid method" };
                break;
        }
        var json = JsonSerializer.Serialize(body).Replace("\"text\":", "\"#text\":");
        var bytes = Encoding.UTF8.GetBytes(json);
        c.Response.StatusCode = status;
        c.Response.ContentType = "application/json";
        c.Response.OutputStream.Write(bytes, 0, bytes.Length);
        c.Response.Close();
    }
}
