using System.Runtime.InteropServices;
using Accessibility;
using Konnichiwatt.Core;
using static Konnichiwatt.UI.Native;

namespace Konnichiwatt.UI;

/// <summary>
/// Windows 11 secondary taskbars (Shell_SecondaryTrayWnd) have no Win32 window for their clock / tray area,
/// only XAML content. We find it through MSAA (oleacc, already available to WinForms — UI Automation would pull
/// in WPF and add ~20 MB to the single-file build): the tray is the cluster of elements stuck to the taskbar's
/// far edge (clock, "show desktop", tray icons). Runs on a background thread — never on the UI thread, which owns
/// windows parented to Explorer and could deadlock with it.
/// </summary>
internal sealed class TaskbarAnchorService : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<IntPtr, Rectangle> _anchors = new();
    private HashSet<IntPtr> _tracked = new();
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop;

    public TaskbarAnchorService()
    {
        new Thread(Loop) { IsBackground = true, Name = "TaskbarAnchors" }.Start();
    }

    /// <summary>Screen rectangle of the tray/clock area of that taskbar, if known yet.</summary>
    public Rectangle? Get(IntPtr taskbar)
    {
        lock (_gate) return _anchors.TryGetValue(taskbar, out var r) ? r : null;
    }

    public void Track(IEnumerable<IntPtr> taskbars)
    {
        var set = taskbars.ToHashSet();
        bool changed;
        lock (_gate)
        {
            changed = !set.SetEquals(_tracked);
            _tracked = set;
            foreach (var gone in _anchors.Keys.Where(k => !set.Contains(k)).ToList()) _anchors.Remove(gone);
        }
        if (changed) _wake.Set();
    }

    private void Loop()
    {
        while (!_stop)
        {
            IntPtr[] snapshot;
            lock (_gate) snapshot = _tracked.ToArray();
            foreach (var h in snapshot)
            {
                var r = Locate(h);
                lock (_gate)
                {
                    if (r is Rectangle rect) _anchors[h] = rect;
                    else _anchors.Remove(h);
                }
            }
            _wake.WaitOne(3000);
        }
    }

    private static Rectangle? Locate(IntPtr taskbar)
    {
        try
        {
            if (!GetWindowRect(taskbar, out var tbr)) return null;
            var tb = Rectangle.FromLTRB(tbr.Left, tbr.Top, tbr.Right, tbr.Bottom);
            bool vertical = tb.Height > tb.Width;

            var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71"); // IID_IAccessible
            if (AccessibleObjectFromWindow(taskbar, OBJID_CLIENT, ref iid, out var obj) != 0 || obj is not IAccessible root)
                return null;
            var rects = new List<Rectangle>();
            Collect(root, 0, tb, rects);
            return EdgeCluster(rects, tb, vertical);
        }
        catch (Exception e)
        {
            Log.Error("Taskbar anchor (MSAA)", e);
            return null;
        }
    }

    private static void Collect(IAccessible a, int depth, Rectangle tb, List<Rectangle> rects)
    {
        if (depth > 10 || rects.Count > 500) return;
        int n;
        try { n = a.accChildCount; } catch { return; }
        if (n <= 0) return;
        var kids = new object[Math.Min(n, 200)];
        if (AccessibleChildren(a, 0, kids.Length, kids, out int got) != 0) return;
        for (int i = 0; i < got; i++)
        {
            if (kids[i] is not IAccessible k) continue;
            try
            {
                k.accLocation(out int x, out int y, out int w, out int h, 0);
                var r = new Rectangle(x, y, w, h);
                if (w > 0 && h > 0 && w < tb.Width && h <= tb.Height + 2 && tb.IntersectsWith(r)) rects.Add(r);
            }
            catch { /* element vanished */ }
            Collect(k, depth + 1, tb, rects);
        }
    }

    /// <summary>Union of the elements touching the far edge (right, or bottom for vertical taskbars), grown while contiguous.</summary>
    private static Rectangle? EdgeCluster(List<Rectangle> rects, Rectangle tb, bool vertical)
    {
        const int EdgeSlack = 30, Joint = 16;
        int Far(Rectangle r) => vertical ? r.Bottom : r.Right;
        int Near(Rectangle r) => vertical ? r.Top : r.Left;
        int tbFar = vertical ? tb.Bottom : tb.Right;

        // Ignore big containers (they span most of the bar and would swallow everything).
        int maxSpan = (int)((vertical ? tb.Height : tb.Width) * 0.4);
        var sorted = rects.Where(r => (vertical ? r.Height : r.Width) <= maxSpan).OrderByDescending(Far).ToList();
        Rectangle? cluster = null;
        foreach (var r in sorted)
        {
            if (cluster == null)
            {
                if (Far(r) < tbFar - EdgeSlack) return null;
                cluster = r;
            }
            else if (Far(r) >= Near(cluster.Value) - Joint)
                cluster = Rectangle.Union(cluster.Value, r);
            else break;
        }
        return cluster;
    }

    private const uint OBJID_CLIENT = 0xFFFFFFFC;

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object? obj);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleChildren(IAccessible parent, int start, int count, [Out] object[] children, out int obtained);

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
    }
}
