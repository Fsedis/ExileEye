using ExileEye.Core;

namespace ExileEye.Tests;

public class StatDbTests
{
    private static StatDb Build()
    {
        const string json = """
        {
          "result": [
            { "label": "Explicit", "entries": [
              { "id": "explicit.stat_3299347043", "text": "# к максимуму здоровья" },
              { "id": "explicit.stat_3372524247", "text": "#% к сопротивлению огню" },
              { "id": "explicit.stat_ms",          "text": "#% повышение скорости передвижения" },
              { "id": "explicit.stat_armes",       "text": "#% повышение брони и энергетического щита" },
              { "id": "explicit.stat_dmg",         "text": "Добавляет от # до # физического урона" }
            ]},
            { "label": "Desecrated", "entries": [
              { "id": "desecrated.stat_freeze",    "text": "#% увеличение порога заморозки" }
            ]},
            { "label": "Implicit", "entries": [
              { "id": "implicit.stat_3299347043", "text": "# к максимуму здоровья" }
            ]}
          ]
        }
        """;
        var db = new StatDb();
        db.Parse(json);
        return db;
    }

    [Fact]
    public void Match_SingleValue_ResolvesIdAndValue()
    {
        var m = Build().Match("+25 к максимуму здоровья")!;
        Assert.Equal("explicit.stat_3299347043", m.Id);
        Assert.Equal(25, m.Value);
    }

    [Fact]
    public void Match_PercentValue_Resolves()
    {
        var m = Build().Match("+15% к сопротивлению огню")!;
        Assert.Equal("explicit.stat_3372524247", m.Id);
        Assert.Equal(15, m.Value);
    }

    [Fact]
    public void Match_TwoValues_AveragesForMin()
    {
        var m = Build().Match("Добавляет от 5 до 11 физического урона")!;
        Assert.Equal("explicit.stat_dmg", m.Id);
        Assert.Equal(new double[] { 5, 11 }, m.Values);
        Assert.Equal(8, m.Value);
    }

    [Fact]
    public void Match_PrefersExplicitOverImplicit_OnSharedText()
    {
        Assert.StartsWith("explicit", Build().Match("+40 к максимуму здоровья")!.Id);
    }

    [Fact]
    public void Match_NonStatLine_ReturnsNull()
    {
        var db = Build();
        Assert.Null(db.Match("Требуется: Уровень 11"));
        Assert.Null(db.Match("Уровень предмета: 80"));
    }

    // Real lines from a copied PoE2 unique (Указ о полёте).
    [Fact]
    public void Match_StripsAdvancedRollRange()
    {
        var m = Build().Match("23(20-30)% увеличение порога заморозки")!;
        Assert.Equal("desecrated.stat_freeze", m.Id);
        Assert.Equal(23, m.Value);   // current roll, not the range
    }

    [Fact]
    public void Match_RuneSuffix_Resolves()
    {
        var m = Build().Match("+18% к сопротивлению огню (rune)")!;
        Assert.Equal("explicit.stat_3372524247", m.Id);
        Assert.Equal(18, m.Value);
    }

    [Fact]
    public void Match_RangeMidSentence_Resolves()
    {
        var m = Build().Match("155(150-200)% повышение брони и энергетического щита")!;
        Assert.Equal("explicit.stat_armes", m.Id);
        Assert.Equal(155, m.Value);
    }

    [Fact]
    public void Match_GroupHeaderLine_ReturnsNull()
    {
        Assert.Null(Build().Match("{ Уникальное свойство — Скорость }"));
    }
}
