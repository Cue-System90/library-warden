using System.Windows;
using System.Windows.Controls;

namespace LibraryWarden.UI;

public enum DialogKind { Info, Question, Warning, Error }

/// <summary>Meldungsfenster im Design der App (ersetzt die helle Windows-MessageBox, auch im Dunkelmodus).</summary>
public partial class DialogWindow : Window
{
    public string? Result { get; private set; }

    public DialogWindow(string message, string title, DialogKind kind, params (string Id, string Text, bool Primary)[] buttons)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        (IconText.Text, var brush) = kind switch
        {
            DialogKind.Question => ("", "Lw.AccentFg"),
            DialogKind.Warning => ("", "Lw.Warn"),
            DialogKind.Error => ("", "Lw.Bad"),
            _ => ("", "Lw.Info"),
        };
        IconText.SetResourceReference(TextBlock.ForegroundProperty, brush);
        RootScale.ScaleX = RootScale.ScaleY = ThemeManager.Scale;

        for (int i = 0; i < buttons.Length; i++)
        {
            var (id, text, primary) = buttons[i];
            var button = new Button
            {
                Content = text,
                MinWidth = 96,
                IsDefault = primary,
                IsCancel = i == buttons.Length - 1,
            };
            if (primary) button.Style = (Style)FindResource("PrimaryButton");
            button.Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0);
            button.Click += (_, _) =>
            {
                Result = id;
                DialogResult = true;
            };
            ButtonPanel.Children.Add(button);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowHelpers.ApplyDarkFrame(this, ThemeManager.IsDark);
    }

    public static string? Show(Window? owner, string message, string title, DialogKind kind, params (string Id, string Text, bool Primary)[] buttons)
    {
        var dialog = new DialogWindow(message, title, kind, buttons);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog.Result;
    }
}

public static class Dialog
{
    public static void Info(Window? owner, string message, string title = "LibraryWarden") =>
        DialogWindow.Show(owner, message, title, DialogKind.Info, ("ok", "OK", true));

    public static void Warning(Window? owner, string message, string title = "LibraryWarden") =>
        DialogWindow.Show(owner, message, title, DialogKind.Warning, ("ok", "OK", true));

    public static void Error(Window? owner, string message, string title = "LibraryWarden") =>
        DialogWindow.Show(owner, message, title, DialogKind.Error, ("ok", "OK", true));

    public static bool Confirm(Window? owner, string message, string title, string yes = "Ja", string no = "Nein",
        DialogKind kind = DialogKind.Question) =>
        DialogWindow.Show(owner, message, title, kind, ("yes", yes, true), ("no", no, false)) == "yes";
}
