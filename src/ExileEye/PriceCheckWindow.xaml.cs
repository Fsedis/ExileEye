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
/// Awakened-style interactive price check: lists the item's recognized mods, each with an
/// include checkbox and an editable minimum, and searches the trade API from the selected mods.
/// This is the core of EE2's create-stat-filters flow (per-mod toggles + min value); the long
/// tail of special rules (pseudo-stats, cluster jewels, unique fixed stats) is not ported.
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

    private readonly ParsedItem _item;
    private readonly TradeClient _trade;
    private readonly string _league;
    private readonly string _language;
    private readonly TradeOptions _options;
    private readonly ObservableCollection<ModFilter> _mods;
    private string? _browseUrl;
    private bool _searching;

    public PriceCheckWindow(ParsedItem item, IReadOnlyList<ModFilter> mods,
        TradeClient trade, string league, string language, TradeOptions options)
    {
        InitializeComponent();
        _item = item;
        _trade = trade;
        _league = league;
        _language = language;
        _options = options;
        _mods = new ObservableCollection<ModFilter>(mods);

        ItemLabel.Text = item.Name ?? item.Type ?? "?";
        ItemSub.Text = item.Name is not null && item.Type is not null ? item.Type : item.Rarity;
        ModList.ItemsSource = _mods;

        Loaded += async (_, _) => await SearchAsync();   // baseline search on open
    }

    /// <summary>Place near the cursor, converting physical pixels to WPF DIPs and clamping on-screen.</summary>
    public void PositionAt(System.Drawing.Point cursorPx)
    {
        double sx = 1, sy = 1;
        var ps = System.Windows.PresentationSource.FromVisual(this);
        if (ps?.CompositionTarget is { } ct)
        {
            sx = ct.TransformFromDevice.M11;
            sy = ct.TransformFromDevice.M22;
        }
        var screen = System.Windows.Forms.Screen.FromPoint(cursorPx).WorkingArea;
        double left = cursorPx.X * sx + 12;
        double top = cursorPx.Y * sy + 12;
        // Clamp using DIP-converted screen bounds.
        double maxLeft = screen.Right * sx - Width - 8;
        double maxTop = screen.Bottom * sy - (ActualHeight > 0 ? ActualHeight : 300) - 8;
        Left = System.Math.Max(screen.Left * sx, System.Math.Min(left, maxLeft));
        Top = System.Math.Max(screen.Top * sy, System.Math.Min(top, maxTop));
    }

    private async void OnSearch(object sender, RoutedEventArgs e) => await SearchAsync();

    private async System.Threading.Tasks.Task SearchAsync()
    {
        if (_searching) return;
        _searching = true;
        SearchButton.IsEnabled = false;
        try
        {
            var stats = new List<TradeStat>();
            foreach (var m in _mods.Where(m => m.Include))
            {
                double? min = double.TryParse(m.MinText, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
                    ? v : null;
                stats.Add(new TradeStat(m.Id, min));
            }
            ResultText.Text = "Searching…";

            var result = await _trade.CheckAsync(_item, _league, _language, stats.Count > 0 ? stats : null, _options);
            if (result is null) { ResultText.Text = "No data (rate-limited or offline)."; ListingList.ItemsSource = null; return; }

            _browseUrl = result.BrowseUrl;
            BrowserButton.IsEnabled = _browseUrl is not null;

            string scope = stats.Count > 0 ? $"{stats.Count} mods" : "base type";
            ResultText.Text = $"{result.Total} online · {scope} · cheapest first";
            ListingList.ItemsSource = result.Listings.Select(l => new
            {
                Price = $"{Format(l.Amount)} {ShortCurrency(l.Currency)}",
                Account = l.Account,
                Age = RelativeAge(l.Listed),
            }).ToList();
        }
        finally
        {
            _searching = false;
            SearchButton.IsEnabled = true;
        }
    }

    private void OnOpenBrowser(object sender, RoutedEventArgs e)
    {
        if (_browseUrl is null) return;
        try { Process.Start(new ProcessStartInfo(_browseUrl) { UseShellExecute = true }); }
        catch { /* no browser */ }
    }

    private static string Format(decimal d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    private static string ShortCurrency(string c) => c switch
    {
        "exalted" => "ex", "divine" => "div", "chaos" => "chaos", "regal" => "regal",
        "annul" => "annul", "vaal" => "vaal", "alch" => "alch", "aug" => "aug",
        "transmute" => "transmute", "mirror" => "mirror", _ => c,
    };

    private static string RelativeAge(DateTimeOffset? listed)
    {
        if (listed is not { } t) return "";
        var d = DateTimeOffset.Now - t;
        if (d.TotalMinutes < 60) return $"{(int)Math.Max(1, d.TotalMinutes)}m";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h";
        return $"{(int)d.TotalDays}d";
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
