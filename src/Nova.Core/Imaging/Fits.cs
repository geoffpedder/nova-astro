using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Nova.Core.Imaging;

public sealed record FitsCard(string Key, object Value, string? Comment = null);

/// <summary>A header card as read from a file. COMMENT/HISTORY cards have their text as the value.</summary>
public sealed record FitsHeaderCard(string Key, string Value, string? Comment);

/// <summary>
/// Minimal FITS support: the primary HDU of a 2-D image (3-D cubes read their first plane).
/// Rows are kept in file order, which is how INDI drivers and NINA display them.
/// </summary>
public static class Fits
{
    public const int BlockSize = 2880;
    private const int CardSize = 80;

    /// <summary>Reads a FITS file. Pass <paramref name="format"/> ".fits.z" for INDI zlib-compressed BLOBs.</summary>
    public static RawImage Read(byte[] data, string format = ".fits")
    {
        if (format.EndsWith(".z", StringComparison.OrdinalIgnoreCase))
            data = Decompress(data);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pos = 0;
        while (true)
        {
            if (pos + CardSize > data.Length) throw new FormatException("FITS header has no END card");
            var card = Encoding.ASCII.GetString(data, pos, CardSize);
            pos += CardSize;
            var key = card[..8].Trim();
            if (key == "END") break;
            if (card[8] == '=' && key.Length > 0) headers.TryAdd(key, ParseValue(card[10..]).Value);
        }
        pos = RoundUpToBlock(pos);

        var bitpix = RequireInt(headers, "BITPIX");
        var naxis = RequireInt(headers, "NAXIS");
        if (naxis < 2) throw new FormatException($"Expected an image (NAXIS >= 2), got NAXIS={naxis}");
        var width = RequireInt(headers, "NAXIS1");
        var height = RequireInt(headers, "NAXIS2");
        var bzero = OptionalDouble(headers, "BZERO", 0);
        var bscale = OptionalDouble(headers, "BSCALE", 1);

        var count = width * height;
        var bytesPerPixel = Math.Abs(bitpix) / 8;
        if (pos + (long)count * bytesPerPixel > data.Length)
            throw new FormatException("FITS data is shorter than the header says");

        var src = data.AsSpan(pos);
        var pixels = new float[count];
        switch (bitpix)
        {
            case 8:
                for (var i = 0; i < count; i++) pixels[i] = (float)(src[i] * bscale + bzero);
                break;
            case 16:
                for (var i = 0; i < count; i++)
                    pixels[i] = (float)(BinaryPrimitives.ReadInt16BigEndian(src[(i * 2)..]) * bscale + bzero);
                break;
            case 32:
                for (var i = 0; i < count; i++)
                    pixels[i] = (float)(BinaryPrimitives.ReadInt32BigEndian(src[(i * 4)..]) * bscale + bzero);
                break;
            case -32:
                for (var i = 0; i < count; i++)
                    pixels[i] = (float)(BinaryPrimitives.ReadSingleBigEndian(src[(i * 4)..]) * bscale + bzero);
                break;
            case -64:
                for (var i = 0; i < count; i++)
                    pixels[i] = (float)(BinaryPrimitives.ReadDoubleBigEndian(src[(i * 8)..]) * bscale + bzero);
                break;
            default:
                throw new FormatException($"Unsupported BITPIX {bitpix}");
        }

        var image = new RawImage(width, height, pixels, bitpix);
        foreach (var (k, v) in headers) image.Headers[k] = v;
        return image;
    }

    /// <summary>Every card of the primary header in file order, with comments (for showing the header).</summary>
    public static IReadOnlyList<FitsHeaderCard> ReadHeader(byte[] data)
    {
        var cards = new List<FitsHeaderCard>();
        for (var pos = 0; pos + CardSize <= data.Length; pos += CardSize)
        {
            var card = Encoding.ASCII.GetString(data, pos, CardSize);
            var key = card[..8].Trim();
            if (key == "END") return cards;
            if (key.Length == 0 && string.IsNullOrWhiteSpace(card)) continue; // padding
            if (card[8] == '=' && card[9] == ' ')
            {
                var (value, comment) = ParseValue(card[10..]);
                cards.Add(new FitsHeaderCard(key, value, comment));
            }
            else
            {
                cards.Add(new FitsHeaderCard(key, card[8..].Trim(), null)); // COMMENT, HISTORY, blank keyword
            }
        }
        throw new FormatException("FITS header has no END card");
    }

