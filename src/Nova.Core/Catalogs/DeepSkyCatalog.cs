using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Nova.Core.Astronomy;

namespace Nova.Core.Catalogs;

/// <param name="Label">The most familiar designation: M 31, NGC 7000, IC 434, Sh2-129, Caldwell 9…</param>
/// <param name="Names">Every designation and common name, the label first.</param>
/// <param name="Type">OpenNGC's type code: G, OCl, GCl, PN, HII, Neb, SNR, Cl+N, RfN, EmN, DrkN, GPair…</param>
/// <param name="Position">J2000.</param>
/// <param name="MajorArcmin">Apparent size, when known.</param>
/// <param name="AngleDegrees">Position angle of the major axis (north through east), when known.</param>
public sealed record DeepSkyObject(
    string Label, IReadOnlyList<string> Names, string Type, EquatorialCoordinates Position,
    double? MajorArcmin, double? MinorArcmin, double? AngleDegrees, double? Magnitude, double? SurfaceBrightness)
{
    /// <summary>The first common name ("Andromeda Galaxy"), if it has one.</summary>
    public string? CommonName => Names.FirstOrDefault(n => !DeepSkyCatalog.IsDesignation(n));

    public string TypeName => DeepSkyCatalog.TypeName(Type);

    /// <summary>Messier first, then Caldwell, then named objects, then the rest: what people mean by a short query.</summary>
    public int Fame => Names.Any(n => n.StartsWith("M ", StringComparison.Ordinal)) ? 0
        : Names.Any(n => n.StartsWith("Caldwell ", StringComparison.Ordinal)) ? 1
        : CommonName is not null ? 2 : 3;
}

/// <summary>
/// Offline deep-sky catalog: OpenNGC (NGC, IC, Messier and addenda, by Mattia Verga, CC BY-SA 4.0) plus the
/// Sharpless HII regions — about 12 500 objects, J2000. Built by tools/build_dso_catalog.py and embedded here.
/// </summary>
public static partial class DeepSkyCatalog
{
    private static readonly Lazy<Index> Data = new(Load);

    public static IReadOnlyList<DeepSkyObject> All => Data.Value.Objects;

    private sealed record Index(DeepSkyObject[] Objects, string[][] Keys);

    /// <summary>
    /// Objects matching a name or designation ("m31", "NGC 7000", "sh2-129", "north america"), best first:
    /// exact designations, then names starting with the query, then names containing it.
    /// </summary>
    public static IReadOnlyList<DeepSkyObject> Search(string query, int limit = 20)
    {
        var q = Normalize(query);
        if (q.Length == 0) return [];
        var data = Data.Value;
        var hits = new List<(int Rank, DeepSkyObject Object)>();
        for (var i = 0; i < data.Objects.Length; i++)
        {
            var rank = int.MaxValue;
            foreach (var key in data.Keys[i])
            {
                if (key == q) { rank = 0; break; }
                if (key.StartsWith(q, StringComparison.Ordinal)) rank = Math.Min(rank, 1);
                else if (q.Length >= 3 && key.Contains(q, StringComparison.Ordinal)) rank = Math.Min(rank, 2);
            }
            if (rank != int.MaxValue) hits.Add((rank, data.Objects[i]));
        }
        if (hits.Count == 0)
        {
            // Nothing: allow a typo or two ("andromda", "north amercia"), closest first.
            for (var i = 0; i < data.Objects.Length; i++)
                if (Fuzzy.Closest(q, data.Keys[i]) is { } d) hits.Add((3 + d, data.Objects[i]));
        }
        return hits
            .OrderBy(h => h.Rank)
            .ThenBy(h => h.Object.Fame)
            // "m3" means M 3 before M 31: shorter designations first, then the brighter.
            .ThenBy(h => h.Object.Label.Length)
            .ThenBy(h => h.Object.Magnitude ?? 99)
            .Take(limit)
            .Select(h => h.Object)
            .ToList();
    }

