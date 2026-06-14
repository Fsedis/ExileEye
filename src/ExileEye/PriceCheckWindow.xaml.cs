using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using ExileEye.Core;
using Wpf.Ui.Controls;

namespace ExileEye;

/// <summary>
/// Awakened-style interactive price check: the item's mods (toggle + min), inline market/age/
/// currency controls that re-search on change, an estimated value with range, and the listings
/// table (price, level, quality, seller, age).
/// </summary>
public partial class PriceCheckWindow : FluentWindow
{
    public sealed class ModFilter
    {
        public required string Text { get; init; }
        public required string Id { get; init; }
        public double Value { get; init; }
        public bool Include { get; set; }
        public string MinText { get; set; } = "";
    }

    private const int FirstPage = 20, NextPage = 10;

    private readonly ParsedItem _item;
    private readonly TradeClient _trade;
    private readonly Settings _settings;
    private readonly ObservableCollection<ModFilter> _mods;
    private TradeSession? _session;
    private readonly ObservableCollection<object> _listingRows = [];
    private string? _browseUrl;
    private bool _searching;
    private bool _loadingMore;
    private bool _populating = true;

    public PriceCheckWindow(ParsedItem item, IReadOnlyList<ModFilter> mods, TradeClient trade, Settings settings)
    {
        InitializeComponent();
        _item = item;
        _trade = trade;
        _settings = settings;
        _mods = new ObservableCollection<ModFilter>(mods);

        ItemLabel.Text = item.Name is not null && item.Type is not null
            ? $"{item.Name} · {item.Type}" : item.Name ?? item.Type ?? "?";
        ModList.ItemsSource = _mods;
        ListingList.ItemsSource = _listingRows;

        StatusBox.ItemsSource = Settings.StatusOptions.Select(o => o.Label);
        StatusBox.SelectedIndex = IndexOf(Settings.StatusOptions, _settings.TradeStatus);
        ListedBox.ItemsSource = Settings.ListedOptions.Select(o => o.Label);
        ListedBox.SelectedIndex = IndexOf(Settings.ListedOptions, _settings.TradeListed);
        CurrencyBox.ItemsSource = Settings.CurrencyOptions.Select(o => o.Label);
        CurrencyBox.SelectedIndex = IndexOf(Settings.CurrencyOptions, _settings.TradeCurrency);
        _populating = false;

        Loaded += async (_, _) => await SearchAsync();
    }

    private static int IndexOf((string Code, string Label)[] opts, string code) =>
        System.Math.Max(0, System.Array.FindIndex(opts, o => o.Code == code));

    public void PositionAt(System.Drawing.Point cursorPx)
    {
        double sx = 1, sy = 1;
        var ps = System.Windows.PresentationSource.FromVisual(this);
        if (ps?.CompositionTarget is { } ct) { sx = ct.TransformFromDevice.M11; sy = ct.TransformFromDevice.M22; }
        var screen = System.Windows.Forms.Screen.FromPoint(cursorPx).WorkingArea;
        double left = cursorPx.X * sx + 12, top = cursorPx.Y * sy + 12;
        Left = System.Math.Max(screen.Left * sx, System.Math.Min(left, screen.Right * sx - Width - 8));
        Top = System.Math.Max(screen.Top * sy, System.Math.Min(top, screen.Bottom * sy - (ActualHeight > 0 ? ActualHeight : 360) - 8));
    }

    // Any control change persists the choice and re-runs the search.
    private async void OnControlChanged(object sender, RoutedEventArgs e)
    {
        if (_populating) return;
        if (StatusBox.SelectedIndex >= 0) _settings.TradeStatus = Settings.StatusOptions[StatusBox.SelectedIndex].Code;
        if (ListedBox.SelectedIndex >= 0) _settings.TradeListed = Settings.ListedOptions[ListedBox.SelectedIndex].Code;
        if (CurrencyBox.SelectedIndex >= 0) _settings.TradeCurrency = Settings.CurrencyOptions[CurrencyBox.SelectedIndex].Code;
        _settings.Save();
        await SearchAsync();
    }

