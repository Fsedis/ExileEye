namespace ExileEye.Core;

public sealed record Match(string Key, Price Price, bool Exact);

/// <summary>
/// Resolves a (noisy) OCR'd name against the price book: exact hit, then unique-prefix
/// (the panel truncates long names), then closest-edit-distance rescue for misreads.
/// </summary>
public static class PriceMatcher
{
    // 0.84 similarity ≈ tolerates 1 wrong char on a ~6-char name, 2 on a 12+ char name —
    // typical OCR slip rates — without letting unrelated items through.
    private const double MinSimilarity = 0.84;
    private const int MinPrefixLength = 6;     // shorter reads prefix-match too eagerly
    private const int MinFuzzyLength = 6;
    private const int MaxLengthGap = 3;        // names differing more than this are never near-matches

    public static Match? Find(IReadOnlyDictionary<string, Price> book, string name)
    {
        if (book.TryGetValue(name, out var price))
            return new Match(name, price, Exact: true);

        // The panel clips long names; an OCR'd read that is a prefix of exactly the kind of key
        // we expect picks the shortest such key (least extrapolation).
        if (name.Length >= MinPrefixLength)
        {
            string? best = null;
            foreach (var key in book.Keys)
                if (key.Length > name.Length && key.StartsWith(name, StringComparison.Ordinal)
                    && (best is null || key.Length < best.Length))
                    best = key;
            if (best is not null) return new Match(best, book[best], Exact: false);
        }

        if (name.Length >= MinFuzzyLength)
        {
            string? best = null;
            double bestScore = MinSimilarity;
            foreach (var key in book.Keys)
            {
                if (Math.Abs(key.Length - name.Length) > MaxLengthGap) continue;
                double score = 1.0 - (double)EditDistance(name, key) / Math.Max(name.Length, key.Length);
                if (score > bestScore) { bestScore = score; best = key; }
            }
            if (best is not null) return new Match(best, book[best], Exact: false);
        }

        return null;
    }

    /// <summary>Levenshtein distance, two-row rolling buffer.</summary>
    internal static int EditDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int subst = prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                cur[j] = Math.Min(subst, Math.Min(prev[j] + 1, cur[j - 1] + 1));
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
