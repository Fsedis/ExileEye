using System.IO;
using ExileEye.Core;

namespace ExileEye.Tests;

public class PriceBookTests
{
    private const string SoftcoreOverview = """
        {
          "items": [
            { "id": "chilling-flux", "name": "Chilling Flux" },
            { "id": "adaptive-alloy", "name": "Adaptive Alloy" }
          ],
          "lines": [
            { "id": "chilling-flux", "primaryValue": 0.5 },
            { "id": "adaptive-alloy", "primaryValue": 1.2 }
          ],
          "core": { "primary": "divine", "rates": { "exalted": 80.0 } }
        }
        """;

    private const string HardcoreOverview = """
        {
          "items": [
            { "id": "orb-of-alchemy", "name": "Orb of Alchemy" },
            { "id": "divine-orb", "name": "Divine Orb" }
          ],
          "lines": [
            { "id": "orb-of-alchemy", "primaryValue": 1.13 },
            { "id": "divine-orb", "primaryValue": 67.51 }
          ],
          "core": { "primary": "exalted", "rates": { "divine": 0.01481, "chaos": 0.2785 } }
        }
        """;

    [Fact]
    public void ParseOverview_KeysAreNormalized_ValuesInBothCurrencies()
    {
        var book = PriceBook.ParseOverview(SoftcoreOverview);
        Assert.Equal(0.5m, book["chilling flux"].Divine);
        Assert.Equal(40.0m, book["chilling flux"].Exalted);   // 0.5 × 80
        Assert.Equal(1.2m, book["adaptive alloy"].Divine);
    }

    [Fact]
    public void ParseOverview_HardcorePrimaryIsExalted()
    {
        var book = PriceBook.ParseOverview(HardcoreOverview);
        Assert.Equal(1.1m, book["orb of alchemy"].Exalted);
        Assert.True(book["orb of alchemy"].Divine < 1m);
        Assert.True(book["divine orb"].Divine >= 0.99m);
    }

    [Fact]
    public void ParseOverview_GarbageJson_ReturnsEmpty()
    {
        Assert.Empty(PriceBook.ParseOverview("not json at all"));
        Assert.Empty(PriceBook.ParseOverview("""{"items":[],"lines":[]}"""));
    }

    [Fact]
    public void AddAliases_CopiesPriceUnderLocalizedKey()
    {
        var book = new Dictionary<string, Price> { ["chilling flux"] = new(0.5m, 40m) };
        var aliases = new Dictionary<string, string>
        {
            ["chilling flux"] = "леденящий расплав",
            ["unknown item"] = "не существует",
        };
        PriceBook.AddAliases(book, aliases);
        Assert.Equal(book["chilling flux"], book["леденящий расплав"]);
        Assert.False(book.ContainsKey("не существует"));
    }

    [Fact]
    public void LoadNameMap_NormalizesBothSides()
    {
        var path = Path.Combine(Path.GetTempPath(), $"exileeye-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"Aldur's Legacy": "Наследие Альдура"}""");
            var map = PriceBook.LoadNameMap(path);
            Assert.Equal("наследие альдура", map["aldur s legacy"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadNameMap_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(PriceBook.LoadNameMap(Path.Combine(Path.GetTempPath(), "no-such-file.json")));
    }

    [Fact]
    public void ShippedRussianNames_LoadAndLookSane()
    {
        // data/ru-names.json is copied next to the test binaries via the project reference.
        var map = PriceBook.LoadRussianNames();
        Assert.True(map.Count >= 200, $"expected the full item set, got {map.Count}");
        Assert.All(map.Values, v => Assert.True(v.Any(c => c >= 'а' && c <= 'я'), $"'{v}' has no Cyrillic"));
    }
}
