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
    private bool _populating;

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

        _populating = true;
        LanguageBox.ItemsSource = Settings.Languages.Select(l => l.Label);
        LanguageBox.SelectedIndex = Math.Max(0,
            Array.FindIndex(Settings.Languages, l => l.Code == _settings.Language));
        PopulateLeagues(Settings.Leagues);
        _populating = false;

        RefreshRegionLabel();

        // Live league list and the price fetch run together; the dropdown quietly upgrades from
        // the built-in fallback once poe.ninja answers.
        var leaguesTask = LeagueDirectory.FetchAsync(_http);
        await ReloadPricesAsync();
        var leagues = await leaguesTask;
        if (leagues.Count > 0)
        {
            _populating = true;
            PopulateLeagues(leagues);
            _populating = false;
        }
    }

    private void PopulateLeagues(IReadOnlyList<string> leagues)
    {
        // The saved league stays selectable even if poe.ninja no longer lists it.
        var list = leagues.Contains(_settings.League)
            ? leagues
            : [.. leagues, _settings.League];
        LeagueBox.ItemsSource = list;
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
        StartStopButton.IsEnabled = _settings.IsCalibrated;
        if (!_settings.IsCalibrated)
            SetStatus(InfoBarSeverity.Warning, "Almost there",
                "Open the game panel and click Calibrate to drag a box around it.");
    }

    private void ShowReadyStatus()
    {
        if (_prices is null) return;
        string at = _prices.FetchedAt is { } t ? t.ToString("HH:mm") : "—";
        SetStatus(InfoBarSeverity.Success, "Ready",
            $"{_prices.Count} items priced · updated {at} · F5 starts/stops, Esc hides");
    }

    private void SetStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
    }

    private void RefreshRegionLabel() =>
        RegionLabel.Text = _settings.IsCalibrated
            ? $"{_settings.RegionWidth}×{_settings.RegionHeight} at {_settings.RegionX},{_settings.RegionY}"
            : "not calibrated";

    private void OnCalibrate(object sender, RoutedEventArgs e)
    {
        bool wasRunning = StopLoop();
        var picked = RegionPicker.Pick();
        if (picked is { } rect)
        {
            _settings.Region = rect;
            _settings.Save();
            RefreshRegionLabel();
            StartStopButton.IsEnabled = _prices?.Count > 0;
            ShowReadyStatus();
        }
        if (wasRunning) StartLoop();
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
        if (_loop is not null || _prices is null || !_settings.IsCalibrated) return;
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

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        StopLoop();
        _prices?.Dispose();
        _icons?.Dispose();
        _http.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
