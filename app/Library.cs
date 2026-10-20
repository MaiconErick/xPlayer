using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;

namespace xPlayer;

/// <summary>
/// The music library. Everything is derived from the files themselves; library.db is only a disposable
/// speed-up (delete it and the next scan rebuilds it from the tags).
/// </summary>
public sealed class Library
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma", ".mp4", ".mka" };

    static string DbPath => Path.Combine(AppSettings.Dir, "library.db");

    public IReadOnlyList<Track> Tracks { get; private set; } = [];
    public bool Scanning { get; private set; }
    public string ScanStatus { get; private set; } = "";
    public event Action? Changed;
    public event Action? ScanStateChanged;

    volatile Dictionary<string, Track> byPath = new(StringComparer.OrdinalIgnoreCase);

    public Track? Find(string path) => byPath.TryGetValue(path, out var t) ? t : null;

    /// <summary>Looks the file up in the library or reads its tags right now.</summary>
    public Track Resolve(string path) => Find(path) ?? TagReader.Read(path);

    public static bool IsAudio(string path) => Extensions.Contains(Path.GetExtension(path));

    static SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={DbPath}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS tracks(
              path TEXT PRIMARY KEY, size INTEGER, mtime INTEGER, title TEXT, artist TEXT, album TEXT, albumartist TEXT,
              genre TEXT, year INTEGER, trackno INTEGER, discno INTEGER, seconds REAL, bitrate INTEGER, samplerate INTEGER, bits INTEGER);
            """;
        cmd.ExecuteNonQuery();
        return c;
    }

    /// <summary>Instant start-up: tracks straight from the cache, before any scanning.</summary>
    public void LoadCache()
    {
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT path,size,mtime,title,artist,album,albumartist,genre,year,trackno,discno,seconds,bitrate,samplerate,bits FROM tracks";
            using var r = cmd.ExecuteReader();
            var list = new List<Track>();
            while (r.Read()) list.Add(FromRow(r));
            Publish(list);
        }
        catch (Exception e) { Log.Error("library cache unreadable (it will be rebuilt)", e); }
    }

    static Track FromRow(SqliteDataReader r) => new()
    {
        Path = r.GetString(0), Size = r.GetInt64(1), Mtime = r.GetInt64(2), Title = r.GetString(3), Artist = r.GetString(4),
        Album = r.GetString(5), AlbumArtist = r.GetString(6), Genre = r.GetString(7), Year = r.GetInt32(8), TrackNo = r.GetInt32(9),
        DiscNo = r.GetInt32(10), Seconds = r.GetDouble(11), Bitrate = r.GetInt32(12), SampleRate = r.GetInt32(13), Bits = r.GetInt32(14),
    };

    void Publish(List<Track> list)
    {
        list.Sort(Compare);
        Tracks = list;
        var map = new Dictionary<string, Track>(list.Count, StringComparer.OrdinalIgnoreCase); foreach (var t in list) map[t.Path] = t; byPath = map;
        Changed?.Invoke();
    }

    public static int Compare(Track a, Track b)
    {
        int c = string.Compare(a.AlbumArtist.Length > 0 ? a.AlbumArtist : a.Artist, b.AlbumArtist.Length > 0 ? b.AlbumArtist : b.Artist,
            StringComparison.CurrentCultureIgnoreCase);
        if (c != 0) return c;
        c = string.Compare(a.Album, b.Album, StringComparison.CurrentCultureIgnoreCase);
        if (c != 0) return c;
        c = a.DiscNo.CompareTo(b.DiscNo);
        if (c != 0) return c;
        c = a.TrackNo.CompareTo(b.TrackNo);
        return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
    }

    public async Task ScanAsync(IEnumerable<string> roots, CancellationToken ct = default)
    {
        if (Scanning) return;
        Scanning = true;
        ScanStatus = "Looking for music…";
        ScanStateChanged?.Invoke();
        try
        {
            var rootList = roots.Where(Directory.Exists).ToList();
            var result = await Task.Run(() => Scan(rootList, ct), ct);
            Publish(result);
            ScanStatus = $"{result.Count:N0} tracks";
        }
        catch (OperationCanceledException) { ScanStatus = "Scan cancelled"; }
        catch (Exception e) { Log.Error("scan failed", e); ScanStatus = "Scan failed: " + e.Message; }
        finally
        {
            Scanning = false;
            ScanStateChanged?.Invoke();
        }
    }

    List<Track> Scan(List<string> roots, CancellationToken ct)
    {
        var cached = Tracks.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        var found = new List<(string Path, long Size, long Mtime)>();
        foreach (var root in roots)
        {
            foreach (var fi in new DirectoryInfo(root).EnumerateFiles("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                if (Extensions.Contains(fi.Extension)) found.Add((fi.FullName, fi.Length, fi.LastWriteTimeUtc.Ticks));
            }
        }

        var keep = new ConcurrentBag<Track>();
        var todo = new List<(string Path, long Size, long Mtime)>();
        foreach (var f in found)
        {
            if (cached.TryGetValue(f.Path, out var old) && old.Size == f.Size && old.Mtime == f.Mtime) keep.Add(old);
            else todo.Add(f);
        }

        // Library on a spinning disk: a few readers in directory order beat many threads seeking everywhere.
        // Results are saved in batches (an interrupted first scan resumes) and shown while the scan is still running.
        int done = 0;
        var fresh = new ConcurrentQueue<Track>();
        long lastPublish = Environment.TickCount64;
        ScanStatus = todo.Count == 0 ? "Up to date" : $"Reading tags 0/{todo.Count:N0}";
        ScanStateChanged?.Invoke();
        var order = Partitioner.Create(todo, loadBalance: true);
        Parallel.ForEach(order, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, f =>
        {
            var t = TagReader.Read(f.Path, f.Size, f.Mtime);
            keep.Add(t);
            fresh.Enqueue(t);
            var n = Interlocked.Increment(ref done);
            if (n % 100 == 0)
            {
                ScanStatus = $"Reading tags {n:N0}/{todo.Count:N0}";
                ScanStateChanged?.Invoke();
            }
            if (n % 400 == 0 && Monitor.TryEnter(fresh))
            {
                try
                {
                    var batch = new List<Track>();
                    while (fresh.TryDequeue(out var x)) batch.Add(x);
                    Upsert(batch);
                    if (Environment.TickCount64 - lastPublish > 2500) { lastPublish = Environment.TickCount64; Publish(keep.ToList()); }
                }
                finally { Monitor.Exit(fresh); }
            }
        });

        var all = keep.ToList();
        var rest = new List<Track>();
        while (fresh.TryDequeue(out var x)) rest.Add(x);
        Upsert(rest);
        Prune(all);
        return all;
    }

    static void Upsert(List<Track> tracks)
    {
        if (tracks.Count == 0) return;
        try
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR REPLACE INTO tracks VALUES($p,$s,$m,$t,$a,$al,$aa,$g,$y,$tn,$dn,$sec,$br,$sr,$b)";
            var ps = new[] { "$p", "$s", "$m", "$t", "$a", "$al", "$aa", "$g", "$y", "$tn", "$dn", "$sec", "$br", "$sr", "$b" }
                .Select(n => ins.Parameters.Add(n, SqliteType.Text)).ToArray();
            foreach (var t in tracks)
            {
                object[] v = [t.Path, t.Size, t.Mtime, t.Title, t.Artist, t.Album, t.AlbumArtist, t.Genre, t.Year, t.TrackNo, t.DiscNo, t.Seconds, t.Bitrate, t.SampleRate, t.Bits];
                for (int i = 0; i < v.Length; i++) ps[i].Value = v[i];
                ins.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception e) { Log.Error("library cache save failed", e); }
    }

    // Forget files that are gone from disk (or from the library folders).
    static void Prune(List<Track> existing)
    {
        try
        {
            var keepPaths = new HashSet<string>(existing.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
            using var c = Open();
            var stale = new List<string>();
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT path FROM tracks";
                using var r = q.ExecuteReader();
                while (r.Read()) if (!keepPaths.Contains(r.GetString(0))) stale.Add(r.GetString(0));
            }
            if (stale.Count == 0) return;
            using var tx = c.BeginTransaction();
            using var del = c.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM tracks WHERE path=$p";
            var p = del.Parameters.Add("$p", SqliteType.Text);
            foreach (var s in stale) { p.Value = s; del.ExecuteNonQuery(); }
            tx.Commit();
        }
        catch (Exception e) { Log.Error("library cache prune failed", e); }
    }

    /// <summary>Typo/accent-tolerant search: every word must appear somewhere in artist/title/album.</summary>
    public IReadOnlyList<Track> Search(string query)
    {
        var words = Fmt.Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return Tracks;
        return Tracks.Where(t => words.All(w => t.SearchText.Contains(w, StringComparison.Ordinal))).ToList();
    }

    /// <summary>All tracks of the same album folder, in disc/track order.</summary>
    public List<Track> AlbumOf(Track t)
    {
        var key = t.AlbumKey;
        return Tracks.Where(x => x.AlbumKey == key).OrderBy(x => x.DiscNo).ThenBy(x => x.TrackNo)
            .ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

public static class TagReader
{
    public static Track Read(string path, long size = -1, long mtime = -1)
    {
        try
        {
            if (size < 0) { var fi = new FileInfo(path); size = fi.Length; mtime = fi.LastWriteTimeUtc.Ticks; }
            using var f = TagLib.File.Create(path, TagLib.ReadStyle.Average | TagLib.ReadStyle.PictureLazy);   // PictureLazy: do not load embedded covers just to scan
            var tag = f.Tag;
            var props = f.Properties;
            string title = tag.Title?.Trim() ?? "";
            return new Track
            {
                Path = path, Size = size, Mtime = mtime,
                Title = title.Length > 0 ? title : Path.GetFileNameWithoutExtension(path),
                Artist = (tag.FirstPerformer ?? tag.FirstAlbumArtist ?? "").Trim(),
                Album = (tag.Album ?? "").Trim(),
                AlbumArtist = (tag.FirstAlbumArtist ?? "").Trim(),
                Genre = (tag.FirstGenre ?? "").Trim(),
                Year = (int)tag.Year, TrackNo = (int)tag.Track, DiscNo = (int)tag.Disc,
                Seconds = props?.Duration.TotalSeconds ?? 0,
                Bitrate = props?.AudioBitrate ?? 0, SampleRate = props?.AudioSampleRate ?? 0, Bits = props?.BitsPerSample ?? 0,
            };
        }
        catch (Exception e)
        {
            Log.Error($"tags unreadable: {path}", e);
            return new Track { Path = path, Size = Math.Max(size, 0), Mtime = Math.Max(mtime, 0), Title = Path.GetFileNameWithoutExtension(path) };
        }
    }
}

/// <summary>Album art straight from the files (embedded picture first, then cover.jpg & friends). Small in-memory LRU only.</summary>
public static class Covers
{
    static readonly string[] FolderNames = ["cover", "folder", "front", "album", "albumart", "albumartsmall", "art"];
    static readonly string[] FolderExts = [".jpg", ".jpeg", ".png", ".webp", ".bmp"];
    static readonly ConcurrentDictionary<string, BitmapSource?> cache = new();
    static readonly ConcurrentQueue<string> order = new();

    public static Task<BitmapSource?> GetAsync(Track t, int width) => Task.Run(() => Get(t, width));

    public static BitmapSource? Get(Track t, int width)
    {
        var key = $"{t.AlbumKey}|{width}";
        if (cache.TryGetValue(key, out var hit)) return hit;
        BitmapSource? bmp = null;
        try
        {
            var bytes = Embedded(t.Path) ?? FromFolder(t.Folder);
            if (bytes != null) bmp = Decode(bytes, width);
        }
        catch (Exception e) { Log.Error($"cover failed for {t.Path}", e); }
        cache[key] = bmp;
        order.Enqueue(key);
        while (order.Count > 160 && order.TryDequeue(out var old)) cache.TryRemove(old, out _);
        return bmp;
    }

    static byte[]? Embedded(string path)
    {
        try
        {
            using var f = TagLib.File.Create(path);
            var pic = f.Tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? f.Tag.Pictures.FirstOrDefault();
            return pic?.Data?.Data;
        }
        catch { return null; }
    }

    static byte[]? FromFolder(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var images = Directory.EnumerateFiles(dir).Where(f => FolderExts.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
            var best = images.FirstOrDefault(f => FolderNames.Contains(Path.GetFileNameWithoutExtension(f).ToLowerInvariant())) ?? images.FirstOrDefault();
            return best == null ? null : File.ReadAllBytes(best);
        }
        catch { return null; }
    }

    static BitmapSource? Decode(byte[] bytes, int width)
    {
        using var ms = new MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (width > 0) bmp.DecodePixelWidth = width;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
