using System.Drawing;
using System.IO;
using ExileEye.Overlay;

namespace ExileEye.Core;

/// <summary>
/// The engine: capture → OCR → price match → row smoothing → overlay. Two trigger modes:
///
/// "hotkey" (default) — idle until F6, then a short burst of OCR frames over the fully-open,
/// static panel; the result stays up until the panel closes, Esc, or the next F6. Most accurate:
/// no mid-animation frames and nothing to false-trigger on bright scenery — and zero background
/// CPU while idle.
///
/// "auto" — continuous brightness-gated scanning; prices appear by themselves when a panel
/// opens. The overlay only shows after OCR resolves a priced item, so bright scenery never
/// flashes prices.
/// </summary>
public sealed class ScanLoop : IDisposable
{
    private const int IdleIntervalMs = 150;    // auto mode: polling for a panel
    private const int ActiveIntervalMs = 100;  // auto mode: panel up, OCR cadence
    private const int FramesToOpen = 2;        // bright frames before OCR starts (hysteresis)
    private const int FramesToClose = 3;       // dark frames before the panel counts as closed
    private const int TopmostEveryFrames = 10; // periodically re-assert overlay z-order over the game
    // The "…" reading hint only shows this long after the brightness gate opens. A real panel
    // confirms (first priced row) well inside this window; anything still unconfirmed after it
    // is a false positive — bright pavement, a vista — and must not leave the hint hanging.
    private const int ReadingHintWindowMs = 2500;

    // Hotkey-mode burst: at least MinFrames reads (confirmation pins need agreeing frames),
    // stop early once two consecutive frames produce identical rows, give up after MaxFrames.
    private const int BurstMinFrames = 3;
    private const int BurstMaxFrames = 25;
    private const int TriggerPollMs = 50;
    private const int HoldPollMs = 150;        // watching for panel close while results are up

