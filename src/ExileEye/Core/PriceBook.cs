using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace ExileEye.Core;

/// <summary>Per-unit price of one item, denominated both ways for display flexibility.</summary>
public sealed record Price(decimal Divine, decimal Exalted);

/// <summary>
/// The live price dictionary: fetched from poe.ninja's PoE2 exchange API, keyed by normalized
/// item name, refreshed in the background every 30 minutes. When the client language is Russian,
/// every entry also gets a second key with the localized name (data/ru-names.json), so the
/// matching pipeline needs no language awareness at all.
/// </summary>
public sealed class PriceBook : IDisposable
{
    private static readonly string[] PanelTypes = ["Currency", "Runes", "Expedition", "Verisium"];
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private System.Threading.Timer? _refresh;
    private volatile Dictionary<string, Price> _prices = new();

    public IReadOnlyDictionary<string, Price> Prices => _prices;
    public DateTime? FetchedAt { get; private set; }
    public int Count => _prices.Count;

    /// <summary>Fires on a thread-pool thread after each successful fetch.</summary>
    public event Action? Updated;

    public PriceBook(HttpClient http) => _http = http;

    public async Task FetchAsync(Settings settings)
    {
        try
        {
            var book = new Dictionary<string, Price>();
            foreach (var type in PanelTypes)
            {
                var json = await DownloadOverviewAsync(settings.League, type);
                if (json is null) continue;
                foreach (var (key, price) in ParseOverview(json))
                    book[key] = price;
            }
            if (settings.Language == "ru")
                AddAliases(book, LoadRussianNames());
            _prices = book;
            FetchedAt = DateTime.Now;
            Updated?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PriceBook] fetch failed: {ex.Message}");
        }
    }

    public void StartAutoRefresh(Settings settings)
    {
        _refresh?.Dispose();
        _refresh = new System.Threading.Timer(_ => Task.Run(() => FetchAsync(settings)),
            null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
    }

    private async Task<string?> DownloadOverviewAsync(string league, string type)
    {
        // The API rejects botty requests; a browser UA + matching Referer keeps it happy.
        var url = $"https://poe.ninja/poe2/api/economy/exchange/current/overview" +
                  $"?league={Uri.EscapeDataString(league)}&type={type}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Referer",
            $"https://poe.ninja/poe2/economy/{league.Replace(" ", "").ToLowerInvariant()}/{type.ToLowerInvariant()}");

        var resp = await _http.SendAsync(req);
        if (resp.IsSuccessStatusCode) return await resp.Content.ReadAsStringAsync();
        Console.Error.WriteLine($"[PriceBook] {type}: HTTP {(int)resp.StatusCode}");
        return null;
    }

    /// <summary>
    /// Overview shape: items[]={id,name}, lines[]={id,primaryValue}, core={primary,rates}.
    /// primaryValue is denominated in the league's primary currency (divine on softcore,
    /// exalted on hardcore), so both display denominations are derived through core.rates
    /// instead of assuming divines.
    /// </summary>
    internal static Dictionary<string, Price> ParseOverview(string json)
    {
        var book = new Dictionary<string, Price>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var idToName = new Dictionary<string, string>();
            if (root.TryGetProperty("items", out var items))
                foreach (var item in items.EnumerateArray())
                    if (item.TryGetProperty("id", out var id) && item.TryGetProperty("name", out var name)
                        && id.GetString() is { } i && name.GetString() is { } n)
                        idToName[i] = n;

            string primary = "divine";
            decimal divinePerPrimary = 1m, exaltedPerPrimary = 1m;
            if (root.TryGetProperty("core", out var core))
            {
                if (core.TryGetProperty("primary", out var p) && p.GetString() is { } ps) primary = ps;
                // The primary currency's own rate is implicit 1 and absent from the rates object.
                if (core.TryGetProperty("rates", out var rates))
                {
                    divinePerPrimary = primary == "divine" ? 1m
                        : rates.TryGetProperty("divine", out var d) ? d.GetDecimal() : 0m;
                    exaltedPerPrimary = primary == "exalted" ? 1m
                        : rates.TryGetProperty("exalted", out var e) ? e.GetDecimal() : 1m;
                }
            }

            if (!root.TryGetProperty("lines", out var lines)) return book;
            foreach (var line in lines.EnumerateArray())
            {
                if (!line.TryGetProperty("id", out var idEl) || idEl.GetString() is not { } id) continue;
                if (!idToName.TryGetValue(id, out var displayName)) continue;
                decimal value = line.TryGetProperty("primaryValue", out var v) ? v.GetDecimal() : 0m;
                var key = ItemText.Normalize(displayName);
                if (key.Length > 0)
                    book[key] = new Price(value * divinePerPrimary, Math.Round(value * exaltedPerPrimary, 1));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PriceBook] parse failed: {ex.Message}");
        }
        return book;
    }

    /// <summary>data/ru-names.json: English display name → official Russian client name.</summary>
    internal static Dictionary<string, string> LoadRussianNames()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "ru-names.json");
        return LoadNameMap(path);
    }

    internal static Dictionary<string, string> LoadNameMap(string path)
    {
        var map = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(path)) return map;
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (raw is null) return map;
            foreach (var (en, localized) in raw)
            {
                var enKey = ItemText.Normalize(en);
                var localKey = ItemText.Normalize(localized);
                if (enKey.Length > 0 && localKey.Length > 0) map[enKey] = localKey;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PriceBook] name map load failed: {ex.Message}");
        }
        return map;
    }

    internal static void AddAliases(Dictionary<string, Price> book, IReadOnlyDictionary<string, string> aliases)
    {
        foreach (var (enKey, localKey) in aliases)
            if (book.TryGetValue(enKey, out var price) && !book.ContainsKey(localKey))
                book[localKey] = price;
    }

    public void Dispose() => _refresh?.Dispose();
}
