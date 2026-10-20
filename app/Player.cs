using NAudio.Wave;

namespace xPlayer;

public enum PlayState { Stopped, Playing, Paused }

/// <summary>NAudio wrapper: one track at a time. Events are raised on the thread that created the player (the UI thread).</summary>
public sealed class AudioPlayer : IDisposable
{
    readonly System.Windows.Threading.Dispatcher ui;
    WaveOutEvent? output;
    WaveStream? reader;
    NAudio.Wave.SampleProviders.VolumeSampleProvider? volumeProvider;
    bool stopping;
    float volume = 0.8f;

    public AudioPlayer(System.Windows.Threading.Dispatcher dispatcher) => ui = dispatcher;

    public PlayState State { get; private set; } = PlayState.Stopped;
    public Track? Current { get; private set; }
    public double Duration => reader?.TotalTime.TotalSeconds ?? Current?.Seconds ?? 0;

    public event Action? StateChanged;
    public event Action? Ended;
    public event Action<Track, Exception>? Failed;

    public double Position
    {
        get => reader?.CurrentTime.TotalSeconds ?? 0;
        set
        {
            if (reader == null) return;
            var t = Math.Clamp(value, 0, Math.Max(0, reader.TotalTime.TotalSeconds - 0.05));
            reader.CurrentTime = TimeSpan.FromSeconds(t);
        }
    }

    public double Volume
    {
        get => volume;
        set { volume = (float)Math.Clamp(value, 0, 1); if (volumeProvider != null) volumeProvider.Volume = volume; }
    }

    public void Open(Track track, double startSeconds = 0, bool play = true)
    {
        Teardown();
        Current = track;
        try
        {
            // Media Foundation handles mp3/flac/m4a/wma/wav; Vorbis (.ogg) needs its own decoder
            reader = track.Path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ? new NAudio.Vorbis.VorbisWaveReader(track.Path) : new AudioFileReader(track.Path);
            ISampleProvider samples = reader as ISampleProvider ?? reader.ToSampleProvider();
            volumeProvider = new NAudio.Wave.SampleProviders.VolumeSampleProvider(samples) { Volume = volume };
            output = new WaveOutEvent { DesiredLatency = 140, NumberOfBuffers = 3 };
            output.PlaybackStopped += OnStopped;
            output.Init(volumeProvider);
            if (startSeconds > 0) Position = startSeconds;
        }
        catch (Exception e)
        {
            Teardown();
            Current = track;
            SetState(PlayState.Stopped);
            throw new InvalidOperationException($"Cannot play {Path.GetFileName(track.Path)}: {e.Message}", e);
        }
        if (play) Play(); else SetState(PlayState.Paused);
    }

    public void Play()
    {
        if (output == null) return;
        output.Play();
        SetState(PlayState.Playing);
    }

    public void Pause()
    {
        if (output == null || State != PlayState.Playing) return;
        output.Pause();
        SetState(PlayState.Paused);
    }

    public void Toggle()
    {
        if (State == PlayState.Playing) Pause();
        else if (output != null) Play();
    }

    public void Stop()
    {
        Teardown();
        Current = null;
        SetState(PlayState.Stopped);
    }

    void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (stopping || sender != output) return;
        Post(() =>
        {
            if (sender != output) return;
            if (e.Exception != null)
            {
                var t = Current;
                SetState(PlayState.Stopped);
                if (t != null) Failed?.Invoke(t, e.Exception);
                return;
            }
            SetState(PlayState.Stopped);
            Ended?.Invoke();
        });
    }

    void Teardown()
    {
        stopping = true;
        try
        {
            if (output != null) { output.PlaybackStopped -= OnStopped; output.Stop(); output.Dispose(); }
            reader?.Dispose();
        }
        catch (Exception e) { Log.Error("audio teardown", e); }
        output = null;
        reader = null;
        volumeProvider = null;
        stopping = false;
    }

    void SetState(PlayState s)
    {
        State = s;
        StateChanged?.Invoke();
    }

    void Post(Action a)
    {
        ui.BeginInvoke(a);
    }

    public void Dispose() => Teardown();
}
