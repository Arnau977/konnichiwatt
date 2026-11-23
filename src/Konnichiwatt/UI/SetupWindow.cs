using System.Runtime.InteropServices;
using Konnichiwatt.Core;
using Konnichiwatt.UI.Fluent;

namespace Konnichiwatt.UI;

/// <summary>
/// Install / update / uninstall window, in the same style as the settings window. Shown when the downloaded .exe is
/// opened from anywhere other than its install folder, and by "Uninstall" in Settings › Apps.
/// </summary>
internal sealed class SetupWindow : Form
{
    public enum Mode { Install, Update, Uninstall }

    private readonly Mode _mode;
    private readonly ToggleSwitch _toggle;
    private readonly SettingCard _card;
    private readonly FlatButton _primary;
    private readonly FlatButton _secondary;
    private string _title;
    private string _subtitle;
    private string _body;
    private string _footnote;
    private bool _footnoteIsError;
    private bool _done;

    /// <summary>The user chose "run without installing".</summary>
    public bool RunHere { get; private set; }

    private SetupWindow(Mode mode)
    {
        _mode = mode;
        F.Init(Theme.IsLightApps(), DeviceDpi / 96f);
        Text = mode switch { Mode.Uninstall => L.UninstallTitle, Mode.Update => L.SetupUpdateTitle, _ => L.SetupTitle };
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { /* default icon */ }
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && _primary!.Enabled) Close(); };
        BackColor = F.Back;
        ClientSize = new Size(F.Px(480), F.Px(340));

        var installed = Installer.InstalledVersion?.ToString(3) ?? "";
        var current = Installer.ThisVersion.ToString(3);
        _title = "Konnichiwatt";
        _subtitle = mode == Mode.Update ? $"{installed}  →  {current}" : L.VersionN(mode == Mode.Uninstall ? installed : current);
        _body = mode switch { Mode.Uninstall => L.UninstallBody, Mode.Update => L.SetupUpdateBody(installed), _ => L.SetupBody };
        _footnote = mode == Mode.Uninstall ? "" : L.SetupFootnote;

        bool startup = mode != Mode.Update || SafeStartsInstalled();
        _toggle = new ToggleSwitch(mode != Mode.Uninstall && startup);
        _card = mode == Mode.Uninstall
            ? new SettingCard("", L.DeleteData, L.DeleteDataDesc, _toggle, F.Px(432))
            : new SettingCard("", L.StartWithWindows, L.StartWithWindowsSetupDesc, _toggle, F.Px(432));
        _card.Location = new Point(F.Px(24), F.Px(150));
        Controls.Add(_card);

        _primary = new FlatButton(mode switch { Mode.Uninstall => L.UninstallButton, Mode.Update => L.UpdateButton, _ => L.InstallButton })
        {
            Primary = true,
        };
        _primary.Width = Math.Max(_primary.Width, F.Px(120));
        _primary.Location = new Point(ClientSize.Width - F.Px(24) - _primary.Width, ClientSize.Height - F.Px(24) - _primary.Height);
        _primary.Click += async (_, _) => await PrimaryClicked();
        Controls.Add(_primary);

        _secondary = new FlatButton(mode == Mode.Install ? L.RunWithoutInstalling : L.Cancel);
        _secondary.Location = new Point(_primary.Left - F.Px(8) - _secondary.Width, _primary.Top);
        _secondary.Click += (_, _) => { RunHere = _mode == Mode.Install; Close(); };
        Controls.Add(_secondary);

        AcceptButtonLike(_primary);
    }

    private static bool SafeStartsInstalled()
    {
        try { return Installer.SamePath(Startup.RegisteredExe, Installer.InstalledExe); } catch { return true; }
    }

    private void AcceptButtonLike(FlatButton b) =>
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && b.Enabled) b.PerformClick(); };

    private async Task PrimaryClicked()
    {
        if (_done) { Close(); return; }
        _primary.Enabled = _secondary.Enabled = _toggle.Enabled = false;
        _footnoteIsError = false;
        _footnote = _mode == Mode.Uninstall ? L.Uninstalling : L.Installing;
        Invalidate();
        bool option = _toggle.Checked;
        try
        {
            if (_mode == Mode.Uninstall)
            {
                await Task.Run(() => Installer.Uninstall(deleteData: option));
                _done = true;
                _body = L.UninstalledBody;
                _footnote = "";
                _card.Visible = false;
                _secondary.Visible = false;
                _primary.SetText(L.Close, F.Px(120));
                _primary.Enabled = true;
                Invalidate();
            }
            else
            {
                await Task.Run(() => Installer.Install(startWithWindows: option));
                Installer.LaunchInstalled("--installed");
                Close();
            }
        }
        catch (Exception e)
        {
            Log.Error(_mode == Mode.Uninstall ? "Uninstall" : "Install", e);
            _footnote = L.InstallFailed(e.Message);
            _footnoteIsError = true;
            _primary.Enabled = _secondary.Enabled = _toggle.Enabled = true;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        F.Prepare(g);
        float x = F.Pf(24), w = ClientSize.Width - F.Pf(48);

        using var bolt = F.Glyphs(30);
        using var title = F.Text(20, semibold: true);
        using var sub = F.Text(12);
        using var body = F.Text(14);
        using var note = F.Text(12);
        F.DrawText(g, Theme.Bolt, bolt, F.Accent, new RectangleF(x - F.Pf(4), F.Pf(22), F.Pf(40), F.Pf(48)));
        F.DrawText(g, _title, title, F.Fg, new RectangleF(x + F.Pf(42), F.Pf(20), w, F.Pf(30)));
        F.DrawText(g, _subtitle, sub, F.Fg2, new RectangleF(x + F.Pf(42), F.Pf(48), w, F.Pf(18)));

        DrawWrapped(g, _body, body, F.Fg, new RectangleF(x, F.Pf(88), w, F.Pf(52)));
        if (_footnote.Length > 0)
        {
            var top = _card.Visible ? _card.Bottom + F.Pf(14) : F.Pf(150);
            var color = _footnoteIsError ? (F.Light ? Color.FromArgb(0xC4, 0x2B, 0x1C) : Color.FromArgb(0xFF, 0x99, 0xA4)) : F.Fg2;
            DrawWrapped(g, _footnote, note, color, new RectangleF(x, top, w, _primary.Top - top - F.Pf(8)));
        }
    }

    private static void DrawWrapped(Graphics g, string s, Font f, Color c, RectangleF box)
    {
        using var b = new SolidBrush(c);
        using var fmt = new StringFormat { Trimming = StringTrimming.EllipsisWord };
        g.DrawString(s, f, b, box, fmt);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = F.Light ? 0 : 1;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));    // immersive dark mode
        int round = 2;
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));   // rounded corners
        int caption = ColorTranslator.ToWin32(F.Back);
        DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int)); // caption colour
        int text = ColorTranslator.ToWin32(F.Fg);
        DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));    // caption text colour
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }

    /// <summary>Shows the install (or update) window. Returns true if the user wants to run this copy without installing.</summary>
    public static bool RunInstall(bool update)
    {
        using var w = new SetupWindow(update ? Mode.Update : Mode.Install);
        Application.Run(w);
        return w.RunHere;
    }

    public static void RunUninstall()
    {
        using var w = new SetupWindow(Mode.Uninstall);
        Application.Run(w);
    }

    /// <summary>Dev aid (<c>--setup-shot dir</c>): renders the three variants to PNG files.</summary>
    public static void SaveScreenshots(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var mode in Enum.GetValues<Mode>())
        {
            using var w = new SetupWindow(mode) { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000), ShowInTaskbar = false };
            w.Show();
            Application.DoEvents();
            using var bmp = new Bitmap(w.ClientSize.Width, w.ClientSize.Height);
            w.DrawToBitmap(bmp, new Rectangle(Point.Empty, w.ClientSize));
            bmp.Save(Path.Combine(dir, $"setup-{mode.ToString().ToLowerInvariant()}.png"));
            w.Close();
        }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
}
