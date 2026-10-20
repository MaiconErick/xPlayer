using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace xPlayer;

public partial class MainWindow : Window
{
    readonly AppSettings settings = AppSettings.Load();
    readonly Library library = new();
    readonly PlayQueue queue = new();
    readonly LyricsService lyricsService = new();
    readonly AudioPlayer player;
    readonly Scrobbler scrobbler;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    readonly string? screenshotPath;
    readonly string? playTestPath;
    readonly bool noPersist;
    readonly bool startPaused;
    readonly string[] startPaths;

    bool seeking, updatingSeek, muted;
    double volumeBeforeMute = 0.8;
    DateTime lastTickAt = DateTime.UtcNow;
    int playId, scrobbleStartedPlayId = -1, failStreak, secondCounter, scrobbleVersion = -1;
    Track? shownTrack;
    HwndSource? hwndSource;

    public MainWindow(string[] args)
    {
        InitializeComponent();
        player = new AudioPlayer(Dispatcher);
        scrobbler = new Scrobbler(settings);

        var i = Array.IndexOf(args, "--screenshot");
        screenshotPath = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var pt = Array.IndexOf(args, "--play-test");
        playTestPath = pt >= 0 && pt + 1 < args.Length ? args[pt + 1] : null;
        noPersist = screenshotPath != null || playTestPath != null;      // test runs never touch your saved queue/settings
        if (playTestPath != null) { settings.LastFm.Enabled = false; settings.Volume = 0; }
        if (args.Contains("--show-translations")) settings.ShowTranslations = true;
        startPaused = args.Contains("--paused") || screenshotPath != null;
        var ti = Array.IndexOf(args, "--tab");
        startPaths = args.Where((a, idx) => !a.StartsWith("--") && !(i >= 0 && idx == i + 1) && !(ti >= 0 && idx == ti + 1)).ToArray();

        RestoreWindow();
        Ambient.Kind = settings.Ambient;
        VolumeBar.Value = settings.Volume;
        player.Volume = settings.Volume;
        queue.Shuffle = settings.Shuffle;
        queue.Repeat = settings.Repeat;
        BtnShuffle.IsChecked = settings.Shuffle;
        UpdateRepeatButton();
        QueueList.ItemsSource = queue.Items;
        queue.Items.CollectionChanged += (_, _) => { UpdateQueueInfo(); QueueEmpty.Visibility = queue.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed; };

        player.StateChanged += OnPlayerState;
        player.Ended += OnTrackEnded;
        player.Failed += (t, e) => { Log.Error($"playback failed: {t.Path}", e); Toast("Playback error: " + e.Message); OnTrackEnded(); };
        library.Changed += () => ShowLibrary();
        library.ScanStateChanged += () => Dispatcher.BeginInvoke(UpdateScanText);

        timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => OnLoaded();
        Closing += (_, _) => OnClosing();
        SizeChanged += (_, _) => FitLyricsPadding();
        PreviewKeyDown += OnPreviewKeyDown;
        DragEnter += Window_DragOver;
        DragOver += Window_DragOver;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += Window_Drop;
        LyricsScroll.SizeChanged += (_, _) => FitLyricsPadding();
        SetLyricsState("ghost", "Play something to see its lyrics");
        UpdateQueueInfo();
        UpdateScrobbleViews(true);
    }