    /// <summary>Objects whose centre is within <paramref name="radiusDegrees"/>, biggest first.</summary>
    public static IReadOnlyList<DeepSkyObject> Near(EquatorialCoordinates center, double radiusDegrees)
    {
        var objects = Data.Value.Objects; // sorted by declination
        int lo = 0, hi = objects.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (objects[mid].Position.DecDegrees < center.DecDegrees - radiusDegrees) lo = mid + 1;
            else hi = mid;
        }
        var result = new List<DeepSkyObject>();
        for (var i = lo; i < objects.Length && objects[i].Position.DecDegrees <= center.DecDegrees + radiusDegrees; i++)
            if (Precession.Separation(center, objects[i].Position) <= radiusDegrees)
                result.Add(objects[i]);
        result.Sort((a, b) => (b.MajorArcmin ?? 0).CompareTo(a.MajorArcmin ?? 0));
        return result;
    }

    /// <summary>The object with exactly this label, if any.</summary>
    public static DeepSkyObject? Find(string label) => All.FirstOrDefault(o => o.Label.Equals(label, StringComparison.OrdinalIgnoreCase));

    public static string TypeName(string type) => type switch
    {
        "G" => "Galaxy",
        "GPair" => "Galaxy pair",
        "GTrpl" => "Galaxy triplet",
        "GGroup" => "Galaxy group",
        "OCl" => "Open cluster",
        "GCl" => "Globular cluster",
        "Cl+N" => "Cluster with nebula",
        "*Ass" => "Star association",
        "PN" => "Planetary nebula",
        "HII" => "Emission nebula",
        "EmN" => "Emission nebula",
        "RfN" => "Reflection nebula",
        "Neb" => "Nebula",
        "DrkN" => "Dark nebula",
        "SNR" => "Supernova remnant",
        _ => type,
    };

    /// <summary>Broad groups for filtering: galaxy, nebula, cluster.</summary>
    public static string Group(string type) => type switch
    {
        "G" or "GPair" or "GTrpl" or "GGroup" => "galaxy",
        "OCl" or "GCl" or "*Ass" => "cluster",
        "PN" => "planetary",
        _ => "nebula", // HII, EmN, RfN, Neb, DrkN, SNR, Cl+N
    };

    /// <summary>Catalogue designations (M 31, NGC 224, LBN 373, Sh2-129, Caldwell 20…) rather than common names.</summary>
    public static bool IsDesignation(string name) => Designation().IsMatch(name);

    [GeneratedRegex(@"^(M|NGC|IC|LBN|UGC|Mel|Cr|Cl|B|PN G|PN|PK|HCG|Abell|Caldwell|C|Sh2)[ -]?\d", RegexOptions.CultureInvariant)]
    private static partial Regex Designation();

    private static readonly Dictionary<char, string> Greek = new()
    {
        ['α'] = "alpha", ['β'] = "beta", ['γ'] = "gamma", ['δ'] = "delta", ['ε'] = "epsilon", ['ζ'] = "zeta",
        ['η'] = "eta", ['θ'] = "theta", ['ι'] = "iota", ['κ'] = "kappa", ['λ'] = "lambda", ['μ'] = "mu",
        ['ν'] = "nu", ['ξ'] = "xi", ['ο'] = "omicron", ['π'] = "pi", ['ρ'] = "rho", ['σ'] = "sigma",
        ['τ'] = "tau", ['υ'] = "upsilon", ['φ'] = "phi", ['χ'] = "chi", ['ψ'] = "psi", ['ω'] = "omega",
    };

    /// <summary>
    /// Lower case, without spaces and punctuation, Greek letters spelled out: "Sh2-129" and "sh2 129" both become
    /// "sh2129", "α Lyr" becomes "alphalyr", "ε¹ Lyr" "epsilon1lyr".
    /// </summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKD))
        {
            var lower = char.ToLowerInvariant(c);
            if (Greek.TryGetValue(lower, out var spelled)) sb.Append(spelled);
            else if (char.IsLetterOrDigit(c)) sb.Append(lower);
        }
        var s = sb.ToString();
        return s.StartsWith("messier", StringComparison.Ordinal) ? "m" + s[7..] : s;
    }

    private static Index Load()
    {
        using var resource = typeof(DeepSkyCatalog).Assembly.GetManifestResourceStream("Nova.Core.Catalogs.dso.tsv.gz")
                             ?? throw new InvalidOperationException("The deep-sky catalog is missing from Nova.Core");
        using var reader = new StreamReader(new GZipStream(resource, CompressionMode.Decompress));
        var objects = new List<DeepSkyObject>(13_000);
        var keys = new List<string[]>(13_000);
        static double? N(string s) => s.Length == 0 ? null : double.Parse(s, CultureInfo.InvariantCulture);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('#')) continue;
            var f = line.Split('\t');
            if (f.Length < 10) continue;
            var names = f[9].Split(" · ");
            objects.Add(new DeepSkyObject(f[8], names, f[7],
                new EquatorialCoordinates(double.Parse(f[0], CultureInfo.InvariantCulture), double.Parse(f[1], CultureInfo.InvariantCulture)),
                N(f[2]), N(f[3]), N(f[4]), N(f[5]), N(f[6])));
            var k = names.Select(Normalize).ToList();
            // Caldwell objects answer to "C 20" too.
            k.AddRange(names.Where(n => n.StartsWith("Caldwell ", StringComparison.Ordinal)).Select(n => "c" + n[9..]));
            keys.Add([.. k.Distinct()]);
        }
        return new Index([.. objects], [.. keys]);
    }
}
