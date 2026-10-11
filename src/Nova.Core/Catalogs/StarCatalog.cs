using System.Globalization;
using System.IO.Compression;
using Nova.Core.Astronomy;

namespace Nova.Core.Catalogs;

/// <param name="Label">The most familiar designation: proper name, else Bayer (ζ Ori), Flamsteed, HD, HIP or Gliese.</param>
/// <param name="Detail">The other designations, e.g. "ζ Ori · 50 Ori · HD 37742 · HIP 26727".</param>
public sealed record CatalogStar(string Label, string Detail, EquatorialCoordinates Position, double Magnitude);

/// <summary>
/// Offline catalog of ~120 000 named stars (Hipparcos, Yale Bright Star and Gliese, to about magnitude 12),
/// J2000. From the HYG database v4.1 by David Nash (astronexus), CC BY-SA 4.0; built by
/// tools/build_star_catalog.py and embedded in this assembly. Loaded on first use.
/// </summary>
public static class StarCatalog
{
    private static readonly Lazy<CatalogStar[]> Stars = new(Load);
    private static readonly Lazy<string[][]> Keys = new(() => Stars.Value.Select(KeysOf).ToArray());

    /// <summary>Stars brighter than this match partial names; fainter ones only an exact designation.</summary>
    private const double BrowsableMagnitude = 6.5;

    /// <summary>
    /// Stars matching a name or designation ("vega", "alpha lyr", "alpha centauri", "α Lyr", "HD 172167"), with
    /// how well: 0 exact, 1 starts with the query, 2 contains it, 3 a near miss (a typo or two, when nothing
    /// else matches). Partial and near matches only for naked-eye stars, so a short query isn't buried under
    /// thousands of catalogue numbers.
    /// </summary>
    public static IReadOnlyList<(int Rank, CatalogStar Star)> Search(string query, int limit = 20)
    {
        var q = DeepSkyCatalog.Normalize(query);
        if (q.Length == 0) return [];
        var stars = Stars.Value;
        var keys = Keys.Value;
        var hits = new List<(int, CatalogStar)>();
        for (var i = 0; i < stars.Length; i++)
        {
            var rank = int.MaxValue;
            foreach (var key in keys[i])
            {
                if (key == q) { rank = 0; break; }
                if (stars[i].Magnitude > BrowsableMagnitude) continue;
                if (key.StartsWith(q, StringComparison.Ordinal)) rank = Math.Min(rank, 1);
                else if (q.Length >= 3 && key.Contains(q, StringComparison.Ordinal)) rank = Math.Min(rank, 2);
            }
            if (rank != int.MaxValue) hits.Add((rank, stars[i]));
        }
        if (hits.Count == 0)
        {
            // Nothing: allow a typo or two ("alpha centuri"), closest first.
            var near = new List<(int Distance, CatalogStar Star)>();
            for (var i = 0; i < stars.Length; i++)
            {
                if (stars[i].Magnitude > BrowsableMagnitude) continue;
                if (Fuzzy.Closest(q, keys[i]) is { } d) near.Add((d, stars[i]));
            }
            return near.OrderBy(n => n.Distance).ThenBy(n => n.Star.Magnitude).Take(limit).Select(n => (3, n.Star)).ToList();
        }
        return hits.OrderBy(h => h.Item1).ThenBy(h => h.Item2.Magnitude).Take(limit).ToList();
    }

    private static readonly char[] Superscripts = ['⁰', '¹', '²', '³', '⁴', '⁵', '⁶', '⁷', '⁸', '⁹'];

    private static string[] KeysOf(CatalogStar star)
    {
        var names = new List<string> { star.Label };
        names.AddRange(star.Detail.Split(" · ", StringSplitOptions.RemoveEmptyEntries));
        var keys = new List<string>();
        foreach (var name in names)
        {
            // "ε¹ Lyr" answers to "epsilon lyr" too, and both to "epsilon lyrae".
            var plain = name.IndexOfAny(Superscripts) >= 0 ? new string(name.Where(c => Array.IndexOf(Superscripts, c) < 0).ToArray()) : name;
            foreach (var variant in new[] { name, plain, Constellations.SpellOut(name), Constellations.SpellOut(plain) })
                if (variant is not null) keys.Add(DeepSkyCatalog.Normalize(variant));
        }
        return [.. keys.Distinct()];
    }

    public static int Count => Stars.Value.Length;

    /// <summary>Stars within <paramref name="radiusDegrees"/> of a J2000 position, brightest first.</summary>
    public static IReadOnlyList<CatalogStar> Near(EquatorialCoordinates center, double radiusDegrees, double faintestMagnitude = 99)
    {
        var stars = Stars.Value; // sorted by declination
        var low = LowerBound(stars, center.DecDegrees - radiusDegrees);
        var result = new List<CatalogStar>();
        for (var i = low; i < stars.Length && stars[i].Position.DecDegrees <= center.DecDegrees + radiusDegrees; i++)
        {
            var star = stars[i];
            if (star.Magnitude <= faintestMagnitude && Precession.Separation(center, star.Position) <= radiusDegrees)
                result.Add(star);
        }
        result.Sort((a, b) => a.Magnitude.CompareTo(b.Magnitude));
        return result;
    }

    private static int LowerBound(CatalogStar[] stars, double dec)
    {
        int lo = 0, hi = stars.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (stars[mid].Position.DecDegrees < dec) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static CatalogStar[] Load()
    {
        using var resource = typeof(StarCatalog).Assembly.GetManifestResourceStream("Nova.Core.Catalogs.stars.tsv.gz")
                             ?? throw new InvalidOperationException("The star catalog is missing from Nova.Core");
        using var reader = new StreamReader(new GZipStream(resource, CompressionMode.Decompress));
        var stars = new List<CatalogStar>(120_000);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('#')) continue;
            var f = line.Split('\t');
            if (f.Length < 5) continue;
            stars.Add(new CatalogStar(f[3], f[4],
                new EquatorialCoordinates(double.Parse(f[0], CultureInfo.InvariantCulture), double.Parse(f[1], CultureInfo.InvariantCulture)),
                double.Parse(f[2], CultureInfo.InvariantCulture)));
        }
        return stars.ToArray();
    }
}
