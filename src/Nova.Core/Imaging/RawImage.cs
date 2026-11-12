namespace Nova.Core.Imaging;

/// <summary>
/// Single-channel image in ADU. Colour sensors arrive here as their raw Bayer mosaic.
/// TODO: debayer for colour previews (read BAYERPAT from the FITS header).
/// </summary>
public sealed class RawImage
{
    public RawImage(int width, int height, float[] pixels, int bitsPerPixel)
    {
        if (pixels.Length != width * height)
            throw new ArgumentException($"Expected {width * height} pixels, got {pixels.Length}");
        Width = width;
        Height = height;
        Pixels = pixels;
        BitsPerPixel = bitsPerPixel;
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>Row-major, top row first, values in ADU.</summary>
    public float[] Pixels { get; }
    /// <summary>FITS BITPIX of the source data (16 for most cameras, -32 for float).</summary>
    public int BitsPerPixel { get; }

    /// <summary>
    /// A colour image read from a file with three planes (R, G, B: a stack, as stacking programs and NOVA's live
    /// stacking write them), each like <see cref="Pixels"/>, which then holds their average (for measuring stars).
    /// Null for everything else: mono, and a colour sensor's raw mosaic.
    /// </summary>
    public float[][]? ColorPlanes { get; init; }

    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
}
