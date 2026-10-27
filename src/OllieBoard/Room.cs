using System.Drawing.Drawing2D;

namespace OllieBoard;

/// What the room looks like: the default cozy room, one scene per supported series, and the UN hall for politics.
enum RoomScene { Cozy, Kimetsu, OnePiece, AttackOnTitan, JujutsuKaisen, HunterXHunter, UnitedNations }

static class RoomScenes
{
    public static string Title(this RoomScene scene) => UI.Text(scene switch
    {
        RoomScene.Cozy => UIText.SceneCozy,
        RoomScene.Kimetsu => UIText.SceneKimetsu,
        RoomScene.OnePiece => UIText.SceneOnePiece,
        RoomScene.AttackOnTitan => UIText.SceneAttackOnTitan,
        RoomScene.JujutsuKaisen => UIText.SceneJujutsuKaisen,
        RoomScene.HunterXHunter => UIText.SceneHunterXHunter,
        _ => UIText.SceneUnitedNations,
    });
}

/// A play room drawn on the desktop, under the app windows. Characters put in it stay on its floor
/// and only play with each other.
sealed class Room
{
    public const double MinWidth = 320, MinHeight = 240;
    private const string OpenKey = "roomOpen", MembersKey = "roomMembers", FrameKey = "roomFrame", SceneKey = "roomScene";

    /// Called when the room opens or gains a member, so members outside can pop in.
    public Action? OnMembersChange;
    /// Called with the shift of the room's origin after it is moved or resized, so members move along.
    public Action<double, double>? OnMove;

    public bool IsOpen { get; private set; }
    public HashSet<string> Members { get; }
    public bool Editing { get; private set; }
    public RectD Frame { get; private set; }
    public RoomScene Scene { get; private set; }

    private RoomWindow? window;
    private RoomWindow Window => window ??= new RoomWindow(this);

    public Room()
    {
        IsOpen = Settings.GetBool(OpenKey, false);
        Members = (Settings.GetStringList(MembersKey) ?? []).ToHashSet();
        Scene = Enum.TryParse<RoomScene>(Settings.GetString(SceneKey), true, out var scene) ? scene : RoomScene.Cozy;
        // Fall back to the default spot when the screen it was on is gone.
        if (RectD.Parse(Settings.GetString(FrameKey)) is { } saved && Screens.All.Any(s => s.Visible.Intersects(saved)))
        {
            Frame = saved;
        }
        else
        {
            var f = Screens.Main.Visible;
            Frame = new RectD(f.MinX + 40, f.MinY + 20, Math.Min(600, f.Width - 80), 300);
        }
    }

    public void Start() => Apply();

    // MARK: Geometry

    /// Height of the wooden floor along the bottom of the room.
    public double FloorHeight => Math.Min(Math.Max(70, Frame.Height * 0.36), Frame.Height - 150);

    /// Where members' feet may be: on the floor, with the whole body inside the walls.
    public RectD Floor => new(Frame.MinX + 36, Frame.MinY + 10, Frame.Width - 72, FloorHeight - 24);

    /// Where a member the user is holding may be dragged: anywhere inside the room.
    public PointD ClampHeld(PointD p) =>
        new(Mathx.Clamp(p.X, Floor.MinX, Floor.MaxX), Mathx.Clamp(p.Y, Floor.MinY, Frame.MaxY - 140));

    public PointD RandomSpot() =>
        new(Mathx.Random(Floor.MinX, Floor.MaxX), Mathx.Random(Floor.MinY, Floor.MaxY));

    // MARK: Changes from the menu

    public void SetOpen(bool open)
    {
        IsOpen = open;
        if (!open) Editing = false;
        Settings.SetBool(OpenKey, open);
        Apply();
        OnMembersChange?.Invoke();
    }

    public void SetMember(string id, bool inside)
    {
        if (inside) Members.Add(id); else Members.Remove(id);
        Settings.SetStringList(MembersKey, Members);
        if (inside && !IsOpen) SetOpen(true); else OnMembersChange?.Invoke();
    }

