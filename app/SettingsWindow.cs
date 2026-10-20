using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace xPlayer;

/// <summary>Modal settings, built in code to stay small. Edits a copy and writes back to AppSettings on Save.</summary>
public sealed class SettingsWindow : Window
{
    readonly AppSettings s;
    readonly ListBox roots = new() { Height = 120, Background = (Brush)Application.Current.FindResource("Crypt2"), BorderBrush = (Brush)Application.Current.FindResource("Edge"), Foreground = Brushes.White };
    readonly TextBox user = new() { Style = (Style)Application.Current.FindResource("Input") };
    readonly PasswordBox pass = new() { Background = (Brush)Application.Current.FindResource("Crypt2"), Foreground = (Brush)Application.Current.FindResource("Bone"), BorderBrush = (Brush)Application.Current.FindResource("Edge"), Padding = new Thickness(10, 7, 10, 7), FontSize = 13 };
    readonly CheckBox lastFmOn = new() { Content = "Scrobble to Last.fm" };
    readonly CheckBox online = new() { Content = "Search lyrics online when the file has none (or only plain text)" };
    readonly CheckBox embed = new() { Content = "Save fetched lyrics into the mp3 (like embed_lyrics.pyw: USLT synced + plain)" };
    readonly CheckBox sidecar = new() { Content = "Save fetched lyrics as a .lrc/.txt next to the file (when not embedding)" };
    readonly ComboBox ambient = new() { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
    readonly StackPanel scrobbleMode = new();
    readonly TextBox scrobbleValue = new() { Style = (Style)Application.Current.FindResource("Input"), Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock scrobbleHint = new() { Foreground = (Brush)Application.Current.FindResource("Mist"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    public bool LibraryChanged { get; private set; }
    public bool RescanRequested { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        s = settings;
        Title = "xPlayer settings";
        Width = 660; Height = 760; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("WindowGrad");
        Foreground = (Brush)Application.Current.FindResource("Bone");
        FontFamily = new FontFamily("Segoe UI");

        foreach (var r in s.LibraryRoots) roots.Items.Add(r);
        user.Text = s.LastFm.Username;
        lastFmOn.IsChecked = s.LastFm.Enabled;
        online.IsChecked = s.OnlineLyrics;
        embed.IsChecked = s.EmbedLyrics;
        sidecar.IsChecked = s.SidecarLyrics;
        foreach (var (id, label) in AmbientLayer.Kinds) ambient.Items.Add(new ComboBoxItem { Content = label, Tag = id, IsSelected = id == s.Ambient });

        var panel = new StackPanel { Margin = new Thickness(26, 20, 26, 10) };
        panel.Children.Add(Heading("Library"));
        panel.Children.Add(Note("Folders scanned for music. Tags and covers are read straight from the files; nothing else is stored."));
        panel.Children.Add(roots);
        var rootButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        rootButtons.Children.Add(Btn("Add folder…", AddFolder));
        rootButtons.Children.Add(Btn("Remove", (_, _) => { if (roots.SelectedItem != null) roots.Items.Remove(roots.SelectedItem); }));
        rootButtons.Children.Add(Btn("Rescan now", (_, _) => { RescanRequested = true; Toast(rootButtons, "Will rescan when you save"); }));
        panel.Children.Add(rootButtons);

        panel.Children.Add(Heading("Last.fm"));
        panel.Children.Add(lastFmOn);
        panel.Children.Add(Label("Username"));
        panel.Children.Add(user);
        panel.Children.Add(Label("Password (leave empty to keep the current login)"));
        panel.Children.Add(pass);
        panel.Children.Add(Note(s.LastFm.SessionKey.Length > 0 ? "Logged in before — a session key is saved." : "Not logged in yet."));

        panel.Children.Add(Label("When does a play count as a scrobble?"));
        foreach (var (tag, text) in new[] { ("seconds", "After a fixed time (instant)"), ("percent", "After a percentage of the track"), ("standard", "Last.fm standard (half the track, max 4 min)") })
        {
            var rb = new RadioButton { Content = text, Tag = tag, GroupName = "scrobbleMode", IsChecked = s.ScrobbleMode == tag, Margin = new Thickness(0, 3, 0, 3), Foreground = (Brush)Application.Current.FindResource("Bone") };
            rb.Checked += (_, _) => UpdateScrobbleHint();
            scrobbleMode.Children.Add(rb);
        }
        if (!scrobbleMode.Children.OfType<RadioButton>().Any(r => r.IsChecked == true)) ((RadioButton)scrobbleMode.Children[0]).IsChecked = true;
        scrobbleValue.Text = s.ScrobbleValue.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        panel.Children.Add(scrobbleMode);
        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        valueRow.Children.Add(scrobbleValue);
        panel.Children.Add(valueRow);
        panel.Children.Add(scrobbleHint);
        UpdateScrobbleHint();

        panel.Children.Add(Heading("Lyrics"));
        panel.Children.Add(online);
        panel.Children.Add(embed);
        panel.Children.Add(sidecar);
        panel.Children.Add(Note("Sources: lrclib, NetEase, Kugou, QQ Music, Musixmatch, Melon (ported from the foobar ESLyric scripts and embed_lyrics.pyw)."));

        panel.Children.Add(Heading("Appearance"));
        panel.Children.Add(Label("Background animation"));
        panel.Children.Add(ambient);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(26, 6, 26, 18) };
        var cancel = Btn("Cancel", (_, _) => DialogResult = false);
        var save = Btn("Save", (_, _) => Save());
        save.Margin = new Thickness(8, 0, 0, 0);
        bar.Children.Add(cancel);
        bar.Children.Add(save);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetRow(bar, 1);
        root.Children.Add(bar);
        Content = root;
    }

    static TextBlock Heading(string t) => new()
    {
        Text = t, FontFamily = (FontFamily)Application.Current.FindResource("Display"), FontSize = 24,
        Foreground = (Brush)Application.Current.FindResource("Pumpkin"), Margin = new Thickness(0, 22, 0, 6),
    };

    static TextBlock Note(string t) => new() { Text = t, Foreground = (Brush)Application.Current.FindResource("Mist"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) };
    static TextBlock Label(string t) => new() { Text = t, Foreground = (Brush)Application.Current.FindResource("Mist"), FontSize = 12, Margin = new Thickness(0, 10, 0, 4) };

    static Button Btn(string text, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.FindResource("Btn"), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += click;
        return b;
    }

    static void Toast(FrameworkElement near, string text) => (near.Parent as Panel)?.Children.Add(new TextBlock { Text = text, Foreground = (Brush)Application.Current.FindResource("Mist"), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });

    string SelectedMode => scrobbleMode.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "seconds";

    void UpdateScrobbleHint()
    {
        scrobbleValue.IsEnabled = SelectedMode != "standard";
        scrobbleHint.Text = SelectedMode switch
        {
            "seconds" => "Seconds of listening before the track is scrobbled (5 = nearly instant). Tracks shorter than 30 s are never scrobbled: Last.fm rejects them.",
            "percent" => "Percent of the track that has to be played (1–100).",
            _ => "Plays count after half of the track, or 4 minutes, whichever comes first.",
        };
    }

    void AddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Add a music folder" };
        if (dlg.ShowDialog(this) == true && !roots.Items.Contains(dlg.FolderName)) roots.Items.Add(dlg.FolderName);
    }

    void Save()
    {
        var newRoots = roots.Items.Cast<string>().ToList();
        LibraryChanged = !newRoots.SequenceEqual(s.LibraryRoots, StringComparer.OrdinalIgnoreCase);
        s.LibraryRoots = newRoots;
        s.LastFm.Enabled = lastFmOn.IsChecked == true;
        var name = user.Text.Trim();
        if (name != s.LastFm.Username || pass.Password.Length > 0)
        {
            s.LastFm.Username = name;
            s.LastFm.SessionKey = "";
            if (pass.Password.Length > 0)
                s.LastFm.PasswordHash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(pass.Password))).ToLowerInvariant();
        }
        s.ScrobbleMode = SelectedMode;
        if (double.TryParse(scrobbleValue.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sv))
            s.ScrobbleValue = s.ScrobbleMode == "percent" ? Math.Clamp(sv, 1, 100) : Math.Clamp(sv, 1, 600);
        s.OnlineLyrics = online.IsChecked == true;
        s.EmbedLyrics = embed.IsChecked == true;
        s.SidecarLyrics = sidecar.IsChecked == true;
        if (ambient.SelectedItem is ComboBoxItem { Tag: string kind }) s.Ambient = kind;
        DialogResult = true;
    }
}
