using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace OllieBoard;

// Stand-ins for the Core Animation layers the macOS version adds for special moves. Each Transient is drawn every
// frame from the time since it was added, in the pet window's y-up point coordinates, until its duration runs out.

/// Keyframe timing helpers (CAKeyframeAnimation / CABasicAnimation).
static class Key
{
    /// Value at `t` seconds of an animation that starts at `begin` and lasts `duration`, interpolating `values`
    /// linearly at `times` (evenly spaced when omitted). Outside its run it returns `before` / `after`.
    public static double At(double t, double duration, double[] values, double[]? times = null, double begin = 0,
        double? before = null, double? after = null)
    {
        var local = (t - begin) / duration;
        if (local < 0) return before ?? values[0];
        if (local > 1) return after ?? values[^1];
        var n = values.Length;
        for (var i = 1; i < n; i++)
        {
            var t0 = times?[i - 1] ?? (double)(i - 1) / (n - 1);
            var t1 = times?[i] ?? (double)i / (n - 1);
            if (local <= t1) return t1 <= t0 ? values[i] : values[i - 1] + (values[i] - values[i - 1]) * (local - t0) / (t1 - t0);
        }
        return values[^1];
    }

    /// From one value to another (fillMode decides what shows outside the run).
    public static double Linear(double t, double duration, double from, double to, double begin = 0, double? before = null, double? after = null) =>
        At(t, duration, [from, to], null, begin, before, after);

    /// A keyframe animation repeating forever from `begin`.
    public static double Repeat(double t, double duration, double[] values, double begin = 0)
    {
        if (t < begin) return values[0];
        var local = (t - begin) % duration;
        return At(local, duration, values);
    }
}

/// RGBA color with components in 0…1, like CGColor.
readonly record struct Rgba(double R, double G, double B, double A = 1)
{
    public static Rgba Gray(double white, double alpha = 1) => new(white, white, white, alpha);
    public Rgba WithAlpha(double a) => this with { A = a };

    public Color ToColor(double opacity = 1) => Color.FromArgb(
        (int)Math.Round(Mathx.Clamp(A * opacity, 0, 1) * 255), (int)Math.Round(Mathx.Clamp(R, 0, 1) * 255),
        (int)Math.Round(Mathx.Clamp(G, 0, 1) * 255), (int)Math.Round(Mathx.Clamp(B, 0, 1) * 255));
}

abstract class Transient
{
    public double Start;
    public double Duration;
    /// Drawn behind the sprite rather than in front of it.
    public bool Behind;

    public abstract void Draw(Graphics g, double t);

    /// Draws `image` upright in a y-up rect, optionally mirrored, scaled about (ax, ay), faded and tinted.
    public static void DrawImage(Graphics g, Image image, double x, double y, double w, double h, double opacity = 1,
        Rgba? tint = null, ImageAttributes? reuse = null)
    {
        if (opacity <= 0.003 || w <= 0 || h <= 0) return;
        var state = g.Save();
        g.TranslateTransform((float)x, (float)(y + h));
        g.ScaleTransform(1, -1);
        var dest = new RectangleF(0, 0, (float)w, (float)h);
        var c = tint ?? new Rgba(1, 1, 1);
        if (opacity >= 0.997 && c is { R: 1, G: 1, B: 1, A: 1 })
        {
            g.DrawImage(image, dest);
        }
        else
        {
            var attributes = reuse ?? new ImageAttributes();
            var matrix = new ColorMatrix
            {
                Matrix00 = (float)Mathx.Clamp(c.R, 0, 1), Matrix11 = (float)Mathx.Clamp(c.G, 0, 1),
                Matrix22 = (float)Mathx.Clamp(c.B, 0, 1), Matrix33 = (float)Mathx.Clamp(c.A * opacity, 0, 1),
            };
            attributes.SetColorMatrix(matrix);
            PointF[] corners = [new(0, 0), new(dest.Width, 0), new(0, dest.Height)];
            g.DrawImage(image, corners, new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel, attributes);
            if (reuse == null) attributes.Dispose();
        }
        g.Restore(state);
    }
}

