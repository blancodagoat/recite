using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Recite;

/// <summary>
/// Full virtual-desktop selection overlay. It paints an already-captured desktop bitmap
/// rather than re-grabbing on mouse-up, which keeps the overlay itself out of the result
/// and removes the flash between commit and capture.
/// </summary>
internal sealed class RegionOverlay : Form
{
    private const int MinimumDrag = 5;
    private const int ReadoutGap = 18;

    private readonly Bitmap desktop;
    private readonly Rectangle virtualBounds;

    private readonly SolidBrush dimBrush = new(Color.FromArgb(102, 0, 0, 0)); // ~40% black
    private readonly SolidBrush readoutBackBrush = new(Color.FromArgb(235, Theme.Background));
    private readonly Pen accentPen = new(Theme.Accent, 1f);
    private readonly Font readoutFont = Theme.Ui(9f);

    // Window and child-control rects frozen at the same instant as the desktop bitmap,
    // topmost first, client coordinates. Hovering without dragging highlights the hit.
    private readonly List<Native.WindowRects> windowRects;

    private bool dragging;
    private Point dragOrigin;
    private Point cursor;
    private Rectangle hover;       // window under the cursor, client coordinates
    private Rectangle selection;   // client coordinates, identical to bitmap coordinates
    private Rectangle lastDirty;

    /// <summary>Committed selection, in screen coordinates.</summary>
    public Rectangle SelectedRegion { get; private set; }

