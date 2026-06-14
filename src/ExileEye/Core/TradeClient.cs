using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ExileEye.Core;

/// <summary>One listing's asking price.</summary>
public sealed record Listing(decimal Amount, string Currency);

/// <summary>A trade stat filter: the stat id and an optional minimum value.</summary>
public sealed record TradeStat(string Id, double? Min = null);

/// <summary>Price-check result: the searchable item, total listings online, and the cheapest few.</summary>
public sealed record PriceCheck(string Label, int Total, IReadOnlyList<Listing> Listings, string? BrowseUrl = null)
{
    /// <summary>A representative price: the modal currency's listings, their low end.</summary>
    public Listing? Typical()
    {
        if (Listings.Count == 0) return null;
        // Most-listed currency, then the lower-middle of those (skips the odd lowball).
        var byCur = Listings.GroupBy(l => l.Currency).OrderByDescending(g => g.Count()).First()
                            .OrderBy(l => l.Amount).ToList();
        return byCur[Math.Min(byCur.Count - 1, byCur.Count / 3)];
    }
}

/// <summary>
/// The official GGG trade API (api/trade2): POST a search, GET the cheapest listings. Public
/// search works without a session for occasional use; we send a descriptive User-Agent (GGG
/// asks for one) and back off on 429 rather than hammering. The API is localized by subdomain —
/// a Russian client copies Russian item names, which only the ru. host accepts (the www host
/// returns 400 "Unknown item base type"), so the host is chosen by client language.
/// </summary>
public sealed class TradeClient
{
    private const string UserAgent =
        "ExileEye/0.1 (+https://github.com/Fsedis/ExileEye; contact prokrastinatorof@gmail.com)";
    private const int FetchBatch = 10;   // the cheapest N listings is plenty for a price read

    private readonly HttpClient _http;
    private readonly RateLimiter _limiter = new();
    public TradeClient(HttpClient http) => _http = http;

    /// <summary>Trade host for the client language — item names are copied localized.</summary>
    private static string BaseFor(string language) =>
        (language == "ru" ? "https://ru.pathofexile.com" : "https://www.pathofexile.com") + "/api/trade2";

    public async Task<PriceCheck?> CheckAsync(ParsedItem item, string league, string language = "en",
        IReadOnlyList<TradeStat>? stats = null)
    {
        if (!item.IsSearchable) return null;
        var baseUrl = BaseFor(language);
        var label = item.Name ?? item.Type ?? "?";

        var query = BuildQuery(item, stats);
        var searchUrl = $"{baseUrl}/search/{Uri.EscapeDataString(league)}";
        var search = await PostJsonAsync(searchUrl, query);
        if (search is null) return null;

        using var doc = JsonDocument.Parse(search);
        var root = doc.RootElement;
        if (!root.TryGetProperty("id", out var idEl) || idEl.GetString() is not { } searchId) return null;
        int total = root.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
        // Browseable trade page for "open in browser".
        var browse = $"{BaseFor(language).Replace("/api/trade2", "/trade2")}/search/{Uri.EscapeDataString(league)}/{searchId}";
        if (!root.TryGetProperty("result", out var resultArr) || resultArr.GetArrayLength() == 0)
            return new PriceCheck(label, total, [], browse);

        var ids = resultArr.EnumerateArray().Take(FetchBatch).Select(e => e.GetString()).Where(s => s is not null).ToList();
        var fetchUrl = $"{baseUrl}/fetch/{string.Join(',', ids)}?query={searchId}";
        var fetched = await GetAsync(fetchUrl);
        if (fetched is null) return new PriceCheck(label, total, [], browse);

        return new PriceCheck(label, total, ParseListings(fetched), browse);
    }

    // {"query":{"status":{"option":"online"}, name?/type?, stats?}, "sort":{"price":"asc"}}
    // Each stat filter carries its id and an optional min value (the user picks which mods and
    // their minimums in the price-check window).
    private static string BuildQuery(ParsedItem item, IReadOnlyList<TradeStat>? stats)
    {
        var q = new Dictionary<string, object>
        {
            ["status"] = new Dictionary<string, string> { ["option"] = "online" },
        };
        if (!string.IsNullOrEmpty(item.Name)) q["name"] = item.Name;
        if (!string.IsNullOrEmpty(item.Type)) q["type"] = item.Type;
        if (stats is { Count: > 0 })
        {
            q["stats"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["type"] = "and",
                    ["filters"] = stats.Select(s =>
                    {
                        var f = new Dictionary<string, object> { ["id"] = s.Id };
                        if (s.Min is { } min)
                            f["value"] = new Dictionary<string, object> { ["min"] = min };
                        return f;
                    }).ToArray(),
                },
            };
        }
        var payload = new Dictionary<string, object>
        {
            ["query"] = q,
            ["sort"] = new Dictionary<string, string> { ["price"] = "asc" },
        };
        return JsonSerializer.Serialize(payload);
    }

    private static IReadOnlyList<Listing> ParseListings(string json)
    {
        var list = new List<Listing>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var arr)) return list;
            foreach (var entry in arr.EnumerateArray())
            {
                if (!entry.TryGetProperty("listing", out var listing)) continue;
                if (!listing.TryGetProperty("price", out var price)) continue;
                if (price.ValueKind != JsonValueKind.Object) continue;
                decimal amount = price.TryGetProperty("amount", out var a) ? a.GetDecimal() : 0m;
                string currency = price.TryGetProperty("currency", out var c) ? c.GetString() ?? "" : "";
                if (amount > 0 && currency.Length > 0) list.Add(new Listing(amount, currency));
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Trade] parse failed: {ex.Message}"); }
        return list;
    }

    private async Task<string?> PostJsonAsync(string url, string body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return await SendAsync(req);
    }

    private async Task<string?> GetAsync(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync(req);
    }

    private async Task<string?> SendAsync(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        try
        {
            await _limiter.AcquireAsync();   // proactively stay under the per-IP window
            var resp = await _http.SendAsync(req);
            _limiter.Configure(Header(resp, "X-Rate-Limit-Ip"));

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Honour the server's retry window, then give it one more go.
                int retryAfter = int.TryParse(Header(resp, "Retry-After"), out var s) ? s : 10;
                Console.Error.WriteLine($"[Trade] rate-limited (429) — waiting {retryAfter}s");
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(retryAfter, 1, 60)));
                await _limiter.AcquireAsync();
                resp = await _http.SendAsync(await CloneAsync(req));
                _limiter.Configure(Header(resp, "X-Rate-Limit-Ip"));
                if (!resp.IsSuccessStatusCode) return null;
            }
            else if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[Trade] HTTP {(int)resp.StatusCode} for {req.RequestUri?.AbsolutePath}");
                return null;
            }
            return await resp.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Trade] request failed: {ex.Message}");
            return null;
        }
    }

    private static string? Header(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

    // A sent HttpRequestMessage can't be reused, so rebuild it for the one retry.
    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage req)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri);
        foreach (var h in req.Headers) clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (req.Content is not null)
        {
            var body = await req.Content.ReadAsStringAsync();
            clone.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        return clone;
    }
}
