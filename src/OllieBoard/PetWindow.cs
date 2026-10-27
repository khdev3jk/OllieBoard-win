using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace OllieBoard;

/// Small transparent window that follows a pet around every screen and draws it: the sprite, its emote, special-move
/// effects, the speech bubble and the agent name tag. Everything inside is laid out in points with y going up, the
/// same as the macOS PetView.
sealed class PetWindow : LayeredWindow
{
    public const double W = 240, H = 194;
    /// Where the pet's feet sit inside the window (bottom-left origin); room below is for the name tag.
    public static readonly PointD Feet = new(120, 22);

    /// Area of the window covered by the character body, used for hit testing.
    public static readonly RectD BodyRect = new(Feet.X - 30, Feet.Y, 60, 112);
    private static readonly double HeadTop = Feet.Y + 118;
    private static readonly PointD EffectOrigin = new(Feet.X + 30, Feet.Y + 82);
    private static PointD BodyCenter => new(Feet.X, Feet.Y + 52);
    private static PointD HeadCenter => new(Feet.X + 2, Feet.Y + 98);

    private readonly Pet pet;
    private Surface? surface;
    private double surfaceScale;

    /// The window's frame in points: what the pet's own coordinates refer to.
    public RectD FullFrame { get; private set; } = new(0, 0, W, H);
    private Point topLeft;
    private Point shownTopLeft = new(int.MinValue, int.MinValue);

    private Bitmap? shownFrame;
    private double shownFacing = 1, shownScale = 1, shownRise = -1;
    private Bitmap? shownEffect;
    private string? shownBubble, shownTag;
    private bool shownThought;
    private Bitmap? overlay;
    private bool needsDraw = true;

    private readonly List<Transient> transients = new();
    private double shakeStart = -10;
    /// A prop standing on the ground next to the pet (Frieren's treasure chest).
    private Bitmap? prop;
    private double propDx, propShown = -10;

    public PetWindow(Pet pet)
    {
        this.pet = pet;
        Text = "OllieBoard";
    }

    public void Place(PointD feet)
    {
        FullFrame = new RectD(Math.Round(feet.X - Feet.X), Math.Round(feet.Y - Feet.Y), W, H);
        topLeft = Screens.TopLeftPixels(FullFrame);
    }

    /// Applies the pet's current look; redraws only when something visible changed or an effect is running.
    public void Render(Bitmap? frame, double facing, double scale, Bitmap? effect, double effectAge, string? bubble,
        bool thought, string? tag)
    {
        var rise = effect == null ? 0 : Math.Round(Math.Min(effectAge, 0.4) / 0.4 * 8);
        var now = Clock.Now;
        transients.RemoveAll(t => now - t.Start > t.Duration);
        var animating = transients.Count > 0 || now - shakeStart < 0.35 || now - propShown < 0.35;
        if (bubble != shownBubble || thought != shownThought || tag != shownTag || surfaceScale != Screens.Scale)
        {
            shownBubble = bubble;
            shownThought = thought;
            shownTag = tag;
            overlay?.Dispose();
            overlay = null;
            needsDraw = true;
        }
        if (frame != shownFrame || facing != shownFacing || scale != shownScale || effect != shownEffect || rise != shownRise)
            needsDraw = true;
        shownFrame = frame;
        shownFacing = facing;
        shownScale = scale;
        shownEffect = effect;
        shownRise = rise;

        if (needsDraw || animating)
        {
            Draw(now);
            Present(surface!, topLeft);
            shownTopLeft = topLeft;
            needsDraw = animating;   // one more pass once the last effect ends, to clear it
        }
        else if (topLeft != shownTopLeft)
        {
            MoveTo(topLeft);
            shownTopLeft = topLeft;
        }
    }

    private void Draw(double now)
    {
        var s = Screens.Scale;
        if (surface == null || surfaceScale != s)
        {
            surface?.Dispose();
            surface = new Surface((int)Math.Round(W * s), (int)Math.Round(H * s));
            surfaceScale = s;
        }
        using var g = Graphics.FromImage(surface.Bitmap);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.Clear(Color.Transparent);
        g.CompositingMode = CompositingMode.SourceOver;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        // Points, y up.
        g.Transform = new Matrix((float)s, 0, 0, (float)-s, 0, surface.Height);

        // Same stacking as the macOS layer tree: prop, effects behind, sprite, emote, effects in front, text.
        if (prop != null)
        {
            var pop = Key.At(now - propShown, 0.35, [0, 1.15, 1]);
            DrawSpriteImage(g, prop, Feet.X + propDx, Feet.Y, pop, pop);
        }

        foreach (var t in transients) if (t.Behind && now >= t.Start) t.Draw(g, now - t.Start);

        if (shownFrame != null)
        {
            var shake = now - shakeStart < 0.35 ? Key.At(now - shakeStart, 0.35, [0, -3, 3, -2, 2, 0]) : 0;
            DrawSpriteImage(g, shownFrame, Feet.X + shake, Feet.Y, shownFacing * shownScale, shownScale);
        }

        if (shownEffect != null)
            Transient.DrawImage(g, shownEffect, EffectOrigin.X, EffectOrigin.Y + shownRise, 52, 52);

        foreach (var t in transients) if (!t.Behind && now >= t.Start) t.Draw(g, now - t.Start);

        g.ResetTransform();
        overlay ??= OverlayImage();
        if (overlay != null) g.DrawImageUnscaled(overlay, 0, 0);
    }