/// An expanding, fading ring (`ring(at:…)`).
sealed class RingEffect(PointD center, double radius, double squash, Rgba color, double width, double toScale,
    double duration, double delay) : Transient
{
    public override void Draw(Graphics g, double t)
    {
        if (t < delay || t > delay + duration) return;
        var scale = Key.Linear(t, duration, 0.4, toScale, delay);
        var opacity = Key.At(t, duration, [0, 0.95, 0], [0, 0.2, 1], delay);
        using var pen = new Pen(color.ToColor(opacity), (float)(width * scale));
        var rx = radius * scale;
        var ry = radius * squash * scale;
        g.DrawEllipse(pen, (float)(center.X - rx), (float)(center.Y - ry), (float)(2 * rx), (float)(2 * ry));
    }
}

/// A stroked polyline whose opacity, drawn fraction (strokeEnd) and horizontal offset can be animated.
sealed class StrokeEffect : Transient
{
    private readonly PointF[] points;
    private readonly double[] lengths;
    public Rgba Color;
    public double Width;
    public float[]? Dash;
    public LineCap Cap = LineCap.Round;
    public Func<double, double> Opacity = _ => 1;
    public Func<double, double>? StrokeEnd;
    public Func<double, double>? OffsetX;

    public StrokeEffect(IReadOnlyList<PointD> path)
    {
        points = path.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();
        lengths = new double[points.Length];
        for (var i = 1; i < points.Length; i++)
            lengths[i] = lengths[i - 1] + Mathx.Hypot(points[i].X - points[i - 1].X, points[i].Y - points[i - 1].Y);
    }

    public override void Draw(Graphics g, double t)
    {
        var opacity = Opacity(t);
        if (opacity <= 0.003 || points.Length < 2) return;
        var drawn = points;
        if (StrokeEnd != null)
        {
            var end = Mathx.Clamp(StrokeEnd(t), 0, 1) * lengths[^1];
            if (end <= 0.01) return;
            var list = new List<PointF> { points[0] };
            for (var i = 1; i < points.Length; i++)
            {
                if (lengths[i] <= end) { list.Add(points[i]); continue; }
                var f = (end - lengths[i - 1]) / Math.Max(lengths[i] - lengths[i - 1], 1e-6);
                list.Add(new PointF((float)(points[i - 1].X + (points[i].X - points[i - 1].X) * f),
                    (float)(points[i - 1].Y + (points[i].Y - points[i - 1].Y) * f)));
                break;
            }
            drawn = list.ToArray();
            if (drawn.Length < 2) return;
        }
        using var pen = new Pen(Color.ToColor(opacity), (float)Width)
        {
            StartCap = Cap, EndCap = Cap, LineJoin = LineJoin.Round,
        };
        if (Dash != null)
        {
            pen.DashPattern = Dash.Select(d => d / (float)Width).ToArray();
            pen.StartCap = pen.EndCap = LineCap.Flat;
        }
        var state = g.Save();
        if (OffsetX != null) g.TranslateTransform((float)OffsetX(t), 0);
        g.DrawLines(pen, drawn);
        g.Restore(state);
    }
}

/// A radial glow (CAGradientLayer of type .radial) filling an ellipse.
sealed class GlowEffect(PointD center, double width, double height, Rgba[] colors) : Transient
{
    public Func<double, double> Opacity = _ => 1;
    public Func<double, double> Scale = _ => 1;

    public override void Draw(Graphics g, double t)
    {
        var opacity = Opacity(t);
        var scale = Scale(t);
        if (opacity <= 0.003 || scale <= 0) return;
        var w = width * scale;
        var h = height * scale;
        using var path = new GraphicsPath();
        path.AddEllipse((float)(center.X - w / 2), (float)(center.Y - h / 2), (float)w, (float)h);
        using var brush = new PathGradientBrush(path) { CenterPoint = new PointF((float)center.X, (float)center.Y) };
        // A path gradient's blend runs from the rim (0) to the center (1).
        var blend = new ColorBlend(colors.Length)
        {
            Colors = colors.Reverse().Select(c => c.ToColor(opacity)).ToArray(),
            Positions = colors.Select((_, i) => (float)i / (colors.Length - 1)).ToArray(),
        };
        brush.InterpolationColors = blend;
        g.FillPath(brush, path);
    }
}

/// An image (prop, vehicle, animal, flower…) moving, scaling and fading over time.
sealed class ImageEffect(Image image, double width, double height) : Transient
{
    /// Anchor inside the image (0…1, y up), which sits at Position.
    public double AnchorX = 0.5, AnchorY = 0.5;
    public Func<double, PointD> Position = _ => PointD.Zero;
    public Func<double, double> Scale = _ => 1;
    public Func<double, double> Opacity = _ => 1;
    public double Flip = 1;
    /// Only the part inside this rect shows (the wall rising out of the ground).
    public RectD? Clip;

