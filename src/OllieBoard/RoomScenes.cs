using System.Drawing.Drawing2D;

namespace OllieBoard;

// The play-room scenes, one per supported series plus the UN hall for politics. Drawn in room coordinates
// (points, y up) and clipped to the room's rounded outline; members stand on the floor strip at the bottom.
sealed class RoomSceneArt(Graphics g, double width, double height)
{
    public static Color Color(double r, double g, double b, double a = 1) =>
        System.Drawing.Color.FromArgb((int)Math.Round(a * 255), (int)r, (int)g, (int)b);

    public static GraphicsPath RoundedRect(RectD r, double radius) =>
        RoundedRectPx(new RectangleF((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height), (float)radius);

    public static GraphicsPath RoundedRectPx(RectangleF r, float radius)
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

    // MARK: Cozy room

    public void DrawCozy(RectD wall, double floorH)
    {
        Fill(wall, Color(253, 240, 232, 0.94));
        for (double x = 14; x < width; x += 38) Fill(new RectD(x, floorH, 16, wall.Height), Color(249, 224, 220, 0.94));
        DrawWindow(wall);
        DrawClock(wall);

        Fill(new RectD(0, 0, width, floorH), Color(206, 150, 100, 0.97));
        const double plank = 16;
        var row = 0;
        for (double y = 0; y < floorH; y += plank, row++)
        {
            Fill(new RectD(0, y, width, 1.5), Color(168, 112, 70, 0.9));
            for (var x = row % 2 == 0 ? 30.0 : 75; x < width; x += 90) Fill(new RectD(x, y, 1.5, plank), Color(168, 112, 70, 0.7));
        }
        var rugWidth = Math.Min(width * 0.55, 340);
        var rug = new RectD(width / 2 - rugWidth / 2, floorH * 0.14, rugWidth, floorH * 0.62);
        Oval(rug, Color(240, 160, 172, 0.95));
        Oval(rug.Inset(12, 7), Color(250, 206, 210, 0.95));
        Fill(new RectD(0, floorH - 2, width, 9), Color(150, 98, 64, 0.97));
    }

    private void DrawWindow(RectD wall)
    {
        if (wall.Height < 90) return;
        var w = Math.Min(140, width * 0.24);
        var h = Math.Min(96, wall.Height * 0.56);
        var frame = new RectD(width * 0.14, wall.MidY - h / 2 + 6, w, h);
        Fill(frame, Color(255, 255, 255));
        var glass = frame.Inset(6, 6);
        Gradient(glass, Color(170, 214, 245), Color(222, 240, 252));
        var cloud = new PointD(glass.MinX + glass.Width * 0.32, glass.MinY + glass.Height * 0.62);
        foreach (var (dx, dy, r) in new[] { (-12.0, 0.0, 8.0), (0.0, 4.0, 11.0), (13.0, 0.0, 8.0) })
            Circle(new PointD(cloud.X + dx, cloud.Y + dy), r, Color(255, 255, 255, 0.95));
        Fill(new RectD(glass.MidX - 2, glass.MinY, 4, glass.Height), Color(255, 255, 255));
        Fill(new RectD(glass.MinX, glass.MidY - 2, glass.Width, 4), Color(255, 255, 255));
        Fill(new RectD(frame.MinX - 6, frame.MinY - 6, frame.Width + 12, 6), Color(150, 98, 64));
    }

    private void DrawClock(RectD wall)
    {
        if (wall.Height < 90 || width < 360) return;
        var r = Math.Min(22, wall.Height * 0.18);
        var c = new PointD(width * 0.76, wall.MidY + 14);
        Circle(c, r, Color(255, 255, 255));
        using (var rim = new Pen(Color(150, 98, 64), 3))
            g.DrawEllipse(rim, (float)(c.X - r), (float)(c.Y - r), (float)(2 * r), (float)(2 * r));
        using var hands = new Pen(Color(90, 60, 45), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(hands, [P(c.X, c.Y + r * 0.62), P(c.X, c.Y), P(c.X + r * 0.45, c.Y)]);
    }

    // MARK: Kimetsu no Yaiba

    /// A wisteria trellis on a moonlit night, over a wooden veranda.
    public void DrawWisteriaNight(RectD wall, double floorH)
    {
        var w = width;
        var h = wall.Height;
        Gradient(wall, Color(84, 60, 128), Color(22, 24, 60));
        for (var i = 0; i < 45; i++)
        {
            var star = new PointD(Noise(i) * w, wall.MinY + h * (0.3 + 0.7 * Noise(i, 1)));
            Circle(star, 0.6 + Noise(i, 2) * 1.1, Color(255, 250, 230, 0.5 + 0.5 * Noise(i, 3)));
        }
        var r = Math.Min(24, h * 0.16);
        var moon = new PointD(w * 0.78, wall.MinY + h * 0.56);
        Circle(moon, r * 1.7, Color(255, 240, 200, 0.12));
        Circle(moon, r, Color(255, 246, 214));
        Shape(new (double X, double Y)[] { (0, 0), (0, 0.22), (0.18, 0.38), (0.36, 0.2), (0.55, 0.34), (0.78, 0.16), (1, 0.3), (1, 0) }
            .Select(p => new PointD(p.X * w, wall.MinY + p.Y * h)).ToArray(), Color(48, 36, 82));

        // Trellis beam with blossoms hanging from it, tapering toward the tips.
        var top = height - 8;
        Fill(new RectD(0, top - 2, w, 10), Color(92, 62, 44));
        Color[] shades = [Color(206, 170, 236), Color(176, 128, 218), Color(150, 100, 200)];
        var i2 = 0;
        for (double x = 8; x < w; x += 22, i2++)
        {
            var length = Math.Min(h * 0.45, 30 + Noise(i2, 4) * 50);
            for (var j = 0; j < (int)(length / 5); j++)
            {
                var t = j * 5.0;
                var spread = 6 * (1 - t / length);
                Circle(new PointD(x + (Noise(i2 * 31 + j, 5) - 0.5) * spread * 2, top - t), 2.4 + spread * 0.35, shades[(i2 + j) % 3]);
            }
            Oval(new RectD(x - 8, top - 7, 11, 5), Color(96, 150, 90));
        }

        Planks(floorH, Color(122, 82, 56), Color(86, 56, 38), Color(70, 46, 32));
    }

    // MARK: One Piece

    /// The Thousand Sunny's lawn deck at sea, under the Straw Hat jolly roger.
    public void DrawSunnyDeck(RectD wall, double floorH)
    {
        var w = width;
        var h = wall.Height;
        Gradient(wall, Color(196, 232, 250), Color(96, 176, 236));
        var sun = new PointD(w * 0.12, wall.MaxY - 34);
        Circle(sun, 30, Color(255, 236, 150, 0.35));
        Circle(sun, 18, Color(255, 226, 120));
        Cloud(new PointD(w * 0.36, wall.MaxY - 38), 1);
        Cloud(new PointD(w * 0.88, wall.MaxY - 64), 0.8);

        var sea = new RectD(0, wall.MinY, w, h * 0.3);
        Gradient(sea, Color(30, 100, 170), Color(70, 150, 210));
        Waves(sea, 6);

        // Mast and flag.
        var mastX = w * 0.68;
        Fill(new RectD(mastX - 5, wall.MinY, 10, h), Color(150, 100, 60));
        var flagH = Math.Min(58, h * 0.36);
        var flagW = Math.Min(84, flagH * 1.45);
        var flag = new RectD(mastX + 5, wall.MaxY - flagH - 12, flagW, flagH);
        Fill(flag, Color(30, 30, 34));
        JollyRoger(flag);

        // Railing between the deck and the sea.
        var wood = Color(164, 112, 70);
        Fill(new RectD(0, wall.MinY + 16, w, 6), wood);
        for (double x = 10; x < w; x += 34) Fill(new RectD(x, wall.MinY, 5, 18), wood);

        Fill(new RectD(0, 0, w, floorH), Color(126, 196, 92));
        for (double x = 0; x < w; x += 40) Fill(new RectD(x, 0, 20, floorH), Color(116, 184, 84));
        Fill(new RectD(0, floorH - 4, w, 8), wood);
    }

    private void JollyRoger(RectD f)
    {
        var r = f.Height * 0.2;
        var c = new PointD(f.MidX, f.MidY - r * 0.4);
        var bone = Color(245, 245, 240);
        foreach (var d in new[] { -1.0, 1.0 })
            Line(new PointD(c.X - r * 1.7, c.Y - r * 1.3 * d), new PointD(c.X + r * 1.7, c.Y + r * 1.3 * d), bone, r * 0.35);
        Circle(c, r, bone);
        Fill(new RectD(c.X - r * 0.55, c.Y - r * 1.15, r * 1.1, r * 0.7), bone);
        foreach (var dx in new[] { -0.38, 0.38 }) Circle(new PointD(c.X + r * dx, c.Y - r * 0.05), r * 0.24, Color(30, 30, 34));
        // The straw hat, with its red band.
        var straw = Color(240, 200, 90);
        Oval(new RectD(c.X - r * 0.85, c.Y + r * 0.6, r * 1.7, r * 1.1), straw);
        Fill(new RectD(c.X - r * 0.84, c.Y + r * 0.72, r * 1.68, r * 0.24), Color(214, 52, 48));
        Oval(new RectD(c.X - r * 1.55, c.Y + r * 0.5, r * 3.1, r * 0.5), straw);
    }

    // MARK: Attack on Titan

    /// Wall Maria, with the Colossal Titan peeking over it, above a cobblestone street.
    public void DrawWallMaria(RectD wall, double floorH)
    {
        var w = width;
        var h = wall.Height;
        Gradient(wall, Color(250, 214, 168), Color(150, 190, 225));
        var top = wall.MinY + h * 0.62;

        var r = Math.Min(h * 0.3, 56);
        var face = new PointD(w * 0.3, top + r * 0.45);
        for (var i = 0; i < 7; i++)
        {
            var puff = new PointD(face.X + (Noise(i, 6) - 0.5) * r * 3, face.Y + r * 0.5 + Noise(i, 7) * r * 0.9);
            Circle(puff, r * (0.3 + 0.3 * Noise(i, 8)), Color(255, 255, 255, 0.45));
        }
        Oval(new RectD(face.X - r * 0.85, face.Y - r, r * 1.7, r * 2), Color(196, 78, 66));
        for (var k = -2; k <= 2; k++)   // bare muscle
        {
            var x = face.X + k * r * 0.15;
            Line(new PointD(x, face.Y + r * 0.88), new PointD(x + k * r * 0.05, face.Y + r * 0.3), Color(160, 52, 46), 1.5);
        }
        Oval(new RectD(face.X - r * 0.62, face.Y + r * 0.18, r * 1.24, r * 0.2), Color(150, 48, 42));
        foreach (var dx in new[] { -0.32, 0.32 })
        {
            var eye = new PointD(face.X + r * dx, face.Y + r * 0.02);
            Circle(eye, r * 0.16, Color(250, 246, 236));
            Circle(eye, r * 0.06, Color(40, 30, 30));
        }

        // The wall itself.
        Fill(new RectD(0, wall.MinY, w, top - wall.MinY), Color(196, 182, 156));
        var seam = Color(170, 154, 128);
        var row = 0;
        for (var y = wall.MinY; y < top; y += 14, row++)
        {
            Fill(new RectD(0, y, w, 1.5), seam);
            for (var x = row % 2 == 0 ? 0.0 : 22; x < w; x += 44) Fill(new RectD(x, y, 1.5, 14), seam);
        }
        Fill(new RectD(0, top - 2, w, 8), Color(150, 136, 112));

        // Survey Corps banner hanging from the parapet.
        var bannerH = Math.Min(64, (top - wall.MinY) * 0.8);
        var bannerW = bannerH * 0.65;
        var banner = new RectD(w * 0.7, top - 2 - bannerH, bannerW, bannerH);
        Fill(banner, Color(46, 84, 60));
        Shape([new PointD(banner.MinX, banner.MinY), new PointD(banner.MidX, banner.MinY - 8), new PointD(banner.MaxX, banner.MinY)],
            Color(46, 84, 60));
        WingsOfFreedom(new PointD(banner.MidX, banner.MidY), bannerW * 0.42);

        Fill(new RectD(0, 0, w, floorH), Color(140, 130, 120));
        row = 0;
        for (double y = 3; y < floorH - 4; y += 13, row++)
        {
            var col = 0;
            for (var x = row % 2 == 0 ? -6.0 : 8; x < w; x += 28, col++)
            {
                var stone = Noise(row * 97 + col, 9) > 0.5 ? Color(174, 164, 152) : Color(160, 150, 138);
                using var path = RoundedRect(new RectD(x, y, 24, 10), 4);
                using var brush = new SolidBrush(stone);
                g.FillPath(brush, path);
            }
        }
        Fill(new RectD(0, floorH - 2, w, 6), Color(110, 100, 92));
    }

    /// Two overlapping wings, white over blue.
    private void WingsOfFreedom(PointD c, double s)
    {
        foreach (var (dx, wing) in new[] { (s * 0.25, Color(70, 110, 196)), (-s * 0.25, Color(245, 245, 245)) })
        {
            Shape([new PointD(c.X + dx + s * 0.5, c.Y - s * 0.9), new PointD(c.X + dx - s * 0.7, c.Y + s * 0.9),
                   new PointD(c.X + dx - s * 0.35, c.Y + s * 0.1), new PointD(c.X + dx - s * 0.75, c.Y + s * 0.05),
                   new PointD(c.X + dx - s * 0.3, c.Y - s * 0.35), new PointD(c.X + dx - s * 0.6, c.Y - s * 0.45)], wing);
        }
    }

    // MARK: Jujutsu Kaisen

    /// Tokyo Jujutsu High at dusk: a torii gate, a temple hall and a stone path.
    public void DrawJujutsuHigh(RectD wall, double floorH)
    {
        var w = width;
        var h = wall.Height;
        var b = wall.MinY;
        Gradient(wall, Color(250, 176, 128), Color(104, 82, 150));
        var forest = Color(52, 70, 70);
        for (var i = 0; i <= (int)(w / 22); i++)
            Circle(new PointD(i * 22 + Noise(i, 9) * 8, b + h * 0.18 + Noise(i, 10) * h * 0.1), 16 + Noise(i, 11) * 8, forest);
        Fill(new RectD(0, b, w, h * 0.18), forest);
        for (var i = 0; i < 6; i++)   // cursed energy drifting about
            Circle(new PointD(Noise(i, 12) * w, b + h * (0.35 + 0.6 * Noise(i, 13))), 3 + Noise(i, 14) * 6, Color(160, 100, 230, 0.3));

        var red = Color(184, 50, 42);
        // Temple hall.
        var hallW = Math.Min(w * 0.36, 200);
        var hallX = w - hallW - w * 0.06;
        var hallH = h * 0.3;
        var roofH = h * 0.18;
        Fill(new RectD(hallX, b, hallW, hallH), Color(236, 226, 206));
        for (var k = 0; k <= 4; k++) Fill(new RectD(hallX + hallW * k / 4 - 3, b, 6, hallH), red);
        Shape([new PointD(hallX - hallW * 0.14, b + hallH + 4), new PointD(hallX + hallW * 1.14, b + hallH + 4),
               new PointD(hallX + hallW * 0.86, b + hallH + roofH), new PointD(hallX + hallW * 0.14, b + hallH + roofH)],
            Color(58, 58, 66));
        Fill(new RectD(hallX + hallW * 0.1, b + hallH + roofH - 3, hallW * 0.8, 6), Color(40, 40, 46));

        // Torii gate.
        var gx = w * 0.26;
        var gw = Math.Min(w * 0.3, 150);
        var gh = Math.Min(h * 0.68, 130);
        foreach (var side in new[] { -1.0, 1.0 }) Fill(new RectD(gx + side * gw * 0.36 - 4.5, b, 9, gh), red);
        Fill(new RectD(gx - gw * 0.46, b + gh * 0.72, gw * 0.92, 7), red);
        Fill(new RectD(gx - 3, b + gh * 0.72, 6, gh * 0.28), red);
        Shape([new PointD(gx - gw * 0.55, b + gh - 4), new PointD(gx + gw * 0.55, b + gh - 4),
               new PointD(gx + gw * 0.62, b + gh + 6), new PointD(gx - gw * 0.62, b + gh + 6)], red);
        Shape([new PointD(gx - gw * 0.62, b + gh + 6), new PointD(gx + gw * 0.62, b + gh + 6),
               new PointD(gx + gw * 0.66, b + gh + 12), new PointD(gx - gw * 0.66, b + gh + 12)], Color(40, 30, 30));

        // Gravel yard with a stone path up to the gate.
        Fill(new RectD(0, 0, w, floorH), Color(176, 170, 160));
        for (var i = 0; i < (int)(w * floorH / 160); i++)
            Circle(new PointD(Noise(i, 15) * w, Noise(i, 16) * floorH), 1, Color(146, 140, 130));
        using (var brush = new SolidBrush(Color(204, 198, 188)))
        {
            for (double y = 4; y < floorH - 8; y += 18)
            {
                using var path = RoundedRect(new RectD(gx - 30, y, 60, 13), 3);
                g.FillPath(brush, path);
            }
        }
        Fill(new RectD(0, floorH - 2, w, 6), Color(120, 116, 108));
    }

    // MARK: Hunter x Hunter

    /// Gon's Whale Island: green hills by the sea, a big tree and a whale spouting offshore.
    public void DrawWhaleIsland(RectD wall, double floorH)
    {
        var w = width;
        var h = wall.Height;
        var b = wall.MinY;
        Gradient(wall, Color(214, 240, 252), Color(110, 190, 240));
        Cloud(new PointD(w * 0.2, wall.MaxY - 36), 0.9);
        Cloud(new PointD(w * 0.62, wall.MaxY - 56), 1.1);

        var sea = new RectD(0, b, w, h * 0.36);
        Gradient(sea, Color(40, 120, 190), Color(80, 160, 215));
        Waves(sea, 17);
        if (w >= 360)
        {
            var whale = new PointD(w * 0.55, sea.MinY + sea.Height * 0.7);
            var blue = Color(54, 84, 140);
            Oval(new RectD(whale.X - 22, whale.Y - 6, 44, 14), blue);
            Shape([new PointD(whale.X + 18, whale.Y), new PointD(whale.X + 32, whale.Y + 9), new PointD(whale.X + 30, whale.Y - 4)], blue);
            foreach (var (dx, dy, r) in new[] { (-12.0, 12.0, 2.5), (-16.0, 17.0, 2.2), (-8.0, 17.0, 2.2), (-12.0, 20.0, 2.0) })
                Circle(new PointD(whale.X + dx, whale.Y + dy), r, Color(255, 255, 255, 0.9));
        }

        Oval(new RectD(-w * 0.1, b - h * 0.3, w * 0.6, h * 0.54), Color(104, 174, 82));
        Oval(new RectD(w * 0.62, b - h * 0.36, w * 0.6, h * 0.6), Color(86, 158, 70));

        var tree = new PointD(w * 0.82, b);
        Fill(new RectD(tree.X - 6, b, 12, h * 0.42), Color(120, 82, 52));
        foreach (var (dx, dy, r) in new[] { (-18.0, 0.42, 20.0), (16.0, 0.44, 20.0), (0.0, 0.55, 24.0), (-4.0, 0.4, 18.0) })
            Circle(new PointD(tree.X + dx, b + h * dy), Math.Min(r, h * 0.16), dy > 0.5 ? Color(90, 165, 72) : Color(70, 140, 60));

        Fill(new RectD(0, 0, w, floorH), Color(120, 186, 86));
        Oval(new RectD(w * 0.2, floorH * 0.15, w * 0.6, floorH * 0.55), Color(206, 170, 120));
        for (var i = 0; i < (int)(w / 14); i++)   // grass tufts
        {
            var p = new PointD(Noise(i, 18) * w, Noise(i, 19) * (floorH - 12));
            Line(p, new PointD(p.X - 2, p.Y + 5), Color(84, 150, 60), 1.2);
            Line(p, new PointD(p.X + 2, p.Y + 6), Color(84, 150, 60), 1.2);
        }
        Fill(new RectD(0, floorH - 3, w, 6), Color(96, 160, 70));
    }

    // MARK: Politics

    /// The UN General Assembly hall: gold wall, the emblem, a row of flags, the green rostrum and blue carpet.
    public void DrawAssemblyHall(RectD wall, double floorH)
    {
        var w = width;
        var h = wall.Height;
        var b = wall.MinY;
        Gradient(wall, Color(214, 170, 92), Color(238, 206, 140));
        for (double x = 20; x < w; x += 36) Fill(new RectD(x, b, 2, h), Color(196, 152, 78, 0.6));

        // Big screens on both sides.
        if (w >= 420)
        {
            var sw = Math.Min(w * 0.16, 90);
            var sh = sw * 0.6;
            foreach (var x in new[] { w * 0.06, w * 0.94 - sw })
            {
                var screen = new RectD(x, b + h * 0.5, sw, sh);
                Fill(screen, Color(40, 40, 50));
                Gradient(screen.Inset(3, 3), Color(30, 60, 120), Color(70, 120, 200));
            }
        }

        // Emblem: a globe in an olive wreath.
        var r = Math.Min(h * 0.24, 42);
        var c = new PointD(w / 2, b + h * 0.6);
        Circle(c, r * 1.4, Color(250, 228, 170, 0.55));
        Circle(c, r, Color(78, 148, 216));
        var globe = Color(255, 255, 255, 0.95);
        using (var pen = new Pen(globe, 1.2f))
        {
            foreach (var k in new[] { 0.33, 0.66 })
                g.DrawEllipse(pen, (float)(c.X - r * k), (float)(c.Y - r * k), (float)(r * 2 * k), (float)(r * 2 * k));
        }
        for (var i = 0; i < 8; i++)
        {
            var a = i / 8.0 * 2 * Math.PI;
            Line(c, new PointD(c.X + Math.Cos(a) * r * 0.9, c.Y + Math.Sin(a) * r * 0.9), globe, 1);
        }
        foreach (var side in new[] { -1.0, 1.0 })
        {
            for (var k = 0; k < 9; k++)
            {
                var a = -Math.PI / 2 + side * (0.12 + k / 8.0 * 0.8) * Math.PI;
                var leaf = new PointD(c.X + Math.Cos(a) * r * 1.2, c.Y + Math.Sin(a) * r * 1.2);
                Oval(new RectD(leaf.X - r * 0.08, leaf.Y - r * 0.08, r * 0.16, r * 0.16), globe);
            }
        }

        // Flags on poles either side of the rostrum (plain stripes, no particular country).
        var deskW = Math.Min(w * 0.5, 260);
        (Color, Color)[] stripes =
        [
            (Color(214, 52, 48), Color(250, 250, 250)), (Color(40, 90, 180), Color(250, 210, 60)),
            (Color(40, 140, 80), Color(250, 250, 250)), (Color(250, 210, 60), Color(214, 52, 48)),
            (Color(250, 250, 250), Color(40, 90, 180)), (Color(30, 30, 34), Color(214, 52, 48)),
        ];
        var poleH = Math.Min(60, h * 0.4);
        var n = 0;
        for (double x = 18; x < w / 2 - deskW / 2 - 10; x += 26)
        {
            foreach (var px in new[] { x, w - x })
            {
                Line(new PointD(px, b), new PointD(px, b + poleH), Color(120, 100, 70), 1.5);
                var (top, bottom) = stripes[n % stripes.Length];
                Fill(new RectD(px + 1, b + poleH - 6, 15, 6), top);
                Fill(new RectD(px + 1, b + poleH - 12, 15, 6), bottom);
                n += 1;
            }
        }

        // Blue carpet with rows of delegate desks.
        Fill(new RectD(0, 0, w, floorH), Color(52, 82, 140));
        for (var k = 0; k < 2; k++) Fill(new RectD(0, floorH * (0.2 + k * 0.35), w, 5), Color(176, 136, 84, 0.85));
        Fill(new RectD(0, floorH - 2, w, 6), Color(150, 112, 60));

        // The green marble rostrum and lectern at the back.
        var rostrum = new RectD(w / 2 - deskW / 2, floorH - 4, deskW, Math.Min(26, h * 0.16));
        Fill(rostrum, Color(46, 96, 76));
        Fill(new RectD(rostrum.MinX, rostrum.MaxY - 3, deskW, 3), Color(214, 176, 96));
        Fill(new RectD(w / 2 - 14, floorH - 4, 28, Math.Min(38, h * 0.24)), Color(36, 80, 62));
    }

    // MARK: Helpers

    private static PointF P(double x, double y) => new((float)x, (float)y);

    private void Fill(RectD r, Color c)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using var brush = new SolidBrush(c);
        g.FillRectangle(brush, (float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
    }

    /// From `bottom` at the rect's bottom edge to `top` at its top edge.
    private void Gradient(RectD r, Color bottom, Color top)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var rect = new RectangleF((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
        // Expanded by a hair so the brush's own wrap-around never shows on the edge rows.
        using var brush = new LinearGradientBrush(RectangleF.Inflate(rect, 0, 0.5f), bottom, top, LinearGradientMode.Vertical);
        g.FillRectangle(brush, rect);
    }

    private void Oval(RectD r, Color c)
    {
        using var brush = new SolidBrush(c);
        g.FillEllipse(brush, (float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
    }

    private void Circle(PointD center, double radius, Color c) =>
        Oval(new RectD(center.X - radius, center.Y - radius, radius * 2, radius * 2), c);

    private void Shape(PointD[] points, Color c)
    {
        using var brush = new SolidBrush(c);
        g.FillPolygon(brush, points.Select(p => P(p.X, p.Y)).ToArray());
    }

    private void Line(PointD from, PointD to, Color c, double lineWidth)
    {
        using var pen = new Pen(c, (float)lineWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, P(from.X, from.Y), P(to.X, to.Y));
    }

    private void Cloud(PointD c, double scale)
    {
        foreach (var (dx, dy, r) in new[] { (-14.0, 0.0, 10.0), (0.0, 5.0, 13.0), (15.0, 0.0, 10.0) })
            Circle(new PointD(c.X + dx * scale, c.Y + dy * scale), r * scale, Color(255, 255, 255, 0.95));
    }

    private void Waves(RectD sea, double salt)
    {
        for (var i = 0; i < (int)(sea.Width / 30); i++)
        {
            var p = new PointD(Noise(i, salt) * sea.Width, sea.MinY + 4 + Noise(i, salt + 1) * (sea.Height - 8));
            Line(p, new PointD(p.X + 10, p.Y), Color(255, 255, 255, 0.6), 1.5);
        }
    }

    private void Planks(double floorH, Color base_, Color seam, Color edge)
    {
        Fill(new RectD(0, 0, width, floorH), base_);
        var row = 0;
        for (double y = 0; y < floorH; y += 16, row++)
        {
            Fill(new RectD(0, y, width, 1.5), seam);
            for (var x = row % 2 == 0 ? 30.0 : 75; x < width; x += 90) Fill(new RectD(x, y, 1.5, 16), seam);
        }
        Fill(new RectD(0, floorH - 2, width, 9), edge);
    }

    /// The same value in 0..<1 on every redraw for the same inputs, so stars and stones don't jump around.
    private static double Noise(int i, double salt = 0)
    {
        var v = Math.Sin(i * 12.9898 + salt * 78.233) * 43758.5453;
        return v - Math.Floor(v);
    }
}
