using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ExileEye.Core;
using ExileEye.Overlay;
using Wpf.Ui.Controls;

namespace ExileEye;

public partial class MainWindow : FluentWindow
{
    // Global hotkeys, registered with the game so it doesn't also act on them (like EE2's
    // globalShortcut). MOD_CONTROL | MOD_NOREPEAT; Ctrl+D price check, Ctrl+S panel scan.
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mod, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    private const uint MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyPrice = 1, HotkeyScan = 2;
    private IntPtr _hwnd;
    private int _capturing;   // HotkeyPrice / HotkeyScan while waiting for a rebind keypress, else 0

    private readonly HttpClient _http = new();
    private Settings _settings = new();
    private PriceBook? _prices;
    private IconStore? _icons;
    private ScanLoop? _loop;
    private TradeClient? _trade;
    private StatDb? _stats;
    private bool _populating;
    private bool _priceCheckBusy;

    public MainWindow()
    {
        InitializeComponent();
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (v is not null) VersionLabel.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
        Loaded += async (_, _) => await InitializeAsync();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        RegisterHotkeys();
    }

    private void RegisterHotkeys()
    {
        if (_hwnd == IntPtr.Zero) return;
        UnregisterHotKey(_hwnd, HotkeyPrice);
        UnregisterHotKey(_hwnd, HotkeyScan);
        RegisterHotKey(_hwnd, HotkeyPrice, _settings.PriceHotkeyMods | MOD_NOREPEAT, _settings.PriceHotkeyVk);
        RegisterHotKey(_hwnd, HotkeyScan, _settings.ScanHotkeyMods | MOD_NOREPEAT, _settings.ScanHotkeyVk);
        RefreshHotkeyLabels();
    }

    private void RefreshHotkeyLabels()
    {
        if (PriceHotkeyText is null) return;   // not yet loaded
        PriceHotkeyText.Text = FormatHotkey(_settings.PriceHotkeyMods, _settings.PriceHotkeyVk);
        ScanHotkeyText.Text = FormatHotkey(_settings.ScanHotkeyMods, _settings.ScanHotkeyVk);
    }

