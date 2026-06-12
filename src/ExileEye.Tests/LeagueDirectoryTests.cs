using ExileEye.Core;

namespace ExileEye.Tests;

public class LeagueDirectoryTests
{
    [Fact]
    public void ParseIndexState_ReturnsLeagueNamesInOrder()
    {
        const string json = """
            {
              "economyLeagues": [
                { "name": "Runes of Aldur", "url": "runesofaldur", "indexed": true },
                { "name": "HC Runes of Aldur", "url": "runesofaldurhc", "hardcore": true },
                { "name": "Standard", "url": "standard" }
              ],
              "oldEconomyLeagues": [ { "name": "Fate of the Vaal" } ]
            }
            """;
        var leagues = LeagueDirectory.ParseIndexState(json);
        Assert.Equal(["Runes of Aldur", "HC Runes of Aldur", "Standard"], leagues);
    }

    [Fact]
    public void ParseIndexState_GarbageOrMissingKey_ReturnsEmpty()
    {
        Assert.Empty(LeagueDirectory.ParseIndexState("not json"));
        Assert.Empty(LeagueDirectory.ParseIndexState("""{"somethingElse": []}"""));
    }
}
