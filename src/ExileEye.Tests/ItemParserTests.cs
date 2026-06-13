using ExileEye.Core;

namespace ExileEye.Tests;

public class ItemParserTests
{
    [Fact]
    public void Currency_NameIsTheType()
    {
        const string clip = "Item Class: Stackable Currency\nRarity: Currency\nDivine Orb\n--------\nStack Size: 3/10\n--------\nRandomises the numeric values...";
        var item = ItemParser.Parse(clip)!;
        Assert.Null(item.Name);
        Assert.Equal("Divine Orb", item.Type);
        Assert.Equal(3, item.StackSize);
    }

    [Fact]
    public void Unique_SearchesByNameWithBaseType()
    {
        const string clip = "Item Class: Gloves\nRarity: Unique\nHeatsleeve\nLeather Gloves\n--------\nQuality: +20%";
        var item = ItemParser.Parse(clip)!;
        Assert.Equal("Heatsleeve", item.Name);
        Assert.Equal("Leather Gloves", item.Type);
    }

    [Fact]
    public void Rare_SearchesByBaseTypeNotRandomName()
    {
        const string clip = "Item Class: Body Armours\nRarity: Rare\nDoom Shell\nAdvanced Plate Vest\n--------\nArmour: 200";
        var item = ItemParser.Parse(clip)!;
        Assert.Null(item.Name);
        Assert.Equal("Advanced Plate Vest", item.Type);
    }

    [Fact]
    public void CarriageReturnsAndBlankLines_Tolerated()
    {
        const string clip = "Item Class: Stackable Currency\r\nRarity: Currency\r\nExalted Orb\r\n--------\r\n";
        var item = ItemParser.Parse(clip)!;
        Assert.Equal("Exalted Orb", item.Type);
    }

    [Fact]
    public void EmptyOrJunk_ReturnsNull()
    {
        Assert.Null(ItemParser.Parse(""));
        Assert.Null(ItemParser.Parse("just some random copied text with no item header colon line\nmore"));
    }
}
