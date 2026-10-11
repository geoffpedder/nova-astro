using System.Globalization;

namespace Nova.Core.Imaging;

/// <summary>Colour filter layout of the top-left 2×2 cell, read row by row (RGGB = R G / G B).</summary>
public enum BayerPattern { RGGB, BGGR, GRBG, GBRG }

public sealed record ColorPlanes(float[] Red, float[] Green, float[] Blue, int Width, int Height);

public static class Debayer
{
    /// <summary>
    /// The pattern from the FITS header (BAYERPAT), shifted by XBAYROFF/YBAYROFF when the frame
    /// is a subframe starting on an odd row or column. Null for mono cameras.
    /// </summary>
    public static BayerPattern? FromHeaders(RawImage image)
    {
        if (!image.Headers.TryGetValue("BAYERPAT", out var text) || !Enum.TryParse<BayerPattern>(text.Trim(), true, out var pattern))
            return null;
        return Shift(pattern, HeaderInt(image, "XBAYROFF"), HeaderInt(image, "YBAYROFF"));
    }

    /// <summary>The pattern seen when the image starts <paramref name="dx"/>, <paramref name="dy"/> pixels in.</summary>
    public static BayerPattern Shift(BayerPattern pattern, int dx, int dy)
    {
        var cells = pattern.ToString();
        var shifted = new char[4];
        for (var y = 0; y < 2; y++)
        for (var x = 0; x < 2; x++)
            shifted[y * 2 + x] = cells[((y + dy) & 1) * 2 + ((x + dx) & 1)];
        return Enum.Parse<BayerPattern>(new string(shifted));
    }

    /// <summary>
    /// Superpixel debayer: each 2×2 cell becomes one RGB pixel (the two greens averaged).
    /// Half resolution, no interpolation artefacts, and fast — right for previews.
    /// </summary>
    public static ColorPlanes SuperPixel(RawImage image, BayerPattern pattern)
    {
        var w = image.Width / 2;
        var h = image.Height / 2;
        var red = new float[w * h];
        var green = new float[w * h];
        var blue = new float[w * h];

        // Offsets of each colour within the 2×2 cell.
        var cells = pattern.ToString();
        int Index(char c, int nth) => Enumerable.Range(0, 4).Where(i => cells[i] == c).ElementAt(nth);
        var r = Index('R', 0);
        var g1 = Index('G', 0);
        var g2 = Index('G', 1);
        var b = Index('B', 0);

        var src = image.Pixels;
        var stride = image.Width;
        for (var y = 0; y < h; y++)
        {
            var row0 = 2 * y * stride;
            var row1 = row0 + stride;
            for (var x = 0; x < w; x++)
            {
                var at0 = row0 + 2 * x;
                var at1 = row1 + 2 * x;
                float Cell(int i) => i switch { 0 => src[at0], 1 => src[at0 + 1], 2 => src[at1], _ => src[at1 + 1] };
                var o = y * w + x;
                red[o] = Cell(r);
                green[o] = (Cell(g1) + Cell(g2)) * 0.5f;
                blue[o] = Cell(b);
            }
        }
        return new ColorPlanes(red, green, blue, w, h);
    }

    /// <summary>
    /// Bilinear debayer at full resolution, straight to interleaved 8-bit RGB: each pixel keeps its own colour and
    /// takes the other two from the nearest pixels of those colours (left/right, up/down or the four diagonals).
    /// <paramref name="maps"/> turn red, green and blue values into display bytes (the stretch), so no full-size
    /// float planes are ever held. Edges mirror, which keeps the colour pattern intact.
    /// </summary>
    public static byte[] Bilinear(RawImage image, BayerPattern pattern, IReadOnlyList<Func<float, byte>> maps)
    {
        int w = image.Width, h = image.Height;
        var src = image.Pixels;
        var cells = pattern.ToString();
        var rgb = new byte[w * h * 3];
        var (mapR, mapG, mapB) = (maps[0], maps[1], maps[2]);

        Parallel.For(0, h, y =>
        {
            int Mirror(int i, int n) => i < 0 ? -i : i >= n ? 2 * n - 2 - i : i;
            float At(int px, int py) => src[Mirror(py, h) * w + Mirror(px, w)];

            for (var x = 0; x < w; x++)
            {
                var own = src[y * w + x];
                var cross = (At(x - 1, y) + At(x + 1, y) + At(x, y - 1) + At(x, y + 1)) * 0.25f;
                var diagonal = (At(x - 1, y - 1) + At(x + 1, y - 1) + At(x - 1, y + 1) + At(x + 1, y + 1)) * 0.25f;
                var across = (At(x - 1, y) + At(x + 1, y)) * 0.5f;
                var updown = (At(x, y - 1) + At(x, y + 1)) * 0.5f;

                float r, g, b;
                switch (cells[(y & 1) * 2 + (x & 1)])
                {
                    case 'R': r = own; g = cross; b = diagonal; break;
                    case 'B': b = own; g = cross; r = diagonal; break;
                    default:
                        // A green pixel: red sits either beside it or above and below it, blue the other way.
                        g = own;
                        var redBeside = cells[(y & 1) * 2 + ((x + 1) & 1)] == 'R';
                        (r, b) = redBeside ? (across, updown) : (updown, across);
                        break;
                }
                var o = (y * w + x) * 3;
                rgb[o] = mapR(r);
                rgb[o + 1] = mapG(g);
                rgb[o + 2] = mapB(b);
            }
        });
        return rgb;
    }

    private static int HeaderInt(RawImage image, string key) =>
        image.Headers.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? (int)d
            : 0;
}
