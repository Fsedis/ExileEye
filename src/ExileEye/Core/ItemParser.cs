namespace ExileEye.Core;

/// <summary>
/// What we pull out of a copied item to price-check it. Either Name (uniques) or Type (currency,
/// gems, rare base types) drives the trade search; Rarity decides which.
/// </summary>
public sealed record ParsedItem(string? Name, string? Type, string Rarity, int StackSize = 1)
{
    public bool IsSearchable => !string.IsNullOrEmpty(Name) || !string.IsNullOrEmpty(Type);
}

/// <summary>
/// Parses the text PoE puts on the clipboard when you Ctrl+C a hovered item. The format is
/// sections separated by dashed lines; the first section carries Item Class, Rarity, the name,
/// and (for gear) the base type on the next line.
/// </summary>
public static class ItemParser
{
    public static ParsedItem? Parse(string clipboard)
    {
        if (string.IsNullOrWhiteSpace(clipboard)) return null;
        var lines = clipboard.Replace("\r", "").Split('\n');

        // Header = everything up to the first dashed divider.
        var header = new List<string>();
        foreach (var line in lines)
        {
            if (IsDivider(line)) break;
            if (line.Trim().Length > 0) header.Add(line.Trim());
        }
        if (header.Count == 0) return null;
        // Must look like a copied PoE item, not arbitrary clipboard text.
        bool looksLikeItem = header.Any(l =>
            l.StartsWith("Item Class:", StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith("Rarity:", StringComparison.OrdinalIgnoreCase));
        if (!looksLikeItem) return null;

        string rarity = ValueAfter(header, "Rarity:") ?? "";
        int rarityIdx = header.FindIndex(l => l.StartsWith("Rarity:", StringComparison.OrdinalIgnoreCase));
        // The name lines are whatever follows "Rarity:" in the header (skip "Item Class:" etc.).
        var nameLines = rarityIdx >= 0
            ? header.Skip(rarityIdx + 1).Where(l => !l.Contains(':')).ToList()
            : header.Where(l => !l.Contains(':')).ToList();
        if (nameLines.Count == 0) return null;

        int stack = ReadStackSize(lines);

        return rarity.ToLowerInvariant() switch
        {
            // Uniques: search by the unique name (line 1); base type (line 2) narrows it.
            "unique" => new ParsedItem(nameLines[0], nameLines.ElementAtOrDefault(1), rarity, stack),
            // Rares: the name is randomised — the base type (line 2) is what's searchable.
            "rare" => new ParsedItem(null, nameLines.ElementAtOrDefault(1) ?? nameLines[0], rarity, stack),
            // Currency, gems, normal items: the name line IS the type.
            _ => new ParsedItem(null, nameLines[0], rarity, stack),
        };
    }

    private static bool IsDivider(string line)
    {
        var t = line.Trim();
        return t.Length >= 3 && t.All(c => c == '-');
    }

    private static string? ValueAfter(IEnumerable<string> lines, string key)
    {
        var line = lines.FirstOrDefault(l => l.StartsWith(key, StringComparison.OrdinalIgnoreCase));
        return line?[key.Length..].Trim();
    }

    // "Stack Size: 3/10" → 3. Absent → 1.
    private static int ReadStackSize(IEnumerable<string> lines)
    {
        var line = lines.FirstOrDefault(l => l.Trim().StartsWith("Stack Size:", StringComparison.OrdinalIgnoreCase));
        if (line is null) return 1;
        var num = new string(line.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(num, out var n) && n >= 1 ? n : 1;
    }
}
