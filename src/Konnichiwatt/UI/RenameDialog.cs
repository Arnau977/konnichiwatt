namespace Konnichiwatt.UI;

/// <summary>Tiny "rename device" prompt. Returns null on cancel, "" to restore the original name.</summary>
internal sealed class RenameDialog : Form
{
    private readonly TextBox _box = new() { Dock = DockStyle.Top };

    private RenameDialog(string originalName, string currentName)
    {
        Text = "Cambiar nombre";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Padding = new Padding(12);
        ClientSize = new Size(380, 130);

        var label = new Label
        {
            Text = $"Nombre para «{originalName}»:",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 28,
        };
        _box.Text = currentName;

        var ok = new Button { Text = "Aceptar", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true };
        var reset = new Button { Text = "Nombre original", AutoSize = true };
        reset.Click += (_, _) => { _box.Text = ""; DialogResult = DialogResult.OK; };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
        };
        buttons.Controls.AddRange(new Control[] { cancel, ok, reset });

        Controls.Add(buttons);
        Controls.Add(_box);
        Controls.Add(label);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public static string? Ask(string originalName, string currentName)
    {
        using var dlg = new RenameDialog(originalName, currentName);
        dlg.Shown += (_, _) => { dlg.Activate(); dlg._box.Focus(); dlg._box.SelectAll(); };
        return dlg.ShowDialog() == DialogResult.OK ? dlg._box.Text.Trim() : null;
    }
}
