using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace LibraryWarden.UI;

/// <summary>Symbol im Infobereich mit Kontextmenü.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Forms.ToolStripMenuItem _pauseItem;

    public TrayIcon(Action open, Action check, Action togglePause, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("LibraryWarden öffnen", null, (_, _) => open());
        openItem.Font = new Drawing.Font(openItem.Font, Drawing.FontStyle.Bold);
        menu.Items.Add(openItem);
        menu.Items.Add(new Forms.ToolStripMenuItem("Jetzt auf neue Inhalte prüfen", null, (_, _) => check()));
        _pauseItem = new Forms.ToolStripMenuItem("Downloads pausieren", null, (_, _) => togglePause());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Beenden", null, (_, _) => exit()));

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "LibraryWarden",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
        _icon.BalloonTipClicked += (_, _) => open();
    }

    static Drawing.Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (resource != null)
                using (resource.Stream)
                    return new Drawing.Icon(resource.Stream, Forms.SystemInformation.SmallIconSize);
        }
        catch { }
        return Drawing.SystemIcons.Application;
    }

    /// <summary>Kontextmenü passend zum hellen/dunklen Design einfärben.</summary>
    public void ApplyTheme(bool dark)
    {
        var menu = _icon.ContextMenuStrip!;
        menu.Renderer = dark ? new Forms.ToolStripProfessionalRenderer(new DarkMenuColors()) : new Forms.ToolStripProfessionalRenderer();
        menu.BackColor = dark ? DarkMenuColors.Background : Drawing.SystemColors.Menu;
        foreach (Forms.ToolStripItem item in menu.Items)
            item.ForeColor = dark ? Drawing.Color.FromArgb(0xE8, 0xEE, 0xED) : Drawing.SystemColors.MenuText;
    }

    sealed class DarkMenuColors : Forms.ProfessionalColorTable
    {
        public static readonly Drawing.Color Background = Drawing.Color.FromArgb(0x22, 0x28, 0x27);
        static readonly Drawing.Color Selected = Drawing.Color.FromArgb(0x1F, 0x46, 0x42);
        static readonly Drawing.Color Line = Drawing.Color.FromArgb(0x36, 0x40, 0x3E);

        public override Drawing.Color ToolStripDropDownBackground => Background;
        public override Drawing.Color ImageMarginGradientBegin => Background;
        public override Drawing.Color ImageMarginGradientMiddle => Background;
        public override Drawing.Color ImageMarginGradientEnd => Background;
        public override Drawing.Color MenuBorder => Line;
        public override Drawing.Color MenuItemBorder => Selected;
        public override Drawing.Color MenuItemSelected => Selected;
        public override Drawing.Color MenuItemSelectedGradientBegin => Selected;
        public override Drawing.Color MenuItemSelectedGradientEnd => Selected;
        public override Drawing.Color SeparatorDark => Line;
        public override Drawing.Color SeparatorLight => Background;
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..62] + "…" : text;

    public void SetPaused(bool paused) => _pauseItem.Text = paused ? "Downloads fortsetzen" : "Downloads pausieren";

    public void ShowBalloon(string title, string text) => _icon.ShowBalloonTip(6000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
