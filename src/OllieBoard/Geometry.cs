using System.Diagnostics;

namespace OllieBoard;

// The app logic uses the same coordinates as the macOS version: points with y going up, origin at the bottom-left
// of the primary screen. Only Screens converts to and from Windows' pixels (y down, origin top-left of the primary).

public readonly record struct PointD(double X, double Y)
{
    public static readonly PointD Zero = new(0, 0);
}

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public static readonly RectD Empty = new(0, 0, 0, 0);
    public double MinX => X;
    public double MinY => Y;
    public double MaxX => X + Width;
    public double MaxY => Y + Height;
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;

    public bool Contains(PointD p) => p.X >= MinX && p.X < MaxX && p.Y >= MinY && p.Y < MaxY;
    public bool Intersects(RectD r) => r.MinX < MaxX && r.MaxX > MinX && r.MinY < MaxY && r.MaxY > MinY;
    public RectD Inset(double dx, double dy) => new(X + dx, Y + dy, Width - 2 * dx, Height - 2 * dy);

    public override string ToString() => FormattableString.Invariant($"{X},{Y},{Width},{Height}");

    public static RectD? Parse(string? s)
    {
        var parts = s?.Split(',');
        if (parts is not { Length: 4 }) return null;
        var v = new double[4];
        for (var i = 0; i < 4; i++)
            if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v[i])) return null;
        return new RectD(v[0], v[1], v[2], v[3]);
    }
}

static class Mathx
{
    public static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);
    public static double Distance(PointD a, PointD b) => Hypot(a.X - b.X, a.Y - b.Y);
    public static double Clamp(double v, double lo, double hi) => Math.Min(Math.Max(v, lo), hi);

    public static double Random(double lo, double hi) => lo + System.Random.Shared.NextDouble() * (hi - lo);
    public static bool Coin() => System.Random.Shared.Next(2) == 0;
    public static T? RandomElement<T>(this IReadOnlyList<T> list) => list.Count == 0 ? default : list[System.Random.Shared.Next(list.Count)];
}

/// Seconds on a monotonic clock (CACurrentMediaTime on macOS).
static class Clock
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();
    public static double Now => Watch.Elapsed.TotalSeconds;
}

/// The screens in app coordinates.
static class Screens
{
    /// Physical pixels per point: the primary display's scale (100% = 1, 150% = 1.5…).
    public static double Scale { get; private set; } = 1;

    public static void Init()
    {
        try { Scale = Math.Max(1, Native.GetDpiForSystem() / 96.0); } catch { Scale = 1; }
        // The first use of Screen hooks display-change notifications to the calling thread, so make it this one.
        _ = Screen.AllScreens;
    }

    private static int PrimaryBottom => Screen.PrimaryScreen?.Bounds.Bottom ?? 0;

    public static RectD FromPixels(Rectangle r) =>
        new(r.Left / Scale, (PrimaryBottom - r.Bottom) / Scale, r.Width / Scale, r.Height / Scale);

    public static PointD FromPixels(double x, double y) => new(x / Scale, (PrimaryBottom - y) / Scale);

    /// Top-left pixel of a rect given in points.
    public static Point TopLeftPixels(RectD r) =>
        new((int)Math.Round(r.MinX * Scale), (int)Math.Round(PrimaryBottom - r.MaxY * Scale));

    public static (double X, double Y) ToPixels(PointD p) => (p.X * Scale, PrimaryBottom - p.Y * Scale);

    public static PointD Mouse
    {
        get { var p = Cursor.Position; return FromPixels(p.X, p.Y); }
    }

    /// Every screen's full frame and visible frame (without the taskbar).
    public static List<(RectD Frame, RectD Visible)> All =>
        Screen.AllScreens.Select(s => (FromPixels(s.Bounds), FromPixels(s.WorkingArea))).ToList();

    public static (RectD Frame, RectD Visible) Main
    {
        get
        {
            var s = Screen.PrimaryScreen ?? Screen.AllScreens[0];
            return (FromPixels(s.Bounds), FromPixels(s.WorkingArea));
        }
    }
}

/// Main-thread scheduling (DispatchQueue.main on macOS).
static class Dispatch
{
    private static SynchronizationContext? ui;

    public static void Init(SynchronizationContext context) => ui = context;

    public static void Async(Action action) => ui!.Post(_ => action(), null);

    /// Runs `action` on the main thread after `seconds`. Call from the main thread.
    public static void After(double seconds, Action action)
    {
        var timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, (int)(seconds * 1000)) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            action();
        };
        timer.Start();
    }
}

/// One background thread running work items in order (a serial DispatchQueue).
sealed class SerialQueue
{
    private readonly System.Collections.Concurrent.BlockingCollection<Action> work = new();

    public SerialQueue(string name)
    {
        var thread = new Thread(() =>
        {
            foreach (var item in work.GetConsumingEnumerable())
            {
                try { item(); } catch (Exception e) { Debug.WriteLine(e); }
            }
        }) { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public void Async(Action action) => work.Add(action);
}
