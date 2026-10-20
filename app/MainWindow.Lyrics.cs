using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace xPlayer;

/// <summary>Animates a ScrollViewer's vertical offset (used to glide to the line being sung).</summary>
public static class ScrollAnim
{
    public static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(ScrollAnim), new PropertyMetadata(0.0, (d, e) => (d as ScrollViewer)?.ScrollToVerticalOffset((double)e.NewValue)));

    public static void To(ScrollViewer sv, double target)
    {
        target = Math.Max(0, target);
        var anim = new DoubleAnimation(sv.VerticalOffset, target, TimeSpan.FromMilliseconds(520)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        sv.BeginAnimation(OffsetProperty, anim);
    }

    public static void Stop(ScrollViewer sv)
    {
        double now = sv.VerticalOffset;
        sv.BeginAnimation(OffsetProperty, null);
        sv.ScrollToVerticalOffset(now);
    }
}

public partial class MainWindow
{
    ParsedLyrics? lyrics;
    LyricsResult? lyricsResult;
    List<LyricLineVm> lineVms = [];
    CancellationTokenSource? lyricsCts;
    int activeLine = -2;
    double lyricOffset;
    DateTime manualScrollAt = DateTime.MinValue;
    readonly List<(Track Track, ParsedLyrics Lyrics, bool Embed)> pendingSaves = [];

    // ------------------------------------------------------------------ loading

    void SetLyricsState(string? sprite, string? text)
    {
        if (text == null) { LyricsState.Visibility = Visibility.Collapsed; return; }
        LyricsState.Visibility = Visibility.Visible;
        LyricsStateSprite.Id = sprite;
        LyricsStateText.Text = text;
    }

    void ClearLyrics()
    {
        lyrics = null;
        lyricsResult = null;
        lineVms = [];
        activeLine = -2;
        LyricsItems.ItemsSource = null;
        LyricsSource.Text = "";
        LyricsScroll.ScrollToVerticalOffset(0);
    }

    async Task LoadLyricsAsync(Track t, bool forceOnline = false)
    {
        lyricsCts?.Cancel();
        var cts = lyricsCts = new CancellationTokenSource();
        ClearLyrics();
        lyricOffset = settings.LyricOffsets.GetValueOrDefault(t.Path);
        UpdateOffsetText();
        SetLyricsState("ghost", "Looking for lyrics…");

        LyricsResult? local = null;
        if (!forceOnline)
        {
            local = await Task.Run(() => lyricsService.LoadLocal(t));
            if (cts.IsCancellationRequested) return;
            if (local?.Lyrics != null) ShowLyrics(local.Lyrics, local.Origin);
        }

        bool needOnline = settings.OnlineLyrics && (forceOnline || local?.Lyrics == null || !local.Lyrics.Synced);
        if (!needOnline)
        {
            if (local?.Lyrics == null) SetLyricsState("moon", "No lyrics for this track");
            return;
        }

        if (local?.Lyrics == null) SetLyricsState("cauldron", "Searching lyric sources…");
        LyricsResult res;
        try
        {
            res = await lyricsService.FetchOnlineAsync(t, cts.Token, new Progress<string>(m =>
            {
                if (!cts.IsCancellationRequested && lyrics == null) SetLyricsState("cauldron", m);
            }), force: forceOnline);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e)
        {
            Log.Error("online lyrics", e);
            if (!cts.IsCancellationRequested && lyrics == null) SetLyricsState("bat1", "Could not reach the lyric sources");
            return;
        }
        if (cts.IsCancellationRequested) return;

        lyricsResult = res;
        if (res.Lyrics != null && (lyrics == null || (res.Lyrics.Synced && !lyrics.Synced)))
        {
            ShowLyrics(res.Lyrics, res.Origin + " (online)");
            if (settings.EmbedLyrics || settings.SidecarLyrics) QueueSave(t, res.Lyrics, embed: settings.EmbedLyrics);
        }
        else if (lyrics == null) SetLyricsState("moon", res.Error ?? "No lyrics found");
    }

    void ShowLyrics(ParsedLyrics l, string origin)
    {
        lyrics = l;
        lineVms = l.Lines.Select(x => new LyricLineVm(x, l.Synced, settings.ShowTranslations)).ToList();
        TbTranslations.Visibility = l.Lines.Any(x => x.Translation.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var vm in lineVms) vm.Apply(1, l.Synced);
        activeLine = -2;
        LyricsItems.ItemsSource = lineVms;
        LyricsSource.Text = $"{origin} · {(l.Synced ? "synced" : "plain text")}";
        SetLyricsState(null, null);
        LyricsScroll.ScrollToVerticalOffset(0);
        FitLyricsPadding();
        UpdateLyricsPosition(player.Position);
    }

    // ------------------------------------------------------------------ following the music

    void FitLyricsPadding()
    {
        double h = LyricsScroll.ActualHeight;
        if (h > 0) LyricsItems.Margin = new Thickness(24, h * 0.42, 24, h * 0.5);
    }

