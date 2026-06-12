using ExileEye.Core;

namespace ExileEye.Tests;

public class ItemTextTests
{
    [Theory]
    [InlineData("Support: Scattering Flame", "support scattering flame")]
    [InlineData("  VERISIUM FLUX  ", "verisium flux")]
    [InlineData("Rune-of-Aldur", "rune of aldur")]
    [InlineData("Aldur's Legacy", "aldur s legacy")]
    [InlineData("Адаптивный Сплав", "адаптивный сплав")]
    [InlineData("Чародейский расплав (Уровень 10)", "чародейский расплав уровень 10")]
    [InlineData("Тёмная руна", "темная руна")]   // ё folds to е — OCR loses the diaeresis
    [InlineData(":::---", "")]
    [InlineData("a   b   c", "a b c")]
    public void Normalize_ProducesCanonicalKey(string input, string expected)
    {
        Assert.Equal(expected, ItemText.Normalize(input));
    }

    [Theory]
    // exchange panel: "Nx" prefix, possibly preceded by OCR junk from the cost glyphs
    [InlineData("14x Adaptive Alloy", "adaptive alloy", 14)]
    [InlineData("3x Rune of Aldur", "rune of aldur", 3)]
    [InlineData("krogin 1x ancient rune of decay", "ancient rune of decay", 1)]
    [InlineData("nerog 11x ancient rune of discovery", "ancient rune of discovery", 11)]
    [InlineData("e l8 n 1x the greatwolf s rune", "the greatwolf s rune", 1)]
    [InlineData("oa a 1x greater orb of transmutation", "greater orb of transmutation", 1)]
    [InlineData("14 x adaptive alloy", "adaptive alloy", 14)]   // OCR split the marker
    // no marker at all
    [InlineData("adaptive alloy", "adaptive alloy", 1)]
    [InlineData("1 mystic alloy", "mystic alloy", 1)]
    [InlineData("1 1 adaptive alloy", "adaptive alloy", 1)]
    [InlineData("b l38 unique quarterstaff", "unique quarterstaff", 1)]
    [InlineData("warding rune of protection i", "warding rune of protection i", 1)]
    // Russian client: the rus model reads the marker's "x" as Cyrillic "х"
    [InlineData("14х Адаптивный сплав", "адаптивный сплав", 14)]
    [InlineData("крогин 1х древняя руна тлена", "древняя руна тлена", 1)]
    [InlineData("Древняя руна вражды", "древняя руна вражды", 1)]
    // combinations panel: "(N)" suffix
    [InlineData("Точильный камень (6)", "точильный камень", 6)]
    [InlineData("Blacksmith's Whetstone (4)", "blacksmith s whetstone", 4)]
    // a trailing level is part of the name, never a stack size
    [InlineData("Чародейский расплав (Уровень 10)", "чародейский расплав уровень 10", 1)]
    [InlineData("Thaumaturgic Flux (Level 10)", "thaumaturgic flux level 10", 1)]
    public void Parse_ExtractsNameAndQuantity(string input, string name, int qty)
    {
        var parsed = ItemText.Parse(input);
        Assert.Equal(name, parsed.Name);
        Assert.Equal(qty, parsed.Quantity);
    }

    [Theory]
    [InlineData("rune", true)]
    [InlineData("void flux", true)]
    [InlineData("руна", true)]
    [InlineData("ab c", false)]      // no 4-letter word
    [InlineData("a1b2c3d4", false)]  // digits break letter runs
    [InlineData("xyz", false)]
    public void LooksLikeName_FiltersNoise(string input, bool expected)
    {
        Assert.Equal(expected, ItemText.LooksLikeName(input));
    }
}
