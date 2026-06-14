using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace ExileEye.Core;

/// <summary>
/// Currency-icon files keyed by the trade currency code (exalted, divine, chaos, regal, …). The
/// code→image map comes from the trade /data/static "Currency" category; icons are downloaded
/// once into cache/currency and reused. The price-check window shows these next to listing prices.
/// </summary>
public sealed class CurrencyIcons
{
    private const string Cdn = "https://web.poecdn.com";
    private readonly Dictionary<string, string> _path = new();   // code → local png path

    private static string CacheDir => Path.Combine(AppContext.BaseDirectory, "cache", "currency");

    public string? PathFor(string code) => _path.TryGetValue(code, out var p) ? p : null;

    public async Task LoadAsync(HttpClient http)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://www.pathofexile.com/api/trade2/data/static");
            req.Headers.TryAddWithoutValidation("User-Agent",
                "ExileEye/0.1 (+https://github.com/Fsedis/ExileEye; contact prokrastinatorof@gmail.com)");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("result", out var cats)) return;
            Directory.CreateDirectory(CacheDir);

            foreach (var cat in cats.EnumerateArray())
            {
                // Listings price almost entirely in the main Currency category.
                if (!(cat.TryGetProperty("id", out var cid) && cid.GetString() == "Currency")) continue;
                if (!cat.TryGetProperty("entries", out var entries)) continue;
                foreach (var e in entries.EnumerateArray())
                {
                    var id = e.TryGetProperty("id", out var i) ? i.GetString() : null;
                    var image = e.TryGetProperty("image", out var im) ? im.GetString() : null;
                    if (id is null || string.IsNullOrEmpty(image)) continue;
                    var path = await DownloadAsync(http, id, image);
                    if (path is not null) _path[id] = path;
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[CurrencyIcons] load failed: {ex.Message}"); }
    }

    private static async Task<string?> DownloadAsync(HttpClient http, string code, string image)
    {
        var path = Path.Combine(CacheDir, code + ".png");
        try
        {
            if (!File.Exists(path))
            {
                var url = image.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? image : Cdn + image;
                var bytes = await http.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(path, bytes);
            }
            return path;
        }
        catch { try { if (File.Exists(path)) File.Delete(path); } catch { } return null; }
    }
}