    void UpdateLyricsPosition(double pos)
    {
        if (lyrics is not { Synced: true }) return;
        int idx = lyrics.IndexAt(pos + lyricOffset);
        if (idx != activeLine)
        {
            activeLine = idx;
            for (int i = 0; i < lineVms.Count; i++) lineVms[i].Apply(i - idx, true);
            if (DateTime.UtcNow - manualScrollAt > TimeSpan.FromSeconds(4)) Dispatcher.BeginInvoke(() => ScrollToLine(idx), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else if (manualScrollAt != DateTime.MinValue && DateTime.UtcNow - manualScrollAt > TimeSpan.FromSeconds(4))
        {
            manualScrollAt = DateTime.MinValue;                       // user stopped browsing: glide back to the current line
            ScrollToLine(activeLine);
        }
    }

    void ScrollToLine(int idx)
    {
        if (idx < 0 || idx >= lineVms.Count) { if (idx < 0) ScrollAnim.To(LyricsScroll, 0); return; }
        if (LyricsItems.ItemContainerGenerator.ContainerFromIndex(idx) is not FrameworkElement el || el.ActualHeight == 0) return;
        double top = el.TransformToAncestor(LyricsScroll).Transform(new Point(0, 0)).Y;
        double target = LyricsScroll.VerticalOffset + top + el.ActualHeight / 2 - LyricsScroll.ViewportHeight / 2;
        ScrollAnim.To(LyricsScroll, target);
    }

    void LyricsScroll_Wheel(object s, MouseWheelEventArgs e)
    {
        manualScrollAt = DateTime.UtcNow;
        ScrollAnim.Stop(LyricsScroll);
        LyricsScroll.ScrollToVerticalOffset(LyricsScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    void LyricLine_Click(object s, MouseButtonEventArgs e)
    {
        if (lyrics is not { Synced: true } || player.Current == null) return;
        if ((s as FrameworkElement)?.Tag is not LyricLineVm vm) return;
        player.Position = Math.Max(0, vm.Line.Time - lyricOffset);
        manualScrollAt = DateTime.MinValue;
    }

    // ------------------------------------------------------------------ translations

    void Translations_Click(object s, RoutedEventArgs e)
    {
        settings.ShowTranslations = TbTranslations.IsChecked == true;
        foreach (var vm in lineVms) vm.ShowTranslation(settings.ShowTranslations);
        // lines changed height: glide back to the line being sung
        if (activeLine >= 0) Dispatcher.BeginInvoke(() => ScrollToLine(activeLine), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------------ offset

    void OffsetEarlier_Click(object s, RoutedEventArgs e) => ShiftLyrics(-0.5);
    void OffsetLater_Click(object s, RoutedEventArgs e) => ShiftLyrics(+0.5);

    void ShiftLyrics(double delta)
    {
        if (shownTrack == null || lyrics is not { Synced: true }) return;
        lyricOffset = Math.Round(lyricOffset + delta, 1);
        if (Math.Abs(lyricOffset) < 0.01) settings.LyricOffsets.Remove(shownTrack.Path);
        else settings.LyricOffsets[shownTrack.Path] = lyricOffset;
        UpdateOffsetText();
        activeLine = -2;
        UpdateLyricsPosition(player.Position);
    }

    void UpdateOffsetText() => OffsetText.Text = Math.Abs(lyricOffset) < 0.01 ? "" : $"{lyricOffset:+0.0;-0.0}s";

    // ------------------------------------------------------------------ sources, refresh, save

    async void Sources_Click(object s, RoutedEventArgs e)
    {
        if (shownTrack == null) return;
        var t = shownTrack;
        if (lyricsResult == null || lyricsResult.Candidates.Count == 0)
        {
            Toast("Searching all sources…");
            try { lyricsResult = await lyricsService.FetchOnlineAsync(t, CancellationToken.None, null, force: true); }
            catch (Exception ex) { Toast("Lyric search failed: " + ex.Message); return; }
            if (shownTrack != t) return;
        }
        var res = lyricsResult;
        if (res == null || res.Candidates.Count == 0) { Toast(res?.Error ?? "No other sources have lyrics for this track"); return; }

        var menu = new ContextMenu { PlacementTarget = BtnSources, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var c in res.Candidates.Take(14))
        {
            var cand = c;
            var item = new MenuItem { Header = $"{cand.Source}  ·  {(cand.HasSynced ? "synced" : "plain")}  ·  {cand.Artist} – {cand.Title}" };
            item.Click += (_, _) =>
            {
                ShowLyrics(LyricsService.Parse(cand), cand.Source);
                LyricsService.Remember(t, cand);
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    void LyricsRefresh_Click(object s, RoutedEventArgs e)
    {
        if (shownTrack != null) _ = LoadLyricsAsync(shownTrack, forceOnline: true);
    }

    void LyricsSave_Click(object s, RoutedEventArgs e)
    {
        if (shownTrack == null || lyrics == null) { Toast("No lyrics to save"); return; }
        SaveNow(shownTrack, lyrics);
    }

    void QueueSave(Track t, ParsedLyrics l, bool embed)
    {
        pendingSaves.RemoveAll(p => p.Track.Path == t.Path);
        pendingSaves.Add((t, l, embed));
    }

    void SaveNow(Track t, ParsedLyrics l)
    {
        if (player.Current?.Path == t.Path && t.Path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            QueueSave(t, l, embed: true);
            Toast("The file is playing: lyrics will be embedded when the track changes");
            return;
        }
        try { Toast(LyricsService.SaveToFile(t, l, allowEmbed: true)); }
        catch (Exception e) { Log.Error("save lyrics", e); Toast("Could not save: " + e.Message); }
    }

    /// <summary>Writes lyrics for tracks that were busy (playing) when the save was requested.</summary>
    void FlushPendingSaves(bool force)
    {
        foreach (var p in pendingSaves.ToList())
        {
            if (!force && player.Current?.Path == p.Track.Path) continue;
            pendingSaves.Remove(p);
            try
            {
                LyricsService.SaveToFile(p.Track, p.Lyrics, allowEmbed: p.Embed);
                Log.Info($"lyrics saved for {p.Track.Path}");
            }
            catch (Exception e) { Log.Error($"lyrics save failed for {p.Track.Path}", e); }
        }
    }
}
