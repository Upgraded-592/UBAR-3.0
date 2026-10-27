# UBAR 3.0 lyrics providers (C# / WinUI 3)

This folder contains an additive multi-provider resolver. It is intentionally a new file; it does not overwrite your existing `MusicPlayerWindow.cs` or `SpicyUbar.cs`.

## Implemented in this bundle

- Spicy Lyrics API using your existing `SPICY_LYRICS_API_KEY` environment variable.
- AMLL TTML DB: public API, no key. Looks up by Spotify ID, then searches by title/artist. AMLL records can be indexed by Apple Music, Spotify, QQ Music and NetEase IDs.
- Apple Music catalog matching through Apple's public iTunes Search API, then checks whether AMLL has TTML indexed for that Apple Music catalog ID. This is **not** direct retrieval from Apple's undocumented first-party lyrics endpoint.
- lrc.red TTML by ISRC, no key. ISRC is obtained from AMLL when available.
- LRCLIB line-synced LRC/plain lyrics, no key.
- Basic successful-result caching and sync-quality ranking.

## Important limitations

This is a practical first integration, not a claim that every platform in the wishlist has a native adapter. Native Apple Music lyrics retrieval, native QQ QRC, NetEase, KuGou, Kuwo, Unison, YouTube, and Spotify/Musixmatch direct endpoints are not implemented here. AMLL may return records indexed with some of those platforms, but that is not the same as calling each platform directly. Apple's frontend endpoint details were not present in the design overview, so this code does not fabricate them.

TTML variants differ. The included parser handles common `<p>`/`<span>` begin/end timing patterns; test it against real Apple Music-style TTML before treating every vocal/translation/background-vocal case as supported.

## Where API keys go

Only the existing Spicy Lyrics integration in this bundle needs a key. The current `SpicyUbar` class already reads `SPICY_LYRICS_API_KEY` from the process environment. Do not paste the key into source code or commit it to Git.

In PowerShell, set a persistent user environment variable:

```powershell
[Environment]::SetEnvironmentVariable('SPICY_LYRICS_API_KEY', 'PASTE_YOUR_KEY_HERE', 'User')
```

Then completely close and restart Visual Studio/UBAR so the process inherits the new variable. To set it only for the current PowerShell session:

```powershell
$env:SPICY_LYRICS_API_KEY = 'PASTE_YOUR_KEY_HERE'
```

The persistent setting is per Windows user. If you distribute UBAR to other people, do not ship your personal key inside the app; use a backend you control or let each user configure their own key.

## Hook it into MusicPlayerWindow

1. Add `LyricsProviderManager.cs` to the UBAR 3.0 project (same `Ubar3` namespace).
2. In `MusicPlayerWindow`, replace the existing `FetchKaraokeWordsAsync` method with the version below. It returns the chosen source and words through a field, preserving your existing loading code.
3. In `LoadLyricsForTrackAsync`, after the fetch succeeds, set attribution to the provider name stored in `_lastLyricsProvider`.

Add this field near the other lyric fields:

```csharp
private string _lastLyricsProvider = "Unknown";
```

Replace `FetchKaraokeWordsAsync` with:

```csharp
private async Task<List<KaraokeWord>?> FetchKaraokeWordsAsync(string title, string artist)
{
    string? spotifyId = await GetSpotifyTrackIdAsync(title, artist);
    var result = await new LyricsProviderManager().GetLyricsAsync(title, artist, spotifyId);
    _lastLyricsProvider = result?.Provider ?? "No provider found";
    return result?.Words;
}
```

Then replace both hard-coded attribution strings in `LoadLyricsForTrackAsync`:

```csharp
_lyricsAttribution.Text = $"Lyrics provided by {_lastLyricsProvider}";
```

Keep your existing `GetSpotifyTrackIdAsync` and `KaraokeWord` class. The existing Spicy parser's model is reused, so no new NuGet packages are required; this file uses .NET's built-in `System.Text.Json` and `System.Xml.Linq`.

## Priority

The resolver currently ranks syllable sync > word sync > line sync > static. Provider priority breaks ties. AMLL and lrc.red are generally TTML word-sync sources, but a given TTML file may only contain line-level timing. The current parser marks any parsed TTML result as `Word`; improve this by inspecting the actual timing granularity if strict sync classification is needed.
