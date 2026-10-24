# Serter – Serato Mix Assistant

A small always-on-top Windows 11 window that sits next to **Serato DJ** and tells you which tracks
will mix well with whatever is loaded on **Deck A** or **Deck B**.

- Updates automatically the moment you load a new track (including TIDAL / streaming tracks).
- Flick the **DECK A | DECK B** switch (or press **Ctrl+Tab**) to see suggestions for the other deck.
  Both decks are worked out in the background, so switching is instant.
- Optional **Auto-switch** follows whichever deck you loaded last.
- Every suggestion shows **artist, track name, genre, BPM and key** (Camelot + musical, e.g. `8A · Am`),
  colour-coded green / amber / red for how well the key and tempo fit.
- **Copy** button on every row puts `Artist Title` on the clipboard – paste it straight into Serato's
  search (TIDAL search included). The format is configurable.
- Hides tracks you've already played this session.
- Type `Artist - Title` in the box at the bottom to get suggestions without Serato (handy for prep).

## How it works

### Reading the decks

Serato doesn't offer a live API, but it writes every track you load to its history as you go, with
the deck number and its own BPM/key analysis. Serter reads that history, read-only:

| Serato version | Where Serter looks |
| --- | --- |
| Serato DJ 4.x | `%APPDATA%\Serato\Library\master.sqlite` (or `%LOCALAPPDATA%`) |
| Serato DJ Pro 3.x and older | `Music\_Serato_\History\Sessions\*.session` |

It picks whichever was written most recently. You can point it somewhere else in Settings.
Decks 1/3 count as **A** (left) and 2/4 as **B** (right).

### Finding tracks that mix well (free APIs)

TIDAL doesn't let third-party apps in, so suggestions come from free music services:

| Service | Key needed? | Used for |
| --- | --- | --- |
| [Deezer API](https://developers.deezer.com/api) | No | Similar tracks ("artist radio", related artists) and genres |
| [GetSongBPM](https://getsongbpm.com) | Free key | **BPM and key** of suggested tracks, plus popular songs at the same BPM |
| [Last.fm API](https://www.last.fm/api) | Free key (optional) | "Listeners also play" similar tracks and genre tags |
| Your Serato history | – | Tracks you've played before that fit, with Serato's own BPM/key |

Each candidate is scored on **harmonic compatibility** (Camelot wheel: same key, relative
major/minor, ±1, +2 energy boost…), **BPM** (within your ± range, half/double-time aware),
**genre** overlap and how similar the services say it is. The best matches come first.

BPM/key data provided by [GetSongBPM.com](https://getsongbpm.com).

## Install

1. Download `Serter.exe` from the latest
   [Release](https://github.com/jondoesflow/serter/releases) or from the newest
   [Build run](https://github.com/jondoesflow/serter/actions/workflows/build.yml) (artifact *Serter-win-x64*).
   It's a single self-contained file, so there's no .NET install needed.
2. Run it. Windows SmartScreen may warn because the app isn't code-signed: click
   **More info → Run anyway**.
3. The settings window opens on first run. Paste your API keys (see below) and click **Save**.
4. Start Serato and load a track.

### Getting the free API keys

**GetSongBPM** (needed for BPM and key of suggestions):
1. Go to <https://getsongbpm.com/api> in your browser.
2. GetSongBPM requires a public page linking back to them. This README has that link, so use this
   repository's URL (`https://github.com/jondoesflow/serter`) as both the website and the backlink.
   The repository must be **public** for this to work.
3. Enter your email, then activate the key from the email they send you.

**Last.fm** (optional, improves suggestions):
1. Sign in at <https://www.last.fm/api/account/create>.
2. Fill in any application name and copy the **API key**.

Keys are stored only on your PC in `%APPDATA%\Serter\settings.json`. Lookups are cached in
`%APPDATA%\Serter\cache` so repeat loads are instant and use fewer API requests.

## Build from source

Requires the .NET 8 SDK.

```powershell
dotnet test tests/Serter.Core.Tests
dotnet publish src/Serter/Serter.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

| Project | What's in it |
| --- | --- |
| `src/Serter.Core` | Serato readers, Camelot key maths, API clients, recommendation engine |
| `src/Serter` | WPF window (Windows only) |
| `tests/Serter.Core.Tests` | Unit tests |

Pushing a `v*` tag makes GitHub Actions build the exe and attach it to a release.