    private readonly Settings _settings;
    private readonly PriceBook _prices;
    private readonly IconStore? _icons;
    private readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "scan.log");
    private CancellationTokenSource? _cts;
    private Task? _task;

    // The global Esc / Ctrl+click hook (App) hides the overlay through this latch; F6 raises
    // the scan request. Static because the hook outlives any single loop instance.
    private static volatile bool _dismissed;
    private static volatile bool _visible;
    private static volatile bool _scanRequested;

    public bool IsRunning => _task is { IsCompleted: false };
    public static bool IsOverlayVisible => _visible;
    public static void Dismiss() => _dismissed = true;
    public static void RequestScan() => _scanRequested = true;

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

        Log($"start mode={_settings.ScanMode} lang={_settings.Language} league='{_settings.League}' " +
            $"prices={_prices.Count} region={_settings.Region}");

        using var ocr = new OcrReader(TessdataFetcher.TessdataDir, _settings.Language, Log);
        var tracker = new RowTracker(Log);
        OverlayHost.Show(_settings.Region, _settings.OverlayGap);
        OverlayHost.SetIcons(_icons?.Divine, _icons?.Exalted);
        _dismissed = false;
        _scanRequested = false;

        if (_settings.ScanMode == "auto") await RunAutoAsync(ocr, tracker, ct);
        else await RunOnDemandAsync(ocr, tracker, ct);

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

            var rows = await BurstAsync(ocr, tracker, ct);
            if (rows.Count == 0) { _visible = false; OverlayHost.Clear(); continue; }

            await HoldAsync(ct);
            _visible = false;
            OverlayHost.Clear();
        }
    }

    /// <summary>A few OCR frames over the static panel; stops early when two consecutive frames
    /// agree (jitter settled), or when nothing priced shows up.</summary>
    private async Task<IReadOnlyList<DisplayRow>> BurstAsync(OcrReader ocr, RowTracker tracker, CancellationToken ct)
    {
        OverlayHost.Update([], showReadingHint: true);
        IReadOnlyList<DisplayRow> rows = [];
        bool confirmed = false;
        string previousShape = "";
        int agreeing = 0;

        for (int f = 0; f < BurstMaxFrames && !ct.IsCancellationRequested && !_scanRequested; f++)
        {
            try
            {
                using var shot = ScreenGrabber.Capture(_settings.Region);
                if (!PanelProbe.LooksOpen(shot, out int lum))
                {
                    Log($"no panel under the region (lum={lum})");
                    break;
                }
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

    /// <summary>Results stay up until the panel closes, Esc/Ctrl+click, or the next F6.</summary>
    private async Task HoldAsync(CancellationToken ct)
    {
        int dark = 0, frame = 0;
        while (!ct.IsCancellationRequested && !_scanRequested)
        {
            if (_dismissed) { _dismissed = false; Log("dismissed"); return; }
            try
            {
                using var shot = ScreenGrabber.Capture(_settings.Region);
                if (!PanelProbe.LooksOpen(shot, out _)) { if (++dark >= FramesToClose) { Log("panel closed"); return; } }
                else dark = 0;
                if (++frame % TopmostEveryFrames == 0) OverlayHost.ReassertTopmost();
            }
            catch (Exception ex)
            {
                Log($"ERROR {ex.GetType().Name}: {ex.Message}");
            }
            try { await Task.Delay(HoldPollMs, ct); } catch (OperationCanceledException) { return; }
        }
    }

    // ---- continuous (auto) mode ---------------------------------------------------------------

    private async Task RunAutoAsync(OcrReader ocr, RowTracker tracker, CancellationToken ct)
    {
        bool panelUp = false, confirmed = false;
        int brightStreak = 0, darkStreak = 0, dismissedDark = 0, frame = 0;
        long panelUpAt = 0;
        IReadOnlyList<DisplayRow> rows = [];

        while (!ct.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            try
            {
                using var shot = ScreenGrabber.Capture(_settings.Region);
                bool bright = PanelProbe.LooksOpen(shot, out int lum);

                if (_dismissed)
                {
                    // Stay hidden until the panel really closes; Esc closes it instantly,
                    // Ctrl+click (a purchase) leaves it open, so no flicker either way.
                    dismissedDark = bright ? 0 : dismissedDark + 1;
                    if (dismissedDark >= FramesToClose) { _dismissed = false; Log("dismiss released"); }
                    panelUp = false; confirmed = false; brightStreak = darkStreak = 0;
                    tracker.Reset(); rows = [];
                    _visible = false;
                    OverlayHost.Clear();
                }
                else
                {
                    dismissedDark = 0;
                    if (bright) { brightStreak++; darkStreak = 0; } else { darkStreak++; brightStreak = 0; }
                    bool wasUp = panelUp;
                    if (!panelUp && brightStreak >= FramesToOpen) { panelUp = true; panelUpAt = Environment.TickCount64; }
                    else if (panelUp && darkStreak >= FramesToClose) panelUp = false;
                    if (panelUp != wasUp) Log($"panel {(panelUp ? "up" : "down")} lum={lum}");

                    if (panelUp)
                    {
                        var (next, anyPriced) = ScanFrame(ocr, tracker, shot);
                        if (anyPriced && !confirmed) { confirmed = true; Log("panel confirmed (priced row)"); }
                        if (next.Count > 0) rows = next;
                    }
                    else
                    {
                        tracker.Reset();
                        rows = [];
                        confirmed = false;
                    }

                    _visible = confirmed;
                    bool hint = panelUp && !confirmed
                                && Environment.TickCount64 - panelUpAt < ReadingHintWindowMs;
                    OverlayHost.Update(confirmed ? rows : [], showReadingHint: hint);
                    if (++frame % TopmostEveryFrames == 0) OverlayHost.ReassertTopmost();
                }
            }
            catch (Exception ex)
            {
                Log($"ERROR {ex.GetType().Name}: {ex.Message}");
            }

            int wait = (panelUp ? ActiveIntervalMs : IdleIntervalMs)
                       - (int)(Environment.TickCount64 - started);
            if (wait > 0)
            {
                try { await Task.Delay(wait, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public void Dispose()
    {
        Stop(TimeSpan.FromSeconds(2));
        _cts?.Dispose();
    }
}
