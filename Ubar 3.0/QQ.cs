using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Ubar3;

public static class QQ
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private const string BaseUrl =
        "https://u.y.qq.com/cgi-bin/musicu.fcg";

    private const string Referer = "https://y.qq.com/";

    private sealed record Song(
        string Title,
        string Artists,
        int Duration,
        string Mid);

    // ============================================================
    // PUBLIC ENTRY POINT
    // ============================================================

    public static async Task<LyricsCandidate?> FetchAsync(
        string title,
        string artist,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title) ||
            string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        try
        {
            var songs = await SearchAsync(
                $"{artist} {title}",
                cancellationToken);

            foreach (var song in songs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(song.Mid))
                    continue;

                if (!SameTitle(song.Title, title))
                    continue;

                if (!SameArtist(song.Artists, artist))
                    continue;

                // Prefer actual word-synchronized QQ QRC lyrics.
                var wordLyrics = await FetchQrcAsync(
                    song.Mid,
                    cancellationToken);

                if (wordLyrics is { Count: > 0 })
                {
                    return new LyricsCandidate
                    {
                        Provider = "QQ Music",
                        Attribution = "Lyrics provided by QQ Music",
                        ProviderPriority = 250,
                        SyncType = "Word",
                        IsWordSynced = true,
                        Words = wordLyrics
                    };
                }

                // Fall back to regular synchronized LRC lyrics.
                var lrc = await FetchLrcAsync(
                    song.Mid,
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(lrc))
                {
                    var parsed = ParseLrc(lrc);

                    if (parsed.Count > 0)
                    {
                        return new LyricsCandidate
                        {
                            Provider = "QQ Music",
                            ProviderPriority = 250,
                            Attribution = "Lyrics provided by QQ Music",
                            SyncType = "Line",
                            Words = parsed
                        };
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Lyrics:QQ] {ex.Message}");
        }

        return null;
    }

    // ============================================================
    // SEARCH
    // ============================================================

    private static async Task<List<Song>> SearchAsync(
        string query,
        CancellationToken ct)
    {
        var payload = new
        {
            req_1 = new
            {
                module = "music.search.SearchCgiService",
                method = "DoSearchForQQMusicDesktop",
                param = new
                {
                    num_per_page = 8,
                    page_num = 1,
                    query,
                    search_type = 0
                }
            }
        };

        using var document = await PostJsonAsync(
            BaseUrl,
            JsonSerializer.Serialize(payload),
            ct);

        var root = document.RootElement;

        int code = GetInt(root, "code");

        if (code == 0 &&
            TryGet(root, "req_1", out var req) &&
            TryGet(req, "data", out var data) &&
            TryGet(data, "body", out var body) &&
            TryGet(body, "song", out var songObject) &&
            TryGet(songObject, "list", out var list) &&
            list.ValueKind == JsonValueKind.Array)
        {
            var songs = new List<Song>();

            foreach (var item in list.EnumerateArray())
            {
                string name = GetString(item, "name");
                string mid = GetString(item, "mid");
                int duration = GetInt(item, "interval");

                string artists = GetArtists(item, "singer");

                songs.Add(new Song(
                    name,
                    artists,
                    duration,
                    mid));
            }

            return songs;
        }

        // QQ sometimes throttles its main search endpoint.
        // Its suggestion endpoint can still work in that situation.
        return await SuggestedSearchAsync(query, ct);
    }

    private static async Task<List<Song>> SuggestedSearchAsync(
        string query,
        CancellationToken ct)
    {
        string url =
            "https://c.y.qq.com/splcloud/fcgi-bin/smartbox_new.fcg" +
            "?key=" + Uri.EscapeDataString(query) +
            "&format=json";

        using var document = await GetJsonAsync(url, ct);

        var root = document.RootElement;

        if (!TryGet(root, "data", out var data) ||
            !TryGet(data, "song", out var songObject) ||
            !TryGet(songObject, "itemlist", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return new List<Song>();
        }

        var mids = items.EnumerateArray()
            .Select(item => GetString(item, "mid"))
            .Where(mid => !string.IsNullOrWhiteSpace(mid))
            .Distinct()
            .ToList();

        if (mids.Count == 0)
            return new List<Song>();

        var detailsPayload = new
        {
            comm = new
            {
                ct = "19",
                cv = "1859",
                uin = "0"
            },
            req_1 = new
            {
                module = "music.trackInfo.UniformRuleCtrl",
                method = "CgiGetTrackInfo",
                param = new
                {
                    types = mids.Select(_ => 0).ToArray(),
                    mids
                }
            }
        };

        using var details = await PostJsonAsync(
            BaseUrl,
            JsonSerializer.Serialize(detailsPayload),
            ct);

        var detailsRoot = details.RootElement;

        if (!TryGet(detailsRoot, "req_1", out var req) ||
            GetInt(req, "code") != 0 ||
            !TryGet(req, "data", out var trackData) ||
            !TryGet(trackData, "tracks", out var tracks) ||
            tracks.ValueKind != JsonValueKind.Array)
        {
            return new List<Song>();
        }

        return tracks.EnumerateArray()
            .Select(track => new Song(
                GetString(track, "name"),
                GetArtists(track, "singer"),
                GetInt(track, "interval"),
                GetString(track, "mid")))
            .ToList();
    }

    // ============================================================
    // QRC WORD-SYNCHRONIZED LYRICS
    // ============================================================

    private static async Task<List<MusicPlayerWindow.KaraokeWord>?>
        FetchQrcAsync(string songMid, CancellationToken ct)
    {
        var payload = new
        {
            comm = new
            {
                ct = "19",
                cv = "1873",
                uin = "0"
            },
            req = new
            {
                module = "music.musichallSong.PlayLyricInfo",
                method = "GetPlayLyricInfo",
                param = new
                {
                    songMID = songMid,
                    qrc = 1,
                    qrc_t = 0,
                    trans = 1,
                    roma = 1,
                    crypt = 1
                }
            }
        };

        using var document = await PostJsonAsync(
            BaseUrl,
            JsonSerializer.Serialize(payload),
            ct);

        var root = document.RootElement;

        if (!TryGet(root, "req", out var req) ||
            !TryGet(req, "data", out var data))
        {
            return null;
        }

        string encrypted = GetString(data, "lyric");

        if (string.IsNullOrWhiteSpace(encrypted))
            return null;

        // Some responses may contain plaintext rather than encrypted QRC.
        if (encrypted.TrimStart().StartsWith("["))
            return QrcCodec.ParseWords(encrypted);

        string? decrypted = QrcCodec.Decrypt(encrypted);

        if (string.IsNullOrWhiteSpace(decrypted))
            return null;

        return QrcCodec.ParseWords(decrypted);
    }

    // ============================================================
    // REGULAR LRC FALLBACK
    // ============================================================

    private static async Task<string?> FetchLrcAsync(
        string songMid,
        CancellationToken ct)
    {
        string url =
            "https://c.y.qq.com/lyric/fcgi-bin/" +
            "fcg_query_lyric_new.fcg" +
            "?songmid=" + Uri.EscapeDataString(songMid) +
            "&format=json&nobase64=1";

        using var document = await GetJsonAsync(url, ct);

        string lyric = GetString(
            document.RootElement,
            "lyric");

        if (string.IsNullOrWhiteSpace(lyric))
            return null;

        // Some QQ responses still return Base64 even when nobase64=1.
        if (!lyric.TrimStart().StartsWith("["))
        {
            try
            {
                string decoded = Encoding.UTF8.GetString(
                    Convert.FromBase64String(lyric));

                if (decoded.Contains('['))
                    lyric = decoded;
            }
            catch
            {
                // Keep the original response if it isn't Base64.
            }
        }

        return lyric;
    }

    private static List<MusicPlayerWindow.KaraokeWord> ParseLrc(
        string lrc)
    {
        var entries = new List<(double Time, string Text)>();

        var regex = new Regex(
            @"\[(\d{1,2}):(\d{2})(?:[.:](\d{1,3}))?\](.*)");

        foreach (string line in lrc.Split('\n'))
        {
            var match = regex.Match(line.TrimEnd('\r'));

            if (!match.Success)
                continue;

            int minutes = int.Parse(match.Groups[1].Value);
            int seconds = int.Parse(match.Groups[2].Value);

            string fraction = match.Groups[3].Value;

            double milliseconds = fraction.Length switch
            {
                1 => int.Parse(fraction) * 100,
                2 => int.Parse(fraction) * 10,
                3 => int.Parse(fraction),
                _ => 0
            };

            double time =
                minutes * 60 +
                seconds +
                milliseconds / 1000.0;

            string text = match.Groups[4].Value.Trim();

            if (!string.IsNullOrWhiteSpace(text))
                entries.Add((time, text));
        }

        entries = entries
            .OrderBy(x => x.Time)
            .ToList();

        var result = new List<MusicPlayerWindow.KaraokeWord>();

        for (int i = 0; i < entries.Count; i++)
        {
            double start = entries[i].Time;

            double end = i + 1 < entries.Count
                ? entries[i + 1].Time
                : start + 5;

            if (end <= start)
                end = start + 0.1;

            result.Add(new MusicPlayerWindow.KaraokeWord
            {
                Text = entries[i].Text,
                StartTime = start,
                EndTime = end,
                LineIndex = i
            });
        }

        return result;
    }

    // ============================================================
    // HTTP HELPERS
    // ============================================================

    private static async Task<JsonDocument> GetJsonAsync(
        string url,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            url);

        request.Headers.Referrer = new Uri(Referer);
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 Chrome/131.0 Safari/537.36");

        using var response = await Http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(ct);

        return JsonDocument.Parse(json);
    }

    private static async Task<JsonDocument> PostJsonAsync(
        string url,
        string json,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            url);

        request.Headers.Referrer = new Uri(Referer);
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 Chrome/131.0 Safari/537.36");

        request.Content = new StringContent(
            json,
            Encoding.UTF8,
            "application/json");

        using var response = await Http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(ct);

        return JsonDocument.Parse(body);
    }

    // ============================================================
    // JSON HELPERS
    // ============================================================

    private static bool TryGet(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string GetString(
        JsonElement element,
        string name)
    {
        return TryGet(element, name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.ToString(),
                _ => ""
            }
            : "";
    }

    private static int GetInt(
        JsonElement element,
        string name)
    {
        if (!TryGet(element, name, out var value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int number))
        {
            return number;
        }

        return int.TryParse(value.ToString(), out number)
            ? number
            : 0;
    }

    private static string GetArtists(
        JsonElement element,
        string property)
    {
        if (!TryGet(element, property, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        return string.Join(
            " ",
            array.EnumerateArray()
                .Select(item => GetString(item, "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name)));
    }

    private static string Normalize(string value)
    {
        return Regex.Replace(
                value.ToLowerInvariant(),
                @"[^\p{L}\p{N}]",
                "")
            .Trim();
    }

    private static bool SameTitle(string a, string b)
    {
        return Normalize(a) == Normalize(b);
    }

    private static bool SameArtist(string result, string requested)
    {
        string a = Normalize(result);
        string b = Normalize(requested);

        return a.Length > 0 &&
               b.Length > 0 &&
               (a.Contains(b) || b.Contains(a));
    }
}

// ==================================================================
// QQ QRC DECODER
// ==================================================================

internal static class QrcCodec
{
    private static readonly byte[] Key =
        Encoding.ASCII.GetBytes("!@#)(*$%123ZXC!@!@#)(NHL");

    private static readonly Regex LineRegex =
        new(@"^\[(\d+),(\d+)](.*)$");

    private static readonly Regex TokenRegex =
        new(@"(.*?)\((\d+),(\d+)\)");

    public static string? Decrypt(string hex)
    {
        try
        {
            byte[] data = Convert.FromHexString(
                Regex.Replace(hex, @"\s+", ""));

            if (data.Length == 0 || data.Length % 8 != 0)
                return null;

            data = Crypt(data, Key[16..24], false);
            data = Crypt(data, Key[8..16], true);
            data = Crypt(data, Key[0..8], false);

            using var input = new MemoryStream(data);
            using var inflater = new ZLibStream(
                input,
                CompressionMode.Decompress);

            using var output = new MemoryStream();

            inflater.CopyTo(output);

            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch
        {
            return null;
        }
    }

    public static List<MusicPlayerWindow.KaraokeWord> ParseWords(
        string raw)
    {
        var result = new List<MusicPlayerWindow.KaraokeWord>();

        string content = Regex.Match(
            raw,
            "LyricContent=\"(.*?)\"\\s*/>",
            RegexOptions.Singleline |
            RegexOptions.IgnoreCase) is { Success: true } match
                ? WebUtility.HtmlDecode(match.Groups[1].Value)
                : raw;

        long offset = 0;
        int lineIndex = 0;

        foreach (string source in content.Split('\n'))
        {
            string row = source.TrimEnd('\r').Trim();

            var offsetMatch = Regex.Match(
                row,
                @"^\[offset:(-?\d+)]",
                RegexOptions.IgnoreCase);

            if (offsetMatch.Success)
            {
                offset = long.Parse(offsetMatch.Groups[1].Value);
                continue;
            }

            var lineMatch = LineRegex.Match(row);

            if (!lineMatch.Success)
                continue;

            long lineStart =
                Math.Max(0, long.Parse(lineMatch.Groups[1].Value) + offset);

            long lineDuration = long.Parse(lineMatch.Groups[2].Value);

            string text = lineMatch.Groups[3].Value;

            var tokens = TokenRegex.Matches(text);

            foreach (Match token in tokens)
            {
                string word = WebUtility.HtmlDecode(
                    token.Groups[1].Value);

                if (string.IsNullOrEmpty(word))
                    continue;

                long wordStart =
                    Math.Max(0, long.Parse(token.Groups[2].Value) + offset);

                long wordDuration =
                    long.Parse(token.Groups[3].Value);

                double start = wordStart / 1000.0;
                double end = (wordStart + wordDuration) / 1000.0;

                if (end <= start)
                    continue;

                result.Add(new MusicPlayerWindow.KaraokeWord
                {
                    Text = word,
                    StartTime = start,
                    EndTime = end,
                    LineIndex = lineIndex,
                    IsPartOfWord = false
                });
            }

            lineIndex++;
        }

        return result
            .OrderBy(word => word.StartTime)
            .ToList();
    }

    // The custom QQ implementation uses modified DES S-boxes.
    // This is not interchangeable with ordinary TripleDES.

    private static byte[] Crypt(
        byte[] input,
        byte[] key,
        bool encrypt)
    {
        var keys = DonorSchedule(key, encrypt);

        var output = new byte[input.Length];

        for (int offset = 0; offset < input.Length; offset += 8)
        {
            byte[] block = input[offset..(offset + 8)];

            int left = 0;
            int right = 0;

            for (int i = 0; i < 32; i++)
            {
                left |= BitNum(block, IPL[i], 31 - i);
                right |= BitNum(block, IPR[i], 31 - i);
            }

            for (int round = 0; round < 15; round++)
            {
                int previous = right;
                right = DonorF(right, keys[round]) ^ left;
                left = previous;
            }

            left = DonorF(right, keys[15]) ^ left;

            DonorJoin(left, right).CopyTo(output, offset);
        }

        return output;
    }

    private static byte[][] DonorSchedule(byte[] key, bool encrypt)
    {
        var schedule = Enumerable.Range(0, 16)
            .Select(_ => new byte[6])
            .ToArray();

        int c = 0;
        int d = 0;

        for (int i = 0; i < 28; i++)
        {
            c |= BitNum(key, KeyC[i], 31 - i);
            d |= BitNum(key, KeyD[i], 31 - i);
        }

        for (int round = 0; round < 16; round++)
        {
            int shift = Shifts[round];

            c = ((c << shift) | (int)((uint)c >> (28 - shift)))
                & unchecked((int)0xfffffff0);

            d = ((d << shift) | (int)((uint)d >> (28 - shift)))
                & unchecked((int)0xfffffff0);

            int target = encrypt ? round : 15 - round;

            for (int j = 0; j < 24; j++)
            {
                schedule[target][j >> 3] |= (byte)(
                    BitR(c, KeyComp[j], 7 - (j & 7)));
            }

            for (int j = 24; j < 48; j++)
            {
                schedule[target][j >> 3] |= (byte)(
                    BitR(d, KeyComp[j] - 27, 7 - (j & 7)));
            }
        }

        return schedule;
    }

    private static int DonorF(int state, byte[] key)
    {
        int t1 =
            BitL(state, 31, 0) |
            ((state & unchecked((int)0xf0000000)) >>> 1) |
            BitL(state, 4, 5) |
            BitL(state, 3, 6) |
            ((state & 0x0f000000) >>> 3) |
            BitL(state, 8, 11) |
            BitL(state, 7, 12) |
            ((state & 0x00f00000) >>> 5) |
            BitL(state, 12, 17) |
            BitL(state, 11, 18) |
            ((state & 0x000f0000) >>> 7) |
            BitL(state, 16, 23);

        int t2 =
            BitL(state, 15, 0) |
            ((state & 0x0000f000) << 15) |
            BitL(state, 20, 5) |
            BitL(state, 19, 6) |
            ((state & 0x00000f00) << 13) |
            BitL(state, 24, 11) |
            BitL(state, 23, 12) |
            ((state & 0x000000f0) << 11) |
            BitL(state, 28, 17) |
            BitL(state, 27, 18) |
            ((state & 0x0000000f) << 9) |
            BitL(state, 0, 23);

        int b0 = ((t1 >>> 24) ^ key[0]) & 0xff;
        int b1 = ((t1 >>> 16) ^ key[1]) & 0xff;
        int b2 = ((t1 >>> 8) ^ key[2]) & 0xff;

        int b3 = ((t2 >>> 24) ^ key[3]) & 0xff;
        int b4 = ((t2 >>> 16) ^ key[4]) & 0xff;
        int b5 = ((t2 >>> 8) ^ key[5]) & 0xff;

        int boxed =
            (SBox[0][SBoxBit(b0 >>> 2)] << 28) |
            (SBox[1][SBoxBit(((b0 & 3) << 4) | (b1 >>> 4))] << 24) |
            (SBox[2][SBoxBit(((b1 & 15) << 2) | (b2 >>> 6))] << 20) |
            (SBox[3][SBoxBit(b2 & 63)] << 16) |
            (SBox[4][SBoxBit(b3 >>> 2)] << 12) |
            (SBox[5][SBoxBit(((b3 & 3) << 4) | (b4 >>> 4))] << 8) |
            (SBox[6][SBoxBit(((b4 & 15) << 2) | (b5 >>> 6))] << 4) |
            SBox[7][SBoxBit(b5 & 63)];

        int output = 0;

        foreach (var (from, to) in Pairs)
            output |= BitL(boxed, from, to);

        return output;
    }

    private static byte[] DonorJoin(int left, int right)
    {
        var output = new byte[8];

        for (int k = 0; k < 8; k++)
        {
            int baseValue = 7 - k;
            int value = 0;

            for (int t = 0; t < 4; t++)
            {
                value |= BitR(right, baseValue + 8 * t, 7 - 2 * t);
                value |= BitR(left, baseValue + 8 * t, 6 - 2 * t);
            }

            output[InverseOrder[k]] = (byte)value;
        }

        return output;
    }

    private static int BitNum(byte[] bytes, int bit, int target)
    {
        int index = (bit / 32) * 4 + 3 - (bit % 32) / 8;

        return (((bytes[index] & 0xff) >> (7 - bit % 8)) & 1)
               << target;
    }

    private static int BitR(int value, int bit, int target) =>
        (int)(((uint)value >> (31 - bit) & 1) << target);

    private static int BitL(int value, int bit, int target) =>
        (int)(((uint)(value << bit) & 0x80000000u) >> target);

    private static int SBoxBit(int value) =>
        (value & 0x20) |
        ((value & 0x1f) >> 1) |
        ((value & 1) << 4);

    private static readonly int[] Shifts =
    {
        1,1,2,2,2,2,2,2,1,2,2,2,2,2,2,1
    };

    private static readonly int[] KeyC =
    {
        56,48,40,32,24,16,8,0,57,49,41,33,25,17,9,1,
        58,50,42,34,26,18,10,2,59,51,43,35
    };

    private static readonly int[] KeyD =
    {
        62,54,46,38,30,22,14,6,61,53,45,37,29,21,13,5,
        60,52,44,36,28,20,12,4,27,19,11,3
    };

    private static readonly int[] KeyComp =
    {
        13,16,10,23,0,4,2,27,14,5,20,9,22,18,11,3,
        25,7,15,6,26,19,12,1,40,51,30,36,46,54,29,39,
        50,44,32,47,43,48,38,55,33,52,45,41,49,35,28,31
    };

    private static readonly int[] IPL =
    {
        57,49,41,33,25,17,9,1,59,51,43,35,27,19,11,3,
        61,53,45,37,29,21,13,5,63,55,47,39,31,23,15,7
    };

    private static readonly int[] IPR =
    {
        56,48,40,32,24,16,8,0,58,50,42,34,26,18,10,2,
        60,52,44,36,28,20,12,4,62,54,46,38,30,22,14,6
    };

    private static readonly (int From, int To)[] Pairs =
    {
        (15,0),(6,1),(19,2),(20,3),(28,4),(11,5),(27,6),(16,7),
        (0,8),(14,9),(22,10),(25,11),(4,12),(17,13),(30,14),(9,15),
        (1,16),(7,17),(23,18),(13,19),(31,20),(26,21),(2,22),(8,23),
        (18,24),(12,25),(29,26),(5,27),(21,28),(10,29),(3,30),(24,31)
    };

    private static readonly int[] InverseOrder =
    {
        3,2,1,0,7,6,5,4
    };

    private static readonly int[][] SBox =
    {
        new[]
        {
            14,4,13,1,2,15,11,8,3,10,6,12,5,9,0,7,
            0,15,7,4,14,2,13,1,10,6,12,11,9,5,3,8,
            4,1,14,8,13,6,2,11,15,12,9,7,3,10,5,0,
            15,12,8,2,4,9,1,7,5,11,3,14,10,0,6,13
        },
        new[]
        {
            15,1,8,14,6,11,3,4,9,7,2,13,12,0,5,10,
            3,13,4,7,15,2,8,15,12,0,1,10,6,9,11,5,
            0,14,7,11,10,4,13,1,5,8,12,6,9,3,2,15,
            13,8,10,1,3,15,4,2,11,6,7,12,0,5,14,9
        },
        new[]
        {
            10,0,9,14,6,3,15,5,1,13,12,7,11,4,2,8,
            13,7,0,9,3,4,6,10,2,8,5,14,12,11,15,1,
            13,6,4,9,8,15,3,0,11,1,2,12,5,10,14,7,
            1,10,13,0,6,9,8,7,4,15,14,3,11,5,2,12
        },
        new[]
        {
            7,13,14,3,0,6,9,10,1,2,8,5,11,12,4,15,
            13,8,11,5,6,15,0,3,4,7,2,12,1,10,14,9,
            10,6,9,0,12,11,7,13,15,1,3,14,5,2,8,4,
            3,15,0,6,10,10,13,8,9,4,5,11,12,7,2,14
        },
        new[]
        {
            2,12,4,1,7,10,11,6,8,5,3,15,13,0,14,9,
            14,11,2,12,4,7,13,1,5,0,15,10,3,9,8,6,
            4,2,1,11,10,13,7,8,15,9,12,5,6,3,0,14,
            11,8,12,7,1,14,2,13,6,15,0,9,10,4,5,3
        },
        new[]
        {
            12,1,10,15,9,2,6,8,0,13,3,4,14,7,5,11,
            10,15,4,2,7,12,9,5,6,1,13,14,0,11,3,8,
            9,14,15,5,2,8,12,3,7,0,4,10,1,13,11,6,
            4,3,2,12,9,5,15,10,11,14,1,7,6,0,8,13
        },
        new[]
        {
            4,11,2,14,15,0,8,13,3,12,9,7,5,10,6,1,
            13,0,11,7,4,9,1,10,14,3,5,12,2,15,8,6,
            1,4,11,13,12,3,7,14,10,15,6,8,0,5,9,2,
            6,11,13,8,1,4,10,7,9,5,0,15,14,2,3,12
        },
        new[]
        {
            13,2,8,4,6,15,11,1,10,9,3,14,5,0,12,7,
            1,15,13,8,10,3,7,4,12,5,6,11,0,14,9,2,
            7,11,4,1,9,12,14,2,0,6,10,13,15,3,5,8,
            2,1,14,7,4,10,8,13,15,12,9,0,3,5,6,11
        }
    };
}