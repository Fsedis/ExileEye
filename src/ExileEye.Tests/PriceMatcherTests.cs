using ExileEye.Core;

namespace ExileEye.Tests;

public class PriceMatcherTests
{
    private static readonly Dictionary<string, Price> Book = new()
    {
        ["chilling flux"] = new(0.5m, 40m),
        ["verisium vision"] = new(2.0m, 160m),
        ["greater orb of transmutation"] = new(0.01m, 0.8m),
        ["древняя руна тлена"] = new(0.3m, 24m),
    };

    [Fact]
    public void ExactMatch_Wins()
    {
        var m = PriceMatcher.Find(Book, "chilling flux");
        Assert.NotNull(m);
        Assert.True(m.Exact);
        Assert.Equal(0.5m, m.Price.Divine);
    }

    [Fact]
    public void PrefixMatch_ResolvesTruncatedName()
    {
        var m = PriceMatcher.Find(Book, "greater orb of trans");
        Assert.NotNull(m);
        Assert.False(m.Exact);
        Assert.Equal("greater orb of transmutation", m.Key);
    }

    [Fact]
    public void FuzzyMatch_RescuesSingleMisread()
    {
        var m = PriceMatcher.Find(Book, "verisium viswn");   // o→w slip
        Assert.NotNull(m);
        Assert.Equal("verisium vision", m.Key);
    }

    [Fact]
    public void FuzzyMatch_WorksOnCyrillic()
    {
        var m = PriceMatcher.Find(Book, "древняя руна тлена");
        Assert.NotNull(m);
        var fuzzy = PriceMatcher.Find(Book, "древняя рина тлена");   // у→и slip
        Assert.NotNull(fuzzy);
        Assert.Equal("древняя руна тлена", fuzzy.Key);
    }

    [Fact]
    public void UnrelatedName_DoesNotMatch()
    {
        Assert.Null(PriceMatcher.Find(Book, "completely different item"));
    }

    [Fact]
    public void ShortName_NeverFuzzyMatches()
    {
        Assert.Null(PriceMatcher.Find(Book, "flux"));
    }

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "abc", 0)]
    [InlineData("abc", "abd", 1)]
    [InlineData("abc", "ab", 1)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("", "abc", 3)]
    public void EditDistance_IsCorrect(string a, string b, int expected)
    {
        Assert.Equal(expected, PriceMatcher.EditDistance(a, b));
    }
}
