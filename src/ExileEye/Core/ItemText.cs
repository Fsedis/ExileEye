using System.Text;

namespace ExileEye.Core;

/// <summary>An OCR'd panel row reduced to a canonical item name and its stack quantity.</summary>
public sealed record ParsedRow(string Name, int Quantity);

/// <summary>
/// Text canonicalization shared by both sides of the price lookup: OCR output and poe.ninja
/// display names must reduce to the same key or nothing ever matches.
/// </summary>
public static class ItemText
{
    /// <summary>
    /// Lowercase, fold ё→е (OCR routinely loses the diaeresis), collapse every run of
    /// non-alphanumerics to a single space. "Aldur's Legacy" and "Наследие Альдура (Уровень 3)"
    /// both come out as plain space-separated words.
    /// </summary>
    public static string Normalize(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        bool pendingSpace = false;
        foreach (var ch in raw.ToLowerInvariant())
        {
            var c = ch == 'ё' ? 'е' : ch;
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                sb.Append(c);
                pendingSpace = false;
            }
            else pendingSpace = true;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Turn a raw OCR line into (name, quantity). Handles both quantity notations the game uses —
    /// the exchange panel's "14x Item" prefix and the combinations panel's "Item (6)" suffix —
    /// plus the junk tokens OCR picks up from row decorations left of the name.
    /// The Russian Tesseract model reads the prefix marker's "x" as Cyrillic "х"; both accepted.
    /// </summary>
    public static ParsedRow Parse(string rawText)
    {
        var tokens = new List<string>(Normalize(rawText).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        int qty = 1;

        // A "14x"/"14х" marker anywhere marks the start of the real name; OCR junk from the
        // cost-glyph column lands before it. Everything up to and including it goes.
        for (int i = 0; i < tokens.Count; i++)
        {
            if (TryReadMarker(tokens, i, out int value, out int span))
            {
                qty = value;
                tokens.RemoveRange(0, i + span);
                break;
            }
        }

        // Leftover leading junk: 1–2 char fragments and digit-bearing tokens are never how an
        // item name starts (verified against the full poe.ninja item list, en and ru).
        while (tokens.Count > 0 && (tokens[0].Length <= 2 || tokens[0].Any(char.IsDigit)))
            tokens.RemoveAt(0);

        // Trailing bare number = the "(N)" stack suffix — unless it follows "level"/"уровень",
        // where it is part of the item name ("Thaumaturgic Flux (Level 8)").
        if (tokens.Count >= 2 && tokens[^1].Length <= 3 && tokens[^1].All(char.IsDigit)
            && tokens[^2] is not ("level" or "уровень"))
        {
            if (qty == 1) qty = int.Parse(tokens[^1]);   // an explicit "Nx" prefix wins
            tokens.RemoveAt(tokens.Count - 1);
        }

        return new ParsedRow(string.Join(' ', tokens), Math.Clamp(qty, 1, 999));
    }

    // "14x" as one token, or "14 x" split across two (OCR sometimes inserts the space).
    private static bool TryReadMarker(List<string> tokens, int i, out int value, out int span)
    {
        value = 0; span = 0;
        var t = tokens[i];
        if (t.Length is >= 2 and <= 4 && (t[^1] is 'x' or 'х') && t[..^1].All(char.IsDigit)
            && int.TryParse(t[..^1], out value) && value >= 1)
        {
            span = 1;
            return true;
        }
        if (t.Length <= 3 && t.All(char.IsDigit) && i + 1 < tokens.Count && tokens[i + 1] is "x" or "х"
            && int.TryParse(t, out value) && value >= 1)
        {
            span = 2;
            return true;
        }
        return false;
    }

    /// <summary>A plausible item name contains at least one word of 4+ letters; OCR noise rarely does.</summary>
    public static bool LooksLikeName(string name)
    {
        if (name.Length < 4) return false;
        int run = 0;
        foreach (var c in name)
        {
            if (char.IsLetter(c)) { if (++run >= 4) return true; }
            else run = 0;
        }
        return false;
    }
}
