using ExileEye.Core;

namespace ExileEye.Tests;

public class RowTrackerTests
{
    private static readonly Price P1 = new(1m, 80m);
    private static readonly Price P2 = new(2m, 160m);

    private static (OcrLine, Match?) Read(int y, string name, Match? match = null, int qty = 1) =>
        (new OcrLine(name, qty, name, y), match);

    [Fact]
    public void ExactMatch_PinsOnFirstFrame()
    {
        var tracker = new RowTracker();
        var rows = tracker.Advance([Read(100, "chilling flux", new Match("chilling flux", P1, Exact: true))]);
        Assert.Single(rows);
        Assert.True(rows[0].Locked);
        Assert.Equal(P1, rows[0].Price);
    }

    [Fact]
    public void FuzzyMatch_NeedsTwoAgreeingFrames()
    {
        var tracker = new RowTracker();
        var m = new Match("verisium vision", P1, Exact: false);
        var first = tracker.Advance([Read(100, "verisium viswn", m)]);
        Assert.False(first[0].Locked);
        var second = tracker.Advance([Read(102, "verisium viswn", m)]);
        Assert.True(second[0].Locked);
    }

    [Fact]
    public void MissNeverUnpinsARow()
    {
        var tracker = new RowTracker();
        tracker.Advance([Read(100, "chilling flux", new Match("chilling flux", P1, Exact: true))]);
        var after = tracker.Advance([Read(101, "garbage read here")]);
        Assert.True(after[0].Locked);
        Assert.Equal("chilling flux", after[0].Name);
    }

    [Fact]
    public void UnseenSlot_EvictsAfterMisses()
    {
        var tracker = new RowTracker();
        tracker.Advance([Read(100, "chilling flux", new Match("chilling flux", P1, Exact: true))]);
        for (int i = 0; i < 4; i++) tracker.Advance([]);
        Assert.Empty(tracker.Advance([]));
    }

    [Fact]
    public void PanelSwitch_TwoChangedRows_ResetsEverything()
    {
        var tracker = new RowTracker();
        tracker.Advance([
            Read(100, "item one", new Match("item one", P1, Exact: true)),
            Read(200, "item two", new Match("item two", P1, Exact: true)),
        ]);
        var rows = tracker.Advance([
            Read(100, "item three", new Match("item three", P2, Exact: true)),
            Read(200, "item four", new Match("item four", P2, Exact: true)),
        ]);
        // Old pins are gone; the new items pin immediately (exact).
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.Locked));
        Assert.Equal("item three", rows[0].Name);
    }

    [Fact]
    public void SingleChangedRow_DoesNotResetOtherRows()
    {
        var tracker = new RowTracker();
        tracker.Advance([
            Read(100, "item one", new Match("item one", P1, Exact: true)),
            Read(200, "item two", new Match("item two", P1, Exact: true)),
        ]);
        // One position reading a different exact-match item is not a panel switch; the other
        // row's pin must survive. (The changed row itself may re-pin — exact reads are trusted.)
        var rows = tracker.Advance([
            Read(100, "item one", new Match("item one", P1, Exact: true)),
            Read(200, "item five", new Match("item five", P2, Exact: true)),
        ]);
        Assert.Equal("item one", rows.First(r => r.CenterY == 100).Name);
        Assert.True(rows.First(r => r.CenterY == 100).Locked);
    }
}
