using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using LibraryWarden.Core;
using Microsoft.Win32;

namespace LibraryWarden.UI;

public partial class MainWindow : Window
{
    const double CaptionHeight = 32;

    readonly VaultService _svc = App.Service;
    readonly ObservableCollection<GameRow> _games = [];
    readonly Dictionary<long, GameRow> _rows = [];
    readonly ObservableCollection<EventRow> _events = [];
    readonly ObservableCollection<ItemRow> _items = [];
    readonly ObservableCollection<Choice> _scaleChoices = [];
    readonly ICollectionView _gamesView;
    readonly DispatcherTimer _refreshTimer;
    readonly DispatcherTimer _clockTimer;
    SettingsForm _form = new();
    bool _refreshRunning, _refreshAgain, _freshLogin, _ready, _syncingUi, _shownOnce;
    bool _maxHover, _maxPressed;
    string _view = "library";

    public MainWindow()
    {
        InitializeComponent();

        GamesGrid.ItemsSource = _games;
        _gamesView = CollectionViewSource.GetDefaultView(_games);
        _gamesView.Filter = FilterGame;
        _gamesView.SortDescriptions.Add(new SortDescription(nameof(GameRow.Title), ListSortDirection.Ascending));
        EventsGrid.ItemsSource = _events;
        ItemsGrid.ItemsSource = _items;
        BindingOperations.EnableCollectionSynchronization(_svc.Downloads.Jobs, _svc.Downloads.JobsLock);
        JobsGrid.ItemsSource = _svc.Downloads.Jobs;
        FilterBox.ItemsSource = Choices.Filters;
        FilterBox.SelectedIndex = 0;
        ScaleBox.ItemsSource = _scaleChoices;
        GameRow.RuleEdited = (id, rule) =>
        {
            _svc.SetRule(id, rule);
            RequestRefresh();
        };

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            _ = RefreshAsync();
        };
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateDownloadSummary();
        _clockTimer.Start();

        _svc.LibraryChanged += () => Dispatcher.InvokeAsync(RequestRefresh);
        _svc.StatusChanged += text => Dispatcher.InvokeAsync(() => StatusText.Text = text);
        _svc.CheckStateChanged += () => Dispatcher.InvokeAsync(UpdateBusy);
        _svc.Api.LoginStateChanged += () => Dispatcher.InvokeAsync(UpdateAccount);
        _svc.Downloads.FileCompleted += () => Dispatcher.InvokeAsync(RequestRefresh);
        _svc.Downloads.StateChanged += () => Dispatcher.InvokeAsync(() =>
        {
            UpdateDownloadSummary();
            RequestRefresh();
        });
        ThemeManager.ThemeChanged += () => Dispatcher.InvokeAsync(SyncAppearanceUi);
        ThemeManager.ScaleChanged += () => Dispatcher.InvokeAsync(ApplyScale);

