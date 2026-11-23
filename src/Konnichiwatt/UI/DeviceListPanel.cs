using Konnichiwatt.UI.Fluent;

namespace Konnichiwatt.UI;

/// <summary>
/// Vertical list of <see cref="DeviceRow"/>s that can be reordered by dragging their grip, with movement you can
/// see: the dragged row follows the pointer, the others slide out of its way, and it glides into its slot on
/// release. The arrows use the same animation. Rows are positioned by hand (a FlowLayoutPanel would snap them).
/// </summary>
internal sealed class DeviceListPanel : Panel
{
    private readonly List<DeviceRow> _rows = new();
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };
    private readonly Dictionary<DeviceRow, float> _y = new();
    private DeviceRow? _dragging;
    private int _grabOffset;

    /// <summary>The user changed the order (drag released or arrow clicked): ids in the new order.</summary>
    public event Action<IReadOnlyList<string>>? OrderChanged;
    /// <summary>Asks the scrolling parent to scroll by this many pixels (dragging near its edges).</summary>
    public event Action<int>? ScrollRequested;

    public bool IsDragging => _dragging != null;
    private int RowH => _rows.Count > 0 ? _rows[0].Height : F.Px(68);
    private int Pitch => RowH + F.Px(4);

    public DeviceListPanel(int width)
    {
        DoubleBuffered = true;
        BackColor = F.Back;
        Width = width;
        Margin = new Padding(0);
        _anim.Tick += (_, _) => Step();
    }

    public void SetRows(IEnumerable<DeviceRow> rows)
    {
        foreach (var r in _rows) { Controls.Remove(r); r.Dispose(); }
        _rows.Clear();
        _y.Clear();
        foreach (var r in rows)
        {
            r.Margin = Padding.Empty;
            r.Width = Width;
            r.DragStarted += BeginDrag;
            r.DragMoved += DragTo;
            r.DragEnded += EndDrag;
            r.MoveRequested += delta => MoveRow(r, delta);
            _rows.Add(r);
            Controls.Add(r);
        }
        Height = _rows.Count == 0 ? 0 : _rows.Count * Pitch;
        for (int i = 0; i < _rows.Count; i++) { _y[_rows[i]] = i * Pitch; _rows[i].Top = i * Pitch; }
        UpdateArrows();
    }

    public IReadOnlyList<string> Ids => _rows.Select(r => r.Id).ToList();

    // ---- Dragging ---------------------------------------------------------------------

    private void BeginDrag(DeviceRow row)
    {
        _dragging = row;
        _grabOffset = PointToClient(Cursor.Position).Y - row.Top;
        row.Dragging = true;
        row.BringToFront();
    }

    private void DragTo(DeviceRow row, int screenY)
    {
        if (_dragging != row) return;
        int max = (_rows.Count - 1) * Pitch;
        int y = Math.Clamp(PointToClient(new Point(0, screenY)).Y - _grabOffset, 0, max);
        row.Top = y;
        _y[row] = y;

        // Slot under the dragged row's centre.
        int slot = Math.Clamp((int)Math.Round(y / (float)Pitch), 0, _rows.Count - 1);
        int current = _rows.IndexOf(row);
        if (slot != current)
        {
            _rows.RemoveAt(current);
            _rows.Insert(slot, row);
            UpdateArrows();
            _anim.Start();
        }

        // Scroll the page when the pointer reaches the visible edge of the list's viewport.
        if (Parent is ScrollableControl sc)
        {
            int inView = sc.PointToClient(new Point(0, screenY)).Y, edge = F.Px(28), step = F.Px(14);
            if (inView < edge) ScrollRequested?.Invoke(-step);
            else if (inView > sc.ClientSize.Height - edge) ScrollRequested?.Invoke(step);
        }
    }

    private void EndDrag(DeviceRow row)
    {
        if (_dragging != row) return;
        _dragging = null;
        row.Dragging = false;
        _anim.Start(); // glide into its slot
        OrderChanged?.Invoke(Ids);
    }

    private void MoveRow(DeviceRow row, int delta)
    {
        int i = _rows.IndexOf(row), j = i + delta;
        if (i < 0 || j < 0 || j >= _rows.Count) return;
        (_rows[i], _rows[j]) = (_rows[j], _rows[i]);
        row.BringToFront();
        UpdateArrows();
        _anim.Start();
        OrderChanged?.Invoke(Ids);
    }

    /// <summary>One animation frame: every row not under the pointer eases towards its slot.</summary>
    private void Step()
    {
        bool moving = false;
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            if (r == _dragging) continue;
            float target = i * Pitch, y = _y[r];
            float next = Math.Abs(target - y) < 1 ? target : y + (target - y) * 0.3f;
            _y[r] = next;
            int top = (int)Math.Round(next);
            if (r.Top != top) r.Top = top;
            if (next != target) moving = true;
        }
        if (!moving && _dragging == null) _anim.Stop();
    }

    private void UpdateArrows()
    {
        for (int i = 0; i < _rows.Count; i++) _rows[i].SetMoveEnabled(i > 0, i < _rows.Count - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anim.Dispose();
        base.Dispose(disposing);
    }
}
