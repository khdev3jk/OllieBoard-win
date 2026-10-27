using System.Drawing.Imaging;
using static OllieBoard.Native;

namespace OllieBoard;

/// A 32-bit premultiplied bitmap backed by a GDI DIB section, so it can be drawn with GDI+ and handed to
/// UpdateLayeredWindow without copying.
sealed class Surface : IDisposable
{
    public readonly int Width, Height;
    public readonly Bitmap Bitmap;
    public readonly IntPtr Dc;
    private readonly IntPtr dib, oldBitmap;

    public Surface(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        var header = new BITMAPINFOHEADER
        {
            biSize = System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = Width,
            biHeight = -Height,   // top-down, like a GDI+ bitmap
            biPlanes = 1,
            biBitCount = 32,
        };
        var screen = GetDC(IntPtr.Zero);
        Dc = CreateCompatibleDC(screen);
        ReleaseDC(IntPtr.Zero, screen);
        dib = CreateDIBSection(Dc, ref header, 0, out var bits, IntPtr.Zero, 0);
        oldBitmap = SelectObject(Dc, dib);
        Bitmap = new Bitmap(Width, Height, Width * 4, PixelFormat.Format32bppPArgb, bits);
    }

    public void Dispose()
    {
        Bitmap.Dispose();
        SelectObject(Dc, oldBitmap);
        DeleteObject(dib);
        DeleteDC(Dc);
    }
}

/// Borderless, never-activated window that shows a Surface with per-pixel transparency.
class LayeredWindow : Form
{
    private bool topmost = true;
    private bool clickThrough;

    public LayeredWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // WinForms parks windows hidden from the taskbar under an owner window, which would keep them above it;
            // the tool-window style already keeps them off the taskbar and out of Alt+Tab.
            cp.Parent = IntPtr.Zero;
            cp.ExStyle &= ~WS_EX_APPWINDOW;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            if (topmost) cp.ExStyle |= WS_EX_TOPMOST;
            if (clickThrough) cp.ExStyle |= WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                m.Result = new IntPtr(MA_NOACTIVATE);
                return;
            case WM_DPICHANGED:
                // The content is redrawn at its own size; don't let the form resize itself to Windows' suggestion.
                m.Result = IntPtr.Zero;
                return;
        }
        base.WndProc(ref m);
    }

    /// Shows a context menu from a window that never activates: like the notification-area icon, an invisible
    /// window of ours is brought to the front first so the menu closes when the user clicks elsewhere.
    public static void ShowMenu(ContextMenuStrip menu, Point at)
    {
        anchor ??= CreateAnchor();
        var previous = GetForegroundWindow();
        menu.Closed += (_, _) => { if (GetForegroundWindow() == anchor.Handle) SetForegroundWindow(previous); };
        SetForegroundWindow(anchor.Handle);
        menu.Show(at);
    }

    private static NativeWindow? anchor;

    private static NativeWindow CreateAnchor()
    {
        var window = new NativeWindow();
        window.CreateHandle(new CreateParams { Caption = "OllieBoard", Style = unchecked((int)0x80000000) });   // WS_POPUP, hidden
        return window;
    }

    /// Makes sure the native window exists without showing it.
    public void EnsureHandle()
    {
        if (!IsHandleCreated) CreateHandle();
    }

    /// Shows `surface` with its top-left corner at `topLeft` (screen pixels).
    public void Present(Surface surface, Point topLeft)
    {
        EnsureHandle();
        var screen = GetDC(IntPtr.Zero);
        var dst = new POINT { X = topLeft.X, Y = topLeft.Y };
        var size = new SIZE { Width = surface.Width, Height = surface.Height };
        var src = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
        UpdateLayeredWindow(Handle, screen, ref dst, ref size, surface.Dc, ref src, 0, ref blend, ULW_ALPHA);
        ReleaseDC(IntPtr.Zero, screen);
    }

    /// Moves the window without redrawing it.
    public void MoveTo(Point topLeft)
    {
        if (IsHandleCreated) SetWindowPos(Handle, IntPtr.Zero, topLeft.X, topLeft.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public void OrderFront()
    {
        EnsureHandle();
        if (!Visible) Show();
    }

    public void OrderOut()
    {
        if (Visible) Hide();
    }

    /// Mouse clicks pass through to whatever is underneath.
    public bool ClickThrough
    {
        get => clickThrough;
        set
        {
            if (clickThrough == value) return;
            clickThrough = value;
            if (IsHandleCreated) SetExStyle(Handle, WS_EX_TRANSPARENT, value);
        }
    }

    /// Above every normal window, or among them.
    public bool Topmost
    {
        get => topmost;
        set
        {
            if (topmost == value) return;
            topmost = value;
            if (IsHandleCreated)
                SetWindowPos(Handle, value ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    /// Puts this window right below `above` in the z-order.
    public void PlaceBelow(IntPtr above)
    {
        if (IsHandleCreated && above != Handle)
            SetWindowPos(Handle, above, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }
}
