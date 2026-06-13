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
    private bool _ctrlDown;

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

        base.OnStartup(e);

        _single = new Mutex(initiallyOwned: true, @"Global\ExileEye.Single", out bool first);
        if (!first) { Shutdown(); return; }

        _hook = new TaskPoolGlobalHook();
        _hook.KeyPressed += (_, ev) =>
        {
            // Esc closes the in-game panel — drop the overlay the moment the key goes down.
            if (ev.Data.KeyCode == KeyCode.VcEscape && ScanLoop.IsOverlayVisible) ScanLoop.Dismiss();
            else if (ev.Data.KeyCode == KeyCode.VcLeftControl) _ctrlDown = true;
        };
        _hook.KeyReleased += (_, ev) =>
        {
            // Toggle on release so key auto-repeat can't fire many toggles.
            if (ev.Data.KeyCode == KeyCode.VcF5)
                Current?.Dispatcher.BeginInvoke(() => (Current.MainWindow as MainWindow)?.ToggleLoop());
            else if (ev.Data.KeyCode == KeyCode.VcF6) ScanLoop.RequestScan();   // on-demand scan
            else if (ev.Data.KeyCode == KeyCode.VcLeftControl) _ctrlDown = false;
        };
        // Ctrl+click is the in-game purchase gesture — hide the overlay out of the way.
        _hook.MousePressed += (_, ev) =>
        {
            if (ev.Data.Button == SharpHook.Data.MouseButton.Button1 && _ctrlDown && ScanLoop.IsOverlayVisible)
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
}