        RestorePlacement();
        Topmost = _svc.Settings.AlwaysOnTop;
        SetRibbonCollapsed(_svc.Settings.RibbonCollapsed, save: false);
        LoadSettingsForm();
        UpdateAccount();
        UpdateBusy();
        StatusText.Text = _svc.Status;
        _ready = true;
        SelectTab("library");
        SyncAppearanceUi();
        ApplyScale();
        _ = RefreshAsync();
    }

    // ================= Fenster =================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowHelpers.ApplyDarkFrame(this, ThemeManager.IsDark);
        // Nach WindowChrome eingehängt → wird zuerst aufgerufen (Andock-Layouts am Maximieren-Knopf)
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        UpdateWindowStateUi();
        UpdateTitleIcon();
    }

    protected override void OnDpiChanged(System.Windows.DpiScale oldDpi, System.Windows.DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateTitleIcon();
    }

    /// <summary>Symbolgröße passend zu Bildschirm-DPI und Zoom wählen (16 px Anzeige = 48 px bei 300 %).</summary>
    void UpdateTitleIcon()
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        var frame = WindowHelpers.IconFrame(16 * dpi * ThemeManager.Scale);
        if (frame != null) TitleIcon.Source = frame;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _shownOnce = true;
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateWindowStateUi();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        TitleLeft.Opacity = CaptionButtons.Opacity = 1;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        TitleLeft.Opacity = CaptionButtons.Opacity = 0.55;
    }

    void UpdateWindowStateUi()
    {
        bool maximized = WindowState == WindowState.Maximized;
        RootBorder.Padding = maximized ? WindowHelpers.MaximizedOverhang(this) : new Thickness(0);
        MaxButton.Content = maximized ? "\uE923" : "\uE922";
        MaxButton.ToolTip = maximized ? "Verkleinern" : "Maximieren";
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>Meldet den Maximieren-Knopf an Windows als echten Maximieren-Knopf → Windows 11 zeigt die Andock-Layouts.</summary>
    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2, WM_NCMOUSELEAVE = 0x02A2;
        const int HTMAXBUTTON = 9;
        switch (msg)
        {
            case WM_NCHITTEST:
                if (IsOverMaxButton(lParam))
                {
                    SetMaxHover(true);
                    handled = true;
                    return new IntPtr(HTMAXBUTTON);
                }
                SetMaxHover(false);
                break;
            case WM_NCLBUTTONDOWN when wParam.ToInt64() == HTMAXBUTTON:
                _maxPressed = true;
                MaxButton.SetResourceReference(BackgroundProperty, "Lw.Pressed");
                handled = true;
                break;
            case WM_NCLBUTTONUP when wParam.ToInt64() == HTMAXBUTTON:
                if (_maxPressed) ToggleMaximize();
                _maxPressed = false;
                SetMaxHover(false);
                handled = true;
                break;
            case WM_NCMOUSELEAVE:
                _maxPressed = false;
                SetMaxHover(false);
                break;
        }
        return IntPtr.Zero;
    }

    bool IsOverMaxButton(IntPtr lParam)
    {
        if (!MaxButton.IsVisible) return false;
        long value = lParam.ToInt64();
        var screen = new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
        try
        {
            var p = MaxButton.PointFromScreen(screen);
            return p.X >= 0 && p.Y >= 0 && p.X < MaxButton.ActualWidth && p.Y < MaxButton.ActualHeight;
        }
        catch
        {
            return false;
        }
    }

    void SetMaxHover(bool hover)
    {
        if (_maxHover == hover) return;
        _maxHover = hover;
        if (hover) MaxButton.SetResourceReference(BackgroundProperty, _maxPressed ? "Lw.Pressed" : "Lw.Hover");
        else MaxButton.ClearValue(BackgroundProperty);
    }

    void RestorePlacement()
    {
        var work = SystemParameters.WorkArea;
        var p = _svc.Settings.Window;
        if (p == null || p.Width < 400 || p.Height < 300 || !WindowHelpers.IsOnScreen(p.Left, p.Top, p.Width))
        {
            // Erster Start: an den Bildschirm anpassen (bei hoher Windows-Skalierung ist der nutzbare Bereich klein)
            Width = Math.Min(Width, work.Width * 0.94);
            Height = Math.Min(Height, work.Height * 0.94);
            if (work.Width < 1100 || work.Height < 800) WindowState = WindowState.Maximized;
            return;
        }
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = p.Left;
        Top = p.Top;
        Width = Math.Min(p.Width, SystemParameters.VirtualScreenWidth);
        Height = Math.Min(p.Height, SystemParameters.VirtualScreenHeight);
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    public void SavePlacement()
    {
        if (!_shownOnce) return;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty || double.IsInfinity(bounds.Width)) return;
        var placement = new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
        if (placement != _svc.Settings.Window) _svc.UpdateAppearance(s => s.Window = placement);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SavePlacement();
        if (!App.IsExiting && _svc.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            bool hintShown;
            lock (_svc.Sync)
            {
                hintShown = _svc.State.TrayHintShown;
                _svc.State.TrayHintShown = true;
            }
            if (!hintShown)
            {
                Notifier.ShowSimple("LibraryWarden läuft weiter", "Der Wächter prüft im Hintergrund. Beenden über das Symbol im Infobereich.");
                _svc.RequestSave();
            }
            return;
        }
        if (!App.IsExiting)
        {
            e.Cancel = true;
            App.Current.ExitApp();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.F5:
                _ = CheckAsync(full: shift);
                e.Handled = true;
                break;
            case Key.F when ctrl:
                SelectTab("library");
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;
            case Key.OemPlus or Key.Add when ctrl:
                StepZoom(+0.1);
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract when ctrl:
                StepZoom(-0.1);
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0 when ctrl:
                SetScale(0);
                e.Handled = true;
                break;
            case Key.F1 when ctrl:
                SetRibbonCollapsed(RibbonPanel.Visibility == Visibility.Visible, save: true);
                e.Handled = true;
                break;
            case >= Key.D1 and <= Key.D5 when ctrl:
                SelectTab(new[] { "library", "changes", "downloads", "settings", "view" }[key - Key.D1]);
                e.Handled = true;
                break;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            StepZoom(e.Delta > 0 ? 0.1 : -0.1);
            e.Handled = true;
        }
        base.OnPreviewMouseWheel(e);
    }

    // ================= Menüband =================

    public void SelectTab(string tab)
    {
        RadioButton target = tab switch
        {
            "changes" => TabChanges,
            "downloads" => TabDownloads,
            "settings" => TabSettings,
            "view" => TabView,
            _ => TabLibrary,
        };
        if (target.IsChecked == true) ShowView(tab);
        else target.IsChecked = true;
    }

    void RibbonTab_Checked(object sender, RoutedEventArgs e)
    {
        if (_ready && sender is RadioButton { Tag: string tab }) ShowView(tab);
    }

    void ShowView(string tab)
    {
        RibbonLibrary.Visibility = tab == "library" ? Visibility.Visible : Visibility.Collapsed;
        RibbonChanges.Visibility = tab == "changes" ? Visibility.Visible : Visibility.Collapsed;
        RibbonDownloads.Visibility = tab == "downloads" ? Visibility.Visible : Visibility.Collapsed;
        RibbonSettings.Visibility = tab == "settings" ? Visibility.Visible : Visibility.Collapsed;
        RibbonView.Visibility = tab == "view" ? Visibility.Visible : Visibility.Collapsed;
        if (tab == "view") return;   // „Ansicht“ hat keinen eigenen Inhalt – die aktuelle Seite bleibt stehen
        _view = tab;
        Tabs.SelectedItem = tab switch
        {
            "changes" => ChangesTab,
            "downloads" => DownloadsTab,
            "settings" => SettingsTab,
            _ => LibraryTab,
        };
        TitleText.Text = tab switch
        {
            "changes" => "LibraryWarden – Änderungen",
            "downloads" => "LibraryWarden – Downloads",
            "settings" => "LibraryWarden – Einstellungen",
            _ => "LibraryWarden – Bibliothek",
        };
        Title = TitleText.Text;
    }

    void RibbonTab_DoubleClick(object sender, MouseButtonEventArgs e) =>
        SetRibbonCollapsed(RibbonPanel.Visibility == Visibility.Visible, save: true);

    void RibbonToggle_Click(object sender, RoutedEventArgs e) =>
        SetRibbonCollapsed(RibbonPanel.Visibility == Visibility.Visible, save: true);

    void SetRibbonCollapsed(bool collapsed, bool save)
    {
        RibbonPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        RibbonToggleButton.Content = collapsed ? "\uE70D" : "\uE70E";
        RibbonToggleButton.ToolTip = collapsed ? "Menüband einblenden (Strg+F1)" : "Menüband ausblenden (Strg+F1)";
        if (save && collapsed != _svc.Settings.RibbonCollapsed) _svc.UpdateAppearance(s => s.RibbonCollapsed = collapsed);
    }

    void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs) return;
        if (e.RemovedItems.Contains(ChangesTab))
            _svc.MarkRead(_ => true);   // Protokoll gesehen → als gelesen markieren
    }

    // ================= Ansicht: Design, Skalierung, Fenster =================

    void SyncAppearanceUi()
    {
        _syncingUi = true;
        ThemeLight.IsChecked = ThemeManager.Choice == ThemeChoice.Light;
        ThemeDark.IsChecked = ThemeManager.Choice == ThemeChoice.Dark;
        ThemeSystem.IsChecked = ThemeManager.Choice == ThemeChoice.System;
        TopmostToggle.IsChecked = Topmost;
        _syncingUi = false;
        ThemeToggleButton.Content = ThemeManager.IsDark ? "\uE706" : "\uE708";
        ThemeToggleButton.ToolTip = ThemeManager.IsDark ? "Helles Design" : "Dunkles Design";
        WindowHelpers.ApplyDarkFrame(this, ThemeManager.IsDark);
    }

    void ThemeToggle_Click(object sender, RoutedEventArgs e) =>
        SetTheme(ThemeManager.IsDark ? ThemeChoice.Light : ThemeChoice.Dark);

    void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingUi || !_ready) return;
        if (sender == ThemeLight) SetTheme(ThemeChoice.Light);
        else if (sender == ThemeDark) SetTheme(ThemeChoice.Dark);
        else SetTheme(ThemeChoice.System);
    }

    void SetTheme(ThemeChoice choice)
    {
        ThemeManager.SetTheme(choice);
        _svc.UpdateAppearance(s => s.Theme = choice);
        SyncAppearanceUi();
    }

    void ApplyScale()
    {
        double scale = ThemeManager.Scale;
        RootScale.ScaleX = RootScale.ScaleY = scale;
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome != null && Math.Abs(chrome.CaptionHeight - CaptionHeight * scale) > 0.1)
        {
            var copy = (WindowChrome)chrome.Clone();
            copy.CaptionHeight = CaptionHeight * scale;
            WindowChrome.SetWindowChrome(this, copy);
        }
        MinWidth = 900 * Math.Min(scale, 1.3);
        MinHeight = 580 * Math.Min(scale, 1.3);
        UpdateTitleIcon();

        _syncingUi = true;
        var system = $"Wie Windows ({ThemeManager.TextScaleFactor * 100:0} %)";
        double[] steps = [0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0];
        var values = steps.ToList();
        if (!ThemeManager.FollowsSystemScale && !values.Contains(scale)) values.Add(scale);
        _scaleChoices.Clear();
        _scaleChoices.Add(new Choice(0.0, system));
        foreach (var v in values.OrderBy(v => v)) _scaleChoices.Add(new Choice(v, $"{v * 100:0} %"));
        ScaleBox.SelectedValue = ThemeManager.FollowsSystemScale ? 0.0 : scale;
        _syncingUi = false;
        ScaleInfo.Text = ThemeManager.FollowsSystemScale
            ? "Folgt der Windows-Textgröße. Strg + Mausrad zoomt."
            : $"Fest {scale * 100:0} %. Strg + 0 = wie Windows.";
    }

    void ScaleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || !_ready || ScaleBox.SelectedValue is not double value) return;
        SetScale(value);
    }

    void ZoomIn_Click(object sender, RoutedEventArgs e) => StepZoom(+0.1);

    void ZoomOut_Click(object sender, RoutedEventArgs e) => StepZoom(-0.1);

    void StepZoom(double delta) => SetScale(Math.Round(Math.Clamp(ThemeManager.Scale + delta, 0.7, 2.5), 2));

    void SetScale(double value)
    {
        ThemeManager.SetScale(value);
        var stored = ThemeManager.FollowsSystemScale ? 0 : ThemeManager.Scale;
        if (Math.Abs(stored - _svc.Settings.UiScale) > 0.001) _svc.UpdateAppearance(s => s.UiScale = stored);
    }

    void Topmost_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostToggle.IsChecked == true;
        _svc.UpdateAppearance(s => s.AlwaysOnTop = Topmost);
    }

    // ================= Anmeldung =================

    void UpdateAccount()
    {
        string? name;
        lock (_svc.Sync) name = _svc.State.Username;
        AccountText.Text = _svc.Api.IsLoggedIn ? (name != null ? $"Angemeldet als {name}" : "Angemeldet") : "Nicht angemeldet";
        LoginButton.Content = _svc.Api.IsLoggedIn ? "Abmelden" : "Anmelden";
    }

    async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_svc.Api.IsLoggedIn)
        {
            if (!Dialog.Confirm(this, "Vom GOG-Konto abmelden?\n\nDer Wächter pausiert, bis du dich wieder anmeldest. Heruntergeladene Dateien bleiben erhalten.",
                    "Abmelden", "Abmelden", "Abbrechen")) return;
            _svc.Api.Logout();
            lock (_svc.Sync) _svc.State.Username = null;
            _freshLogin = true;
            UpdateAccount();
            return;
        }
        await LoginAsync();
    }

    public async Task LoginAsync()
    {
        var dialog = new LoginWindow(clearCookies: _freshLogin);
        if (IsVisible) dialog.Owner = this;
        if (dialog.ShowDialog() != true) return;
        _freshLogin = false;
        UpdateAccount();
        bool baselineDone;
        lock (_svc.Sync) baselineDone = _svc.State.BaselineDone;
        if (!baselineDone)
            StatusText.Text = "Angemeldet – die Bibliothek wird zum ersten Mal eingelesen. Das kann bei großen Bibliotheken einige Minuten dauern …";
        await _svc.RunCheckAsync(full: !baselineDone);
        UpdateAccount();
    }

    // ================= Prüfen & Laden =================

    async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync(full: false);

    async void FullCheck_Click(object sender, RoutedEventArgs e) => await CheckAsync(full: true);

    async Task CheckAsync(bool full)
    {
        if (!_svc.Api.IsLoggedIn)
        {
            await LoginAsync();
            return;
        }
        await _svc.RunCheckAsync(full);
    }

    void UpdateBusy()
    {
        bool busy = _svc.IsChecking;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CheckButton.IsEnabled = FullCheckButton.IsEnabled = CheckButton2.IsEnabled = FullCheckButton2.IsEnabled = !busy;
        UpdateNextCheck();
    }

    void UpdateNextCheck()
    {
        if (!_svc.Api.IsLoggedIn) NextCheckText.Text = "Wächter: nicht angemeldet";
        else if (!_svc.Settings.WatcherEnabled) NextCheckText.Text = "Wächter: aus";
        else if (_svc.IsChecking) NextCheckText.Text = "Wächter: prüft gerade";
        else NextCheckText.Text = $"Nächste Prüfung: {Fmt.Time(_svc.NextCheck)}";
    }

    public bool EnsureLibraryRoot()
    {
        if (!string.IsNullOrWhiteSpace(_svc.Settings.LibraryRoot)) return true;
        Dialog.Info(this, "Bitte zuerst einen Ordner für die Bibliothek wählen.\n\nAm besten eine Festplatte mit viel freiem Platz – komplette Bibliotheken können mehrere TB groß sein.",
            "Speicherort wählen");
        var dialog = new OpenFolderDialog { Title = "Ordner für die GOG-Bibliothek wählen" };
        if (dialog.ShowDialog(this) != true) return false;
        var settings = _svc.Settings.Clone();
        settings.LibraryRoot = dialog.FolderName;
        _svc.UpdateSettings(settings);
        LoadSettingsForm();
        return true;
    }

    void DownloadAll_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLibraryRoot()) return;
        var plan = _svc.PlanMissing(null, explicitRequest: false);
        if (plan.Count == 0)
        {
            Dialog.Info(this, "Alles ist auf dem aktuellen Stand – es fehlen keine Dateien.", "Alles Fehlende laden");
            return;
        }
        var bytes = plan.Sum(r => r.File.Size);
        var games = plan.Select(r => r.GameId).Distinct().Count();
        var free = FreeSpace(_svc.Settings.LibraryRoot);
        var message = $"{plan.Count} Dateien aus {games} Spielen ({Fmt.Bytes(bytes)}) werden heruntergeladen.\n" +
                      $"Freier Speicher in „{_svc.Settings.LibraryRoot}“: {(free >= 0 ? Fmt.Bytes(free) : "unbekannt")}.";
        bool tooSmall = free >= 0 && free < bytes;
        if (tooSmall) message += "\n\nAchtung: Der freie Speicher reicht nicht für alles.";
        if (!Dialog.Confirm(this, message, "Alles Fehlende laden", "Starten", "Abbrechen", tooSmall ? DialogKind.Warning : DialogKind.Question))
            return;
        _svc.Downloads.Enqueue(plan);
        SelectTab("downloads");
    }

    static long FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) || root.StartsWith(@"\\") ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }

    void StartDownloads(IReadOnlyCollection<long> gameIds)
    {
        if (gameIds.Count == 0)
        {
            StatusText.Text = "Bitte zuerst ein oder mehrere Spiele markieren.";
            return;
        }
        if (!EnsureLibraryRoot()) return;
        var (count, bytes) = _svc.EnqueueMissing(gameIds, explicitRequest: true);
        StatusText.Text = count == 0
            ? "Für die Auswahl fehlt nichts (oder es wird bereits geladen)."
            : $"{count} Dateien ({Fmt.Bytes(bytes)}) in die Warteschlange gestellt.";
    }

    void DownloadSelectedGames_Click(object sender, RoutedEventArgs e) =>
        StartDownloads(GamesGrid.SelectedItems.OfType<GameRow>().Select(r => r.Id).ToList());

    void DetailDownload_Click(object sender, RoutedEventArgs e)
    {
        if (GamesGrid.SelectedItem is GameRow row) StartDownloads([row.Id]);
    }

    void ChangesDownload_Click(object sender, RoutedEventArgs e) =>
        StartDownloads(EventsGrid.SelectedItems.OfType<EventRow>().Where(r => r.GameId > 0).Select(r => r.GameId).Distinct().ToList());

    void MarkSelectedGamesRead_Click(object sender, RoutedEventArgs e)
    {
        var ids = GamesGrid.SelectedItems.OfType<GameRow>().Select(r => r.Id).ToHashSet();
        _svc.MarkRead(ev => ids.Contains(ev.GameId));
    }

    void ChangesMarkRead_Click(object sender, RoutedEventArgs e) => _svc.MarkRead(_ => true);

    void EventsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => JumpToGame();

    void JumpToGame_Click(object sender, RoutedEventArgs e) => JumpToGame();

    void JumpToGame()
    {
        if (EventsGrid.SelectedItem is not EventRow ev || !_rows.TryGetValue(ev.GameId, out var row)) return;
        SearchBox.Text = "";
        FilterBox.SelectedIndex = 0;
        SelectTab("library");
        GamesGrid.SelectedItem = row;
        GamesGrid.ScrollIntoView(row);
    }

    void DetailOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (GamesGrid.SelectedItem is not GameRow row || string.IsNullOrWhiteSpace(_svc.Settings.LibraryRoot)) return;
        GameSnapshot? game;
        lock (_svc.Sync) game = _svc.State.Games.GetValueOrDefault(row.Id);
        if (game == null) return;
        var folder = Path.Combine(_svc.Settings.LibraryRoot, PathUtil.SafeName(game.Title, game.Slug.Length > 0 ? game.Slug : game.Id.ToString()));
        OpenPath(Directory.Exists(folder) ? folder : _svc.Settings.LibraryRoot);
    }

    async void DetailOpenGog_Click(object sender, RoutedEventArgs e)
    {
        if (GamesGrid.SelectedItem is not GameRow row)
        {
            StatusText.Text = "Bitte zuerst ein Spiel auswählen.";
            return;
        }
        string slug;
        lock (_svc.Sync) slug = _svc.State.Games.GetValueOrDefault(row.Id)?.Slug ?? "";
        var language = GogApi.StoreLanguage(System.Globalization.CultureInfo.CurrentUICulture);
        StatusText.Text = $"Suche die Shopseite von „{row.Title}“ …";

        string? page;
        try
        {
            page = await _svc.Api.ResolveStorePageAsync(slug, language, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Ohne Verbindung lässt sich nichts prüfen – dann die Seite direkt versuchen.
            Log.Warn($"Shopseite für {row.Title} nicht prüfbar: {ex.Message}");
            page = slug.Length > 0 ? $"https://www.gog.com/{language}/game/{slug}" : null;
        }

        if (page != null)
        {
            OpenPath(page);
            StatusText.Text = $"Shopseite von „{row.Title}“ geöffnet.";
        }
        else
        {
            // Keine Shopseite für dieses Land (z. B. in Deutschland nicht gelistete Spiele, Goodies-Pakete, aus dem Shop
            // genommene Titel): GOG würde nur auf die Spieleübersicht umleiten. GOG Database zeigt jedes Produkt an.
            OpenPath($"https://www.gogdb.org/product/{row.Id}");
            StatusText.Text = $"„{row.Title}“ hat im GOG-Shop deines Landes keine Seite – stattdessen die Produktseite bei GOG Database (gogdb.org, inoffiziell) geöffnet.";
        }
    }

    void OpenLibraryRoot_Click(object sender, RoutedEventArgs e)
    {
        if (EnsureLibraryRoot()) OpenPath(_svc.Settings.LibraryRoot);
    }

    static void OpenPath(string target)
    {
        try
        {
            if (!target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Directory.CreateDirectory(target);
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Konnte {target} nicht öffnen", ex);
        }
    }

    // ================= Downloads =================

    void Pause_Click(object sender, RoutedEventArgs e) => _svc.Downloads.SetPaused(!_svc.Downloads.Paused);

    void CancelJobs_Click(object sender, RoutedEventArgs e) => _svc.Downloads.Cancel(JobsGrid.SelectedItems.OfType<DownloadJob>().ToList());

    void RetryJobs_Click(object sender, RoutedEventArgs e)
    {
        var selected = JobsGrid.SelectedItems.OfType<DownloadJob>().ToList();
        List<DownloadJob> jobs;
        lock (_svc.Downloads.JobsLock) jobs = selected.Count > 0 ? selected : [.. _svc.Downloads.Jobs];
        _svc.Downloads.Retry(jobs);
    }

    void ClearJobs_Click(object sender, RoutedEventArgs e) => _svc.Downloads.ClearFinished();

    void UpdateDownloadSummary()
    {
        var (active, remaining, speed) = _svc.Downloads.Totals();
        bool paused = _svc.Downloads.Paused;
        PauseButton.Content = paused ? "Fortsetzen" : "Pausieren";
        PauseButton.Tag = paused ? "\uE768" : "\uE769";
        DownloadsHeader.Text = active > 0 ? $"Downloads ({active})" : "Downloads";
        var staging = _svc.Downloads.StagingInfo();
        string text;
        if (active == 0)
            text = string.IsNullOrWhiteSpace(_svc.Settings.LibraryRoot) ? "Kein Bibliotheksordner festgelegt." : "Keine aktiven Downloads.";
        else if (paused)
            text = $"Pausiert · {active} Dateien · noch {Fmt.Bytes(remaining)}";
        else
        {
            var eta = speed > 0 ? TimeSpan.FromSeconds(remaining / speed) : (TimeSpan?)null;
            text = $"{active} Dateien · noch {Fmt.Bytes(remaining)} · {(speed > 0 ? Fmt.Speed(speed) : remaining > 0 ? "startet …" : "überträgt …")}" +
                   (eta is { } t && remaining > 0 ? $" · ca. {(t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(1, t.Minutes)} min")}" : "");
        }
        if (_svc.Settings.StagingEnabled || staging.Pending > 0)
        {
            text += $"\nZwischenspeicher: {Fmt.Bytes(staging.UsedBytes)} von {Fmt.Bytes(staging.LimitBytes)}" +
                    (staging.Pending > 0 ? $" · {staging.Pending} Datei(en) warten aufs Übertragen" : "");
            if (staging.MoverStatus.Length > 0) text += $"\n{staging.MoverStatus}";
        }
        DownloadSummary.Text = text;
        if (DateTime.Now.Second % 15 == 0) UpdateNextCheck();
    }

    // ================= Bibliothek anzeigen =================

    void RequestRefresh()
    {
        if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
    }

    sealed record GameData(long Id, string Title, RowState State, string StatusText, long Wanted, long Missing, int Unread,
        DateTimeOffset? LastChange, GameRule Rule, bool Hidden);

    sealed record ViewData(List<GameData> Games, List<EventRow> Events, string Summary, int Unread);

    async Task RefreshAsync()
    {
        if (_refreshRunning)
        {
            _refreshAgain = true;
            return;
        }
        _refreshRunning = true;
        try
        {
            var downloading = _svc.Downloads.ActiveGameIds();
            var paused = _svc.Downloads.Paused;
            var data = await Task.Run(() => BuildView(downloading, paused));
            ApplyGames(data.Games);
            ApplyEvents(data.Events);
            SummaryText.Text = data.Summary;
            ChangesHeader.Text = data.Unread > 0 ? $"Änderungen ({data.Unread})" : "Änderungen";
            UpdateFilterCount();
            ShowDetails();
            UpdateAccount();
            UpdateNextCheck();
        }
        catch (Exception ex)
        {
            Log.Error("Ansicht konnte nicht aktualisiert werden", ex);
        }
        finally
        {
            _refreshRunning = false;
            if (_refreshAgain)
            {
                _refreshAgain = false;
                RequestRefresh();
            }
        }
    }

    ViewData BuildView(HashSet<long> downloading, bool paused)
    {
        lock (_svc.Sync)
        {
            var settings = _svc.Settings;
            var unread = _svc.State.Events.Where(e => !e.Read && e.Kind != ChangeKind.Info)
                .GroupBy(e => e.GameId).ToDictionary(g => g.Key, g => g.Count());
            var lastChange = _svc.State.Events.Where(e => e.Kind != ChangeKind.Info)
                .GroupBy(e => e.GameId).ToDictionary(g => g.Key, g => g.Max(e => e.Time));

            var games = new List<GameData>();
            int complete = 0, incomplete = 0;
            long missingTotal = 0;
            foreach (var game in _svc.State.Games.Values)
            {
                var rule = settings.RuleFor(game.Id);
                var eval = Planner.Evaluate(game, settings, _svc.State);
                var (state, text) = Describe(game, eval, downloading.Contains(game.Id), paused, rule);
                if (!game.Removed && rule != GameRule.Ignore)
                {
                    if (state == RowState.Complete) complete++;
                    if (eval.MissingCount > 0)
                    {
                        incomplete++;
                        missingTotal += eval.MissingBytes;
                    }
                }
                games.Add(new GameData(game.Id, game.Title, state, text, eval.WantedBytes, eval.MissingBytes,
                    unread.GetValueOrDefault(game.Id), lastChange.TryGetValue(game.Id, out var t) ? t : null, rule, game.IsHidden));
            }

            var events = _svc.State.Events.Take(1000).Select(e => new EventRow
            {
                Id = e.Id, GameId = e.GameId, Time = e.Time, GameTitle = e.GameTitle, Kind = e.Kind, Text = e.Text, Unread = !e.Read,
            }).ToList();

            int totalGames = _svc.State.Games.Values.Count(g => !g.Removed);
            string summary = totalGames == 0
                ? ""
                : $"{totalGames} Spiele · {complete} vollständig · {incomplete} offen ({Fmt.Bytes(missingTotal)}) · geprüft {Fmt.Time(_svc.State.LastQuickCheck)}";
            return new ViewData(games, events, summary, unread.Values.Sum());
        }
    }

    static (RowState, string) Describe(GameSnapshot game, GameEval eval, bool downloading, bool paused, GameRule rule)
    {
        if (game.Removed) return (RowState.Removed, "Nicht mehr in der Bibliothek");
        if (downloading) return (RowState.Downloading, paused ? "In Warteschlange (pausiert)" : "Wird geladen …");
        if (rule == GameRule.Ignore) return (RowState.Ignored, "Ignoriert");
        if (game.LastDetails == null) return (RowState.Unknown, game.DetailsError != null ? "Details nicht abrufbar" : "Noch nicht geprüft");
        if (eval.WantedCount == 0) return (RowState.Nothing, "Nichts passend (System/Sprache)");
        if (eval.MissingCount == 0) return (RowState.Complete, "Vollständig");
        if (eval.OutdatedCount > 0) return (RowState.Outdated, $"Update verfügbar ({eval.OutdatedCount})");
        if (eval.MissingCount < eval.WantedCount) return (RowState.Partial, $"Unvollständig ({eval.WantedCount - eval.MissingCount}/{eval.WantedCount})");
        return (RowState.Missing, "Nicht geladen");
    }

    void ApplyGames(List<GameData> games)
    {
        var seen = new HashSet<long>();
        foreach (var g in games)
        {
            seen.Add(g.Id);
            if (!_rows.TryGetValue(g.Id, out var row))
            {
                row = new GameRow { Id = g.Id };
                _rows[g.Id] = row;
                _games.Add(row);
            }
            row.Title = g.Title;
            row.State = g.State;
            row.StatusText = g.StatusText;
            row.WantedBytes = g.Wanted;
            row.MissingBytes = g.Missing;
            row.Unread = g.Unread;
            row.LastChange = g.LastChange;
            row.IsHidden = g.Hidden;
            row.LoadRule(g.Rule);
        }
        foreach (var id in _rows.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _games.Remove(_rows[id]);
            _rows.Remove(id);
        }
        var filter = (LibraryFilter)(FilterBox.SelectedValue ?? LibraryFilter.All);
        if (filter != LibraryFilter.All || SearchBox.Text.Length > 0) _gamesView.Refresh();
    }

    void ApplyEvents(List<EventRow> events)
    {
        if (_events.Count == events.Count && _events.Select(e => (e.Id, e.Unread)).SequenceEqual(events.Select(e => (e.Id, e.Unread)))) return;
        _events.Clear();
        foreach (var e in events) _events.Add(e);
    }

    bool FilterGame(object o)
    {
        if (o is not GameRow row) return false;
        var search = SearchBox.Text.Trim();
        if (search.Length > 0 && !row.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase)) return false;
        return (LibraryFilter)(FilterBox.SelectedValue ?? LibraryFilter.All) switch
        {
            LibraryFilter.New => row.HasNew,
            LibraryFilter.Missing => row.State is RowState.Missing or RowState.Partial or RowState.Outdated or RowState.Downloading,
            LibraryFilter.Outdated => row.State == RowState.Outdated,
            LibraryFilter.Complete => row.State == RowState.Complete,
            LibraryFilter.Ignored => row.State == RowState.Ignored,
            LibraryFilter.Hidden => row.IsHidden,
            LibraryFilter.Problems => row.State is RowState.Unknown or RowState.Removed,
            _ => true,
        };
    }

    void Filter_Changed(object sender, EventArgs e)
    {
        if (!_ready) return;
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _gamesView.Refresh();
        UpdateFilterCount();
    }

    void UpdateFilterCount()
    {
        int shown = _gamesView.Cast<object>().Count();
        FilterCountText.Text = shown == _games.Count ? "Suchen & Filtern" : $"Suchen & Filtern · {shown} von {_games.Count}";
    }

    void GamesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowDetails();
        if (GamesGrid.SelectedItem is GameRow { HasNew: true } row)
        {
            var id = row.Id;
            _svc.MarkRead(ev => ev.GameId == id);
        }
    }

    void ShowDetails()
    {
        if (GamesGrid.SelectedItem is not GameRow row)
        {
            DetailTitle.Text = "Kein Spiel ausgewählt";
            DetailInfo.Text = "Ein Spiel anklicken, um seine Dateien und deren Stand zu sehen.";
            DetailDownloadButton.IsEnabled = false;
            _items.Clear();
            return;
        }

        var activeKeys = _svc.Downloads.ActiveKeys();
        var rows = new List<ItemRow>();
        string info;
        lock (_svc.Sync)
        {
            if (!_svc.State.Games.TryGetValue(row.Id, out var game))
            {
                _items.Clear();
                return;
            }
            var settings = _svc.Settings;
            var wanted = Planner.SelectWanted(game, settings).Select(i => i.Key).ToHashSet();
            foreach (var item in game.Items)
            {
                bool isWanted = wanted.Contains(item.Key);
                var itemState = Planner.StateOf(item, _svc.State, settings.LibraryRoot);
                bool queued = item.Files.Any(f => activeKeys.Contains(Planner.LocalKey(item, f)));
                var (state, text) = queued
                    ? (RowState.Downloading, "In Arbeit")
                    : itemState switch
                    {
                        ItemState.Done => (RowState.Complete, "✓ Geladen"),
                        ItemState.Outdated => (RowState.Outdated, "Veraltet"),
                        ItemState.Partial => (RowState.Partial, "Teilweise"),
                        _ => isWanted ? (RowState.Missing, "Fehlt") : (RowState.Nothing, "–"),
                    };
                rows.Add(new ItemRow
                {
                    WantedMark = isWanted ? "✓" : "",
                    KindText = Fmt.Kind(item.Kind) + (item.Files.Count > 1 ? $" ({item.Files.Count} Teile)" : ""),
                    Name = item.Name,
                    Product = item.IsDlc ? item.ProductTitle : "",
                    OsText = item.Kind == ItemKind.Extra ? "" : Fmt.Os(item.Os),
                    LanguageText = item.Kind == ItemKind.Extra ? "" : Fmt.Language(item),
                    Version = item.Version ?? "",
                    SizeText = Fmt.Bytes(item.TotalSize),
                    Size = item.TotalSize,
                    StateText = text,
                    State = state,
                    SortOrder = (isWanted ? 0 : 100) + (item.IsDlc ? 10 : 0) + (int)item.Kind,
                });
            }
            info = $"{game.Items.Count} Einträge bei GOG, {wanted.Count} davon passen zu deiner Auswahl" +
                   $" · zuletzt geprüft {Fmt.Time(game.LastDetails)}" +
                   (game.DetailsError != null ? $" · Fehler: {game.DetailsError}" : "") +
                   (game.IsHidden ? " · bei GOG ausgeblendet" : "");
        }

        DetailTitle.Text = row.Title;
        DetailInfo.Text = info;
        DetailDownloadButton.IsEnabled = row.MissingBytes > 0 || row.State is RowState.Missing or RowState.Partial or RowState.Outdated;
        _items.Clear();
        foreach (var r in rows.OrderBy(r => r.SortOrder).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)) _items.Add(r);
    }

    // ================= Einstellungen =================

    void LoadSettingsForm()
    {
        string available;
        lock (_svc.Sync)
        {
            var languages = _svc.State.Games.Values.SelectMany(g => g.Items)
                .Where(i => i.Kind == ItemKind.Installer && !string.IsNullOrEmpty(i.Language))
                .GroupBy(i => i.Language!.ToLowerInvariant())
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key} ({g.First().LanguageFull ?? g.Key})")
                .Take(20).ToList();
            available = languages.Count > 0 ? "In deiner Bibliothek vorhanden: " + string.Join(", ", languages) : "";
        }
        _form = SettingsForm.From(_svc.Settings, available);
        SettingsPanel.DataContext = _form;
    }

    void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Ordner für die GOG-Bibliothek wählen" };
        if (Directory.Exists(_form.LibraryRoot)) dialog.InitialDirectory = _form.LibraryRoot;
        if (dialog.ShowDialog(this) == true) _form.LibraryRoot = dialog.FolderName;
    }

    void BrowseStaging_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Ordner für den Zwischenspeicher wählen (am besten auf der internen SSD)" };
        if (Directory.Exists(_form.StagingDir)) dialog.InitialDirectory = _form.StagingDir;
        if (dialog.ShowDialog(this) == true) _form.StagingDir = dialog.FolderName;
    }

    void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var settings = _form.ToSettings(_svc.Settings, out var error);
        if (settings == null)
        {
            Dialog.Warning(this, error ?? "Ungültige Eingabe.", "Einstellungen");
            return;
        }
        if (settings.LibraryRoot.Length > 0 && !Path.IsPathFullyQualified(settings.LibraryRoot))
        {
            Dialog.Warning(this, "Bitte einen vollständigen Pfad angeben, z. B. D:\\Spiele.", "Einstellungen");
            return;
        }
        if (settings.StagingEnabled && settings.LibraryRoot.Length > 0 &&
            string.Equals(Path.GetPathRoot(Path.GetFullPath(settings.EffectiveStagingDir)), Path.GetPathRoot(Path.GetFullPath(settings.LibraryRoot)),
                StringComparison.OrdinalIgnoreCase) &&
            !Dialog.Confirm(this, "Zwischenspeicher und Bibliothek liegen auf demselben Laufwerk. Dann bringt der Zwischenspeicher keinen Vorteil – " +
                                  "jede Datei würde nur zusätzlich kopiert.\n\nTrotzdem so speichern?", "Zwischenspeicher", "Speichern", "Zurück", DialogKind.Warning))
            return;
        _svc.UpdateSettings(settings);
        LoadSettingsForm();
        StatusText.Text = "Einstellungen gespeichert.";
        UpdateNextCheck();
    }

    void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        LoadSettingsForm();
        StatusText.Text = "Änderungen verworfen.";
    }

    void OpenDataDir_Click(object sender, RoutedEventArgs e) => OpenPath(AppPaths.RoamingDir);

    void OpenLogDir_Click(object sender, RoutedEventArgs e) => OpenPath(AppPaths.LogDir);
}
