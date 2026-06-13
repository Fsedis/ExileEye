namespace ExileEye.Core;

/// <summary>
/// What we pull out of a copied item to price-check it. Either Name (uniques) or Type (currency,
/// gems, rare base types) drives the trade search; Rarity decides which. Names are in the client
/// language — mapping to the trade query is a separate step.
/// </summary>
public sealed record ParsedItem(string? Name, string? Type, string Rarity, int StackSize = 1)
{
    public bool IsSearchable => !string.IsNullOrEmpty(Name) || !string.IsNullOrEmpty(Type);
}

/// <summary>
/// Parses the text PoE puts on the clipboard when you Ctrl+C a hovered item. The header lines
/// ("Item Class:", "Rarity:", name, base type) and the rarity values are localized, so the
/// parser is language-aware. Header strings ported from Exiled Exchange 2 (MIT) — see THIRD-PARTY.
/// </summary>
public static class ItemParser
{
    private sealed record Lang(
        string ItemClass, string Rarity, string StackSize, string Unique, string Rare);

    // Add languages here as needed; en + ru cover the current audience.
    private static readonly Lang[] Languages =
    [
        new("Item Class:", "Rarity:", "Stack Size:", "Unique", "Rare"),
        new("Класс предмета:", "Редкость:", "Размер стопки:", "Уникальный", "Редкий"),
    ];

    /// <summary>True if the text looks like a copied PoE item in any supported language.</summary>
    public static bool IsPoeItem(string? text) =>
        !string.IsNullOrEmpty(text) && Detect(text) is not null;

    public static ParsedItem? Parse(string clipboard)
    {
        if (string.IsNullOrWhiteSpace(clipboard)) return null;
        var lang = Detect(clipboard);
        if (lang is null) return null;

        var lines = clipboard.Replace("\r", "").Split('\n');
        var header = new List<string>();
        foreach (var line in lines)
        {
            if (IsDivider(line)) break;
            if (line.Trim().Length > 0) header.Add(line.Trim());
        }
        if (header.Count == 0) return null;

        string rarityValue = ValueAfter(header, lang.Rarity) ?? "";
        int rarityIdx = header.FindIndex(l => l.StartsWith(lang.Rarity, StringComparison.OrdinalIgnoreCase));
        var nameLines = (rarityIdx >= 0 ? header.Skip(rarityIdx + 1) : header)
            .Where(l => !l.Contains(':')).ToList();
        if (nameLines.Count == 0) return null;

        int stack = ReadStackSize(lines, lang.StackSize);

        if (rarityValue.Equals(lang.Unique, StringComparison.OrdinalIgnoreCase))
            return new ParsedItem(nameLines[0], nameLines.ElementAtOrDefault(1), "unique", stack);
        if (rarityValue.Equals(lang.Rare, StringComparison.OrdinalIgnoreCase))
            return new ParsedItem(null, nameLines.ElementAtOrDefault(1) ?? nameLines[0], "rare", stack);
        // Currency, gems, normal: the name line is the type.
        return new ParsedItem(null, nameLines[0], "other", stack);
    }

    private static Lang? Detect(string text) =>
        Languages.FirstOrDefault(l =>
            text.StartsWith(l.ItemClass, StringComparison.Ordinal) ||
            text.StartsWith(l.Rarity, StringComparison.Ordinal));

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

    private static int ReadStackSize(IEnumerable<string> lines, string key)
    {
        var line = lines.FirstOrDefault(l => l.Trim().StartsWith(key, StringComparison.OrdinalIgnoreCase));
        if (line is null) return 1;
        var num = new string(line.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(num, out var n) && n >= 1 ? n : 1;
    }
}
