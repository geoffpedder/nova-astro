namespace Nova.Core.Catalogs;

/// <summary>Forgiving name matching: how many typos (letters added, missing or changed) apart two names are.</summary>
public static class Fuzzy
{
    /// <summary>Typos allowed for a query this long: none for short ones (too many accidental matches), then 1, then 2.</summary>
    public static int Allowed(int length) => length < 5 ? 0 : length < 9 ? 1 : 2;

    /// <summary>
    /// The fewest typos between a (normalised) query and any of the keys — whole, or the start of a longer key
    /// as far as the query goes — or null if more than allowed.
    /// </summary>
    public static int? Closest(string query, IEnumerable<string> keys)
    {
        var max = Allowed(query.Length);
        if (max == 0) return null;
        int? best = null;
        foreach (var key in keys)
        {
            var d = Distance(query, key, max);
            // A typo in the first part of a longer name: compare with its start, give or take the letters a typo shifts.
            for (var length = query.Length - max; length <= query.Length + max && length < key.Length; length++)
                if (length > 0) d = Math.Min(d, Distance(query, key[..length], max));
            if (d <= max && (best is null || d < best)) best = d;
        }
        return best;
    }

    /// <summary>Levenshtein distance, giving up (returning max + 1) once it's over <paramref name="max"/>.</summary>
    public static int Distance(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }
            if (rowMin > max) return max + 1;
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
