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

    public Estimate? Estimated() => EstimateOf(Listings);

    /// <summary>
    /// A rough value from the most-listed currency: the median as the estimate, low/high as the
    /// fetched range. Reliability drops with few listings or a wide spread (the price is uncertain).
    /// </summary>
    public static Estimate? EstimateOf(IReadOnlyList<Listing> listings)
    {
        if (listings.Count == 0) return null;
        var group = listings.GroupBy(l => l.Currency).OrderByDescending(g => g.Count()).First();
        var amounts = group.Select(l => l.Amount).OrderBy(a => a).ToList();
        decimal low = amounts[0], high = amounts[^1], mid = amounts[amounts.Count / 2];
        double spread = (double)(high / Math.Max(low, 0.01m));
        string reliability = amounts.Count < 4 || spread > 4 ? "low"
            : amounts.Count < 8 || spread > 2 ? "medium" : "high";
        return new Estimate(mid, low, high, group.Key, reliability, amounts.Count);
    }
}

/// <summary>
/// A running price-check search: holds the result hash list and a cursor so listings page in as
/// the user scrolls, without re-running the search.
/// </summary>
public sealed class TradeSession
{
    private readonly TradeClient _client;
    private readonly string _baseUrl, _searchId;
    private readonly List<string> _ids;
    private int _cursor;
    private bool _loading;

    public int Total { get; }
    public string BrowseUrl { get; }
    public List<Listing> Listings { get; } = [];
    public bool HasMore => _cursor < _ids.Count;

    internal TradeSession(TradeClient client, string baseUrl, string searchId, List<string> ids, int total, string browse)
    {
        _client = client; _baseUrl = baseUrl; _searchId = searchId; _ids = ids; Total = total; BrowseUrl = browse;
    }

    /// <summary>Fetch roughly the next <paramref name="count"/> listings; returns how many were added.</summary>
    public async Task<int> FetchMoreAsync(int count)
    {
        if (_loading) return 0;
        _loading = true;
        try
        {
            int added = 0;
            while (added < count && HasMore)
            {
                var batch = _ids.Skip(_cursor).Take(TradeClient.Chunk).ToList();
                _cursor += batch.Count;
                var ls = await _client.FetchBatchAsync(_baseUrl, _searchId, batch);
                Listings.AddRange(ls);
                added += ls.Count;
                if (ls.Count == 0) break;
            }
            return added;
        }
        finally { _loading = false; }
    }

    public Estimate? Estimated() => PriceCheck.EstimateOf(Listings);
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

    /// <summary>One-shot price check (toast/headless): search + fetch the first page of listings.</summary>
    public async Task<PriceCheck?> CheckAsync(ParsedItem item, string league, string language = "en",
        IReadOnlyList<TradeStat>? stats = null, TradeOptions? options = null)
    {
        var session = await SearchAsync(item, league, language, stats, options);
        if (session is null) return null;
        await session.FetchMoreAsync(FetchTarget);
        return new PriceCheck(item.Name ?? item.Type ?? "?", session.Total, session.Listings, session.BrowseUrl);
    }

    /// <summary>
    /// Run the search and return a session that fetches listings on demand — the window fetches
    /// the first page, then more as you scroll. The result hash list (up to ~100) is kept so paging
    /// needs no re-search.
    /// </summary>
    public async Task<TradeSession?> SearchAsync(ParsedItem item, string league, string language = "en",
        IReadOnlyList<TradeStat>? stats = null, TradeOptions? options = null)
    {
        if (!item.IsSearchable) return null;
        var baseUrl = BaseFor(language);

        var query = BuildQuery(item, stats, options ?? new TradeOptions());
        var search = await PostJsonAsync($"{baseUrl}/search/{Uri.EscapeDataString(league)}", query);
        if (search is null) return null;

        using var doc = JsonDocument.Parse(search);
        var root = doc.RootElement;
        if (!root.TryGetProperty("id", out var idEl) || idEl.GetString() is not { } searchId) return null;
        int total = root.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
        var browse = $"{baseUrl.Replace("/api/trade2", "/trade2")}/search/{Uri.EscapeDataString(league)}/{searchId}";

        var ids = root.TryGetProperty("result", out var arr)
            ? arr.EnumerateArray().Select(e => e.GetString()).Where(s => s is not null).Select(s => s!).ToList()
            : [];
        return new TradeSession(this, baseUrl, searchId, ids, total, browse);
    }

    internal async Task<IReadOnlyList<Listing>> FetchBatchAsync(string baseUrl, string searchId, IEnumerable<string> ids)
    {
        var batch = string.Join(',', ids);
        if (batch.Length == 0) return [];
        var json = await GetAsync($"{baseUrl}/fetch/{batch}?query={searchId}");
        return json is null ? [] : ParseListings(json);
    }

    internal const int Chunk = FetchChunk;

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