    private static string FormatHotkey(uint mods, uint vk)
    {
        var parts = new List<string>();
        if ((mods & 2) != 0) parts.Add("Ctrl");
        if ((mods & 1) != 0) parts.Add("Alt");
        if ((mods & 4) != 0) parts.Add("Shift");
        if ((mods & 8) != 0) parts.Add("Win");
        parts.Add(vk == 0 ? "—" : System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)vk).ToString());
        return string.Join("+", parts);
    }

    private void OnRebindPrice(object sender, RoutedEventArgs e) => BeginCapture(HotkeyPrice);
    private void OnRebindScan(object sender, RoutedEventArgs e) => BeginCapture(HotkeyScan);

    private void BeginCapture(int which)
    {
        _capturing = which;
        var label = which == HotkeyPrice ? PriceHotkeyText : ScanHotkeyText;
        label.Text = "press keys… (Esc to cancel)";
    }

    // Captures the next key combo while rebinding. Modifier-only presses are ignored so the user
    // can hold Ctrl/Alt and then tap the letter.
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (_capturing == 0) { base.OnPreviewKeyDown(e); return; }
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin)
        { e.Handled = true; return; }   // wait for the real key

        int which = _capturing;
        _capturing = 0;
        e.Handled = true;
        if (key == System.Windows.Input.Key.Escape) { RefreshHotkeyLabels(); return; }

        uint mods = (uint)System.Windows.Input.Keyboard.Modifiers;   // Alt=1,Ctrl=2,Shift=4,Win=8 — matches MOD_*
        uint vk = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        if (which == HotkeyPrice) { _settings.PriceHotkeyMods = mods; _settings.PriceHotkeyVk = vk; }
        else { _settings.ScanHotkeyMods = mods; _settings.ScanHotkeyVk = vk; }
        _settings.Save();
        RegisterHotkeys();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            if (wParam.ToInt32() == HotkeyPrice) { TriggerPriceCheck(); handled = true; }
            else if (wParam.ToInt32() == HotkeyScan) { TriggerScan(); handled = true; }
        }
        return IntPtr.Zero;
    }

    private async Task InitializeAsync()
    {
        _settings = Settings.Load();
        RegisterHotkeys();   // SourceInitialized ran with defaults; re-arm with the saved bindings

        // Resolve the league list first (one quick call), then fetch prices exactly once for
        // the league that will actually be selected.
        var leagues = await LeagueDirectory.FetchAsync(_http);

        _populating = true;
        LanguageBox.ItemsSource = Settings.Languages.Select(l => l.Label);
        LanguageBox.SelectedIndex = Math.Max(0,
            Array.FindIndex(Settings.Languages, l => l.Code == _settings.Language));
        StatusBox.ItemsSource = Settings.StatusOptions.Select(o => o.Label);
        StatusBox.SelectedIndex = Math.Max(0, Array.FindIndex(Settings.StatusOptions, o => o.Code == _settings.TradeStatus));
        SessionBox.Text = _settings.PoeSessId;
        ListedBox.ItemsSource = Settings.ListedOptions.Select(o => o.Label);
        ListedBox.SelectedIndex = Math.Max(0, Array.FindIndex(Settings.ListedOptions, o => o.Code == _settings.TradeListed));
        PopulateLeagues(leagues.Count > 0 ? leagues : Settings.Leagues);
        _populating = false;

        await ReloadPricesAsync();
    }

    private void PopulateLeagues(IReadOnlyList<string> leagues)
    {
        LeagueBox.ItemsSource = leagues;
        if (!leagues.Contains(_settings.League))
        {
            // The saved league ended — poe.ninja lists the current one first.
            _settings.League = leagues[0];
            _settings.Save();
        }
        LeagueBox.SelectedItem = _settings.League;
    }

    private async Task ReloadPricesAsync()
    {
        SetStatus(InfoBarSeverity.Informational, "Loading", "Fetching prices from poe.ninja…");

        _prices?.Dispose();
        _prices = new PriceBook(_http);
        _prices.Updated += () => Dispatcher.BeginInvoke(ShowReadyStatus);

        var ocrModel = TessdataFetcher.EnsureAsync(_settings.Language, _http);
        await Task.WhenAll(_prices.FetchAsync(_settings), ocrModel);
        _prices.StartAutoRefresh(_settings);

        // Overlay currency sprites (cached on disk; falls back to "div"/"ex" text when absent).
        _icons?.Dispose();
        _icons = new IconStore();
        await _icons.LoadAsync(_http, _prices.DivineIconUrl, _prices.ExaltedIconUrl);

        // Trade stat dictionary for rare-item mod search (cached on disk after first fetch).
        _stats = new StatDb();
        await _stats.LoadAsync(_http, _settings.Language);

        if (!ocrModel.Result)
        {
            SetStatus(InfoBarSeverity.Error, "OCR model missing",
                "Couldn't download the Russian OCR data — check your connection and reselect the language.");
            return;
        }
        if (_prices.Count == 0)
        {
            SetStatus(InfoBarSeverity.Warning, "No prices",
                "poe.ninja returned nothing — check your connection or the selected league.");
            return;
        }

        ShowReadyStatus();
    }

    private void ShowReadyStatus()
    {
        if (_prices is null) return;
        string at = _prices.FetchedAt is { } t ? t.ToString("HH:mm") : "—";
        string scanKey = FormatHotkey(_settings.ScanHotkeyMods, _settings.ScanHotkeyVk);
        string priceKey = FormatHotkey(_settings.PriceHotkeyMods, _settings.PriceHotkeyVk);
        SetStatus(InfoBarSeverity.Success, "Ready",
            $"{_prices.Count} items priced · updated {at} · {scanKey} scans a panel · {priceKey} prices the hovered item");
    }

    private void SetStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
    }

    /// <summary>Ctrl+S: scan the panel, auto-starting the engine if it isn't running yet.</summary>
    internal void TriggerScan()
    {
        if (_loop is null) StartLoop();
        ScanLoop.RequestScan();
    }

    private void StartLoop()
    {
        if (_loop is not null || _prices is null) return;
        if (!TessdataFetcher.HasLanguage(_settings.Language)) return;
        _loop = new ScanLoop(_settings, _prices, _icons);
        _loop.Start();
    }

    private bool StopLoop()
    {
        if (_loop is null) return false;
        _loop.Dispose();
        _loop = null;
        return true;
    }

    private void OnTradeOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_populating) return;
        if (StatusBox.SelectedIndex >= 0)
            _settings.TradeStatus = Settings.StatusOptions[StatusBox.SelectedIndex].Code;
        if (ListedBox.SelectedIndex >= 0)
            _settings.TradeListed = Settings.ListedOptions[ListedBox.SelectedIndex].Code;
        _settings.Save();
    }

    private void OnSessionChanged(object sender, RoutedEventArgs e)
    {
        if (_populating) return;
        var v = SessionBox.Text.Trim();
        if (v == _settings.PoeSessId) return;
        _settings.PoeSessId = v;
        _settings.Save();
        if (_trade is not null) _trade.SessionId = v;
    }

    private async void OnLanguageChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_populating || LanguageBox.SelectedIndex < 0) return;
        var code = Settings.Languages[LanguageBox.SelectedIndex].Code;
        if (code == _settings.Language) return;
        StopLoop();   // the OCR engine's language is fixed at start
        _settings.Language = code;
        _settings.Save();
        await ReloadPricesAsync();   // re-applies localized aliases, downloads the OCR model
    }

    private async void OnLeagueChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_populating || LeagueBox.SelectedItem is not string league || league == _settings.League) return;
        _settings.League = league;
        _settings.Save();
        await ReloadPricesAsync();
    }

    // ---- clipboard price check (F7) -----------------------------------------------------------

    /// <summary>
    /// Copy the hovered item (synthetic Ctrl+C), parse it, query the trade API, and pop the price
    /// next to the cursor. Invoked on the UI thread (STA) so clipboard access is safe; the network
    /// call is awaited without blocking. The user's clipboard is restored afterwards.
    /// </summary>
    internal async void TriggerPriceCheck()
    {
        if (_priceCheckBusy || _prices is null) return;
        _priceCheckBusy = true;
        try
        {
            _trade ??= new TradeClient(_http);
            _trade.SessionId = _settings.PoeSessId;   // may be empty; required for instant-buyout
            var accent = System.Drawing.Color.FromArgb(120, 160, 255);
            var grey = System.Drawing.Color.FromArgb(150, 150, 150);
            var green = System.Drawing.Color.FromArgb(96, 255, 128);

            string? backup = SafeClipboardText();
            // Clear first so the poll can tell a fresh copy from stale content (EE2 does this).
            if (ItemParser.IsPoeItem(backup)) RestoreClipboard("");
            InputSender.SendCtrlC();
            // Poll until the game writes item text (copy latency varies), up to ~0.5s.
            string copied = "";
            for (int i = 0; i < 9; i++)
            {
                await Task.Delay(55);
                var now = SafeClipboardText() ?? "";
                if (ItemParser.IsPoeItem(now)) { copied = now; break; }
            }

            DumpClipboardDebug(backup, copied);

            var item = ItemParser.Parse(copied);
            if (item is null || !item.IsSearchable)
            {
                RestoreClipboard(backup);
                string why = string.IsNullOrEmpty(copied)
                    ? "couldn't copy — try running ExileEye as admin"
                    : "not a recognized item";
                PriceToast.Show(System.Windows.Forms.Cursor.Position, "ExileEye", why, grey);
                return;
            }

            var label = item.Name ?? item.Type ?? "?";
            RestoreClipboard(backup);

            // Recognized mod lines → the interactive window (toggle mods, set minimums, search).
            var mods = new List<PriceCheckWindow.ModFilter>();
            if (_stats is { Count: > 0 })
            {
                foreach (var line in copied.Replace("\r", "").Split('\n'))
                {
                    var m = _stats.Match(line.Trim());
                    if (m is null || mods.Any(x => x.Id == m.Id)) continue;
                    mods.Add(new PriceCheckWindow.ModFilter
                    {
                        Text = line.Trim(), Id = m.Id, Value = m.Value,
                        Include = false,
                        MinText = m.Value == 0 ? "" : Format((decimal)m.Value),
                    });
                }
            }

            var tradeOpts = new TradeOptions(_settings.TradeStatus, _settings.TradeListed);

            if (mods.Count > 0)
            {
                var win = new PriceCheckWindow(item, mods, _trade, _settings.League, _settings.Language, tradeOpts);
                win.Show();
                win.PositionAt(System.Windows.Forms.Cursor.Position);
                return;
            }

            // No mods (currency, gems, uniques) → quick toast price.
            PriceToast.Show(System.Windows.Forms.Cursor.Position, label, "checking price…", accent);
            var result = await _trade.CheckAsync(item, _settings.League, _settings.Language, null, tradeOpts);
            var at = System.Windows.Forms.Cursor.Position;
            if (result is null) { PriceToast.Show(at, label, "no data (rate-limited or offline)", grey); return; }
            var cheap = result.Cheapest;
            string body = cheap is null
                ? $"{result.Total} online · no price"
                : $"from {Format(cheap.Amount)} {cheap.Currency} · {result.Total} online";
            PriceToast.Show(at, label, body, result.Total > 0 ? green : grey);
        }
        finally { _priceCheckBusy = false; }
    }

    private static string Format(decimal d) =>
        d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    // Records what F7 actually copied, so a failure can be diagnosed from the bytes (empty =>
    // input injection / admin; non-empty but unparsed => parser/language).
    private static void DumpClipboardDebug(string? backup, string copied, uint injected = 0)
    {
        try
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "debug");
            System.IO.Directory.CreateDirectory(dir);
            var parsed = ItemParser.Parse(copied);
            var text = $"injected events: {injected}\n" +
                       $"backup length: {backup?.Length ?? -1}\n" +
                       $"copied length: {copied.Length}\n" +
                       $"IsPoeItem: {ItemParser.IsPoeItem(copied)}\n" +
                       $"parsed: name='{parsed?.Name}' type='{parsed?.Type}' rarity='{parsed?.Rarity}' stack={parsed?.StackSize}\n" +
                       "---- copied text ----\n" + copied;
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "clipboard.txt"), text);
        }
        catch { /* best-effort diagnostic */ }
    }

    private static string? SafeClipboardText() => ClipboardText.Read();

    private static void RestoreClipboard(string? text)
    {
        if (text is not null) ClipboardText.Write(text);
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HotkeyPrice);
            UnregisterHotKey(_hwnd, HotkeyScan);
        }
        StopLoop();
        _prices?.Dispose();
        _icons?.Dispose();
        _http.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
