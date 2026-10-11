using System.Windows;
using LibraryWarden.Core;
using Microsoft.Web.WebView2.Core;

namespace LibraryWarden.UI;

/// <summary>Zeigt die offizielle GOG-Anmeldeseite und fängt den Rücksprung mit dem Anmeldecode ab.</summary>
public partial class LoginWindow : Window
{
    readonly bool _clearCookies;
    bool _completing;

    public LoginWindow(bool clearCookies)
    {
        InitializeComponent();
        _clearCookies = clearCookies;
        var scale = ThemeManager.Scale;
        HeaderScale.ScaleX = HeaderScale.ScaleY = scale;
        Width *= Math.Min(scale, 1.5);
        Height *= Math.Min(scale, 1.3);
        if (ThemeManager.IsDark) Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x22, 0x28, 0x27);
        Loaded += async (_, _) => await InitAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowHelpers.ApplyDarkFrame(this, ThemeManager.IsDark);
    }

    async Task InitAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDir);
            await Web.EnsureCoreWebView2Async(environment);
            Web.ZoomFactor = ThemeManager.Scale;
            if (_clearCookies) Web.CoreWebView2.CookieManager.DeleteAllCookies();
            Web.CoreWebView2.NavigationStarting += OnNavigationStarting;
            Web.CoreWebView2.NavigationCompleted += (_, _) => LoadingText.Visibility = Visibility.Collapsed;
            Web.CoreWebView2.Navigate(GogApi.LoginUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Dialog.Warning(this,
                "Für die Anmeldung wird die Microsoft-Edge-WebView2-Laufzeit benötigt.\n\nDownload: https://developer.microsoft.com/microsoft-edge/webview2/",
                "WebView2 fehlt");
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Anmeldefenster konnte nicht geladen werden", ex);
            Dialog.Error(this, $"Die Anmeldeseite konnte nicht geladen werden:\n{ex.Message}");
            Close();
        }
    }

    async void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.Uri.StartsWith("https://embed.gog.com/on_login_success", StringComparison.OrdinalIgnoreCase)) return;
        e.Cancel = true;
        if (_completing) return;

        var code = System.Web.HttpUtility.ParseQueryString(new Uri(e.Uri).Query)["code"];
        if (string.IsNullOrEmpty(code)) return;

        _completing = true;
        Web.Visibility = Visibility.Hidden;
        LoadingText.Text = "Anmeldung wird abgeschlossen …";
        LoadingText.Visibility = Visibility.Visible;
        try
        {
            await App.Service.Api.LoginWithCodeAsync(code);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Log.Error("Anmeldung fehlgeschlagen", ex);
            Dialog.Warning(this, $"Die Anmeldung ist fehlgeschlagen:\n{ex.Message}");
            _completing = false;
            Web.Visibility = Visibility.Visible;
            Web.CoreWebView2.Navigate(GogApi.LoginUrl);
        }
    }
}
