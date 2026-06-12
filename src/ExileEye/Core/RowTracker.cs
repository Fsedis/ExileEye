namespace ExileEye.Core;

/// <summary>What the overlay shows for one panel row.</summary>
public sealed record DisplayRow(int CenterY, string Name, int Quantity, Price? Price, bool Locked);

/// <summary>
/// Smooths jittery per-frame OCR into stable display rows. Each screen position gets a slot;
/// a slot pins its price once the same item is read confidently (exact match: first read,
/// inexact: two consecutive agreeing reads) and a later misread can't dislodge it. Unpriced
/// slots keep retrying every frame, so an early garbage read self-heals. If several pinned
/// positions suddenly read different priced items, the user switched panels — start over.
/// </summary>
public sealed class RowTracker
{
    private const int SameRowTolerance = 20;   // px a read may drift and still be the same row
    private const int ReadsToPin = 2;          // agreeing inexact reads before a slot pins
    private const int MissesToDrop = 3;        // frames a slot survives without being seen

    private sealed class Slot
    {
        public int Y;
        public DisplayRow Current = null!;
        public bool Pinned;
        public string? Candidate;
        public int CandidateStreak;
        public int MissedFrames;
    }

    private readonly List<Slot> _slots = [];
    private readonly Action<string>? _log;

    public RowTracker(Action<string>? log = null) => _log = log;

    public void Reset() => _slots.Clear();

    public IReadOnlyList<DisplayRow> Advance(IReadOnlyList<(OcrLine Line, Match? Match)> frame)
    {
        DetectPanelSwitch(frame);

        var seen = new HashSet<Slot>();
        foreach (var (line, match) in frame)
        {
            var slot = ClosestFreeSlot(line.CenterY, seen);
            if (slot is null)
            {
                slot = new Slot { Y = line.CenterY };
                _slots.Add(slot);
            }
            seen.Add(slot);
            slot.MissedFrames = 0;

            var row = new DisplayRow(slot.Y, match?.Key ?? line.Name, line.Quantity, match?.Price, Locked: false);
            if (!slot.Pinned) slot.Current = row;

            if (match is null)
            {
                slot.Candidate = null;
                slot.CandidateStreak = 0;
                continue;
            }

            slot.CandidateStreak = slot.Candidate == match.Key ? slot.CandidateStreak + 1 : 1;
            slot.Candidate = match.Key;
            if (slot.CandidateStreak >= (match.Exact ? 1 : ReadsToPin))
            {
                if (!slot.Pinned || slot.Current.Name != match.Key)
                    _log?.Invoke($"pinned y={slot.Y} '{match.Key}'");
                slot.Pinned = true;
                slot.Current = row with { Locked = true };
            }
        }

        for (int i = _slots.Count - 1; i >= 0; i--)
            if (!seen.Contains(_slots[i]) && ++_slots[i].MissedFrames > MissesToDrop)
                _slots.RemoveAt(i);

        return _slots.OrderBy(s => s.Y).Select(s => s.Current).ToList();
    }

    private Slot? ClosestFreeSlot(int y, HashSet<Slot> taken)
    {
        Slot? best = null;
        int bestDist = int.MaxValue;
        foreach (var slot in _slots)
        {
            if (taken.Contains(slot)) continue;
            int d = Math.Abs(slot.Y - y);
            if (d <= SameRowTolerance && d < bestDist) { bestDist = d; best = slot; }
        }
        return best;
    }

    // Pinned slots are deliberately sticky, which would freeze the previous panel's prices if
    // the user opens a different panel without the region ever going dark. Two or more pinned
    // positions reading a different priced item is that signature.
    private void DetectPanelSwitch(IReadOnlyList<(OcrLine Line, Match? Match)> frame)
    {
        int conflicts = 0;
        foreach (var (line, match) in frame)
        {
            if (match is null) continue;
            var pinned = _slots.FirstOrDefault(s =>
                s.Pinned && Math.Abs(s.Y - line.CenterY) <= SameRowTolerance);
            if (pinned is not null && pinned.Current.Name != match.Key) conflicts++;
        }
        if (conflicts >= 2)
        {
            _log?.Invoke($"panel switch ({conflicts} rows changed) — resetting");
            _slots.Clear();
        }
    }
}
