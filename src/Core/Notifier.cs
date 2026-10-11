using Microsoft.Toolkit.Uwp.Notifications;

namespace LibraryWarden.Core;

/// <summary>Windows-Meldungen (Toast) mit Schaltflächen; bei Problemen Ersatz über die Tray-Sprechblase.</summary>
public static class Notifier
{
    static bool _enabled;

    public static Action<string, string>? Fallback { get; set; }

    public static void Init(Action<ToastArguments> onActivated)
    {
        ToastNotificationManagerCompat.OnActivated += e =>
        {
            try { onActivated(ToastArguments.Parse(e.Argument)); }
            catch (Exception ex) { Log.Error("Meldungsaktion fehlgeschlagen", ex); }
        };
        _enabled = true;
    }

    public static void ShowChanges(IReadOnlyList<ChangeEvent> events, IReadOnlyCollection<long> autoStarted)
    {
        var games = events.GroupBy(e => e.GameId).ToList();
        if (games.Count == 0) return;

        var title = games.Count == 1 ? $"Neue Inhalte: {games[0].First().GameTitle}" : $"Neue Inhalte in {games.Count} Spielen";
        var body = games.Count == 1
            ? string.Join("\n", games[0].Take(3).Select(e => e.Text))
            : string.Join("\n", games.Take(3).Select(g => $"{g.First().GameTitle}: {g.First().Text}{(g.Count() > 1 ? $" (+{g.Count() - 1})" : "")}"));

        var manual = events.Where(e => e.Kind is ChangeKind.NewGame or ChangeKind.Added or ChangeKind.Updated)
            .Select(e => e.GameId).Distinct().Where(id => !autoStarted.Contains(id)).ToList();
        var builder = new ToastContentBuilder()
            .AddArgument("action", "show")
            .AddText(title)
            .AddText(body);
        if (autoStarted.Count > 0)
            builder.AddAttributionText(manual.Count == 0 ? "Download wurde automatisch gestartet" : $"{autoStarted.Count} Download(s) automatisch gestartet");
        else if (games.Count > 3)
            builder.AddAttributionText($"… und {games.Count - 3} weitere");

        if (manual.Count > 0)
            builder.AddButton(new ToastButton()
                .SetContent("Jetzt herunterladen")
                .AddArgument("action", "download")
                .AddArgument("ids", string.Join(",", manual)));
        builder.AddButton(new ToastButton().SetContent("Anzeigen").AddArgument("action", "show"));
        Show(builder, title, body);
    }

    public static void ShowSimple(string title, string text, string action = "show")
    {
        var builder = new ToastContentBuilder().AddArgument("action", action).AddText(title).AddText(text);
        Show(builder, title, text);
    }

    static void Show(ToastContentBuilder builder, string title, string text)
    {
        if (!_enabled) return;
        try
        {
            builder.Show();
        }
        catch (Exception ex)
        {
            Log.Error("Windows-Meldung konnte nicht angezeigt werden", ex);
            Fallback?.Invoke(title, text);
        }
    }
}