    /// <summary>Writes unsigned 16-bit data the standard way: BITPIX 16 with BZERO 32768.</summary>
    public static byte[] WriteUInt16(int width, int height, ReadOnlySpan<ushort> pixels, IEnumerable<FitsCard>? extraCards = null)
    {
        if (pixels.Length != width * height) throw new ArgumentException("Pixel count does not match dimensions");

        var cards = new List<FitsCard>
        {
            new("SIMPLE", true, "conforms to FITS standard"),
            new("BITPIX", 16, "array data type"),
            new("NAXIS", 2, "number of array dimensions"),
            new("NAXIS1", width),
            new("NAXIS2", height),
            new("BZERO", 32768, "offset data range to that of unsigned short"),
            new("BSCALE", 1),
        };
        if (extraCards is not null) cards.AddRange(extraCards);

        var header = new StringBuilder();
        foreach (var card in cards) header.Append(FormatCard(card));
        header.Append("END".PadRight(CardSize));
        var headerBytes = RoundUpToBlock(header.Length);
        var dataBytes = RoundUpToBlock(pixels.Length * 2);

        var output = new byte[headerBytes + dataBytes];
        output.AsSpan(0, headerBytes).Fill((byte)' ');
        Encoding.ASCII.GetBytes(header.ToString(), output);
        var dst = output.AsSpan(headerBytes);
        for (var i = 0; i < pixels.Length; i++)
            BinaryPrimitives.WriteInt16BigEndian(dst[(i * 2)..], (short)(pixels[i] - 32768));
        return output;
    }

    /// <summary>
    /// Writes 32-bit floating-point data (BITPIX -32): one plane as a 2-D image, three (R, G, B) as a 3-D cube
    /// (NAXIS3 = 3), as stacking programs write colour.
    /// </summary>
    public static byte[] WriteFloat32(int width, int height, IReadOnlyList<float[]> planes, IEnumerable<FitsCard>? extraCards = null)
    {
        if (planes.Count is not (1 or 3)) throw new ArgumentException("One plane (mono) or three (R, G, B)");
        if (planes.Any(p => p.Length != width * height)) throw new ArgumentException("Pixel count does not match dimensions");

        var cards = new List<FitsCard>
        {
            new("SIMPLE", true, "conforms to FITS standard"),
            new("BITPIX", -32, "32-bit floating point"),
            new("NAXIS", planes.Count == 1 ? 2 : 3, "number of array dimensions"),
            new("NAXIS1", width),
            new("NAXIS2", height),
        };
        if (planes.Count == 3) cards.Add(new FitsCard("NAXIS3", 3, "R, G, B"));
        if (extraCards is not null) cards.AddRange(extraCards);

        var header = new StringBuilder();
        foreach (var card in cards) header.Append(FormatCard(card));
        header.Append("END".PadRight(CardSize));
        var headerBytes = RoundUpToBlock(header.Length);
        var dataBytes = RoundUpToBlock(planes.Count * width * height * 4);

        var output = new byte[headerBytes + dataBytes];
        output.AsSpan(0, headerBytes).Fill((byte)' ');
        Encoding.ASCII.GetBytes(header.ToString(), output);
        var dst = output.AsSpan(headerBytes);
        var at = 0;
        foreach (var plane in planes)
            foreach (var v in plane)
            {
                BinaryPrimitives.WriteSingleBigEndian(dst[at..], v);
                at += 4;
            }
        return output;
    }

    internal static string FormatCard(FitsCard card)
    {
        var key = card.Key.ToUpperInvariant();
        if (key.Length > 8) throw new ArgumentException($"FITS keyword too long: {card.Key}");

        var value = card.Value switch
        {
            bool b => (b ? "T" : "F").PadLeft(20),
            string s => $"'{s.Replace("'", "''"),-8}'".PadRight(20),
            double d => d.ToString("G15", CultureInfo.InvariantCulture).PadLeft(20),
            float f => f.ToString("G9", CultureInfo.InvariantCulture).PadLeft(20),
            IFormattable n => n.ToString(null, CultureInfo.InvariantCulture).PadLeft(20),
            var other => other.ToString()!.PadLeft(20),
        };
        var text = $"{key,-8}= {value}" + (card.Comment is null ? "" : $" / {card.Comment}");
        return text.Length > CardSize ? text[..CardSize] : text.PadRight(CardSize);
    }

    /// <summary>Splits a card's value field into the value (unquoted for strings) and the comment after '/'.</summary>
    private static (string Value, string? Comment) ParseValue(string field)
    {
        var s = field.TrimStart();
        var rest = s;
        string value;
        if (s.StartsWith('\''))
        {
            var sb = new StringBuilder();
            var i = 1;
            for (; i < s.Length; i++)
            {
                if (s[i] != '\'') { sb.Append(s[i]); continue; }
                if (i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i++; continue; }
                break;
            }
            value = sb.ToString().TrimEnd();
            rest = i + 1 < s.Length ? s[(i + 1)..] : "";
            var commentStart = rest.IndexOf('/');
            return (value, commentStart >= 0 ? NullIfEmpty(rest[(commentStart + 1)..].Trim()) : null);
        }
        var slash = s.IndexOf('/');
        value = (slash >= 0 ? s[..slash] : s).Trim();
        return (value, slash >= 0 ? NullIfEmpty(s[(slash + 1)..].Trim()) : null);
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static int RequireInt(Dictionary<string, string> headers, string key) =>
        headers.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i
            : throw new FormatException($"FITS header missing or invalid {key}");

    private static double OptionalDouble(Dictionary<string, string> headers, string key, double fallback) =>
        headers.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : fallback;

    private static int RoundUpToBlock(int length) => (length + BlockSize - 1) / BlockSize * BlockSize;

    private static byte[] Decompress(byte[] data)
    {
        using var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
