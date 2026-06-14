using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExileEye.Core;

/// <summary>A copied mod line resolved to a trade stat: its id and the numbers it carried.</summary>
public sealed record StatMatch(string Id, IReadOnlyList<double> Values)
{
    /// <summary>Representative number for a "min" filter — the average (handles "adds # to #").</summary>
    public double Value => Values.Count == 0 ? 0 : Values.Average();
}

/// <summary>
/// The trade stat dictionary from the official, per-language /api/trade2/data/stats. Maps a mod
/// line to its trade stat id by templatizing: replace the numbers in the line with '#' and look
/// up the result against the API's text templates (e.g. "+25 к максимуму здоровья" → "# к
/// максимуму здоровья" → explicit.stat_3299347043). This is how Awakened/EE2 do it; the data is
/// GGG's own, localized by host, so Russian items resolve directly. Cached on disk.
/// </summary>
public sealed class StatDb
{
    private readonly Dictionary<string, string> _byTemplate = new(StringComparer.OrdinalIgnoreCase);
    public int Count => _byTemplate.Count;

    private static string CachePath(string lang) =>
        Path.Combine(AppContext.BaseDirectory, "cache", $"stats-{lang}.json");

    private static string HostFor(string lang) =>
        lang == "ru" ? "https://ru.pathofexile.com" : "https://www.pathofexile.com";

    public async Task LoadAsync(HttpClient http, string language)
    {
        string? json = await ReadCacheOrFetch(http, language);
        if (json is null) return;
        Parse(json);
    }

    private async Task<string?> ReadCacheOrFetch(HttpClient http, string language)
    {
        var cache = CachePath(language);
        try { if (File.Exists(cache)) return await File.ReadAllTextAsync(cache); } catch { }
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{HostFor(language)}/api/trade2/data/stats");
            req.Headers.TryAddWithoutValidation("User-Agent",
                "ExileEye/0.1 (+https://github.com/Fsedis/ExileEye; contact prokrastinatorof@gmail.com)");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            try { Directory.CreateDirectory(Path.GetDirectoryName(cache)!); await File.WriteAllTextAsync(cache, json); } catch { }
            return json;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[StatDb] fetch failed: {ex.Message}"); return null; }
    }

    internal void Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var cats)) return;
            foreach (var cat in cats.EnumerateArray())
            {
                if (!cat.TryGetProperty("entries", out var entries)) continue;
                foreach (var entry in entries.EnumerateArray())
                {
                    var id = entry.TryGetProperty("id", out var i) ? i.GetString() : null;
                    var text = entry.TryGetProperty("text", out var t) ? t.GetString() : null;
                    if (id is null || string.IsNullOrEmpty(text)) continue;
                    var key = Normalize(text);
                    // Prefer explicit mods when several stat ids share the same text.
                    if (!_byTemplate.TryGetValue(key, out var existing)
                        || (!existing.StartsWith("explicit") && id.StartsWith("explicit")))
                        _byTemplate[key] = id;
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[StatDb] parse failed: {ex.Message}"); }
    }

    /// <summary>Resolve one mod line to a stat, or null if it isn't a recognized stat.</summary>
    public StatMatch? Match(string line)
    {
        line = line.Trim();
        // Group headers ("{ Уникальное свойство — ... }") and dividers aren't mods.
        if (line.Length == 0 || line.StartsWith("{") || line.All(c => c == '-')) return null;

        // PoE2 prints the affix roll range right after the value — "23(20-30)%". Drop the
        // parenthetical range so the current roll ("23") is what we templatize and read.
        line = Regex.Replace(line, @"\((?:\d+(?:\.\d+)?)(?:\s*[-–]\s*\d+(?:\.\d+)?)+\)", "");
        // Strip the "unchangeable value" marker some unique stats carry.
        line = Regex.Replace(line, @"\s*[—-]\s*(?:Неизменяемое значение|Unchangeable Value)\s*$",
            "", RegexOptions.IgnoreCase);

        var values = new List<double>();
        var template = Regex.Replace(line, @"[+-]?\d+(?:\.\d+)?", m =>
        {
            if (double.TryParse(m.Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) values.Add(v);
            return "#";
        });
        var key = Normalize(template);
        if (_byTemplate.TryGetValue(key, out var id)) return new StatMatch(id, values);

        // The trade DB stores one canonical wording; the game shows the negated form ("4 fewer"
        // for value -4 of a "# more" stat). Swap a known negative word to its positive counterpart
        // and match the canonical stat with negated values.
        foreach (var (neg, pos) in Antonyms)
        {
            if (!key.Contains(neg, StringComparison.Ordinal)) continue;
            var swapped = key.Replace(neg, pos, StringComparison.Ordinal);
            if (_byTemplate.TryGetValue(swapped, out var negId))
                return new StatMatch(negId, values.Select(v => -v).ToList());
        }
        return null;
    }

    // Negative↔positive wording pairs for stats whose displayed text flips on a negative roll.
    private static readonly (string Neg, string Pos)[] Antonyms =
    [
        ("меньше", "больше"), ("уменьшение", "увеличение"), ("снижение", "повышение"),
        ("медленнее", "быстрее"), ("короче", "дольше"),
        ("reduced", "increased"), ("fewer", "more"), ("less", "more"),
        ("slower", "faster"), ("shorter", "longer"),
    ];

    // Templates sometimes carry trailing tags like " (implicit)"/" (рунное)"; drop a trailing
    // parenthetical and collapse whitespace so item lines and templates line up.
    private static string Normalize(string s)
    {
        s = Regex.Replace(s, @"\s*\([^)]*\)\s*$", "");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }
}