    public void SetScene(RoomScene newScene)
    {
        Scene = newScene;
        Settings.SetString(SceneKey, newScene.Key());
        if (!IsOpen) SetOpen(true); else Window.Redraw();
    }

    public void SetEditing(bool on)
    {
        if (on && !IsOpen) SetOpen(true);
        Editing = on;
        Apply();
    }

    /// Moves or resizes the room (while editing), taking its members along.
    public void SetFrame(RectD newFrame)
    {
        var dx = newFrame.MinX - Frame.MinX;
        var dy = newFrame.MinY - Frame.MinY;
        Frame = newFrame;
        Window.Redraw();
        OnMove?.Invoke(dx, dy);
    }

    public void SaveFrame() => Settings.SetString(FrameKey, Frame.ToString());

    private void Apply()
    {
        if (!IsOpen) { window?.OrderOut(); return; }
        // Under the app windows; above everything while it is being moved or resized.
        Window.Topmost = Editing;
        Window.ClickThrough = !Editing;
        Window.Redraw();
        Window.OrderFront();
        if (!Editing) Pin([]);
    }

    /// Keeps the room just above the desktop (and its icons), with `members` right above it, so app windows cover
    /// the room and its members alike.
    public void Pin(IEnumerable<IntPtr> members)
    {
        if (!IsOpen || Editing || window == null) return;
        var desktop = ShellDesktop.FindHost();
        if (desktop == IntPtr.Zero) return;
        var ours = new HashSet<IntPtr>(members) { window.Handle };
        var above = Native.GetWindow(desktop, Native.GW_HWNDPREV);
        while (above != IntPtr.Zero && ours.Contains(above)) above = Native.GetWindow(above, Native.GW_HWNDPREV);
        // Each window put right below `above` lands on top of the ones placed before it.
        window.PlaceBelow(above);
        foreach (var member in members)
            Native.SetWindowPos(member, above, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }
}

/// Paints the room's scene (see RoomSceneArt); the cozy default is a striped wall with a window and a clock,
/// wooden floor and a rug. While editing it can be dragged around, resized from the bottom-right corner and closed
/// with its done button.
sealed class RoomWindow : LayeredWindow
{
    private readonly Room room;
    private Surface? surface;

    public RoomWindow(Room room)
    {
        this.room = room;
        Text = "OllieBoard Room";
        EnsureHandle();
    }

    private double Width0 => room.Frame.Width;
    private double Height0 => room.Frame.Height;
    private RectD GripRect => new(Width0 - 30, 0, 30, 30);
    private RectD DoneRect => new(Width0 - 74, Height0 - 36, 62, 24);

    public void Redraw()
    {
        var s = Screens.Scale;
        var w = (int)Math.Round(Width0 * s);
        var h = (int)Math.Round(Height0 * s);
        if (surface == null || surface.Width != w || surface.Height != h)
        {
            surface?.Dispose();
            surface = new Surface(w, h);
        }
        using (var g = Graphics.FromImage(surface.Bitmap))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.Transform = new Matrix((float)s, 0, 0, (float)-s, 0, (float)(Height0 * s));
            Draw(g);
        }
        Present(surface, Screens.TopLeftPixels(room.Frame));
    }

    private void Draw(Graphics g)
    {
        var art = new RoomSceneArt(g, Width0, Height0);
        var floorH = room.FloorHeight;
        var wall = new RectD(0, floorH, Width0, Height0 - floorH);
        using var outline = RoomSceneArt.RoundedRect(new RectD(0, 0, Width0, Height0).Inset(1.5, 1.5), 14);

        var state = g.Save();
        g.SetClip(outline);
        switch (room.Scene)
        {
            case RoomScene.Cozy: art.DrawCozy(wall, floorH); break;
            case RoomScene.Kimetsu: art.DrawWisteriaNight(wall, floorH); break;
            case RoomScene.OnePiece: art.DrawSunnyDeck(wall, floorH); break;
            case RoomScene.AttackOnTitan: art.DrawWallMaria(wall, floorH); break;
            case RoomScene.JujutsuKaisen: art.DrawJujutsuHigh(wall, floorH); break;
            case RoomScene.HunterXHunter: art.DrawWhaleIsland(wall, floorH); break;
            case RoomScene.UnitedNations: art.DrawAssemblyHall(wall, floorH); break;
        }
        g.Restore(state);
        using (var pen = new Pen(RoomSceneArt.Color(120, 78, 52, 0.95), 3))
            g.DrawPath(pen, outline);

        if (room.Editing) DrawEditing(g);
    }

