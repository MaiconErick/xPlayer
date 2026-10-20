using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace xPlayer;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One audio file with the tags we care about. Cheap to keep 50k of these in memory.</summary>
public sealed class Track
{
    public string Path { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string AlbumArtist { get; init; } = "";
    public string Genre { get; init; } = "";
    public int Year { get; init; }
    public int TrackNo { get; init; }
    public int DiscNo { get; init; }
    public double Seconds { get; init; }
    public int Bitrate { get; init; }
    public int SampleRate { get; init; }
    public int Bits { get; init; }
    public long Size { get; init; }
    public long Mtime { get; init; }

    public string Display => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} - {Title}";
    public string Time => Fmt.Time(Seconds);
    public string Format => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public bool IsKpop => Fmt.Norm(Genre).Contains("kpop");

    /// <summary>Key shared by every track of the same album (cover lookup, album grouping).</summary>
    public string AlbumKey => $"{Folder}|{Fmt.Norm(Album)}";

    string? searchText;
    public string SearchText => searchText ??= Fmt.Fold($"{Artist} {Title} {Album} {AlbumArtist}");

    public static Track FromPathOnly(string path) => new()
    {
        Path = path,
        Title = System.IO.Path.GetFileNameWithoutExtension(path),
    };
}

/// <summary>An entry of the play queue.</summary>
public sealed class QueueItem : Observable
{
    public Track Track { get; }
    public QueueItem(Track t) => Track = t;

    bool playing;
    public bool IsPlaying { get => playing; set => Set(ref playing, value); }

    bool failed;
    public bool Failed { get => failed; set => Set(ref failed, value); }
}

public static class Fmt
{
    public static string Time(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>Lowercase letters/digits only (any script): used for comparing titles across sources.</summary>
    public static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>Accent- and case-insensitive text for typo-tolerant search ("jao" finds "Jão").</summary>
    public static string Fold(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (var ch in d)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark && ch != '゙' && ch != '゚') continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }
}