    /// Draws a frame-sized image with its feet anchor at (x, y), scaled about that point.
    private static void DrawSpriteImage(Graphics g, Image image, double x, double y, double sx, double sy)
    {
        if (sx == 0 || sy == 0) return;
        var layout = SpriteSheet.Layout;
        var size = layout.FrameSize;
        var state = g.Save();
        g.TranslateTransform((float)x, (float)y);
        g.ScaleTransform((float)sx, (float)sy);
        Transient.DrawImage(g, image, -layout.AnchorX, -(size - layout.AnchorY), size, size);
        g.Restore(state);
    }

    // MARK: Bubble and name tag

    /// Bubble and name tag rendered once into a bitmap at the screen's scale.
    private Bitmap? OverlayImage()
    {
        if (shownBubble == null && shownTag == null) return null;
        var bitmap = new Bitmap(surface!.Width, surface.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        if (shownTag != null) DrawTag(g, shownTag);
        if (shownBubble != null) DrawBubble(g, shownBubble, shownThought);
        return bitmap;
    }

    /// A y-up rect in points to a y-down rect in pixels.
    private static RectangleF Px(RectD r)
    {
        var s = Screens.Scale;
        return new RectangleF((float)(r.X * s), (float)((H - r.MaxY) * s), (float)(r.Width * s), (float)(r.Height * s));
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// Small name plate under the feet of agent mascots.
    private static void DrawTag(Graphics g, string text)
    {
        var s = Screens.Scale;
        using var font = TextBlit.Font(10);
        const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        var size = TextBlit.Measure(text, font, int.MaxValue, flags);
        var w = Math.Min(W - 8, Math.Ceiling(size.Width / s) + 12);
        var h = Math.Ceiling(size.Height / s) + 4;
        var rect = new RectD(Feet.X - w / 2, Feet.Y - h - 4, w, h);
        var back = Color.FromArgb(43, 41, 56);
        using (var path = RoundedRect(Px(rect), (float)(h / 2 * s)))
        using (var brush = new SolidBrush(Color.FromArgb((int)(0.82 * 255), back)))
            g.FillPath(brush, path);
        var textRect = Rectangle.Round(Px(rect.Inset(6, 2)));
        TextBlit.Draw(g, text, font, Color.White, back, 0.82, textRect, flags);
    }

    /// Speech bubble with a pointer, or a rounder thought bubble trailing little puffs.
    private static void DrawBubble(Graphics g, string text, bool thought)
    {
        var s = Screens.Scale;
        using var font = TextBlit.Font(12);
        const TextFormatFlags flags = TextFormatFlags.WordBreak;
        var maxWidth = W - 12;
        var textSize = TextBlit.Measure(text, font, (int)((maxWidth - 16) * s), flags);
        var w = Math.Min(maxWidth, Math.Ceiling(textSize.Width / s) + 16);
        var h = Math.Min(60, Math.Ceiling(textSize.Height / s)) + 10;
        var rect = new RectD((W - w) / 2, Math.Min(HeadTop + 12, H - h - 2), w, h);

        var ink = Color.FromArgb(74, 46, 59);
        var radius = thought ? Math.Min(rect.Height / 2, 14) : 9;
        using var body = RoundedRect(Px(rect), (float)(radius * s));
        using var tail = new GraphicsPath();
        if (thought)
        {
            tail.AddEllipse(Px(new RectD(rect.MidX - 9, rect.MinY - 9, 7, 6)));
            tail.AddEllipse(Px(new RectD(rect.MidX - 13, rect.MinY - 15, 4.5, 4)));
        }
        else
        {
            PointF P(double x, double y) => new((float)(x * s), (float)((H - y) * s));
            tail.AddPolygon([P(rect.MidX - 5, rect.MinY + 1), P(rect.MidX + 1, rect.MinY - 7), P(rect.MidX + 5, rect.MinY + 1)]);
        }
        using (var fill = new SolidBrush(Color.FromArgb((int)(0.96 * 255), Color.White)))
        {
            g.FillPath(fill, body);
            g.FillPath(fill, tail);
        }
        using (var pen = new Pen(Color.FromArgb((int)(0.85 * 255), ink), (float)(1.4 * s)))
            g.DrawPath(pen, body);
        if (thought)
        {
            using var pen = new Pen(Color.FromArgb((int)(0.85 * 255), ink), (float)(1.2 * s));
            g.DrawPath(pen, tail);
        }
        var textRect = Rectangle.Round(Px(rect.Inset(8, 5)));
        TextBlit.Draw(g, text, font, ink, Color.White, 1, textRect, flags);
    }

    // MARK: Special-move effects

    /// Adds an effect for `seconds` (behind the sprite, or in front of it below the bubble).
    private void Add(Transient t, double seconds, bool behind = false)
    {
        t.Start = Clock.Now;
        t.Duration = seconds;
        t.Behind = behind;
        transients.Add(t);
    }

    private void Ring(PointD center, double radius, Rgba color, double width, double to, double duration,
        double delay = 0, double squash = 1) =>
        Add(new RingEffect(center, radius, squash, color, width, to, duration, delay), delay + duration + 0.1);

    /// A one-off burst of particles; `configure` sets up the emitter.
    private void Burst(PointD at, Effect image, double rate, double seconds, double lifetime, double width = 30,
        double height = 30, bool behind = false, Action<EmitterEffect>? configure = null)
    {
        var emitter = new EmitterEffect
        {
            Position = at, ShapeWidth = width, ShapeHeight = height, Image = SpriteSheet.Effect(image, 96),
            BirthRate = rate, EmitFor = seconds, Lifetime = lifetime,
        };
        configure?.Invoke(emitter);
        Add(emitter, seconds + lifetime + emitter.LifetimeRange + 0.3, behind);
    }

    private static StrokeEffect Stroke(IReadOnlyList<PointD> path, Rgba color, double width) =>
        new(path) { Color = color, Width = width };

    /// Evenly spaced opacity keyframes over `seconds`, invisible outside them.
    private static Func<double, double> Flicker(double seconds, double[] values, double begin = 0) =>
        t => Key.At(t, seconds, values, null, begin, 0, 0);

    /// A puff of smoke (transformations, popping out of a chest, landing a blow).
    public void Puff(PointD? at = null) =>
        Burst(at ?? BodyCenter, Effect.Puff, 70, 0.12, 0.6, configure: c =>
        {
            c.Velocity = 50; c.VelocityRange = 20; c.EmissionRange = Math.PI * 2;
            c.ScaleBase = 0.1; c.ScaleRange = 0.03; c.ScaleSpeed = 0.12;
            c.AlphaSpeed = -1.6; c.SpinRange = 1.5;
        });

    /// Anya's telepathy: rings rippling out from her head.
    public void Telepathy()
    {
        for (var i = 0; i < 4; i++) Ring(HeadCenter, 18, new Rgba(1, 0.44, 0.66), 2.5, 2.8, 1.1, i * 0.4);
    }

    /// Nezuko's Exploding Blood: pink flames bursting out in every direction.
    public void FlameBurst()
    {
        Ring(BodyCenter, 30, new Rgba(1, 0.42, 0.66), 6, 3.2, 0.5);
        Burst(BodyCenter, Effect.Flame, 170, 0.35, 0.75, 50, 60, configure: c =>
        {
            c.LifetimeRange = 0.25;
            c.Velocity = 115; c.VelocityRange = 40; c.EmissionRange = Math.PI * 2;
            c.YAcceleration = 90;
            c.ScaleBase = 0.12; c.ScaleRange = 0.05; c.ScaleSpeed = -0.08;
            c.AlphaSpeed = -1.2;
        });
    }

    /// Frieren's spell: a field of flowers popping up around her feet, wilting after `seconds`.
    public void FlowerField(double seconds)
    {
        Effect[] kinds = [Effect.Flower1, Effect.Flower2, Effect.Flower3];
        for (var i = 0; i < 18; i++)
        {
            var image = SpriteSheet.Effect(kinds[i % 3], 64);
            if (image == null) continue;
            var size = Mathx.Random(20, 30);
            var a = Mathx.Random(0, 2 * Math.PI);
            var r = Mathx.Random(0.3, 1);
            var position = new PointD(Feet.X + Math.Cos(a) * 104 * r, Feet.Y - 2 + Math.Sin(a) * 16 * r);
            var begin = i * 0.07;
            var flower = new ImageEffect(image, size, size)
            {
                AnchorX = 0.5, AnchorY = 0.03,   // the bottom of the stem
                Position = _ => position,
                Scale = t => Key.At(t, 0.45, [0, 1.25, 1], [0, 0.7, 1], begin, before: 0, after: 1),
                Opacity = t => Key.Linear(t, 0.6, 1, 0, seconds - 0.6, before: 1, after: 0),
            };
            // Flowers nearer the viewer than her feet grow in front of her.
            Add(flower, seconds, behind: position.Y > Feet.Y);
        }
        Burst(new PointD(Feet.X, Feet.Y + 4), Effect.Sparkle, 6, seconds - 1.5, 1.6, 200, 10, configure: c =>
        {
            c.Velocity = 20; c.EmissionLongitude = Math.PI / 2; c.EmissionRange = 0.6;
            c.ScaleBase = 0.07; c.ScaleRange = 0.03; c.AlphaSpeed = -0.5;
        });
    }

    /// Notes floating up from Bocchi's guitar.
    public void Notes(double seconds, double facing) =>
        Burst(new PointD(Feet.X + facing * 10, Feet.Y + 40), Effect.Note, 3, seconds, 2.2, 10, 10, configure: c =>
        {
            c.Velocity = 35; c.VelocityRange = 10;
            c.EmissionLongitude = Math.PI / 2 - facing * 0.5; c.EmissionRange = 0.6;
            c.ScaleBase = 0.16; c.ScaleRange = 0.04; c.AlphaSpeed = -0.4; c.SpinRange = 0.8;
            c.RedRange = 0.5; c.GreenRange = 0.3; c.BlueRange = 0.2;
        });

    /// Rem's oni form: a pulsing glow behind her and light rising around her (tinted `color` for other moves).
    public void Aura(double seconds, Rgba? color = null)
    {
        Rgba[] colors = color is { } c0
            ? [c0.WithAlpha(0.55), c0.WithAlpha(0)]
            : [new Rgba(0.75, 0.88, 1, 0.55), new Rgba(0.55, 0.73, 0.97, 0)];
        Add(new GlowEffect(new PointD(BodyCenter.X, BodyCenter.Y + 12), 150, 190, colors)
        {
            Opacity = Flicker(seconds, [0, 1, 0.6, 1, 0.6, 1, 0]),
        }, seconds, behind: true);
        Burst(new PointD(Feet.X, Feet.Y + 10), Effect.Glow, 22, seconds - 0.5, 1.3, 70, 20, configure: c =>
        {
            c.Velocity = 30; c.VelocityRange = 10; c.EmissionLongitude = Math.PI / 2; c.EmissionRange = 0.3;
            c.ScaleBase = 0.07; c.ScaleRange = 0.03; c.AlphaSpeed = -0.7;
            if (color is { } tint) c.Color = tint;
        });
    }

    /// Xi's Skynet robot: a red laser from its eyes to the edge of the window, sparking where it ends.
    public void Laser(double seconds, double facing)
    {
        var eye = new PointD(Feet.X + facing * 43, Feet.Y + 62);
        var end = new PointD(facing > 0 ? W : 0, eye.Y - 6);
        foreach (var (width, color) in new[] { (7.0, new Rgba(1, 0.18, 0.15, 0.35)), (2.4, new Rgba(1, 0.45, 0.4)) })
        {
            var beam = Stroke([eye, end], color, width);
            beam.Opacity = Flicker(seconds, [0, 1, 0.7, 1, 0.8, 1, 0.75, 1, 0]);
            Add(beam, seconds);
        }
        Ring(eye, 6, new Rgba(1, 0.2, 0.15), 1.6, 2.6, 0.5);
        Burst(new PointD(end.X - facing * 6, end.Y), Effect.Sparkle, 30, seconds, 0.5, 8, 8, configure: c =>
        {
            c.Velocity = 60; c.VelocityRange = 25;
            c.EmissionLongitude = facing > 0 ? Math.PI : 0; c.EmissionRange = 1.2;
            c.ScaleBase = 0.06; c.ScaleRange = 0.02; c.AlphaSpeed = -1.8;
            c.Color = new Rgba(1, 0.35, 0.3);
        });
    }

    /// Kim's missile coming down as fireworks: a flash, three bursts, a comic "BOOM!" and falling glitter.
    public void Fireworks(double facing)
    {
        var center = new PointD(Feet.X + facing * 44, Feet.Y + 128);
        Add(new GlowEffect(center, 150, 150, [new Rgba(1, 1, 0.85, 0.95), new Rgba(1, 0.75, 0.3, 0)])
        {
            Opacity = t => Key.At(t, 0.5, [0, 1, 0], [0, 0.15, 1], 0, 0, 0),
        }, 0.6);

        (double R, double G, double B)[] colors = [(1, 0.8, 0.32), (1, 0.38, 0.45), (0.45, 0.8, 1)];
        PointD[] offsets = [PointD.Zero, new(-facing * 52, -22), new(facing * 40, -30)];
        for (var k = 0; k < colors.Length; k++)
        {
            var c = new PointD(center.X + offsets[k].X, center.Y + offsets[k].Y);
            var color = new Rgba(colors[k].R, colors[k].G, colors[k].B);
            var delay = k * 0.35;
            Ring(c, 16, color, 3, 3.4, 0.6, delay);
            Dispatch.After(delay, () => Burst(c, Effect.Sparkle, 120, 0.15, 0.9, 10, 10, configure: e =>
            {
                e.Velocity = 75; e.VelocityRange = 20; e.EmissionRange = Math.PI * 2;
                e.YAcceleration = -40;
                e.ScaleBase = 0.1; e.ScaleRange = 0.04; e.AlphaSpeed = -1;
                e.Color = color;
            }));
        }

        if (SpriteSheet.Effect(Effect.Boom, 128) is { } boom)
        {
            Add(new ImageEffect(boom, 86, 86)
            {
                Position = _ => new PointD(center.X, center.Y - 8),
                Scale = t => Key.At(t, 1.5, [0.2, 1.25, 1, 1, 0.8], [0, 0.15, 0.25, 0.8, 1], 0.1),
                Opacity = t => Key.At(t, 1.5, [0, 1, 1, 0], [0, 0.1, 0.8, 1], 0.1, 0, 0),
            }, 1.7);
        }

        Burst(new PointD(center.X, center.Y + 20), Effect.Sparkle, 25, 1.2, 1.6, 140, 20, configure: e =>
        {
            e.Velocity = 10; e.EmissionLongitude = -Math.PI / 2; e.YAcceleration = -45;
            e.ScaleBase = 0.05; e.ScaleRange = 0.02; e.AlphaSpeed = -0.6;
            e.RedRange = 0.4; e.GreenRange = 0.4; e.BlueRange = 0.4;
        });
        Shake();
    }

    /// Trump's wall: rising out of the ground in front of him course by course, then sinking back after `seconds`.
    public void Wall(double seconds, double facing)
    {
        var length = W - Feet.X - 18;
        const double height = 88;
        var ground = new RectD(facing > 0 ? Feet.X + 18 : Feet.X - 18 - length, Feet.Y - 3, length, height);
        if (SpriteSheet.Effect(Effect.Wall) is { } image)
        {
            double Rise(double t) => t < seconds - 0.5
                ? Key.At(t, 0.9, [-height / 2, -height * 0.2, -height * 0.1, height * 0.2, height * 0.3, height / 2], [0, 0.3, 0.4, 0.7, 0.8, 1])
                : Key.Linear(t, 0.5, height / 2, -height / 2, seconds - 0.5);
            // Clipped at the ground line, so the wall seems to come up out of it.
            Add(new ImageEffect(image, length, height)
            {
                Clip = ground,
                Position = t => new PointD(ground.MidX, ground.MinY + Rise(t)),
            }, seconds);
        }
        Burst(new PointD(ground.MidX, Feet.Y), Effect.Puff, 40, 0.9, 0.7, length, 4, configure: c =>
        {
            c.Velocity = 25; c.VelocityRange = 10; c.EmissionLongitude = Math.PI / 2; c.EmissionRange = 0.8;
            c.ScaleBase = 0.08; c.ScaleRange = 0.03; c.ScaleSpeed = 0.08; c.AlphaSpeed = -1.4;
        });
        Shake();
    }

    /// Trump's tariffs: gold coins pouring off his board toward whoever he faces.
    public void Coins(double facing)
    {
        var board = new PointD(Feet.X + facing * 46, Feet.Y + 70);
        Ring(board, 14, new Rgba(1, 0.84, 0.3), 2.5, 3, 0.5);
        Burst(board, Effect.Coin, 26, 1.2, 1.1, 20, 30, configure: c =>
        {
            c.Velocity = 120; c.VelocityRange = 40;
            c.EmissionLongitude = facing > 0 ? 0.35 : Math.PI - 0.35; c.EmissionRange = 0.5;
            c.YAcceleration = -160;
            c.ScaleBase = 0.11; c.ScaleRange = 0.03; c.SpinRange = 6; c.AlphaSpeed = -0.4;
        });
    }

    /// Kim's applause: confetti raining down on the crowd.
    public void Confetti(double seconds) =>
        Burst(new PointD(Feet.X, Feet.Y + 150), Effect.Sparkle, 30, seconds, 2, 220, 10, configure: c =>
        {
            c.Velocity = 20; c.VelocityRange = 10; c.EmissionLongitude = -Math.PI / 2; c.EmissionRange = 0.5;
            c.YAcceleration = -25;
            c.ScaleBase = 0.06; c.ScaleRange = 0.03; c.SpinRange = 4; c.AlphaSpeed = -0.4;
            c.RedRange = 0.8; c.GreenRange = 0.8; c.BlueRange = 0.8;
        });

    /// Xi's parade: a convoy of tanks and armored trucks rolling past behind him.
    public void Convoy(double seconds, double facing)
    {
        Effect[] vehicles = [Effect.Tank, Effect.Truck, Effect.Tank, Effect.Truck];
        var drive = (seconds - 0.4) / 2;
        var gap = drive / 3;
        var from = facing > 0 ? -50 : W + 50;
        var to = facing > 0 ? W + 50 : -50;
        for (var k = 0; k < vehicles.Length; k++)
        {
            if (SpriteSheet.Effect(vehicles[k], 128) is not { } image) continue;
            var begin = k * gap;
            Add(new ImageEffect(image, 80, 80)
            {
                AnchorY = 0.03, Flip = facing,
                Position = t => new PointD(Key.Linear(t, drive, from, to, begin),
                    Key.Repeat(t, 0.25, [Feet.Y + 16, Feet.Y + 17.5, Feet.Y + 16])),
            }, begin + drive + 0.1, behind: true);
        }
        Burst(new PointD(Feet.X, Feet.Y + 16), Effect.Puff, 8, seconds - 0.6, 0.8, 240, 4, behind: true, configure: c =>
        {
            c.Velocity = 12; c.EmissionLongitude = Math.PI / 2; c.EmissionRange = 0.6;
            c.ScaleBase = 0.06; c.ScaleRange = 0.02; c.ScaleSpeed = 0.06; c.AlphaSpeed = -1.2;
        });
    }

    /// Putin's bear ride: dust kicked up behind the galloping bear.
    public void Dust(double seconds, double facing) =>
        Burst(new PointD(Feet.X - facing * 30, Feet.Y + 6), Effect.Puff, 18, seconds, 0.8, 20, 6, behind: true, configure: c =>
        {
            c.Velocity = 60; c.VelocityRange = 20;
            c.EmissionLongitude = facing > 0 ? Math.PI - 0.3 : 0.3; c.EmissionRange = 0.4;
            c.ScaleBase = 0.08; c.ScaleRange = 0.03; c.ScaleSpeed = 0.1; c.AlphaSpeed = -1.2;
        });

    /// Sword arcs sweeping down in front of the pet, or `full` rings all around it (Tanjiro, Zoro, Levi, Mikasa).
    public void Slash(Rgba color, double facing, int count = 1, bool full = false)
    {
        for (var k = 0; k < count; k++)
        {
            var r = 56 + (k % 2) * 10;
            var rotation = k * 0.35 - 0.2;
            var points = new List<PointD>();
            const int steps = 40;
            for (var i = 0; i <= steps; i++)
            {
                double x, y;
                if (full)
                {
                    var a = 2 * Math.PI * i / steps;
                    (x, y) = (r * Math.Cos(a), r * 0.45 * Math.Sin(a));
                }
                else
                {
                    var a = 1.4 - 2.3 * i / steps;   // clockwise from 1.4 to -0.9 radians
                    (x, y) = (r * Math.Cos(a), r * Math.Sin(a));
                }
                // Rotate, then mirror toward `facing`, then move to the body.
                var rx = x * Math.Cos(rotation) - y * Math.Sin(rotation);
                var ry = x * Math.Sin(rotation) + y * Math.Cos(rotation);
                points.Add(new PointD(BodyCenter.X + rx * facing, BodyCenter.Y + ry));
            }
            var begin = k * 0.16;
            foreach (var (width, stroke) in new[] { (7.0, color), (2.2, Rgba.Gray(1, 0.95)) })
            {
                var arc = Stroke(points, stroke, width);
                arc.StrokeEnd = t => Key.Linear(t, 0.18, 0, 1, begin, 0, 1);
                arc.Opacity = t => Key.At(t, 0.5, [1, 1, 0], [0, 0.4, 1], begin, 0, 0);
                Add(arc, begin + 0.6);
            }
        }
    }

    /// A bolt of lightning from the top of the window down to the ground `dx` points from the pet (Nami, Eren, Killua).
    public void Lightning(double dx, Rgba? color = null)
    {
        var c = color ?? new Rgba(1, 0.88, 0.3);
        var ground = new PointD(Feet.X + dx, Feet.Y + 4);
        var p = new PointD(ground.X + Mathx.Random(-20, 20), H);
        var path = new List<PointD> { p };
        while (p.Y > ground.Y + 30)
        {
            p = new PointD(ground.X + Mathx.Random(-16, 16), p.Y - Mathx.Random(16, 28));
            path.Add(p);
        }
        path.Add(ground);
        foreach (var (width, stroke) in new[] { (8.0, c.WithAlpha(0.4)), (2.6, Rgba.Gray(1)) })
        {
            var bolt = Stroke(path, stroke, width);
            bolt.Cap = LineCap.Flat;
            bolt.Opacity = Flicker(0.5, [0, 1, 0.2, 1, 0.6, 0]);
            Add(bolt, 0.55);
        }
        Ring(ground, 20, c, 3, 2.4, 0.45, squash: 0.3);
        Burst(ground, Effect.Sparkle, 80, 0.15, 0.6, 12, 6, configure: e =>
        {
            e.Velocity = 70; e.VelocityRange = 25; e.EmissionLongitude = Math.PI / 2; e.EmissionRange = 1.4;
            e.YAcceleration = -120;
            e.ScaleBase = 0.06; e.ScaleRange = 0.02; e.AlphaSpeed = -1.6;
            e.Color = c;
        });
    }

    /// Speed lines trailing behind a pet that just dashed toward `facing`.
    public void Streak(double facing, Rgba color)
    {
        for (var k = 0; k < 5; k++)
        {
            var y = Feet.Y + 14 + k * 20;
            var line = Stroke([new PointD(Feet.X - facing * 24, y), new PointD(Feet.X - facing * Mathx.Random(80, 118), y)], color, 3);
            line.OffsetX = t => Key.Linear(t, 0.45, 0, -facing * 30);
            line.Opacity = t => Key.Linear(t, 0.45, 0.9, 0, 0, 0, 0);
            Add(line, 0.5);
        }
    }

    /// Rings bursting out of a point `dx`, `dy` from the feet (a punch landing, a fish splashing).
    public void Shockwave(Rgba color, double dx = 0, double dy = 52, int rings = 2, double to = 3)
    {
        for (var i = 0; i < rings; i++) Ring(new PointD(Feet.X + dx, Feet.Y + dy), 18, color, 4, to, 0.5, i * 0.15);
    }

    /// A thick glowing beam from `dx`, `dy` to the edge of the window it faces (Gojo's Hollow Purple).
    public void Beam(Rgba color, double facing, double dx, double dy, double seconds)
    {
        var start = new PointD(Feet.X + facing * dx, Feet.Y + dy);
        var end = new PointD(facing > 0 ? W : 0, start.Y);
        foreach (var (width, stroke) in new[] { (26.0, color.WithAlpha(0.35)), (12.0, color), (4.0, Rgba.Gray(1, 0.95)) })
        {
            var ray = Stroke([start, end], stroke, width);
            ray.Opacity = Flicker(seconds, [0, 1, 0.8, 1, 0.85, 1, 0]);
            Add(ray, seconds);
        }
        Ring(start, 12, color, 3, 2.6, 0.5);
        Shake();
    }

    /// Particles rising around the pet, tinted `color` (Luffy's Gear Fifth, Killua's lightning).
    public void Sparks(Rgba color, double seconds) =>
        Burst(new PointD(Feet.X, Feet.Y + 30), Effect.Sparkle, 24, seconds, 1, 80, 60, configure: c =>
        {
            c.Velocity = 35; c.VelocityRange = 15; c.EmissionLongitude = Math.PI / 2; c.EmissionRange = 0.8;
            c.ScaleBase = 0.06; c.ScaleRange = 0.03; c.SpinRange = 3; c.AlphaSpeed = -0.9;
            c.Color = color;
        });

    /// Gojo's domain: a dark, starry dome swelling out around him for `seconds`.
    public void Domain(double seconds)
    {
        Add(new GlowEffect(BodyCenter, W, H * 1.2,
            [new Rgba(0.1, 0.05, 0.25, 0.85), new Rgba(0.35, 0.2, 0.7, 0.5), new Rgba(0.5, 0.4, 1, 0)])
        {
            Scale = t => Key.Linear(t, 0.6, 0.1, 1),
            Opacity = t => Key.At(t, seconds, [0, 1, 1, 0], [0, 0.1, 0.85, 1], 0, 0, 0),
        }, seconds, behind: true);
        Burst(BodyCenter, Effect.Sparkle, 20, seconds - 0.8, 1.2, W * 0.8, 140, behind: true, configure: c =>
        {
            c.Velocity = 6; c.EmissionRange = Math.PI * 2;
            c.ScaleBase = 0.04; c.ScaleRange = 0.02; c.AlphaSpeed = -0.6;
            c.Color = new Rgba(0.8, 0.85, 1);
        });
    }

    /// Animals (`image`, drawn facing +x) dashing from the pet off the side of the window it faces,
    /// or scattering both ways when `scatter` is set (Megumi's shikigami).
    public void Runners(Effect image, int count, double facing, double size = 50, bool scatter = false)
    {
        if (SpriteSheet.Effect(image, 128) is { } bitmap)
        {
            for (var k = 0; k < count; k++)
            {
                var side = scatter ? (k % 2 == 0 ? facing : -facing) : facing;
                var y = Feet.Y + 2 + Mathx.Random(-6, 8);
                var delay = k * (scatter ? 0.06 : 0.15);
                var run = scatter ? Mathx.Random(0.6, 0.9) : 0.8;
                var from = Feet.X + side * 10;
                var to = side > 0 ? W + size : -size;
                Add(new ImageEffect(bitmap, size, size)
                {
                    AnchorY = 0.03, Flip = side,
                    Position = t => new PointD(Key.Linear(t, run, from, to, delay), Key.Repeat(t, 0.22, [y, y + 8, y])),
                }, delay + run + 0.1);
            }
        }
        Puff(new PointD(Feet.X + facing * 10, Feet.Y + 16));
    }

    /// Kurapika's chain shooting out toward `facing` and reeling back in.
    public void Chain(double facing)
    {
        var hand = new PointD(Feet.X + facing * 22, Feet.Y + 58);
        var end = new PointD(facing > 0 ? W - 6 : 6, Feet.Y + 30);
        var control = new PointD((hand.X + end.X) / 2, hand.Y + 24);
        var path = Enumerable.Range(0, 31).Select(i =>
        {
            var t = i / 30.0;
            return new PointD((1 - t) * (1 - t) * hand.X + 2 * (1 - t) * t * control.X + t * t * end.X,
                (1 - t) * (1 - t) * hand.Y + 2 * (1 - t) * t * control.Y + t * t * end.Y);
        }).ToList();
        foreach (var (width, stroke) in new[] { (6.0, Rgba.Gray(0.25)), (3.0, Rgba.Gray(0.85)) })
        {
            var links = Stroke(path, stroke, width);
            links.Dash = [7, 4];
            links.StrokeEnd = t => Key.At(t, 1.4, [0, 1, 1, 0], [0, 0.25, 0.7, 1], 0, 0, 0);
            Add(links, 1.4);
        }
    }

    /// A short jolt of the sprite, for big impacts.
    public void Shake() => shakeStart = Clock.Now;

    /// Rem's morning star hitting the ground `dx` points in front of her.
    public void Impact(double dx)
    {
        var ground = new PointD(Feet.X + dx, Feet.Y + 4);
        Ring(ground, 22, Rgba.Gray(0.45), 3, 2.2, 0.45, squash: 0.3);
        Puff(ground);
    }

    /// Shows `image` standing on the ground `dx` points from the pet, or removes it.
    public void ShowProp(Bitmap? image, double dx)
    {
        prop = image;
        propDx = dx;
        needsDraw = true;
        if (image != null) propShown = Clock.Now;
    }

    // MARK: Mouse

    private PointD grabOffset;
    private PointD mouseDownAt;
    private bool didDrag;
    private ContextMenuStrip? skillMenu;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var mouse = Screens.Mouse;
        if (e.Button == MouseButtons.Left)
        {
            mouseDownAt = mouse;
            grabOffset = new PointD(mouse.X - pet.Position.X, mouse.Y - pet.Position.Y);
            didDrag = false;
        }
        else if (e.Button == MouseButtons.Right)
        {
            ShowSkillMenu();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if ((e.Button & MouseButtons.Left) == 0) return;
        var mouse = Screens.Mouse;
        if (!didDrag && Mathx.Distance(mouse, mouseDownAt) < 4) return;
        if (!didDrag) { didDrag = true; pet.BeginDrag(); }
        pet.Drag(new PointD(mouse.X - grabOffset.X, mouse.Y - grabOffset.Y));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (didDrag) pet.EndDrag(); else pet.Clicked();
        didDrag = false;
    }

    /// Right-click lists the character's special moves.
    private void ShowSkillMenu()
    {
        if (pet.Agent != null || pet.Character.Skills.Length == 0) return;
        skillMenu?.Dispose();
        skillMenu = new ContextMenuStrip();
        skillMenu.Items.Add(new ToolStripMenuItem(pet.Character.DisplayName(Languages.Current)) { Enabled = false });
        foreach (var skill in pet.Character.Skills)
            skillMenu.Items.Add(skill.Title(), null, (_, _) => pet.UseSkill(skill));
        ShowMenu(skillMenu, Cursor.Position);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            surface?.Dispose();
            overlay?.Dispose();
            skillMenu?.Dispose();
        }
        base.Dispose(disposing);
    }
}
