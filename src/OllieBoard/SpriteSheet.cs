using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;

namespace OllieBoard;

/// Layout shared by every character sheet (written by SpriteGen in the macOS project).
sealed class SheetLayout
{
    public sealed record AnimInfo(int Row, int Frames, double Fps, bool Loop);

    public double FrameSize;
    public double PixelScale;
    /// Feet position inside a frame, y measured from the top.
    public double AnchorX, AnchorY;
    public Dictionary<string, AnimInfo> Animations = new();
    /// Emote icons, in order, in effects.png.
    public List<string> Effects = new();

    public static SheetLayout Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var layout = new SheetLayout
        {
            FrameSize = root.GetProperty("frameSize").GetDouble(),
            PixelScale = root.GetProperty("pixelScale").GetDouble(),
            AnchorX = root.GetProperty("anchor").GetProperty("x").GetDouble(),
            AnchorY = root.GetProperty("anchor").GetProperty("y").GetDouble(),
        };
        foreach (var a in root.GetProperty("animations").EnumerateObject())
            layout.Animations[a.Name] = new AnimInfo(a.Value.GetProperty("row").GetInt32(), a.Value.GetProperty("frames").GetInt32(),
                a.Value.GetProperty("fps").GetDouble(), a.Value.GetProperty("loop").GetBoolean());
        foreach (var e in root.GetProperty("effects").EnumerateArray()) layout.Effects.Add(e.GetString() ?? "");
        return layout;
    }
}

sealed class SpriteSheet
{
    public static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Sprites");
    public static readonly SheetLayout Layout = SheetLayout.Load(Path.Combine(Directory, "layout.json"));

    private static readonly Dictionary<string, SpriteSheet> Cache = new();

    /// Shared so each sheet is decoded only once. Agent mascots have no special moves, so they skip those rows.
    public static SpriteSheet Named(string id, bool skills = true)
    {
        if (Cache.TryGetValue(id, out var sheet)) return sheet;
        sheet = new SpriteSheet(id, skills);
        Cache[id] = sheet;
        return sheet;
    }

    /// Side of a frame in screen pixels.
    public static int FramePixels => (int)Math.Round(Layout.FrameSize * Screens.Scale);

    private static Dictionary<Effect, Bitmap>? effects;
    private static readonly Dictionary<(Effect, int), Bitmap> ScaledEffects = new();

    /// Emote and prop images shared by every character, at full resolution.
    public static Bitmap? Effect(Effect effect)
    {
        if (effects == null)
        {
            var names = Layout.Effects;
            var images = Cells("effects", names.Select((_, i) => (i, 0)).ToList(), (int)(Layout.FrameSize * Layout.PixelScale));
            effects = new();
            for (var i = 0; i < names.Count; i++)
                if (Enum.TryParse<Effect>(names[i], true, out var e)) effects[e] = images[i];
        }
        return effects.GetValueOrDefault(effect);
    }

    /// The same image scaled down to `pixels` once, for cheap drawing at that size.
    public static Bitmap? Effect(Effect effect, int pixels)
    {
        if (ScaledEffects.TryGetValue((effect, pixels), out var scaled)) return scaled;
        var full = Effect(effect);
        if (full == null) return null;
        scaled = Resize(full, pixels);
        ScaledEffects[(effect, pixels)] = scaled;
        return scaled;
    }

    private readonly Dictionary<Anim, Bitmap[]> frames = new();

    private SpriteSheet(string characterId, bool skills)
    {
        foreach (var anim in Enum.GetValues<Anim>())
        {
            if (!skills && anim.IsSkill()) continue;
            if (!Layout.Animations.TryGetValue(anim.Key(), out var info)) continue;
            frames[anim] = Cells(characterId, Enumerable.Range(0, info.Frames).Select(c => (c, info.Row)).ToList(), FramePixels).ToArray();
        }
    }

    /// Cuts the given (column, row) cells out of `<name>.png`, each scaled to `size` pixels.
    private static List<Bitmap> Cells(string name, List<(int Col, int Row)> positions, int size)
    {
        var path = Path.Combine(Directory, name + ".png");
        if (!File.Exists(path)) throw new FileNotFoundException("Missing sprite sheet", path);
        using var sheet = new Bitmap(path);
        var px = (int)(Layout.FrameSize * Layout.PixelScale);
        var result = new List<Bitmap>();
        foreach (var (col, row) in positions)
        {
            var cell = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(cell);
            Quality(g);
            using var wrap = new ImageAttributes();
            wrap.SetWrapMode(WrapMode.TileFlipXY);   // no dark fringe at the cell's edges
            g.DrawImage(sheet, new Rectangle(0, 0, size, size), col * px, row * px, px, px, GraphicsUnit.Pixel, wrap);
            result.Add(cell);
        }
        return result;
    }

    private static Bitmap Resize(Bitmap source, int size)
    {
        var copy = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(copy);
        Quality(g);
        using var wrap = new ImageAttributes();
        wrap.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, wrap);
        return copy;
    }

    private static void Quality(Graphics g)
    {
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    /// Frame of `anim` after it has been playing for `elapsed` seconds.
    public Bitmap? Frame(Anim anim, double elapsed)
    {
        if (!frames.TryGetValue(anim, out var list) || !Layout.Animations.TryGetValue(anim.Key(), out var info)) return null;
        var i = (int)(elapsed * info.Fps);
        return list[info.Loop ? i % list.Length : Math.Min(i, list.Length - 1)];
    }

    public double Duration(Anim anim) =>
        Layout.Animations.TryGetValue(anim.Key(), out var info) ? info.Frames / info.Fps : 0;
}
