using System.Drawing;
using System.IO;
using ExileEye.Overlay;

namespace ExileEye.Core;

/// <summary>
/// The engine: capture → panel gate → OCR → price match → row smoothing → overlay, ~7 times a
/// second while a panel is up. The overlay appears only after OCR resolves at least one priced
/// item ("confirmed"), so random bright screens never flash prices.
/// </summary>
public sealed class ScanLoop : IDisposable
{
    private const int IdleIntervalMs = 150;    // polling for a panel
    private const int ActiveIntervalMs = 100;  // panel up, OCR cadence
    private const int FramesToOpen = 2;        // bright frames before OCR starts (hysteresis)
    private const int FramesToClose = 3;       // dark frames before the panel counts as closed
    private const int TopmostEveryFrames = 10; // periodically re-assert overlay z-order over the game
    // The "…" reading hint only shows this long after the brightness gate opens. A real panel
    // confirms (first priced row) well inside this window; anything still unconfirmed after it
    // is a false positive — bright pavement, a vista — and must not leave the hint hanging.
    private const int ReadingHintWindowMs = 2500;

    private readonly Settings _settings;
    private readonly PriceBook _prices;
    private readonly IconStore? _icons;
    private readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "scan.log");
    private CancellationTokenSource? _cts;
    private Task? _task;

    // The global Esc / Ctrl+click hook (App) hides the overlay through this latch; the loop
    // releases it once the panel has actually gone dark for a few frames.
    private static volatile bool _dismissed;
    private static volatile bool _visible;

    public bool IsRunning => _task is { IsCompleted: false };
    public static bool IsOverlayVisible => _visible;
    public static void Dismiss() => _dismissed = true;

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
        var tracker = new RowTracker(Log);
        OverlayHost.Show(_settings.Region, _settings.OverlayGap);
        OverlayHost.SetIcons(_icons?.Divine, _icons?.Exalted);

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
                        var lines = ocr.Read(shot);
                        if (lines.Count > 0)
                        {
                            var book = _prices.Prices;
                            var matched = lines.Select(l => (l, PriceMatcher.Find(book, l.Name))).ToList();
                            Log($"ocr {lines.Count} rows → " + string.Join(" | ",
                                matched.Select(m => $"'{m.l.RawText}'{(m.Item2 is null ? " MISS" : $" → {m.Item2.Key}")}")));

                            if (!confirmed && matched.Any(m => m.Item2 is not null))
                            {
                                confirmed = true;
                                Log("panel confirmed (priced row)");
                            }
                            rows = tracker.Advance(matched);
                        }
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

        _visible = false;
        OverlayHost.Close();
        Log("stopped");
    }

    public void Dispose()
    {
        Stop(TimeSpan.FromSeconds(2));
        _cts?.Dispose();
    }
}
