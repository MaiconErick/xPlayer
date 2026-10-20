using System.Text.Json;
using System.Text.Json.Serialization;

namespace xPlayer;

public sealed class LastFmSettings
{
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>md5 of the password; the password itself is never stored.</summary>
    public string PasswordHash { get; set; } = "";
    public string SessionKey { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string ApiUrl { get; set; } = "https://ws.audioscrobbler.com/2.0/";
}

public sealed class AppSettings
{
    public List<string> LibraryRoots { get; set; } = [@"D:\Music\xNewMusic"];
    public LastFmSettings LastFm { get; set; } = new();
    public double Volume { get; set; } = 0.8;
    public bool Shuffle { get; set; }
    public string Repeat { get; set; } = "off";           // off | all | one
    public string Ambient { get; set; } = "wisps";
    public bool ShowLyrics { get; set; } = true;
    public bool ShowLeft { get; set; } = true;
    public double LeftWidth { get; set; } = 420;
    public double LyricsWidth { get; set; } = 440;
    public bool EmbedLyrics { get; set; }                  // write fetched lyrics into the file (USLT, like embed_lyrics.pyw)
    public bool SidecarLyrics { get; set; }                // write fetched lyrics as a .lrc next to the file
    public bool OnlineLyrics { get; set; } = true;
    /// <summary>Translations that some sources attach to each line (NetEase sends a Chinese one). Hidden unless switched on.</summary>
    public bool ShowTranslations { get; set; }
    /// <summary>When a play counts as a scrobble: "seconds" (fixed, "instant" at a few seconds), "percent" of the track, or "standard" (Last.fm: half, max 4 min).</summary>
    public string ScrobbleMode { get; set; } = "seconds";
    public double ScrobbleValue { get; set; } = 5;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1500;
    public double WindowHeight { get; set; } = 900;
    public bool Maximized { get; set; }
    public int QueueIndex { get; set; } = -1;
    public double QueuePosition { get; set; }
    public Dictionary<string, double> LyricOffsets { get; set; } = new();   // per track path, seconds

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static string Dir
    {
        get
        {
            var d = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "xPlayer");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string FilePath => System.IO.Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception e) { Log.Error("settings unreadable, using defaults", e); }
        var fresh = new AppSettings();
        fresh.ImportXScrobbler();
        return fresh;
    }

    /// <summary>First run: reuse the Last.fm account of the Python xScrobbler so nothing has to be typed again.</summary>
    void ImportXScrobbler()
    {
        try
        {
            const string legacy = @"C:\DEV\Python\xScrobbler\config.json";
            if (!File.Exists(legacy)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(legacy, System.Text.Encoding.UTF8));
            var r = doc.RootElement;
            string Get(string k) => r.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
            LastFm.ApiKey = Get("api_key"); LastFm.ApiSecret = Get("api_secret"); LastFm.Username = Get("username");
            LastFm.PasswordHash = Get("password_hash"); LastFm.SessionKey = Get("session_key");
            Log.Info("imported the Last.fm account from xScrobbler");
        }
        catch (Exception e) { Log.Error("xScrobbler import failed", e); }
    }

    public void Save()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e) { Log.Error("settings save failed", e); }
    }
}

public static class Log
{
    static readonly object gate = new();
    static string Path => System.IO.Path.Combine(AppSettings.Dir, "xplayer.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg, Exception? e = null) => Write("ERROR", e == null ? msg : $"{msg}: {e.GetType().Name}: {e.Message}");

    static void Write(string level, string msg)
    {
        try
        {
            lock (gate)
            {
                var fi = new FileInfo(Path);
                if (fi.Exists && fi.Length > 512_000) File.Move(Path, Path + ".old", true);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}
