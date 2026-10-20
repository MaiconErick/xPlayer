using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace xPlayer;

public partial class MainWindow
{
    readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    bool searchWired;
    Point dragStart;
    bool dragArmed;

    // ------------------------------------------------------------------ tabs

    void Tab_Checked(object s, RoutedEventArgs e)
    {
        if (ViewLibrary == null) return;
        ViewLibrary.Visibility = TabLibrary.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewQueue.Visibility = TabQueue.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ViewScrobbles.Visibility = TabScrobbles.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (TabScrobbles.IsChecked == true) UpdateScrobbleViews(true);
    }

    // ------------------------------------------------------------------ library

    void Search_Changed(object s, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!searchWired)
        {
            searchWired = true;
            searchTimer.Tick += (_, _) => { searchTimer.Stop(); ShowLibrary(); };
        }
        searchTimer.Stop();
        searchTimer.Start();
    }

    void ShowLibrary()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(ShowLibrary); return; }
        var q = SearchBox.Text.Trim();
        var list = q.Length == 0 ? library.Tracks : library.Search(q);
        LibraryList.ItemsSource = list;
        LibraryInfo.Text = q.Length == 0 ? $"{library.Tracks.Count:N0} tracks" : $"{list.Count:N0} of {library.Tracks.Count:N0} tracks";
        UpdateScanText();
    }

    IEnumerable<Track> SelectedTracks() => LibraryList.SelectedItems.Cast<Track>().ToList();

    void Library_DoubleClick(object s, MouseButtonEventArgs e)
    {
        if (ItemAt<ListBoxItem>(e.OriginalSource) == null || LibraryList.SelectedItem is not Track t) return;
        PlayWithAlbum(t);
    }

    void PlayWithAlbum(Track t)
    {
        var album = string.IsNullOrWhiteSpace(t.Album) ? [t] : library.AlbumOf(t);
        if (album.Count == 0) album = [t];
        var items = queue.Replace(album);
        var idx = Math.Max(0, album.FindIndex(x => x.Path == t.Path));
        PlayItem(items[idx]);
    }

    void Library_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && LibraryList.SelectedItem is Track t) { PlayWithAlbum(t); e.Handled = true; }
    }

    void Library_ContextMenuOpening(object s, ContextMenuEventArgs e)
    {
        if (LibraryList.SelectedItems.Count == 0) e.Handled = true;
    }

    void LibPlay_Click(object s, RoutedEventArgs e)
    {
        var tracks = SelectedTracks().ToList();
        if (tracks.Count == 0) return;
        var items = queue.Replace(tracks);
        PlayItem(items[0]);
    }

    void LibPlayNext_Click(object s, RoutedEventArgs e) { queue.PlayNext(SelectedTracks()); Toast("Queued to play next"); }
    void LibAdd_Click(object s, RoutedEventArgs e) { queue.Add(SelectedTracks()); Toast("Added to the queue"); }
    void LibAlbum_Click(object s, RoutedEventArgs e) { if (LibraryList.SelectedItem is Track t) PlayWithAlbum(t); }
    void LibExplorer_Click(object s, RoutedEventArgs e) { if (LibraryList.SelectedItem is Track t) RevealInExplorer(t.Path); }

    static void RevealInExplorer(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception e) { Log.Error("explorer", e); }
    }

    // ------------------------------------------------------------------ queue

    void UpdateQueueInfo()
    {
        double total = queue.Items.Sum(i => i.Track.Seconds);
        QueueInfo.Text = queue.Items.Count == 0 ? "" : $"{queue.Items.Count:N0} tracks · {Fmt.Time(total)}";
    }

    void Queue_DoubleClick(object s, MouseButtonEventArgs e)
    {
        if (ItemAt<ListBoxItem>(e.OriginalSource) == null || QueueList.SelectedItem is not QueueItem q) return;
        PlayItem(q);
    }

    void QueuePlay_Click(object s, RoutedEventArgs e) { if (QueueList.SelectedItem is QueueItem q) PlayItem(q); }
    void QueueRemove_Click(object s, RoutedEventArgs e) => RemoveSelectedFromQueue();
    void QueueExplorer_Click(object s, RoutedEventArgs e) { if (QueueList.SelectedItem is QueueItem q) RevealInExplorer(q.Track.Path); }
    void QueueClear_Click(object s, RoutedEventArgs e) { queue.Clear(); }
    void QueueLocate_Click(object s, RoutedEventArgs e) { if (queue.Current != null) { QueueList.ScrollIntoView(queue.Current); QueueList.SelectedItem = queue.Current; } }

    void Queue_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { RemoveSelectedFromQueue(); e.Handled = true; }
        else if (e.Key == Key.Enter && QueueList.SelectedItem is QueueItem q) { PlayItem(q); e.Handled = true; }
    }

    void RemoveSelectedFromQueue()
    {
        var sel = QueueList.SelectedItems.Cast<QueueItem>().ToList();
        if (sel.Count == 0) return;
        bool removingCurrent = sel.Contains(queue.Current!);
        var after = removingCurrent ? NextAfter(sel) : null;
        queue.Remove(sel);
        if (removingCurrent)
        {
            if (after != null) PlayItem(after);
            else { player.Stop(); OnPlayerState(); }
        }
    }

    QueueItem? NextAfter(List<QueueItem> removing)
    {
        if (queue.Current == null) return null;
        int i = queue.Items.IndexOf(queue.Current);
        for (int k = i + 1; k < queue.Items.Count; k++) if (!removing.Contains(queue.Items[k])) return queue.Items[k];
        return null;
    }

    // ------------------------------------------------------------------ drag out of the lists

    void List_PreviewMouseDown(object s, MouseButtonEventArgs e)
    {
        var item = ItemAt<ListBoxItem>(e.OriginalSource);
        if (item == null) { dragArmed = false; return; }
        dragStart = e.GetPosition(null);
        dragArmed = e.LeftButton == MouseButtonState.Pressed;
    }

    bool ShouldDrag(MouseEventArgs e)
    {
        if (!dragArmed || e.LeftButton != MouseButtonState.Pressed) return false;
        var d = e.GetPosition(null) - dragStart;
        return Math.Abs(d.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(d.Y) > SystemParameters.MinimumVerticalDragDistance;
    }

    void Library_PreviewMouseMove(object s, MouseEventArgs e)
    {
        if (!ShouldDrag(e) || LibraryList.SelectedItems.Count == 0) return;
        dragArmed = false;
        var data = new DataObject("xPlayer.Tracks", SelectedTracks().ToList());
        DragDrop.DoDragDrop(LibraryList, data, DragDropEffects.Copy);
    }

    void Queue_PreviewMouseMove(object s, MouseEventArgs e)
    {
        if (!ShouldDrag(e) || QueueList.SelectedItems.Count == 0) return;
        dragArmed = false;
        var data = new DataObject("xPlayer.QueueItems", QueueList.SelectedItems.Cast<QueueItem>().ToList());
        DragDrop.DoDragDrop(QueueList, data, DragDropEffects.Move);
    }

    // ------------------------------------------------------------------ drop onto the queue / the window

    void Queue_DragOver(object s, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("xPlayer.QueueItems") ? DragDropEffects.Move
                  : e.Data.GetDataPresent("xPlayer.Tracks") || e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy
                  : DragDropEffects.None;
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    int DropIndex(DragEventArgs e)
    {
        var pos = e.GetPosition(QueueList);
        var item = ItemAt<ListBoxItem>(QueueList.InputHitTest(pos));
        if (item == null) return queue.Items.Count;
        int idx = QueueList.ItemContainerGenerator.IndexFromContainer(item);
        if (idx < 0) return queue.Items.Count;
        var local = e.GetPosition(item);
        return local.Y > item.ActualHeight / 2 ? idx + 1 : idx;
    }

    async void Queue_Drop(object s, DragEventArgs e)
    {
        e.Handled = true;
        DropOverlay.Visibility = Visibility.Collapsed;
        int at = DropIndex(e);
        if (e.Data.GetData("xPlayer.QueueItems") is List<QueueItem> moving)
        {
            var ordered = moving.OrderBy(q => queue.Items.IndexOf(q)).ToList();
            int before = ordered.Count(q => queue.Items.IndexOf(q) < at);
            foreach (var q in ordered) queue.Items.Remove(q);
            at = Math.Clamp(at - before, 0, queue.Items.Count);
            foreach (var q in ordered) queue.Items.Insert(at++, q);
            QueueList.SelectedItems.Clear();
            foreach (var q in ordered) QueueList.SelectedItems.Add(q);
            queue.Save();
        }
        else if (e.Data.GetData("xPlayer.Tracks") is List<Track> tracks)
        {
            queue.Add(tracks, at);
            Toast($"Added {tracks.Count} track(s)");
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var tracks2 = await Task.Run(() => DroppedTracks(files));
            queue.Add(tracks2, at);
            Toast($"Added {tracks2.Count} track(s)");
        }
    }

    void Window_DragOver(object s, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("xPlayer.Tracks");
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok)
        {
            DropOverlay.Visibility = Visibility.Visible;
            DropText.Text = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? "Drop to play now" : "Drop to add to the queue";
        }
        e.Handled = true;
    }

    async void Window_Drop(object s, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
        bool replace = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Data.GetData("xPlayer.Tracks") is List<Track> tracks)
        {
            AddTracks(tracks, replace);
            return;
        }
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            await AddPathsAsync(files, replace, play: true);
    }

    /// <summary>Files, folders and .m3u playlists -> tracks (done off the UI thread; tags are read in parallel).</summary>
    List<Track> DroppedTracks(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p))
                    files.AddRange(Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                        .Where(Library.IsAudio).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase));
                else if (p.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                    files.AddRange(ReadPlaylist(p));
                else if (File.Exists(p) && Library.IsAudio(p))
                    files.Add(p);
            }
            catch (Exception e) { Log.Error($"drop: {p}", e); }
        }
        return files.AsParallel().AsOrdered().Select(library.Resolve).ToList();
    }

    static IEnumerable<string> ReadPlaylist(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var full = Path.IsPathRooted(line) ? line : Path.GetFullPath(Path.Combine(dir, line));
            if (File.Exists(full) && Library.IsAudio(full)) yield return full;
        }
    }

    public async Task AddPathsAsync(IEnumerable<string> paths, bool replace, bool play)
    {
        var list = paths.ToList();
        Toast("Reading files…");
        var tracks = await Task.Run(() => DroppedTracks(list));
        if (tracks.Count == 0) { Toast("No audio files found"); return; }
        AddTracks(tracks, replace, play);
    }

    void AddTracks(List<Track> tracks, bool replace, bool play = true)
    {
        bool idle = player.Current == null || player.State == PlayState.Stopped;
        List<QueueItem> added;
        if (replace) { added = queue.Replace(tracks); }
        else added = queue.Add(tracks);
        Toast(replace ? $"Playing {tracks.Count} track(s)" : $"Added {tracks.Count} track(s) to the queue");
        if (added.Count == 0) return;
        if (replace || idle)
        {
            if (play) PlayItem(added[0]);
            else try { OpenItem(added[0], 0, play: false); } catch (Exception e) { Log.Error("open", e); }
        }
    }

    /// <summary>Called for a second instance ("Open with xPlayer" while it is already running).</summary>
    public void OpenFromOtherInstance(string[] paths)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (paths.Length > 0) _ = AddPathsAsync(paths, replace: true, play: true);
    }

    // ------------------------------------------------------------------ helpers

    static T? ItemAt<T>(object? source) where T : DependencyObject
    {
        var d = source as DependencyObject;
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    // ------------------------------------------------------------------ scrobbles tab

    void UpdateScrobbleViews(bool force)
    {
        if (!force && scrobbler.Store.Version == scrobbleVersion) { UpdateScrobbleHeader(); return; }
        scrobbleVersion = scrobbler.Store.Version;
        if (force || TabScrobbles.IsChecked == true)
            ScrobbleList.ItemsSource = scrobbler.Store.Recent(300).Select(r => new ScrobbleVm(r)).ToList();
        UpdateScrobbleHeader();
    }

    void UpdateScrobbleHeader()
    {
        var c = scrobbler.Store.Counts();
        ScrobbleStats.Text = $"Today {c.TodayOk}/{c.Today}  ·  {c.Confirmed:N0} confirmed  ·  {c.Pending} pending  ·  {c.Problems} problems";
        string acct = !scrobbler.Configured ? "Last.fm is not set up (Settings → Last.fm)"
            : scrobbler.Auth == "error" ? "Login failed: " + scrobbler.AuthDetail
            : $"{settings.LastFm.Username}" + (scrobbler.AccountPlays is int n ? $" · {n:N0} scrobbles on Last.fm" : "") + (scrobbler.Online ? "" : " · offline, retrying");
        ScrobbleAccount.Text = acct;
    }

    List<long> SelectedScrobbleIds() => ScrobbleList.SelectedItems.Cast<ScrobbleVm>().Select(v => v.Row.Id).ToList();

    void ScrobbleRetry_Click(object s, RoutedEventArgs e)
    {
        var rows = scrobbler.Store.Recent(2000);
        scrobbler.Store.Requeue(rows.Where(r => r.Status == "failed").Select(r => r.Id));
        scrobbler.Store.Reverify(rows.Where(r => r.Status == "unverified").Select(r => r.Id));
        scrobbler.Wake();
    }

    void ScrobbleSend_Click(object s, RoutedEventArgs e) { scrobbler.Store.Requeue(SelectedScrobbleIds()); scrobbler.Wake(); }
    void ScrobbleVerify_Click(object s, RoutedEventArgs e) { scrobbler.Store.Reverify(SelectedScrobbleIds()); scrobbler.Wake(); }
}
