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
        // Trade price-check check: search the live trade API for a type/name in the saved league.
        //   ExileEye.exe --pricecheck "Divine Orb"
        if (e.Args.Length >= 2 && e.Args[0] == "--pricecheck")
        {
            RunHeadlessPriceCheck(e.Args[1]);
            Environment.Exit(0);
            return;
        }

        base.OnStartup(e);

        _single = new Mutex(initiallyOwned: true, @"Global\ExileEye.Single", out bool first);
        if (!first) { Shutdown(); return; }

        // Ctrl+D (price check) and Ctrl+S (scan) are registered via Win32 RegisterHotKey in
        // MainWindow — like EE2's Electron globalShortcut: fires once, no auto-repeat, and is taken
        // from the game. The raw hook here only handles Esc and click to dismiss the overlay.
        _hook = new TaskPoolGlobalHook();
        _hook.KeyPressed += (_, ev) =>
        {
            if (ev.Data.KeyCode == KeyCode.VcEscape && ScanLoop.IsOverlayVisible) ScanLoop.Dismiss();
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

    private static void RunHeadlessPriceCheck(string typeOrName)
    {
        var outPath = Path.Combine(AppContext.BaseDirectory, "price-check.txt");
        var lines = new List<string>();
        try
        {
            var settings = Settings.Load();
            // Treat the arg as a currency/type search (the common case for a quick check).
            var item = new ParsedItem(null, typeOrName, "Currency");
            lines.Add($"[pricecheck] '{typeOrName}' league='{settings.League}'");
            using var http = new System.Net.Http.HttpClient();
            var client = new TradeClient(http);
            var result = Task.Run(() => client.CheckAsync(item, settings.League, settings.Language)).GetAwaiter().GetResult();
            if (result is null) { lines.Add("  no result (rate-limited or error)"); }
            else
            {
                lines.Add($"  {result.Label}: {result.Total} online, {result.Listings.Count} fetched");
                foreach (var l in result.Listings) lines.Add($"    {l.Amount} {l.Currency}");
                var typ = result.Typical();
                lines.Add($"  typical: {(typ is null ? "—" : $"{typ.Amount} {typ.Currency}")}");
            }
        }
        catch (Exception ex) { lines.Add($"[pricecheck] ERROR {ex}"); }
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
