using System.Drawing;
using System.IO;
using System.Net.Http;

namespace ExileEye.Core;

/// <summary>
/// Divine / Exalted orb sprites for the overlay. URLs come from the poe.ninja Currency overview
/// (PriceBook captures them during the fetch); images are cached on disk so later launches work
/// offline. Either icon may stay null — the overlay falls back to "div"/"ex" text.
/// </summary>
public sealed class IconStore : IDisposable
{
    private const string Cdn = "https://web.poecdn.com";

    public Bitmap? Divine { get; private set; }
    public Bitmap? Exalted { get; private set; }

    private static string CacheDir => Path.Combine(AppContext.BaseDirectory, "cache");

    public async Task LoadAsync(HttpClient http, string? divineUrl, string? exaltedUrl)
    {
        Divine = await GetAsync(http, "divine.png", divineUrl);
        Exalted = await GetAsync(http, "exalted.png", exaltedUrl);
    }

    private static async Task<Bitmap?> GetAsync(HttpClient http, string cacheName, string? url)
    {
        var cached = Path.Combine(CacheDir, cacheName);
        try
        {
            if (!File.Exists(cached))
            {
                if (string.IsNullOrEmpty(url)) return null;
                var full = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : Cdn + url;
                var bytes = await http.GetByteArrayAsync(full);
                Directory.CreateDirectory(CacheDir);
                await File.WriteAllBytesAsync(cached, bytes);
            }
            // Load via memory so the file isn't locked for the app's lifetime.
            using var ms = new MemoryStream(await File.ReadAllBytesAsync(cached));
            return new Bitmap(ms);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Icons] {cacheName}: {ex.Message}");
            try { if (File.Exists(cached)) File.Delete(cached); } catch { }   // poison-proof the cache
            return null;
        }
    }

    public void Dispose()
    {
        Divine?.Dispose();
        Exalted?.Dispose();
        Divine = Exalted = null;
    }
}
