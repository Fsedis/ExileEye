using System.Net.Http;
using System.Windows;
using ExileEye.Core;
using ExileEye.Overlay;
using Wpf.Ui.Controls;

namespace ExileEye;

public partial class MainWindow : FluentWindow
{
    private readonly HttpClient _http = new();
    private Settings _settings = new();
    private PriceBook? _prices;
    private IconStore? _icons;
    private ScanLoop? _loop;
    private TradeClient? _trade;
    private bool _populating;
    private bool _priceCheckBusy;

    public MainWindow()
    {
        InitializeComponent();
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (v is not null) VersionLabel.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        _settings = Settings.Load();

        // Resolve the league list first (one quick call), then fetch prices exactly once for
        // the league that will actually be selected.
        var leagues = await LeagueDirectory.FetchAsync(_http);

        _populating = true;
        LanguageBox.ItemsSource = Settings.Languages.Select(l => l.Label);
        LanguageBox.SelectedIndex = Math.Max(0,
            Array.FindIndex(Settings.Languages, l => l.Code == _settings.Language));
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
        StartStopButton.IsEnabled = false;
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
        StartStopButton.IsEnabled = true;
    }

    private void ShowReadyStatus()
    {
        if (_prices is null) return;
        string at = _prices.FetchedAt is { } t ? t.ToString("HH:mm") : "—";
        SetStatus(InfoBarSeverity.Success, "Ready",
            $"{_prices.Count} items priced · updated {at} · F6 scans a panel · F7 prices the hovered item");
    }

    private void SetStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
    }

    private void OnStartStop(object sender, RoutedEventArgs e) => ToggleLoop();

    /// <summary>Shared by the button and the global F5 hook (marshalled to the UI thread).</summary>
    internal void ToggleLoop()
    {
        if (_loop is null) StartLoop();
        else StopLoop();
    }

    private void StartLoop()
    {
        if (_loop is not null || _prices is null) return;
        if (!TessdataFetcher.HasLanguage(_settings.Language)) return;
        _loop = new ScanLoop(_settings, _prices, _icons);
        _loop.Start();
        StartStopButton.Content = "Stop  (F5)";
        StartStopButton.Icon = new SymbolIcon(SymbolRegular.Stop24);
        StartStopButton.Appearance = ControlAppearance.Danger;
    }

    private bool StopLoop()
    {
        if (_loop is null) return false;
        _loop.Dispose();
        _loop = null;
        StartStopButton.Content = "Start  (F5)";
        StartStopButton.Icon = new SymbolIcon(SymbolRegular.Play24);
        StartStopButton.Appearance = ControlAppearance.Primary;
        return true;
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
            var accent = System.Drawing.Color.FromArgb(120, 160, 255);
            var grey = System.Drawing.Color.FromArgb(150, 150, 150);
            var green = System.Drawing.Color.FromArgb(96, 255, 128);

            string? backup = SafeClipboardText();
            InputSender.SendCtrlC();
            // Poll until the game replaces the clipboard (or give up) — more reliable than a
            // fixed wait, since copy latency varies.
            string copied = "";
            for (int i = 0; i < 8; i++)
            {
                await Task.Delay(60);
                var now = SafeClipboardText();
                if (!string.IsNullOrEmpty(now) && now != backup) { copied = now; break; }
            }

            var item = ItemParser.Parse(copied);
            if (item is null || !item.IsSearchable)
            {
                RestoreClipboard(backup);
                PriceToast.Show(System.Windows.Forms.Cursor.Position, "ExileEye", "Hover an item, then press F7", grey);
                return;
            }

            var label = item.Name ?? item.Type ?? "?";
            PriceToast.Show(System.Windows.Forms.Cursor.Position, label, "checking price…", accent);
            var result = await _trade.CheckAsync(item, _settings.League);
            RestoreClipboard(backup);

            var at = System.Windows.Forms.Cursor.Position;
            if (result is null)
            {
                PriceToast.Show(at, label, "no data (rate-limited or offline)", grey);
                return;
            }
            var typ = result.Typical();
            string body = typ is null
                ? $"{result.Total} online · no price"
                : $"{Format(typ.Amount)} {typ.Currency} · {result.Total} online";
            PriceToast.Show(at, label, body, result.Total > 0 ? green : grey);
        }
        finally { _priceCheckBusy = false; }
    }

    private static string Format(decimal d) =>
        d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string? SafeClipboardText()
    {
        try { return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null; }
        catch { return null; }
    }

    private static void RestoreClipboard(string? text)
    {
        try { if (!string.IsNullOrEmpty(text)) System.Windows.Clipboard.SetText(text); }
        catch { /* clipboard busy — leave it */ }
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        StopLoop();
        _prices?.Dispose();
        _icons?.Dispose();
        _http.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