    public override void Draw(Graphics g, double t)
    {
        var scale = Scale(t);
        var opacity = Opacity(t);
        if (scale <= 0 || opacity <= 0.003) return;
        var p = Position(t);
        var state = g.Save();
        if (Clip is { } clip) g.SetClip(new RectangleF((float)clip.X, (float)clip.Y, (float)clip.Width, (float)clip.Height));
        g.TranslateTransform((float)p.X, (float)p.Y);
        g.ScaleTransform((float)(scale * Flip), (float)scale);
        DrawImage(g, image, -AnchorX * width, -AnchorY * height, width, height, opacity);
        g.Restore(state);
    }
}

/// A one-off burst of particles (a CAEmitterLayer with one CAEmitterCell), simulated on the CPU.
sealed class EmitterEffect : Transient
{
    public PointD Position;
    public double ShapeWidth = 30, ShapeHeight = 30;
    public Image? Image;
    public double BirthRate;
    /// Emission stops after this many seconds.
    public double EmitFor;
    public double Lifetime, LifetimeRange;
    public double Velocity, VelocityRange;
    public double EmissionLongitude, EmissionRange;
    public double YAcceleration;
    public double ScaleBase = 1, ScaleRange, ScaleSpeed;
    public double AlphaSpeed;
    public double SpinRange;
    public Rgba Color = new(1, 1, 1);
    public double RedRange, GreenRange, BlueRange;

    private sealed record Particle(double Born, PointD Origin, double Vx, double Vy, double Life, double Scale, double Spin, Rgba Color);
    private readonly List<Particle> particles = new();
    private double lastT;
    private readonly ImageAttributes attributes = new();
    private double pending;

    /// Side of the source image in points; particle sizes are a fraction of it (cell.scale).
    private static double ImageSide => SpriteSheet.Layout.FrameSize * SpriteSheet.Layout.PixelScale;

    public override void Draw(Graphics g, double t)
    {
        if (Image == null) return;
        var r = System.Random.Shared;
        double Vary(double range) => (r.NextDouble() * 2 - 1) * range;
        var emitEnd = Math.Min(t, EmitFor);
        if (emitEnd > lastT)
        {
            pending += (emitEnd - lastT) * BirthRate;
            while (pending >= 1)
            {
                pending -= 1;
                var born = lastT + r.NextDouble() * (emitEnd - lastT);
                var a = r.NextDouble() * 2 * Math.PI;
                var d = Math.Sqrt(r.NextDouble());
                var origin = new PointD(Position.X + Math.Cos(a) * d * ShapeWidth / 2, Position.Y + Math.Sin(a) * d * ShapeHeight / 2);
                var angle = EmissionLongitude + Vary(EmissionRange / 2);
                var speed = Velocity + Vary(VelocityRange);
                particles.Add(new Particle(born, origin, Math.Cos(angle) * speed, Math.Sin(angle) * speed,
                    Math.Max(0.05, Lifetime + Vary(LifetimeRange)), ScaleBase + Vary(ScaleRange), Vary(SpinRange),
                    new Rgba(Color.R + Vary(RedRange), Color.G + Vary(GreenRange), Color.B + Vary(BlueRange), Color.A)));
            }
        }
        lastT = Math.Max(lastT, t);
        particles.RemoveAll(p => t - p.Born > p.Life);
        foreach (var p in particles)
        {
            var age = t - p.Born;
            if (age < 0) continue;
            var scale = p.Scale + ScaleSpeed * age;
            var alpha = Mathx.Clamp(1 + AlphaSpeed * age, 0, 1);
            if (scale <= 0 || alpha <= 0.003) continue;
            var x = p.Origin.X + p.Vx * age;
            var y = p.Origin.Y + p.Vy * age + 0.5 * YAcceleration * age * age;
            var side = ImageSide * scale;
            var state = g.Save();
            g.TranslateTransform((float)x, (float)y);
            if (p.Spin != 0) g.RotateTransform((float)(p.Spin * age * 180 / Math.PI));
            DrawImage(g, Image, -side / 2, -side / 2, side, side, alpha, p.Color, attributes);
            g.Restore(state);
        }
    }
}
