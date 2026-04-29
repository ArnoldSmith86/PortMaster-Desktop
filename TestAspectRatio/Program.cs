using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

var apiKey = Environment.GetEnvironmentVariable("STEAMGRIDDB_API_KEY") ?? "";
if (string.IsNullOrEmpty(apiKey))
{
    Console.WriteLine("Set STEAMGRIDDB_API_KEY env var");
    return;
}

var http = new HttpClient();
var testGames = new[]
{
    ("Celeste", "steam", "504230", "Celeste"),
    ("Magnibox", "steam", "1591810", "Magnibox"),
    ("OneShot", "steam", "420530", "OneShot"),
    ("Braid", "steam", "26800", "Braid"),
};

foreach (var (title, platform, id, searchTitle) in testGames)
{
    Console.WriteLine($"\n{'='} {title} ({platform} {id}) {'='}\n");

    // Try platform-specific lookup
    var sgdbUrl = await FetchSgdbCover(platform, id);
    if (!string.IsNullOrEmpty(sgdbUrl))
    {
        Console.WriteLine($"✓ SteamGridDB: {sgdbUrl}");
        var (w, h) = await GetImageDimensions(sgdbUrl);
        if (w > 0) Console.WriteLine($"  {w}x{h} ({(double)w/h:F3})");
    }
    else
    {
        Console.WriteLine($"✗ SteamGridDB: No cover found");

        // Try name search fallback
        var searchUrl = await SearchAndFetchCover(searchTitle);
        if (!string.IsNullOrEmpty(searchUrl))
        {
            Console.WriteLine($"✓ Name search: {searchUrl}");
            var (w, h) = await GetImageDimensions(searchUrl);
            if (w > 0) Console.WriteLine($"  {w}x{h} ({(double)w/h:F3})");
        }
        else
        {
            Console.WriteLine($"✗ Name search: No cover found");
        }
    }
}

async Task<string?> FetchSgdbCover(string platform, string id)
{
    var req = new HttpRequestMessage(HttpMethod.Get,
        $"https://www.steamgriddb.com/api/v2/grids/{platform}/{id}?dimensions=600x900");
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    try
    {
        var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadFromJsonAsync<SgdbResponse>();
        return json?.Data?.FirstOrDefault()?.Url;
    }
    catch { return null; }
}

async Task<string?> SearchAndFetchCover(string title)
{
    try
    {
        var searchReq = new HttpRequestMessage(HttpMethod.Get,
            $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(title)}");
        searchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var searchResp = await http.SendAsync(searchReq);
        if (!searchResp.IsSuccessStatusCode) return null;
        var searchJson = await searchResp.Content.ReadFromJsonAsync<SgdbSearchResponse>();
        var sgdbId = searchJson?.Data?.FirstOrDefault()?.Id;
        if (sgdbId == null) return null;

        var gridReq = new HttpRequestMessage(HttpMethod.Get,
            $"https://www.steamgriddb.com/api/v2/grids/game/{sgdbId}?dimensions=600x900");
        gridReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var gridResp = await http.SendAsync(gridReq);
        if (!gridResp.IsSuccessStatusCode) return null;
        var gridJson = await gridResp.Content.ReadFromJsonAsync<SgdbResponse>();
        return gridJson?.Data?.FirstOrDefault()?.Url;
    }
    catch { return null; }
}

async Task<(int w, int h)> GetImageDimensions(string url)
{
    const int PeekBytes = 4096;
    try
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseContentRead);
        if (!resp.IsSuccessStatusCode) return (0, 0);
        var data = await resp.Content.ReadAsByteArrayAsync();
        return ParseDimensions(data.AsSpan(0, Math.Min(data.Length, PeekBytes)));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Error downloading: {ex.Message}");
        return (0, 0);
    }
}

(int w, int h) ParseDimensions(ReadOnlySpan<byte> buf)
{
    // PNG
    if (buf.Length >= 24 && buf[0] == 0x89 && buf[1] == 0x50 && buf[2] == 0x4E && buf[3] == 0x47)
    {
        int w = (buf[16] << 24) | (buf[17] << 16) | (buf[18] << 8) | buf[19];
        int h = (buf[20] << 24) | (buf[21] << 16) | (buf[22] << 8) | buf[23];
        return (w, h);
    }

    // JPEG
    if (buf.Length >= 4 && buf[0] == 0xFF && buf[1] == 0xD8)
    {
        int i = 2;
        while (i + 3 < buf.Length)
        {
            if (buf[i] != 0xFF) break;
            byte m = buf[i + 1];
            if (m is 0xD8 or 0xD9) break;
            if (m is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB)
            {
                if (i + 8 < buf.Length)
                {
                    int h = (buf[i + 5] << 8) | buf[i + 6];
                    int w = (buf[i + 7] << 8) | buf[i + 8];
                    if (w > 0 && h > 0 && w < 20000 && h < 20000)
                        return (w, h);
                }
                break;
            }
            int segLen = (buf[i + 2] << 8) | buf[i + 3];
            if (segLen < 2) break;
            i += 2 + segLen;
        }
    }

    // WebP: RIFF....WEBP signature at start
    if (buf.Length >= 12 && buf[0] == 0x52 && buf[1] == 0x49 && buf[2] == 0x46 && buf[3] == 0x46
        && buf[8] == 0x57 && buf[9] == 0x45 && buf[10] == 0x42 && buf[11] == 0x50)
    {
        // WebP: try to find VP8/VP8L/VP8X chunk
        // This is a simplified parser; full parsing is complex
        Console.WriteLine("  (WebP format detected but not fully supported)");
    }

    return (0, 0);
}

record SgdbResponse(
    [property: JsonPropertyName("data")] List<SgdbGrid>? Data);

record SgdbGrid(
    [property: JsonPropertyName("url")] string? Url);

record SgdbSearchResponse(
    [property: JsonPropertyName("data")] List<SgdbGameResult>? Data);

record SgdbGameResult(
    [property: JsonPropertyName("id")] int? Id);
