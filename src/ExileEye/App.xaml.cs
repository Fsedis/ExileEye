using System.IO;
using System.Threading;
using System.Windows;
using ExileEye.Core;
using SharpHook;
using SharpHook.Data;

namespace ExileEye;

public partial class App : System.Windows.Application
{
    private TaskPoolGlobalHook? _hook;

    // Only one instance may own the global hook and the overlay.
    private static Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Headless pipeline check: run OCR + parsing on an image, print rows to ocr-check.txt.
        //   ExileEye.exe --read <image.png> [en|ru]
        if (e.Args.Length >= 2 && e.Args[0] == "--read")
        {
            RunHeadlessRead(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : null);
            Environment.Exit(0);
            return;
        }
        // Full-window locating check: find text lines anywhere in a screenshot, print boxes.
        //   ExileEye.exe --locate <image.png> [en|ru]
        if (e.Args.Length >= 2 && e.Args[0] == "--locate")
        {
            RunHeadlessLocate(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : null);
            Environment.Exit(0);
            return;
        }

        base.OnStartup(e);

        _single = new Mutex(initiallyOwned: true, @"Global\ExileEye.Single", out bool first);
        if (!first) { Shutdown(); return; }

        _hook = new TaskPoolGlobalHook();
        _hook.KeyPressed += (_, ev) =>
        {
            // Esc closes the in-game panel — drop the overlay the moment the key goes down.
            if (ev.Data.KeyCode == KeyCode.VcEscape && ScanLoop.IsOverlayVisible) ScanLoop.Dismiss();
        };
        _hook.KeyReleased += (_, ev) =>
        {
            // Toggle on release so key auto-repeat can't fire many toggles.
            if (ev.Data.KeyCode == KeyCode.VcF5)
                Current?.Dispatcher.BeginInvoke(() => (Current.MainWindow as MainWindow)?.ToggleLoop());
            else if (ev.Data.KeyCode == KeyCode.VcF6) ScanLoop.RequestScan();   // on-demand scan
        };
        _hook.MousePressed += (_, ev) =>
        {
            // The user has read the prices and is now clicking the item — any left click dismisses.
            if (ev.Data.Button == SharpHook.Data.MouseButton.Button1 && ScanLoop.IsOverlayVisible)
                ScanLoop.Dismiss();
        };
        _ = _hook.RunAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }

    private static void RunHeadlessRead(string imagePath, string? language)
    {
        var outPath = Path.Combine(AppContext.BaseDirectory, "ocr-check.txt");
        var lines = new List<string>();
        try
        {
            var lang = language ?? Settings.Load().Language;
            lines.Add($"[read] image='{imagePath}' lang={lang}");
            using var bmp = (System.Drawing.Bitmap)System.Drawing.Image.FromFile(imagePath);
            using var ocr = new OcrReader(TessdataFetcher.TessdataDir, lang, lines.Add);
            foreach (var row in ocr.Read(bmp))
                lines.Add($"  y={row.CenterY} qty={row.Quantity} name='{row.Name}' raw='{row.RawText}'");
        }
        catch (Exception ex)
        {
            lines.Add($"[read] ERROR {ex}");
        }
        try { File.WriteAllLines(outPath, lines); } catch { }
    }

    private static void RunHeadlessLocate(string imagePath, string? language)
    {
        var outPath = Path.Combine(AppContext.BaseDirectory, "locate-check.txt");
        var lines = new List<string>();
        try
        {
            var lang = language ?? Settings.Load().Language;
            lines.Add($"[locate] image='{imagePath}' lang={lang}");
            using var bmp = (System.Drawing.Bitmap)System.Drawing.Image.FromFile(imagePath);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var ocr = new OcrReader(TessdataFetcher.TessdataDir, lang, lines.Add);
            var found = ocr.ReadFull(bmp);
            sw.Stop();
            lines.Add($"[locate] {found.Count} lines in {sw.ElapsedMilliseconds} ms:");
            foreach (var l in found)
                lines.Add($"  x={l.Left}..{l.Right} y={l.CenterY} qty={l.Quantity} name='{l.Name}' raw='{l.RawText}'");

            // Fetch the real price book and compute the region exactly as the live locator will.
            using var http = new System.Net.Http.HttpClient();
            var settings = Settings.Load();
            settings.Language = lang;
            var book = new PriceBook(http);
            // Task.Run keeps the awaits off the WPF startup sync-context (a direct .GetResult()
            // here deadlocks: the continuation can't marshal back to the blocked UI thread).
            Task.Run(() => book.FetchAsync(settings)).GetAwaiter().GetResult();
            lines.Add($"[locate] price book: {book.Count} items");
            var matched = found.Where(l => PriceMatcher.Find(book.Prices, l.Name) is not null).ToList();
            lines.Add($"[locate] {matched.Count} matched the book:");
            foreach (var l in matched)
                lines.Add($"  MATCH x={l.Left}..{l.Right} y={l.CenterY} '{l.Name}'");
            var region = PanelLocator.Locate(found, book.Prices);
            lines.Add($"[locate] computed region: {(region is { } r ? $"{r.Width}x{r.Height} at {r.X},{r.Y}" : "none")}");
        }
        catch (Exception ex)
        {
            lines.Add($"[locate] ERROR {ex}");
        }
        try { File.WriteAllLines(outPath, lines); } catch { }
    }
}