    private void DrawEditing(Graphics g)
    {
        var accent = SystemColors.Highlight;
        using (var dashed = RoomSceneArt.RoundedRect(new RectD(0, 0, Width0, Height0).Inset(4, 4), 12))
        using (var pen = new Pen(accent, 2.5f) { DashPattern = [8 / 2.5f, 5 / 2.5f] })
            g.DrawPath(pen, dashed);

        using (var grip = new Pen(accent, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            for (var i = 0; i < 3; i++)
            {
                var d = (float)(8 + i * 6);
                g.DrawLine(grip, (float)(Width0 - 8 - d), 8, (float)(Width0 - 8), 8 + d);
            }
        }

        using (var done = RoomSceneArt.RoundedRect(DoneRect, 7))
        using (var brush = new SolidBrush(accent))
            g.FillPath(brush, done);

        // Text in pixels, y down.
        var state = g.Save();
        g.ResetTransform();
        var s = Screens.Scale;
        Rectangle Px(RectD r) => Rectangle.Round(new RectangleF((float)(r.X * s), (float)((Height0 - r.MaxY) * s),
            (float)(r.Width * s), (float)(r.Height * s)));
        using (var font = TextBlit.Font(12))
        {
            TextBlit.Draw(g, UI.Text(UIText.Done), font, Color.White, accent, 1, Px(DoneRect.Inset(3, 3)),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
        using (var font = TextBlit.Font(11, bold: false))
        {
            var hint = UI.Text(UIText.RoomHint);
            const TextFormatFlags flags = TextFormatFlags.SingleLine;
            var size = TextBlit.Measure(hint, font, int.MaxValue, flags);
            var pill = new RectD(14, Height0 - 34, size.Width / s + 16, size.Height / s + 6);
            if (pill.MaxX < DoneRect.MinX - 6)
            {
                var back = Color.FromArgb(60, 40, 30);
                using (var path = RoomSceneArt.RoundedRectPx(Px(pill), (float)(pill.Height / 2 * s)))
                using (var brush = new SolidBrush(Color.FromArgb((int)(0.75 * 255), back)))
                    g.FillPath(brush, path);
                TextBlit.Draw(g, hint, font, Color.White, back, 0.75, Px(pill.Inset(8, 3)), flags);
            }
        }
        g.Restore(state);
    }

    // MARK: Mouse (only while editing; otherwise the window ignores the mouse)

    private bool resizing;
    private PointD startMouse;
    private RectD startFrame;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !room.Editing) return;
        var m = Screens.Mouse;
        var p = new PointD(m.X - room.Frame.MinX, m.Y - room.Frame.MinY);
        if (DoneRect.Contains(p)) { room.SetEditing(false); return; }
        resizing = GripRect.Contains(p);
        startMouse = m;
        startFrame = room.Frame;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if ((e.Button & MouseButtons.Left) == 0 || !room.Editing) return;
        var m = Screens.Mouse;
        var dx = m.X - startMouse.X;
        var dy = m.Y - startMouse.Y;
        RectD f;
        if (resizing)
        {
            // The top-left corner stays put.
            var w = Math.Max(Room.MinWidth, startFrame.Width + dx);
            var h = Math.Max(Room.MinHeight, startFrame.Height - dy);
            f = new RectD(startFrame.MinX, startFrame.MaxY - h, w, h);
        }
        else
        {
            f = startFrame with { X = startFrame.X + dx, Y = startFrame.Y + dy };
        }
        room.SetFrame(new RectD(Math.Round(f.X), Math.Round(f.Y), Math.Round(f.Width), Math.Round(f.Height)));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (room.Editing) room.SaveFrame();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) surface?.Dispose();
        base.Dispose(disposing);
    }
}
