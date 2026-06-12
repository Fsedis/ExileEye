using System.Net.Http;
using System.Text.Json;

namespace ExileEye.Core;

/// <summary>
/// Current PoE2 economy leagues from poe.ninja's index, so a new league shows up in the
/// dropdown the day it launches — no app update needed. Falls back to the built-in list
/// (Settings.Leagues) when offline.
/// </summary>
public static class LeagueDirectory
{
    private const string IndexUrl = "https://poe.ninja/poe2/api/data/index-state";

    public static async Task<IReadOnlyList<string>> FetchAsync(HttpClient http)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, IndexUrl);
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Referer", "https://poe.ninja/poe2/economy");
            var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return [];
            return ParseIndexState(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Leagues] fetch failed: {ex.Message}");
            return [];
        }
    }

    internal static IReadOnlyList<string> ParseIndexState(string json)
    {
        var leagues = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("economyLeagues", out var arr)) return leagues;
            foreach (var league in arr.EnumerateArray())
                if (league.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } n)
                    leagues.Add(n);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Leagues] parse failed: {ex.Message}");
        }
        return leagues;
    }
}
