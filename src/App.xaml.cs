using System.Windows;
using System.Windows.Threading;
using LibraryWarden.Core;
using LibraryWarden.UI;
using Microsoft.Toolkit.Uwp.Notifications;

namespace LibraryWarden;

public partial class App : Application
{
    const string InstanceName = @"Local\LibraryWarden.Instance";
    const string ShowSignalName = @"Local\LibraryWarden.Show";

    Mutex? _mutex;
    EventWaitHandle? _showSignal;
    TrayIcon? _tray;
    MainWindow? _main;

    public static VaultService Service { get; internal set; } = null!;
    public static bool IsExiting { get; private set; }
    public static new App Current => (App)Application.Current;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && e.Args[0] is "--selftest" or "--render-ui")
        {
            int code = await DiagnosticsRunner.RunAsync(e.Args);
            IsExiting = true;
            Shutdown(code);
            return;
        }

        _mutex = new Mutex(true, InstanceName, out bool isFirst);
        if (!isFirst)
        {
            // Läuft schon: vorhandenes Fenster nach vorne holen und beenden.
            try { using var signal = EventWaitHandle.OpenExisting(ShowSignalName); signal.Set(); } catch { }
            Shutdown();
            return;
        }

        AppPaths.Ensure();
        Log.PruneOldFiles();
        Log.Info($"LibraryWarden {typeof(App).Assembly.GetName().Version?.ToString(3)} gestartet ({string.Join(' ', e.Args)}).");
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Schwerer Fehler", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("Unbeobachteter Fehler", ex.Exception);
            ex.SetObserved();
        };
        System.Windows.Forms.Application.EnableVisualStyles();

        Service = new VaultService();
        Service.NewContentFound += (events, auto) => Dispatcher.InvokeAsync(() => OnNewContent(events, auto));
        Service.Downloads.QueueFinished += () => Dispatcher.InvokeAsync(OnQueueFinished);
        Service.Downloads.StateChanged += () => Dispatcher.InvokeAsync(UpdateTray);
        Service.LibraryChanged += () => Dispatcher.InvokeAsync(UpdateTray);
        Service.CheckStateChanged += () => Dispatcher.InvokeAsync(UpdateTray);

        try { Autostart.Apply(Service.Settings.Autostart); }
        catch (Exception ex) { Log.Error("Autostart konnte nicht gesetzt werden", ex); }

        _tray = new TrayIcon(
            open: () => ShowMain(),
            check: () => _ = Service.RunCheckAsync(full: false),
            togglePause: () => Service.Downloads.SetPaused(!Service.Downloads.Paused),
            exit: ExitApp);
        Notifier.Fallback = (title, text) => _tray?.ShowBalloon(title, text);
        Notifier.Init(args => Dispatcher.InvokeAsync(() => OnToastAction(args)));

        ThemeManager.ThemeChanged += () => _tray?.ApplyTheme(ThemeManager.IsDark);
        ThemeManager.Initialize(Service.Settings.Theme, Service.Settings.UiScale);

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        new Thread(() =>
        {
            while (_showSignal.WaitOne())
            {
                if (IsExiting) return;
                Dispatcher.InvokeAsync(() => ShowMain());
            }
        }) { IsBackground = true, Name = "ShowSignal" }.Start();

        _main = new MainWindow();
        bool background = e.Args.Contains("--tray") || ToastNotificationManagerCompat.WasCurrentProcessToastActivated();
        if (!background || !Service.Api.IsLoggedIn) ShowMain();
        Service.Start();
        UpdateTray();

        if (!Service.Api.IsLoggedIn && _main.IsVisible)
            await _main.LoginAsync();
    }

    public void ShowMain(string? tab = null)
    {
        if (_main == null) return;
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true;
        _main.Topmost = false;
        if (tab != null) _main.SelectTab(tab);
    }

    void OnNewContent(IReadOnlyList<ChangeEvent> events, IReadOnlyList<long> autoStarted)
    {
        Notifier.ShowChanges(events, autoStarted);
        UpdateTray();
    }

    void OnQueueFinished()
    {
        var (ok, failed) = Service.Downloads.TakeFinishedStats();
        if (ok + failed == 0) return;
        var text = $"{ok} Datei(en) geladen" + (failed > 0 ? $", {failed} fehlgeschlagen" : "") + ".";
        Log.Info("Warteschlange leer: " + text);
        if (_main is { IsActive: true }) return;
        Notifier.ShowSimple(failed > 0 ? "Downloads beendet – mit Fehlern" : "Downloads abgeschlossen", text, "downloads");
    }

    void OnToastAction(ToastArguments args)
    {
        args.TryGetValue("action", out string? action);
        if (action == "download" && args.TryGetValue("ids", out string? ids))
        {
            var gameIds = ids.Split(',').Select(s => long.TryParse(s, out var v) ? v : 0).Where(v => v > 0).ToList();
            ShowMain("downloads");
            if (_main!.EnsureLibraryRoot()) Service.EnqueueMissing(gameIds, explicitRequest: true);
        }
        else
        {
            ShowMain(action == "downloads" ? "downloads" : "changes");
        }
    }

    void UpdateTray()
    {
        if (_tray == null) return;
        var unread = Service.UnreadCount();
        var (active, remaining, _) = Service.Downloads.Totals();
        var parts = new List<string> { "LibraryWarden" };
        if (Service.IsChecking) parts.Add("prüft …");
        if (unread > 0) parts.Add($"{unread} neue Inhalte");
        if (active > 0) parts.Add(Service.Downloads.Paused ? $"{active} Downloads pausiert" : $"{active} Downloads ({Fmt.Bytes(remaining)})");
        _tray.SetTooltip(string.Join(" · ", parts));
        _tray.SetPaused(Service.Downloads.Paused);
    }

    public void ExitApp()
    {
        if (Service.Downloads.HasActive && !Service.Downloads.Paused &&
            !Dialog.Confirm(_main, "Es laufen noch Downloads. Sie werden beim nächsten Start fortgesetzt.\n\nLibraryWarden trotzdem beenden?",
                "LibraryWarden beenden", "Beenden", "Weiterladen"))
            return;
        _main?.SavePlacement();
        IsExiting = true;
        Service.Shutdown();
        _tray?.Dispose();
        _tray = null;
        _main?.Close();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExiting = true;
        Service?.Shutdown();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }

    void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unbehandelter Fehler", e.Exception);
        e.Handled = true;
        var text = $"Ein unerwarteter Fehler ist aufgetreten:\n\n{e.Exception.Message}\n\nDetails stehen im Log.";
        try { Dialog.Error(_main, text); }
        catch { MessageBox.Show(text, "LibraryWarden", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
