using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ExileEye.Core;

/// <summary>One listing: asking price, who's selling, when it was listed, and the item's level/quality.</summary>
public sealed record Listing(decimal Amount, string Currency, string Account, DateTimeOffset? Listed,
    int? ReqLevel = null, int? Quality = null);

/// <summary>A trade stat filter: the stat id and an optional minimum value.</summary>
public sealed record TradeStat(string Id, double? Min = null);

/// <summary>
/// Where/how to search. Status is the trade market/availability (labels from EE2):
///   "securable" = Instant buyout · "available" = Instant or Online · "online" = Online
///   (personal trade) · "any" = incl. offline.
/// Listed is the "indexed" age filter ("", "1day", "3days", "1week") — fresher listings are the
/// ones you can actually buy now.
/// </summary>
public sealed record TradeOptions(string Status = "available", string Listed = "", string Currency = "");

/// <summary>An estimated value from the fetched listings, with a spread and a confidence note.</summary>
public sealed record Estimate(decimal Mid, decimal Low, decimal High, string Currency, string Reliability, int Count);

/// <summary>Price-check result: the searchable item, total listings online, and the cheapest few.</summary>
public sealed record PriceCheck(string Label, int Total, IReadOnlyList<Listing> Listings, string? BrowseUrl = null)
{
    /// <summary>The cheapest listing (results come sorted by price ascending).</summary>
    public Listing? Cheapest => Listings.Count > 0 ? Listings[0] : null;

    /// <summary>
    /// A rough value from the most-listed currency: the median as the estimate, low/high as the
    /// fetched range. Reliability drops with few listings or a wide spread (the price is uncertain).
    /// </summary>
    public Estimate? Estimated()
    {
        if (Listings.Count == 0) return null;
        var group = Listings.GroupBy(l => l.Currency).OrderByDescending(g => g.Count()).First();
        var amounts = group.Select(l => l.Amount).OrderBy(a => a).ToList();
        decimal low = amounts[0], high = amounts[^1], mid = amounts[amounts.Count / 2];
        double spread = (double)(high / Math.Max(low, 0.01m));
        string reliability = amounts.Count < 4 || spread > 4 ? "low"
            : amounts.Count < 8 || spread > 2 ? "medium" : "high";
        return new Estimate(mid, low, high, group.Key, reliability, amounts.Count);
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
    private const int FetchTarget = 20;  // how many cheapest listings to show (scrollable)
    private const int FetchChunk = 10;   // the fetch endpoint accepts at most 10 ids per call

    private readonly HttpClient _http;
    private readonly RateLimiter _limiter = new();
    public TradeClient(HttpClient http) => _http = http;

    /// <summary>POESESSID cookie. When set, the API authenticates the request — required for the
    /// securable (instant-buyout) market and for higher rate limits.</summary>
    public string? SessionId { get; set; }

    /// <summary>Trade host for the client language — item names are copied localized.</summary>
    private static string BaseFor(string language) =>
        (language == "ru" ? "https://ru.pathofexile.com" : "https://www.pathofexile.com") + "/api/trade2";

    public async Task<PriceCheck?> CheckAsync(ParsedItem item, string league, string language = "en",
        IReadOnlyList<TradeStat>? stats = null, TradeOptions? options = null)
    {
        if (!item.IsSearchable) return null;
        var baseUrl = BaseFor(language);
        var label = item.Name ?? item.Type ?? "?";

        var query = BuildQuery(item, stats, options ?? new TradeOptions());
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

        var ids = resultArr.EnumerateArray().Take(FetchTarget).Select(e => e.GetString())
            .Where(s => s is not null).Select(s => s!).ToList();

        // Fetch in chunks of 10 (the endpoint's max) and combine, so the result list is long
        // enough to scroll through.
        var listings = new List<Listing>();
        for (int i = 0; i < ids.Count; i += FetchChunk)
        {
            var batch = string.Join(',', ids.Skip(i).Take(FetchChunk));
            var fetched = await GetAsync($"{baseUrl}/fetch/{batch}?query={searchId}");
            if (fetched is not null) listings.AddRange(ParseListings(fetched));
        }
        return new PriceCheck(label, total, listings, browse);
    }

    // {"query":{"status":{"option":"online"}, name?/type?, stats?}, "sort":{"price":"asc"}}
    // Each stat filter carries its id and an optional min value (the user picks which mods and
    // their minimums in the price-check window).
    private static string BuildQuery(ParsedItem item, IReadOnlyList<TradeStat>? stats, TradeOptions opts)
    {
        var q = new Dictionary<string, object>
        {
            ["status"] = new Dictionary<string, string> { ["option"] = opts.Status },
        };
        if (!string.IsNullOrEmpty(item.Name)) q["name"] = item.Name;
        if (!string.IsNullOrEmpty(item.Type)) q["type"] = item.Type;

        // trade_filters: listing age + valuation currency. (Sale type defaults to buyout, omitted.)
        var tf = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(opts.Listed)) tf["indexed"] = new Dictionary<string, string> { ["option"] = opts.Listed };
        if (!string.IsNullOrEmpty(opts.Currency)) tf["price"] = new Dictionary<string, string> { ["option"] = opts.Currency };
        if (tf.Count > 0)
            q["filters"] = new Dictionary<string, object> { ["trade_filters"] = new Dictionary<string, object> { ["filters"] = tf } };

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
                if (amount <= 0 || currency.Length == 0) continue;

                string account = listing.TryGetProperty("account", out var acc)
                    && acc.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                DateTimeOffset? listed = listing.TryGetProperty("indexed", out var idx)
                    && DateTimeOffset.TryParse(idx.GetString(), out var dt) ? dt : null;
                var (reqLevel, quality) = entry.TryGetProperty("item", out var it)
                    ? ReadItemLevelQuality(it) : (null, null);
                list.Add(new Listing(amount, currency, account, listed, reqLevel, quality));
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Trade] parse failed: {ex.Message}"); }
        return list;
    }

    // Required level (from requirements) and quality % (from properties), both localized — match
    // loosely by the field name and pull the first number out of the value.
    private static (int?, int?) ReadItemLevelQuality(JsonElement item)
    {
        int? reqLevel = null, quality = null;
        if (item.TryGetProperty("requirements", out var reqs) && reqs.ValueKind == JsonValueKind.Array)
            foreach (var r in reqs.EnumerateArray())
            {
                var name = (r.TryGetProperty("name", out var n) ? n.GetString() : "")?.ToLowerInvariant() ?? "";
                if (name.Contains("level") || name.Contains("уров"))
                    reqLevel = FirstNumber(r) ?? reqLevel;
            }
        if (item.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Array)
            foreach (var p in props.EnumerateArray())
            {
                var name = (p.TryGetProperty("name", out var n) ? n.GetString() : "")?.ToLowerInvariant() ?? "";
                if (name.Contains("quality") || name.Contains("ачест"))
                    quality = FirstNumber(p) ?? quality;
            }
        return (reqLevel, quality);
    }

    // requirements/properties carry values as [["65", 0]] — pull the first integer out.
    private static int? FirstNumber(JsonElement entry)
    {
        if (!entry.TryGetProperty("values", out var vals) || vals.ValueKind != JsonValueKind.Array) return null;
        foreach (var v in vals.EnumerateArray())
        {
            if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 && v[0].GetString() is { } s)
            {
                var digits = new string(s.Where(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var num)) return num;
            }
        }
        return null;
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
        if (!string.IsNullOrWhiteSpace(SessionId))
            req.Headers.TryAddWithoutValidation("Cookie", $"POESESSID={SessionId.Trim()}");
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
