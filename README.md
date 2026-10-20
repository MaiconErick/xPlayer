# xPlayer

A Windows music player written in C# / WPF on top of NAudio. Local library, queue, synced
lyrics and native Last.fm scrobbling — built as a compact, keyboard-friendly desktop player
with its own look.

## Features

- **Playback** — NAudio WASAPI output, gapless queue, shuffle, repeat, seek, volume
  normalization.
- **Library** — folder scan with tag reading, album/artist views, instant search.
- **Synced lyrics** — LRC engine with multiple sources in one pipeline (local files, LRCLIB,
  NetEase), automatic matching and manual search.
- **Last.fm** — native scrobbling with a self-test screen that verifies the whole
  auth → now-playing → scrobble chain.
- **Now playing** — large artwork, blurred ambient background derived from the cover,
  waveform-ish progress.
- **Themes** — a full Halloween theme (fonts, sprites, ambient particles).
- **CLI mode** — `Cli.cs` allows scripted playback and scrobble tests.

## Tech

C# · .NET (WPF) · NAudio · MVVM-ish view models · no third-party UI framework

## Architecture

```
app/
├── App.xaml / MainWindow.xaml   shell and main window
├── Player.cs, PlayQueue.cs      playback engine and queue
├── Library.cs                   scanning and metadata
├── Lyrics.cs, LyricSources.cs   lyric providers and LRC engine
├── LastFm.cs                    scrobbling client
├── Settings.cs, Theme.cs        persistence and theming
└── Ambient.cs                   animated background
```

## Build

Requirements: .NET SDK (Windows).

```powershell
.\build.ps1
```

or

```powershell
dotnet build app\xPlayer.csproj -c Release
```

## License

MIT — see [LICENSE](LICENSE).