    public RegionOverlay(Bitmap desktop)
    {
        this.desktop = desktop;
        virtualBounds = ScreenCapture.VirtualBounds;

        // Snapshotted before the overlay window exists, so the list matches the frozen
        // desktop bitmap and never contains the overlay itself.
        var clip = new Rectangle(Point.Empty, virtualBounds.Size);
        windowRects = Native.VisibleWindowRects()
            .Select(w => new Native.WindowRects(
                Rectangle.Intersect(ToClient(w.Bounds), clip),
                w.Children
                    .Select(c => Rectangle.Intersect(ToClient(c), clip))
                    .Where(c => c.Width > 0 && c.Height > 0)
                    .ToList()))
            .Where(w => w.Bounds.Width > 0 && w.Bounds.Height > 0)
            .ToList();

        // AutoScaleMode.None keeps client coordinates identical to physical pixels, so a
        // drag on a 150% display lands exactly where the user put it.
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        BackColor = Color.Black;
        Bounds = virtualBounds;
        Text = AppInfo.Name;

        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer,
            true);
    }

    /// <summary>Crops the committed region out of the desktop bitmap already in hand.</summary>
    public Bitmap CreateResult()
    {
        var region = Rectangle.Intersect(
            new Rectangle(Point.Empty, desktop.Size), ToClient(SelectedRegion));

        if (region.Width <= 0 || region.Height <= 0)
        {
            region = new Rectangle(0, 0, 1, 1);
        }

        return desktop.Clone(region, PixelFormat.Format32bppArgb);
    }

    private Rectangle ToClient(Rectangle screenRect) =>
        new(screenRect.X - virtualBounds.X, screenRect.Y - virtualBounds.Y, screenRect.Width, screenRect.Height);

    private Rectangle ToScreen(Rectangle clientRect) =>
        new(clientRect.X + virtualBounds.X, clientRect.Y + virtualBounds.Y, clientRect.Width, clientRect.Height);

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ReassertBounds();
        Activate();
        Focus();
        UpdateHover(PointToClient(Cursor.Position));
    }

    /// <summary>
    /// The overlay spans displays of differing DPI, so Windows will offer it a rescale as
    /// soon as it straddles a boundary. Accepting that would resize the window away from
    /// the virtual bounds; the geometry here is fixed and physical, so the message is
    /// swallowed and the bounds re-asserted.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DPICHANGED)
        {
            m.Result = IntPtr.Zero;
            ReassertBounds();
            return;
        }

        base.WndProc(ref m);
    }

    private void ReassertBounds()
    {
        if (Handle != IntPtr.Zero && Bounds != virtualBounds)
        {
            Native.SetWindowPos(
                Handle, IntPtr.Zero,
                virtualBounds.X, virtualBounds.Y, virtualBounds.Width, virtualBounds.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            Cancel();
            return;
        }

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        dragging = true;
        dragOrigin = e.Location;
        cursor = e.Location;

        // The hover highlight stays up until the drag passes the click threshold, so a
        // press-and-release on a window never flashes down to a point selection.
        RefreshDirty();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        cursor = e.Location;
        if (!dragging)
        {
            UpdateHover(e.Location);
            return;
        }

        var drag = Normalise(dragOrigin, e.Location);
        selection = IsClick(drag) ? hover : drag;
        RefreshDirty();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !dragging)
        {
            return;
        }

        dragging = false;
        var drag = Normalise(dragOrigin, e.Location);

        if (IsClick(drag))
        {
            // A click captures the highlighted window; over bare desktop it's a cancel.
            if (hover.IsEmpty)
            {
                Cancel();
                return;
            }

            selection = hover;
        }
        else
        {
            selection = drag;
        }

        SelectedRegion = ToScreen(selection);
        DialogResult = DialogResult.OK;
        Close();
    }

    private static bool IsClick(Rectangle drag) =>
        drag.Width < MinimumDrag || drag.Height < MinimumDrag;

    /// <summary>Highlights the topmost frozen window or child control under the cursor, if any.</summary>
    private void UpdateHover(Point p)
    {
        var hit = Native.HitTest(windowRects, p);
        if (hit != hover || hit != selection)
        {
            hover = hit;
            selection = hit;
            RefreshDirty();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Escape)
        {
            Cancel();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void Cancel()
    {
        dragging = false;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    private static Rectangle Normalise(Point a, Point b) => Rectangle.FromLTRB(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    /// <summary>
    /// Repaints only what moved. Invalidating the whole virtual desktop on every mouse
    /// move is visibly slow once several 4K displays are involved.
    /// </summary>
    private void RefreshDirty()
    {
        var dirty = Rectangle.Union(
            Rectangle.Inflate(selection, 2, 2),
            dragging ? ReadoutBounds() : Rectangle.Empty);

        var invalid = lastDirty.IsEmpty ? dirty : Rectangle.Union(lastDirty, dirty);
        lastDirty = dirty;

        if (!invalid.IsEmpty)
        {
            Invalidate(Rectangle.Inflate(invalid, 1, 1));
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Every pixel is painted in OnPaint.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        var clip = Rectangle.Intersect(e.ClipRectangle, ClientRectangle);
        if (clip.Width <= 0 || clip.Height <= 0)
        {
            return;
        }

        // The undimmed desktop, 1:1. The explicit src/dest overload avoids the DPI unit
        // conversion DrawImage would otherwise apply.
        g.DrawImage(desktop, clip, clip, GraphicsUnit.Pixel);

        // Dim everything except the live selection.
        var visible = Rectangle.Intersect(selection, clip);
        g.SetClip(clip);
        if (visible.Width > 0 && visible.Height > 0)
        {
            g.ExcludeClip(visible);
        }

        g.FillRectangle(dimBrush, clip);
        g.ResetClip();

        if (selection.Width > 0 && selection.Height > 0)
        {
            // Drawn just outside the selection so the preview stays pixel-exact.
            g.DrawRectangle(accentPen, selection.X - 1, selection.Y - 1, selection.Width + 1, selection.Height + 1);
        }

        if (dragging)
        {
            DrawReadout(g);
        }
    }

    private string ReadoutText() => $"{selection.Width} × {selection.Height}";

    private Rectangle ReadoutBounds()
    {
        // TextRenderer measures without a Graphics, which matters because this runs on
        // every mouse move; CreateGraphics per motion event is not free.
        var size = TextRenderer.MeasureText(ReadoutText(), readoutFont);

        var box = new Rectangle(
            cursor.X + ReadoutGap, cursor.Y + ReadoutGap, size.Width + 16, size.Height + 8);

        // Keep the readout on-screen near the right and bottom edges.
        if (box.Right > ClientRectangle.Right - 4)
        {
            box.X = cursor.X - ReadoutGap - box.Width;
        }

        if (box.Bottom > ClientRectangle.Bottom - 4)
        {
            box.Y = cursor.Y - ReadoutGap - box.Height;
        }

        box.X = Math.Max(ClientRectangle.Left + 4, box.X);
        box.Y = Math.Max(ClientRectangle.Top + 4, box.Y);
        return box;
    }

    private void DrawReadout(Graphics g)
    {
        var box = ReadoutBounds();
        g.FillRectangle(readoutBackBrush, box);
        g.DrawRectangle(accentPen, box.X, box.Y, box.Width - 1, box.Height - 1);

        // Drawn with TextRenderer to match the metrics ReadoutBounds measured with.
        TextRenderer.DrawText(
            g, ReadoutText(), readoutFont, box, Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            dimBrush.Dispose();
            readoutBackBrush.Dispose();
            accentPen.Dispose();
            readoutFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
