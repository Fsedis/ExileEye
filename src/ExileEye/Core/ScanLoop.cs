using System.Drawing;
using System.IO;
using ExileEye.Overlay;

namespace ExileEye.Core;

/// <summary>
/// The engine: idle until F6, then a short burst of OCR frames over the fully-open, static
/// panel — capture → OCR → price match → row smoothing → overlay. The result stays up until the
/// user dismisses it (a mouse click, Esc, or Ctrl+click) or presses F6 again. On-demand only:
/// most accurate (no mid-animation frames, nothing to false-trigger on bright scenery) and zero
/// background CPU while idle.
/// </summary>
public sealed class ScanLoop : IDisposable
{
    private const int TopmostEveryFrames = 10; // periodically re-assert overlay z-order over the game

    // The burst: at least MinFrames reads (confirmation pins need agreeing frames), stop early
    // once two consecutive frames produce identical rows, give up after MaxFrames.
    private const int BurstMinFrames = 3;
    private const int BurstMaxFrames = 25;
    private const int TriggerPollMs = 50;
    private const int HoldPollMs = 150;        // watching for dismissal while results are up

    private readonly Settings _settings;
    private readonly PriceBook _prices;
    private readonly IconStore? _icons;
    private readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "scan.log");
    private CancellationTokenSource? _cts;
    private Task? _task;
    private Rectangle _region;   // current panel region (screen coords); empty until located

    // The global Esc / click hook (App) hides the overlay through this latch; F6 raises the scan
    // request. Static because the hook outlives any single loop instance.
    private static volatile bool _dismissed;
    private static volatile bool _visible;
    private static volatile bool _scanRequested;

    public bool IsRunning => _task is { IsCompleted: false };
    public static bool IsOverlayVisible => _visible;
    public static void RequestScan() => _scanRequested = true;

    public static void Dismiss()
    {
        // Hide instantly off the hook thread; the scan loop sees the latch on its next poll and
        // settles into its hidden state. Without the immediate clear the overlay would linger
        // up to one poll interval after the click, which is exactly what felt "stuck".
        _dismissed = true;
        _visible = false;
        OverlayHost.Clear();
    }

    public ScanLoop(Settings settings, PriceBook prices, IconStore? icons = null)
    {
        _settings = settings;
        _prices = prices;
        _icons = icons;
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop(TimeSpan timeout)
    {
        _cts?.Cancel();
        try { _task?.Wait(timeout); } catch { }
    }

    private void Log(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try { File.WriteAllText(_logPath, ""); } catch { }

        if (!TessdataFetcher.HasLanguage(_settings.Language))
        {
            Log("ERROR OCR model for the selected language is not installed yet");
            return;
        }

        Log($"start lang={_settings.Language} league='{_settings.League}' " +
            $"prices={_prices.Count} region={_settings.Region}");

        using var ocr = new OcrReader(TessdataFetcher.TessdataDir, _settings.Language, Log);
        // Save the exact image Tesseract sees on each scan, so a bad read can be diagnosed from
        // the pixels rather than guessed at. Paired with the raw capture written in BurstAsync.
        var debugDir = Path.Combine(AppContext.BaseDirectory, "debug");
        try { Directory.CreateDirectory(debugDir); ocr.DebugInputPath = Path.Combine(debugDir, "ocr-input.png"); } catch { }
        var tracker = new RowTracker(Log);
        // The last-used panel region is a fast-path guess; revalidated on first scan and replaced
        // by a fresh full-window locate whenever it stops yielding priced rows.
        _region = _settings.Region;
        // Only dock the overlay if we already have a region; otherwise the first locate docks it.
        if (_region.Width > 0 && _region.Height > 0)
            OverlayHost.Show(_region, _settings.OverlayGap);
        OverlayHost.SetIcons(_icons?.Divine, _icons?.Exalted);
        _dismissed = false;
        _scanRequested = false;

        await RunOnDemandAsync(ocr, tracker, ct);

        _visible = false;
        OverlayHost.Close();
        Log("stopped");
    }

    /// <summary>One capture → OCR → match → tracker step. Returns the rows and whether any priced.</summary>
    private (IReadOnlyList<DisplayRow> Rows, bool AnyPriced) ScanFrame(OcrReader ocr, RowTracker tracker, Bitmap shot)
    {
        var lines = ocr.Read(shot);
        if (lines.Count == 0) return ([], false);

        var book = _prices.Prices;
        var matched = lines.Select(l => (l, PriceMatcher.Find(book, l.Name))).ToList();
        Log($"ocr {lines.Count} rows → " + string.Join(" | ",
            matched.Select(m => $"'{m.l.RawText}'{(m.Item2 is null ? " MISS" : $" → {m.Item2.Key}")}")));

        return (tracker.Advance(matched), matched.Any(m => m.Item2 is not null));
    }

    // ---- on-demand (hotkey) mode --------------------------------------------------------------

    private async Task RunOnDemandAsync(OcrReader ocr, RowTracker tracker, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_scanRequested)
            {
                try { await Task.Delay(TriggerPollMs, ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            _scanRequested = false;
            _dismissed = false;
            tracker.Reset();
            Log("scan triggered");

            var rows = await AcquireAsync(ocr, tracker, ct);
            if (rows.Count == 0) { _visible = false; OverlayHost.Clear(); continue; }

            await HoldAsync(ct);
            _visible = false;
            OverlayHost.Clear();
        }
    }

    /// <summary>
    /// Get priced rows for this F6 press. Tries the cached region first (fast); if it yields
    /// nothing — first run, the panel moved, or a different panel is open — locates the panel
    /// afresh from a full-window capture, re-docks the overlay, and scans the new region.
    /// </summary>
    private async Task<IReadOnlyList<DisplayRow>> AcquireAsync(OcrReader ocr, RowTracker tracker, CancellationToken ct)
    {
        if (_region.Width > 0 && _region.Height > 0)
        {
            var rows = await BurstAsync(ocr, tracker, ct, _region);
            if (rows.Count > 0) return rows;
            Log("cached region yielded nothing — relocating");
            tracker.Reset();
        }

        OverlayHost.Update([], showReadingHint: true);
        var located = Locate(ocr);
        if (located is null) { Log("no panel found on screen"); return []; }

        _region = located.Value;
        _settings.Region = _region;
        _settings.Save();
        OverlayHost.Show(_region, _settings.OverlayGap);   // re-dock the strip to the new region
        Log($"located panel: {_region.Width}x{_region.Height} at {_region.X},{_region.Y}");

        return await BurstAsync(ocr, tracker, ct, _region);
    }

    /// <summary>Full-window OCR → match against the book → bound the panel (screen coords).</summary>
    private Rectangle? Locate(OcrReader ocr)
    {
        var window = GameWindow.Bounds();
        using var shot = ScreenGrabber.Capture(window);
        try { shot.Save(Path.Combine(AppContext.BaseDirectory, "debug", "window.png"),
            System.Drawing.Imaging.ImageFormat.Png); } catch { }

        var lines = ocr.ReadFull(shot);
        var local = PanelLocator.Locate(lines, _prices.Prices);
        if (local is not { } r) return null;
        // PanelLocator works in capture coords; shift to screen coords by the window origin.
        return new Rectangle(r.X + window.X, r.Y + window.Y, r.Width, r.Height);
    }

    /// <summary>A few OCR frames over the static panel; stops early when two consecutive frames
    /// agree (jitter settled), or when nothing priced shows up. The region hugs the names, so
    /// no left-crop (that would eat into them).</summary>
    private async Task<IReadOnlyList<DisplayRow>> BurstAsync(
        OcrReader ocr, RowTracker tracker, CancellationToken ct, Rectangle region)
    {
        ocr.LeftCrop = 0;   // auto-located region already starts at the names
        OverlayHost.Update([], showReadingHint: true);
        IReadOnlyList<DisplayRow> rows = [];
        bool confirmed = false;
        string previousShape = "";
        int agreeing = 0;

        for (int f = 0; f < BurstMaxFrames && !ct.IsCancellationRequested && !_scanRequested && !_dismissed; f++)
        {
            try
            {
                using var shot = ScreenGrabber.Capture(region);
                if (f == 0)
                    try { shot.Save(Path.Combine(AppContext.BaseDirectory, "debug", "capture.png"),
                        System.Drawing.Imaging.ImageFormat.Png); } catch { }
                var (next, anyPriced) = ScanFrame(ocr, tracker, shot);
                if (anyPriced) confirmed = true;
                if (next.Count > 0) rows = next;

                _visible = confirmed;
                OverlayHost.Update(confirmed ? rows : [], showReadingHint: !confirmed);

                // Shape = names + lock flags; two identical consecutive frames mean OCR has
                // settled and more frames won't improve anything.
                var shape = string.Join("|", rows.Select(r => $"{r.Name}:{r.Quantity}:{r.Locked}"));
                agreeing = shape == previousShape && shape.Length > 0 ? agreeing + 1 : 0;
                previousShape = shape;
                if (confirmed && f + 1 >= BurstMinFrames && agreeing >= 1) break;
            }
            catch (Exception ex)
            {
                Log($"ERROR {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (!confirmed)
        {
            Log("burst found nothing priced");
            return [];
        }
        Log($"burst done: {rows.Count} rows, {rows.Count(r => r.Locked)} pinned");
        return rows;
    }

    /// <summary>
    /// Results stay up until the user dismisses (a mouse click, Esc, or Ctrl+click — all routed
    /// through the latch) or presses F6 again. Deliberately does NOT auto-hide on a "panel
    /// closed" brightness reading: the gate false-positives on bright scenery (stone floors,
    /// vistas), which would either keep the overlay stuck open or — worse — never let it close.
    /// The click that closes the panel (item, close button, or the game world) dismisses anyway.
    /// </summary>
    private async Task HoldAsync(CancellationToken ct)
    {
        int frame = 0;
        while (!ct.IsCancellationRequested && !_scanRequested)
        {
            if (_dismissed) { _dismissed = false; Log("dismissed"); return; }
            if (++frame % TopmostEveryFrames == 0) OverlayHost.ReassertTopmost();
            try { await Task.Delay(HoldPollMs, ct); } catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose()
    {
        Stop(TimeSpan.FromSeconds(2));
        _cts?.Dispose();
    }
}
