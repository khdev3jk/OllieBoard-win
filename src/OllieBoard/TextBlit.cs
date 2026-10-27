using System.Drawing.Imaging;

namespace OllieBoard;

/// Text drawn with GDI (TextRenderer), which falls back to other fonts for Japanese and symbols the way Windows
/// does. GDI text doesn't keep alpha, so it is drawn onto an opaque copy of its background and then copied over.
static class TextBlit
{
    public static Font Font(double points, bool bold = true)
    {
        var face = Languages.Current == Language.Ja
            ? (bold ? "Yu Gothic UI Semibold" : "Yu Gothic UI")
            : (bold ? "Segoe UI Semibold" : "Segoe UI");
        return new Font(face, (float)(points * Screens.Scale), GraphicsUnit.Pixel);
    }

    public static Size Measure(string text, Font font, int maxWidth, TextFormatFlags flags) =>
        TextRenderer.MeasureText(text, font, new Size(maxWidth, int.MaxValue), flags | TextFormatFlags.NoPadding);

    /// Draws `text` in `rect` (pixels) over a background of `back` shown at `alpha`.
    public static void Draw(Graphics g, string text, Font font, Color fore, Color back, double alpha, Rectangle rect,
        TextFormatFlags flags)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        using var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb);
        using (var bg = Graphics.FromImage(bitmap))
        {
            bg.Clear(back);
            TextRenderer.DrawText(bg, text, font, new Rectangle(0, 0, rect.Width, rect.Height), fore, back,
                flags | TextFormatFlags.NoPadding);
        }
        if (alpha >= 0.997)
        {
            g.DrawImageUnscaled(bitmap, rect.Location);
            return;
        }
        // Replace the pixels so they come out exactly `back` at `alpha`, like the fill under them.
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = (float)alpha });
        var mode = g.CompositingMode;
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        g.DrawImage(bitmap, rect, 0, 0, rect.Width, rect.Height, GraphicsUnit.Pixel, attributes);
        g.CompositingMode = mode;
    }
}
