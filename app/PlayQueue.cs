using System.Collections.ObjectModel;
using System.Text.Json;

namespace xPlayer;

/// <summary>The play queue: ordering, shuffle, repeat, and persistence between runs.</summary>
public sealed class PlayQueue
{
    readonly Random rng = new();
    readonly List<QueueItem> history = [];
    readonly HashSet<QueueItem> shufflePlayed = [];

    public ObservableCollection<QueueItem> Items { get; } = [];
    public QueueItem? Current { get; private set; }
    public bool Shuffle { get; set; }
    public string Repeat { get; set; } = "off";      // off | all | one

    public event Action? Changed;

    public int IndexOf(QueueItem? item) => item == null ? -1 : Items.IndexOf(item);

    public void SetCurrent(QueueItem? item, bool pushHistory = true)
    {
        if (Current == item) return;
        if (Current != null)
        {
            Current.IsPlaying = false;
            if (pushHistory) history.Add(Current);
            if (history.Count > 200) history.RemoveAt(0);
        }
        Current = item;
        if (item != null) { item.IsPlaying = true; item.Failed = false; shufflePlayed.Add(item); }
        Changed?.Invoke();
    }

    public QueueItem Add(Track t)
    {
        var q = new QueueItem(t);
        Items.Add(q);
        Changed?.Invoke();
        return q;
    }

    public List<QueueItem> Add(IEnumerable<Track> tracks, int? at = null)
    {
        var added = tracks.Select(t => new QueueItem(t)).ToList();
        int i = Math.Clamp(at ?? Items.Count, 0, Items.Count);
        foreach (var q in added) Items.Insert(i++, q);
        Changed?.Invoke();
        return added;
    }

    /// <summary>Queue the tracks right after the one that is playing.</summary>
    public List<QueueItem> PlayNext(IEnumerable<Track> tracks)
    {
        int at = Current == null ? Items.Count : Items.IndexOf(Current) + 1;
        return Add(tracks, at);
    }

    public List<QueueItem> Replace(IEnumerable<Track> tracks)
    {
        Items.Clear();
        history.Clear();
        shufflePlayed.Clear();
        Current = null;
        return Add(tracks);
    }

    public void Remove(IEnumerable<QueueItem> items)
    {
        foreach (var q in items.ToList())
        {
            if (q == Current) Current = null;
            Items.Remove(q);
            history.Remove(q);
            shufflePlayed.Remove(q);
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        Items.Clear();
        history.Clear();
        shufflePlayed.Clear();
        Current = null;
        Changed?.Invoke();
    }

    public void Move(int from, int to)
    {
        if (from < 0 || from >= Items.Count) return;
        to = Math.Clamp(to, 0, Items.Count - 1);
        if (from == to) return;
        Items.Move(from, to);
        Changed?.Invoke();
    }

    /// <summary>The item that should play after the current one (null = end of queue).</summary>
    public QueueItem? Next(bool auto)
    {
        if (Items.Count == 0) return null;
        if (auto && Repeat == "one" && Current != null) return Current;
        if (Shuffle)
        {
            var pool = Items.Where(i => !shufflePlayed.Contains(i)).ToList();
            if (pool.Count == 0)
            {
                if (Repeat != "all" && auto) return null;
                shufflePlayed.Clear();
                if (Current != null) shufflePlayed.Add(Current);
                pool = Items.Where(i => !shufflePlayed.Contains(i)).ToList();
                if (pool.Count == 0) return Current;
            }
            return pool[rng.Next(pool.Count)];
        }
        int idx = Current == null ? -1 : Items.IndexOf(Current);
        if (idx + 1 < Items.Count) return Items[idx + 1];
        return Repeat == "all" || !auto ? Items[0] : null;
    }

    public QueueItem? Previous()
    {
        while (history.Count > 0)
        {
            var q = history[^1];
            history.RemoveAt(history.Count - 1);
            if (Items.Contains(q)) return q;
        }
        if (Current == null) return Items.FirstOrDefault();
        int idx = Items.IndexOf(Current);
        return idx > 0 ? Items[idx - 1] : (Repeat == "all" ? Items[^1] : Current);
    }

    // ---- persistence

    sealed record Saved(List<string> Paths, int Index);

    static string FilePath => Path.Combine(AppSettings.Dir, "queue.json");

    public void Save()
    {
        try
        {
            var saved = new Saved(Items.Select(i => i.Track.Path).ToList(), Current == null ? -1 : Items.IndexOf(Current));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(saved));
        }
        catch (Exception e) { Log.Error("queue save failed", e); }
    }

    /// <summary>Restores the previous queue; returns the index that was current (or -1).</summary>
    public int Restore(Library lib)
    {
        try
        {
            if (!File.Exists(FilePath)) return -1;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath));
            if (saved == null) return -1;
            var tracks = saved.Paths.Where(File.Exists).Select(lib.Resolve).ToList();
            foreach (var t in tracks) Items.Add(new QueueItem(t));
            return saved.Index >= 0 && saved.Index < Items.Count ? saved.Index : -1;
        }
        catch (Exception e)
        {
            Log.Error("queue restore failed", e);
            return -1;
        }
    }
}