    private async void OnSearch(object sender, RoutedEventArgs e) => await SearchAsync();

    private async System.Threading.Tasks.Task SearchAsync()
    {
        if (_searching) return;
        _searching = true;
        SearchButton.IsEnabled = false;
        try
        {
            var stats = _mods.Where(m => m.Include).Select(m => new TradeStat(m.Id,
                double.TryParse(m.MinText, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null)).ToList();
            var opts = new TradeOptions(_settings.TradeStatus, _settings.TradeListed, _settings.TradeCurrency);
            EstimateText.Text = "Searching…"; RangeText.Text = ""; StatusLine.Text = "";
            _modCount = stats.Count;

            _listingRows.Clear();
            _session = await _trade.SearchAsync(_item, _settings.League, _settings.Language,
                stats.Count > 0 ? stats : null, opts);
            if (_session is null) { EstimateText.Text = "No data (rate-limited or offline)"; return; }

            _browseUrl = _session.BrowseUrl;
            BrowserButton.IsEnabled = _browseUrl is not null;
            await _session.FetchMoreAsync(FirstPage);
            Render();
        }
        finally { _searching = false; SearchButton.IsEnabled = true; }
    }

    private int _modCount;

    // Appends only the not-yet-shown listings, so loading more keeps the scroll position.
    private void Render()
    {
        if (_session is null) return;
        var est = _session.Estimated();
        EstimateText.Text = est is null ? "no price" : $"≈ {Format(est.Mid)} {ShortCurrency(est.Currency)}";
        RangeText.Text = est is null ? "" : $"range {Format(est.Low)}–{Format(est.High)} · reliability {est.Reliability}";
        StatusLine.Text = $"{_session.Total} online · {(_modCount > 0 ? $"{_modCount} mods" : "base type")}"
            + (_session.HasMore ? " · scroll for more" : "");
        for (int i = _listingRows.Count; i < _session.Listings.Count; i++)
        {
            var l = _session.Listings[i];
            _listingRows.Add(new
            {
                Price = $"{Format(l.Amount)} {ShortCurrency(l.Currency)}",
                Level = l.ReqLevel is { } lv ? $"L{lv}" : "",
                Quality = l.Quality is { } q ? $"{q}%" : "",
                Account = l.Account,
                Age = RelativeAge(l.Listed),
                Tooltip = l.Description,
                HasTooltip = l.Description.Length > 0,
            });
        }
    }

    // Near the bottom and more to load → fetch the next page and append.
    private async void OnListingScroll(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (_loadingMore || _session is null || !_session.HasMore) return;
        if (e.VerticalOffset < e.ExtentHeight - e.ViewportHeight - 40) return;   // not near the bottom yet
        _loadingMore = true;
        try { if (await _session.FetchMoreAsync(NextPage) > 0) Render(); }
        finally { _loadingMore = false; }
    }

    private void OnOpenBrowser(object sender, RoutedEventArgs e)
    {
        if (_browseUrl is null) return;
        try { Process.Start(new ProcessStartInfo(_browseUrl) { UseShellExecute = true }); } catch { }
    }

    private static string Format(decimal d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    private static string ShortCurrency(string c) => c switch
    {
        "exalted" => "ex", "divine" => "div", "regal" => "regal", "annul" => "annul",
        "vaal" => "vaal", "alch" => "alch", "aug" => "aug", "mirror" => "mirror", _ => c,
    };

    private static string RelativeAge(System.DateTimeOffset? listed)
    {
        if (listed is not { } t) return "";
        var d = System.DateTimeOffset.Now - t;
        if (d.TotalMinutes < 60) return $"{(int)System.Math.Max(1, d.TotalMinutes)}m";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h";
        return $"{(int)d.TotalDays}d";
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