    // ------------------------------------------------------------------ lifecycle

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.DarkTitleBar(hwnd, 0x12060B /* #0B0612 as COLORREF */, 0xD5E8F2 /* bone */);
        hwndSource = HwndSource.FromHwnd(hwnd);
        hwndSource?.AddHook(WndProc);
        foreach (var (id, vk) in new[] { (1, Native.VK_MEDIA_PLAY_PAUSE), (2, Native.VK_MEDIA_NEXT), (3, Native.VK_MEDIA_PREV), (4, Native.VK_MEDIA_STOP) })
            Native.RegisterHotKey(hwnd, id, 0, vk);          // silently ignored if another app owns the key
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_HOTKEY) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case 1: PlayPause(); break;
            case 2: NextTrack(); break;
            case 3: PrevTrack(); break;
            case 4: player.Stop(); queue.SetCurrent(null); OnPlayerState(); break;
        }
        handled = true;
        return IntPtr.Zero;
    }

    void RestoreWindow()
    {
        Width = Math.Clamp(settings.WindowWidth, MinWidth, SystemParameters.VirtualScreenWidth);
        Height = Math.Clamp(settings.WindowHeight, MinHeight, SystemParameters.VirtualScreenHeight);
        if (!double.IsNaN(settings.WindowLeft) && !double.IsNaN(settings.WindowTop)
            && settings.WindowLeft > SystemParameters.VirtualScreenLeft - 40 && settings.WindowLeft < SystemParameters.VirtualScreenWidth - 100
            && settings.WindowTop > SystemParameters.VirtualScreenTop - 10 && settings.WindowTop < SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = settings.WindowLeft;
            Top = settings.WindowTop;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (settings.Maximized) WindowState = WindowState.Maximized;
        ColLeft.Width = new GridLength(settings.LeftWidth);
        ColLyrics.Width = new GridLength(settings.LyricsWidth);
        TbLeft.IsChecked = settings.ShowLeft;
        TbLyrics.IsChecked = settings.ShowLyrics;
        TbTranslations.IsChecked = settings.ShowTranslations;
        ApplyPanelVisibility();
    }

    void OnLoaded()
    {
        library.LoadCache();
        UpdateScanText();
        if (settings.LibraryRoots.Count > 0) _ = library.ScanAsync(settings.LibraryRoots);

        if (startPaths.Length > 0)
        {
            _ = AddPathsAsync(startPaths, replace: true, play: !startPaused);
        }
        else
        {
            int idx = queue.Restore(library);
            if (idx >= 0)
            {
                var item = queue.Items[idx];
                try { OpenItem(item, settings.QueuePosition, play: false); }
                catch (Exception e) { Log.Error("restore track", e); }
            }
        }
        var cmd = Environment.GetCommandLineArgs();
        int tabArg = Array.IndexOf(cmd, "--tab");
        if (tabArg >= 0 && tabArg + 1 < cmd.Length)
        {
            if (cmd[tabArg + 1] == "queue") TabQueue.IsChecked = true;
            else if (cmd[tabArg + 1] == "scrobbles") TabScrobbles.IsChecked = true;
        }
        timer.Start();
        if (screenshotPath != null) _ = TakeScreenshotLater();
        if (playTestPath != null) _ = PlayTestAsync();
    }

    async Task TakeScreenshotLater()
    {
        for (int i = 0; i < 60 && (shownTrack == null || LyricsItems.Items.Count == 0); i++) await Task.Delay(500);
        await Task.Delay(1800);
        var root = (FrameworkElement)Content;
        var rtb = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(screenshotPath!)) enc.Save(fs);
        Application.Current.Shutdown();
    }


    /// <summary>--play-test: really plays (volume 0) through the audio device and reports what the engine did.</summary>
    async Task PlayTestAsync()
    {
        var log = new System.Text.StringBuilder();
        void Say(string t) => log.AppendLine(t);
        try
        {
            for (int i = 0; i < 40 && queue.Items.Count < 2; i++) await Task.Delay(250);
            Say($"queue: {queue.Items.Count} tracks");
            VolumeBar.Value = 0;
            if (player.State != PlayState.Playing) PlayPause();
            await Task.Delay(3500);
            Say($"after 3.5 s: state={player.State} position={player.Position:0.0}s of {player.Duration:0.0}s track='{player.Current?.Title}'");
            player.Position = 60;
            await Task.Delay(1200);
            Say($"after seek to 60 s: position={player.Position:0.0}s");
            player.Toggle();
            double frozen = player.Position;
            await Task.Delay(1000);
            Say($"paused: state={player.State} drift={player.Position - frozen:0.00}s");
            player.Toggle();
            NextTrack();
            await Task.Delay(2500);
            Say($"after Next: state={player.State} position={player.Position:0.0}s track='{player.Current?.Title}'");
            PrevTrack();
            await Task.Delay(1500);
            Say($"after Prev: state={player.State} track='{player.Current?.Title}'");
            Say($"lyrics lines loaded: {lineVms.Count} synced={lyrics?.Synced}  active line={activeLine}");
            Say($"scrobble badge: {ScrobbleBadgeText.Text}");
            Say("PLAY TEST DONE");
        }
        catch (Exception e) { Say("PLAY TEST ERROR: " + e); }
        File.WriteAllText(playTestPath!, log.ToString());
        Application.Current.Shutdown();
    }
    void OnClosing()
    {
        settings.Maximized = WindowState == WindowState.Maximized;
        var b = RestoreBounds;
        settings.WindowLeft = b.Left; settings.WindowTop = b.Top; settings.WindowWidth = b.Width; settings.WindowHeight = b.Height;
        if (ColLeft.Width.Value > 100) settings.LeftWidth = ColLeft.Width.Value;
        if (ColLyrics.Width.Value > 100) settings.LyricsWidth = ColLyrics.Width.Value;
        settings.ShowLeft = TbLeft.IsChecked == true;
        settings.ShowLyrics = TbLyrics.IsChecked == true;
        settings.Volume = muted ? volumeBeforeMute : VolumeBar.Value;
        settings.Shuffle = queue.Shuffle;
        settings.Repeat = queue.Repeat;
        settings.QueueIndex = queue.IndexOf(queue.Current);
        settings.QueuePosition = player.Position;
        if (!noPersist) { settings.Save(); queue.Save(); }
        scrobbler.Stop();
        player.Dispose();                                   // release the file first so pending lyric saves can write it
        FlushPendingSaves(force: true);
        var hwnd = new WindowInteropHelper(this).Handle;
        for (int id = 1; id <= 4; id++) Native.UnregisterHotKey(hwnd, id);
    }

    // ------------------------------------------------------------------ playback

    public void OpenItem(QueueItem item, double start, bool play)
    {
        queue.SetCurrent(item);
        player.Open(item.Track, start, play);
        playId++;
        failStreak = 0;
        ShowTrack(item.Track);
        QueueList.ScrollIntoView(item);
    }

    void PlayItem(QueueItem item, double start = 0)
    {
        try { OpenItem(item, start, play: true); }
        catch (Exception e)
        {
            Log.Error("play failed", e);
            item.Failed = true;
            Toast(e.Message);
            if (++failStreak >= 5) { player.Stop(); OnPlayerState(); return; }
            var next = queue.Next(true);
            if (next != null && next != item) PlayItem(next);
        }
    }

    void PlayPause()
    {
        if (player.Current == null)
        {
            var start = queue.Current ?? queue.Items.FirstOrDefault();
            if (start != null) PlayItem(start);
            return;
        }
        player.Toggle();
    }

    void NextTrack()
    {
        var n = queue.Next(false);
        if (n != null) PlayItem(n);
    }

    void PrevTrack()
    {
        if (player.Position > 3 && player.Current != null) { player.Position = 0; return; }
        var p = queue.Previous();
        if (p != null) PlayItem(p);
    }

    void OnTrackEnded()
    {
        var n = queue.Next(true);
        if (n != null) PlayItem(n);
        else { queue.SetCurrent(null, pushHistory: false); OnPlayerState(); }
    }

    void OnPlayerState()
    {
        BtnPlay.Content = player.State == PlayState.Playing ? "" : "";
        if (player.State == PlayState.Stopped && player.Current == null && queue.Current == null)
        {
            Title = "xPlayer";
            SeekBar.Value = 0;
            TimeNow.Text = "0:00";
        }
    }

    void Tick()
    {
        var now = DateTime.UtcNow;
        double dt = (now - lastTickAt).TotalSeconds;
        lastTickAt = now;
        bool playing = player.State == PlayState.Playing;

        if (playing && player.Current != null && playId != scrobbleStartedPlayId)
        {
            scrobbleStartedPlayId = playId;
            scrobbler.TrackStarted(player.Current, player.Duration);
        }
        scrobbler.Tick(playing, dt, player.Duration);

        double pos = player.Position, dur = player.Duration;
        if (!seeking)
        {
            updatingSeek = true;
            SeekBar.Maximum = Math.Max(1, dur);
            SeekBar.Value = Math.Min(pos, SeekBar.Maximum);
            updatingSeek = false;
            TimeNow.Text = Fmt.Time(pos);
        }
        TimeTotal.Text = Fmt.Time(dur);
        UpdateLyricsPosition(pos);

        if (++secondCounter % 4 == 0)
        {
            UpdateScrobbleBadge();
            UpdateScrobbleViews(false);
        }
    }

    // ------------------------------------------------------------------ transport handlers

    void PlayPause_Click(object s, RoutedEventArgs e) => PlayPause();
    void Next_Click(object s, RoutedEventArgs e) => NextTrack();
    void Prev_Click(object s, RoutedEventArgs e) => PrevTrack();

    void Shuffle_Click(object s, RoutedEventArgs e) => queue.Shuffle = BtnShuffle.IsChecked == true;

    void Repeat_Click(object s, RoutedEventArgs e)
    {
        queue.Repeat = queue.Repeat switch { "off" => "all", "all" => "one", _ => "off" };
        UpdateRepeatButton();
    }

    void UpdateRepeatButton()
    {
        BtnRepeat.Content = queue.Repeat == "one" ? "" : "";
        BtnRepeat.IsChecked = queue.Repeat != "off";
        BtnRepeat.ToolTip = "Repeat: " + queue.Repeat;
    }

    void Seek_Down(object s, MouseButtonEventArgs e) => seeking = true;

    void Seek_Up(object s, MouseButtonEventArgs e)
    {
        if (seeking && player.Current != null) player.Position = SeekBar.Value;
        seeking = false;
    }

    void Seek_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (seeking && !updatingSeek) TimeNow.Text = Fmt.Time(SeekBar.Value);
    }

    void Volume_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (player == null) return;
        player.Volume = e.NewValue;
        muted = false;
        BtnMute.Content = e.NewValue <= 0.001 ? "" : "";
    }

    void Mute_Click(object s, RoutedEventArgs e)
    {
        if (!muted) { volumeBeforeMute = VolumeBar.Value; muted = true; VolumeBar.Value = 0; muted = true; BtnMute.Content = ""; }
        else { VolumeBar.Value = volumeBeforeMute; muted = false; }
    }

    void OnPreviewKeyDown(object s, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or PasswordBox) return;
        switch (e.Key)
        {
            case Key.Space: PlayPause(); break;
            case Key.Right: if (player.Current != null) player.Position += 5; break;
            case Key.Left: if (player.Current != null) player.Position -= 5; break;
            case Key.N: NextTrack(); break;
            case Key.P: PrevTrack(); break;
            case Key.OemOpenBrackets: ShiftLyrics(-0.5); break;
            case Key.OemCloseBrackets: ShiftLyrics(+0.5); break;
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control: TabLibrary.IsChecked = true; SearchBox.Focus(); SearchBox.SelectAll(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------ now playing panel

    void ShowTrack(Track t)
    {
        shownTrack = t;
        NpTitle.Text = t.Title;
        NpArtist.Text = t.Artist;
        NpAlbum.Text = string.Join("  ·  ", new[] { t.Album, t.Year > 0 ? t.Year.ToString() : "" }.Where(x => x.Length > 0));
        var tech = new List<string> { t.Format };
        if (t.SampleRate > 0) tech.Add($"{t.SampleRate / 1000.0:0.#} kHz");
        if (t.Bits > 0) tech.Add($"{t.Bits}-bit");
        if (t.Bitrate > 0) tech.Add($"{t.Bitrate} kbps");
        NpTech.Text = string.Join("  ·  ", tech);
        MiniTitle.Text = t.Title;
        MiniArtist.Text = t.Artist;
        Title = $"{t.Display} — xPlayer";
        OnPlayerState();
        UpdateScrobbleBadge();
        FlushPendingSaves(force: false);
        _ = LoadCoverAsync(t);
        _ = LoadLyricsAsync(t);
        Spook();
    }

    async Task LoadCoverAsync(Track t)
    {
        var big = await Covers.GetAsync(t, 900);
        var mini = await Covers.GetAsync(t, 120);
        if (shownTrack != t) return;
        CoverImage.Background = big == null ? null : new ImageBrush(big) { Stretch = Stretch.UniformToFill };
        CoverFallback.Visibility = big == null ? Visibility.Visible : Visibility.Collapsed;
        MiniCover.Background = mini == null ? (Brush)FindResource("Crypt2") : new ImageBrush(mini) { Stretch = Stretch.UniformToFill };
    }

    void CoverHost_SizeChanged(object s, SizeChangedEventArgs e)
    {
        double side = Math.Max(120, Math.Min(e.NewSize.Width, e.NewSize.Height) - 8);
        CoverBox.Width = CoverBox.Height = Math.Min(side, 640);
    }

    /// <summary>A little ghost or bat flutters across the cover whenever the track changes.</summary>
    void Spook()
    {
        if (CoverBox.ActualWidth < 100) return;
        var id = new[] { "ghost", "bat1", "bat2", "ghost" }[Random.Shared.Next(4)];
        var sprite = Sprites.Make(id, 46);
        sprite.Opacity = 0;
        double y = 20 + Random.Shared.NextDouble() * Math.Max(20, CoverBox.ActualHeight * 0.35);
        Canvas.SetTop(sprite, y);
        SpookLayer.Children.Add(sprite);
        var move = new DoubleAnimation(-60, CoverBox.ActualWidth + 20, TimeSpan.FromSeconds(3.4)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(3.4) };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromPercent(0.2)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromPercent(0.8)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        var bob = new DoubleAnimation(y, y + 26, TimeSpan.FromSeconds(1.1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
        move.Completed += (_, _) => SpookLayer.Children.Remove(sprite);
        sprite.BeginAnimation(Canvas.LeftProperty, move);
        sprite.BeginAnimation(OpacityProperty, fade);
        sprite.BeginAnimation(Canvas.TopProperty, bob);
    }

    void UpdateScrobbleBadge()
    {
        (string text, Brush brush) = (null!, null!);
        if (!scrobbler.Configured) (text, brush) = ("Last.fm: not set up", Theme.MistBrush);
        else if (scrobbler.Auth == "error") (text, brush) = ("Last.fm: login failed", Theme.BloodBrush);
        else if (shownTrack == null) (text, brush) = ("Last.fm ready", Theme.MistBrush);
        else if (string.IsNullOrWhiteSpace(shownTrack.Artist)) (text, brush) = ("No artist tag — won't scrobble", Theme.MistBrush);
        else
        {
            var (counted, remaining, tooShort) = scrobbler.Progress;
            bool started = playId == scrobbleStartedPlayId;
            if (!started)
            {
                bool shortTrack = shownTrack.Seconds > 0 && shownTrack.Seconds < 30;
                (text, brush) = shortTrack ? ("Too short to scrobble", Theme.MistBrush)
                    : ($"Scrobbles after {Fmt.Time(Math.Ceiling(scrobbler.ThresholdFor(shownTrack.Seconds)))} of listening", Theme.MistBrush);
            }
            else if (tooShort) (text, brush) = ("Too short to scrobble", Theme.MistBrush);
            else if (!counted) (text, brush) = ($"Scrobbles in {Fmt.Time(Math.Ceiling(remaining))}", Theme.MistBrush);
            else
            {
                var row = scrobbler.CurrentRow();
                (text, brush) = row?.Status switch
                {
                    "confirmed" => ("✓ Scrobbled", Theme.SlimeBrush),
                    "sent" => ("↗ Sent · verifying…", Theme.PumpkinBrush),
                    "ignored" or "failed" => ("✕ " + row.Detail, Theme.BloodBrush),
                    "unverified" => ("? Not found in your history", Theme.PumpkinBrush),
                    _ => ("… Scrobble queued", Theme.PumpkinBrush),
                };
            }
        }
        ScrobbleBadgeText.Text = text;
        ScrobbleBadgeText.Foreground = brush;
    }

    // ------------------------------------------------------------------ toast + panels

    readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(3.2) };
    bool toastWired;

    public void Toast(string text)
    {
        ToastText.Text = text;
        ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        if (!toastWired)
        {
            toastWired = true;
            toastTimer.Tick += (_, _) =>
            {
                toastTimer.Stop();
                ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
            };
        }
        toastTimer.Stop();
        toastTimer.Start();
    }

    void TogglePanels_Click(object s, RoutedEventArgs e)
    {
        if (TbLeft.IsChecked != true && ColLeft.Width.Value > 100) settings.LeftWidth = ColLeft.Width.Value;
        if (TbLyrics.IsChecked != true && ColLyrics.Width.Value > 100) settings.LyricsWidth = ColLyrics.Width.Value;
        ApplyPanelVisibility();
    }

    void ApplyPanelVisibility()
    {
        bool left = TbLeft.IsChecked == true, lyr = TbLyrics.IsChecked == true;
        ColLeft.MinWidth = left ? 260 : 0;
        ColLeft.Width = left ? new GridLength(Math.Max(260, settings.LeftWidth)) : new GridLength(0);
        ColSplit1.Width = new GridLength(left ? 8 : 0);
        ColLyrics.MinWidth = lyr ? 280 : 0;
        ColLyrics.Width = lyr ? new GridLength(Math.Max(280, settings.LyricsWidth)) : new GridLength(0);
        ColSplit2.Width = new GridLength(lyr ? 8 : 0);
        LeftCard.Visibility = Split1.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        LyricsCard.Visibility = Split2.Visibility = lyr ? Visibility.Visible : Visibility.Collapsed;
    }

    void Settings_Click(object s, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(settings) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        settings.Save();
        Ambient.Kind = settings.Ambient;
        scrobbler.SettingsChanged();
        if (dlg.LibraryChanged || dlg.RescanRequested) _ = library.ScanAsync(settings.LibraryRoots);
        UpdateScrobbleBadge();
        UpdateScrobbleViews(true);
    }

    void UpdateScanText()
    {
        ScanText.Text = library.Scanning ? "⟳ " + library.ScanStatus : library.ScanStatus;
    }
}
