using System.Drawing;

namespace ExileEye.Core;

/// <summary>
/// Finds the panel on screen without any manual calibration: OCR the whole game window, keep the
/// lines whose names match the price book (quest text, HUD, other overlays never do), and bound
/// the cluster of matches. The price-book match itself is the detector — far more robust than
/// guessing at a panel's colour or border art, and it adapts to any panel, resolution, or window
/// position. Outliers (a stray accidental match elsewhere on screen) are dropped by keeping only
/// the densest vertical run of matched rows.
/// </summary>
public static class PanelLocator
{
    // Two matched rows belong to the same panel when their horizontal spans overlap (or nearly):
    // a panel's rows form a vertical column, while stray screen text (quest tracker, HUD) sits in
    // a different column entirely. Clustering on X — not Y — survives the big vertical gap a
    // section separator ("Additional reward") puts between rows of the SAME panel.
    private const int ColumnGap = 40;   // horizontal slack between same-column rows
    private const int PadX = 14;
    private const int PadTop = 16;
    private const int PadBottom = 16;

    /// <summary>
    /// The panel's bounding rectangle in the same coordinate space as <paramref name="lines"/>,
    /// or null if nothing matched the price book.
    /// </summary>
    public static Rectangle? Locate(IReadOnlyList<LocatedLine> lines, IReadOnlyDictionary<string, Price> book)
    {
        var matched = lines.Where(l => PriceMatcher.Find(book, l.Name) is not null).ToList();
        if (matched.Count == 0) return null;

        // Group matches whose horizontal spans overlap into columns; the busiest column is the
        // panel. Greedy over Left-sorted spans, merging while they keep overlapping.
        var byLeft = matched.OrderBy(l => l.Left).ToList();
        var best = new List<LocatedLine>();
        var current = new List<LocatedLine> { byLeft[0] };
        int clusterRight = byLeft[0].Right;
        for (int i = 1; i < byLeft.Count; i++)
        {
            if (byLeft[i].Left <= clusterRight + ColumnGap)
            {
                current.Add(byLeft[i]);
                clusterRight = Math.Max(clusterRight, byLeft[i].Right);
            }
            else
            {
                if (current.Count > best.Count) best = current;
                current = [byLeft[i]];
                clusterRight = byLeft[i].Right;
            }
        }
        if (current.Count > best.Count) best = current;

        int left = best.Min(l => l.Left) - PadX;
        int right = best.Max(l => l.Right) + PadX;
        int top = best.Min(l => l.CenterY) - PadTop;
        int bottom = best.Max(l => l.CenterY) + PadBottom;
        return Rectangle.FromLTRB(Math.Max(0, left), Math.Max(0, top), right, bottom);
    }
}
