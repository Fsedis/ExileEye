using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;

namespace ExileEye;

/// <summary>
/// Embedded-browser login: opens the real pathofexile.com sign-in (handles 2FA/captcha), then
/// reads the authenticated POESESSID cookie — no manual copy from dev-tools. The same approach
/// Electron tools (Awakened/EE2) use, via WebView2 here.
/// </summary>
public partial class LoginWindow : FluentWindow
{
    private const string LoginUrl = "https://www.pathofexile.com/login";
    private const string Site = "https://www.pathofexile.com";

    /// <summary>The captured POESESSID, or null if the user closed without logging in.</summary>
    public string? SessionId { get; private set; }

    public LoginWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            // Keep the browser profile beside the app so the login persists between launches.
            var dataDir = Path.Combine(AppContext.BaseDirectory, "webview2");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
            await Web.EnsureCoreWebView2Async(env);
            Web.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            Web.CoreWebView2.Navigate(LoginUrl);
        }
        catch (Exception ex)
        {
            Hint.Severity = InfoBarSeverity.Error;
            Hint.Title = "WebView2 unavailable";
            Hint.Message = $"Install the Microsoft Edge WebView2 runtime, or paste POESESSID manually. ({ex.Message})";
        }
    }

    // Once the user lands on a logged-in pathofexile.com page (anything but /login), grab the
    // session cookie and finish. A guest also has a POESESSID, so we only capture after leaving
    // the login page — which only happens on a successful sign-in redirect.
    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var url = Web.CoreWebView2.Source ?? "";
        if (!url.StartsWith(Site, StringComparison.OrdinalIgnoreCase)) return;
        if (url.Contains("/login", StringComparison.OrdinalIgnoreCase)) return;

        var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync(Site);
        foreach (var c in cookies)
        {
            if (c.Name == "POESESSID" && !string.IsNullOrWhiteSpace(c.Value))
            {
                SessionId = c.Value;
                DialogResult = true;
                Close();
                return;
            }
        }
    }
}
