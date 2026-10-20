using System.Diagnostics;
using System.Text;

namespace xPlayer;

/// <summary>Headless self-checks: `xPlayer.exe --lyrics-test out.txt file.mp3` runs every lyric source against a real file.</summary>
public static class Cli
{
    public static bool TryRun(string[] args)
    {
        int shi = Array.IndexOf(args, "--settings-shot");
        if (shi >= 0 && shi + 1 < args.Length)
        {
            var w = new SettingsWindow(new AppSettings()) { Width = 660, Height = 940, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = 50, Top = 20 };
            w.Show();
            w.UpdateLayout();
            var root = (System.Windows.FrameworkElement)w.Content;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = File.Create(args[shi + 1])) enc.Save(fs);
            return true;
        }
        int ci = Array.IndexOf(args, "--scrobble-test");
        if (ci >= 0 && ci + 1 < args.Length)
        {
            File.WriteAllText(args[ci + 1], ScrobbleSelfTest.Run());
            return true;
        }
        int di = Array.IndexOf(args, "--decode-test");
        if (di >= 0 && di + 1 < args.Length)
        {
            var report = new StringBuilder();
            foreach (var f in args.Skip(di + 2))
            {
                try
                {
                    using NAudio.Wave.WaveStream r = f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ? new NAudio.Vorbis.VorbisWaveReader(f) : new NAudio.Wave.AudioFileReader(f);
                    var sp = r as NAudio.Wave.ISampleProvider ?? NAudio.Wave.WaveExtensionMethods.ToSampleProvider(r);
                    var buf = new float[r.WaveFormat.SampleRate * r.WaveFormat.Channels * 2];
                    int n = sp.Read(buf, 0, buf.Length);
                    r.CurrentTime = TimeSpan.FromSeconds(Math.Min(30, r.TotalTime.TotalSeconds / 2));
                    int n2 = sp.Read(buf, 0, buf.Length);
                    float peak = buf.Take(n2).Select(Math.Abs).DefaultIfEmpty(0).Max();
                    report.AppendLine($"OK   {Path.GetExtension(f),-6} {r.TotalTime.TotalSeconds,6:0.0}s {r.WaveFormat.SampleRate}Hz {r.WaveFormat.Channels}ch read={n} afterSeek={n2} peak={peak:0.00}  {Path.GetFileName(f)}");
                }
                catch (Exception e) { report.AppendLine($"FAIL {Path.GetExtension(f),-6} {e.GetType().Name}: {e.Message}  {Path.GetFileName(f)}"); }
            }
            File.WriteAllText(args[di + 1], report.ToString());
            return true;
        }
        int si = Array.IndexOf(args, "--scan-test");
        if (si >= 0 && si + 1 < args.Length)
        {
            var lib = new Library();
            var sw0 = Stopwatch.StartNew();
            var roots = args.Skip(si + 2).ToList();
            Task.Run(() => lib.ScanAsync(roots)).GetAwaiter().GetResult();
            File.WriteAllText(args[si + 1], $"{lib.Tracks.Count} tracks in {sw0.Elapsed.TotalSeconds:0.0}s ({lib.ScanStatus})" + Environment.NewLine
                + string.Join(Environment.NewLine, lib.Tracks.Take(5).Select(t => $"{t.Artist} | {t.Title} | {t.Album} | {t.Seconds:0}s | {t.Format} {t.Bitrate}kbps")));
            return true;
        }
        int i = Array.IndexOf(args, "--lyrics-test");
        if (i < 0 || i + 2 >= args.Length) return false;
        var outPath = args[i + 1];
        var sb = new StringBuilder();
        foreach (var path in args.Skip(i + 2))
        {
            var t = TagReader.Read(path);
            sb.AppendLine($"=== {t.Artist} - {t.Title} | {t.Album} | {t.Seconds:0}s | genre '{t.Genre}' kpop={t.IsKpop}");
            var svc = new LyricsService();
            var q = LyricQuery.Of(t);
            foreach (var s in svc.Sources)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    var found = s.FindAsync(q, cts.Token).GetAwaiter().GetResult();
                    sb.AppendLine($"  [{s.Name}] {found.Count} candidate(s) in {sw.ElapsedMilliseconds} ms");
                    foreach (var c in found.Take(3))
                    {
                        var text = c.Synced ?? c.Plain ?? "";
                        var parsed = LrcParser.Parse(text, c.Source);
                        sb.AppendLine($"      {(c.HasSynced ? "SYNCED" : "plain ")} '{c.Artist}' - '{c.Title}' lines={parsed.Lines.Count} first='{parsed.Lines.FirstOrDefault()?.Text}'");
                    }
                }
                catch (Exception e) { sb.AppendLine($"  [{s.Name}] FAILED after {sw.ElapsedMilliseconds} ms: {e.GetType().Name}: {e.Message}"); }
            }
            var sw2 = Stopwatch.StartNew();
            var res = svc.FetchOnlineAsync(t, CancellationToken.None, null, force: true).GetAwaiter().GetResult();
            sb.AppendLine($"  >> picked: {(res.Lyrics == null ? "NOTHING (" + res.Error + ")" : $"{res.Origin} synced={res.Lyrics.Synced} lines={res.Lyrics.Lines.Count}")} ; {res.Candidates.Count} acceptable candidates ; {sw2.ElapsedMilliseconds} ms");
            foreach (var c in res.Candidates.Take(6)) sb.AppendLine($"      {c.Score:0.00} {c.Label}");
        }
        File.WriteAllText(outPath, sb.ToString());
        return true;
    }
}
