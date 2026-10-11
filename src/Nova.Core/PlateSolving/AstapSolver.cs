using System.Diagnostics;
using System.Globalization;
using Nova.Core.Astronomy;

namespace Nova.Core.PlateSolving;

/// <summary>
/// Plate solving with ASTAP's command line (https://www.hnsky.org/astap.htm). Arch: <c>yay -S astap</c> plus a
/// star database such as <c>d20-star-db-astap</c>; both install to /opt/astap. Option and result formats are
/// from ASTAP's source (astap_main.pas): -ra hours, -spd = dec + 90, -fov = image height in degrees,
/// -r = search radius; the answer is written to an .ini next to the image.
/// </summary>
public sealed class AstapSolver : IPlateSolver
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private static readonly string[] Candidates =
    [
        "/opt/astap/astap", "/usr/bin/astap", "/usr/local/bin/astap", "/opt/astap/astap_cli", "/usr/bin/astap_cli",
        @"C:\Program Files\astap\astap.exe", @"C:\Program Files\astap\astap_cli.exe",
    ];

    private readonly string? _executable;
    private readonly string? _databaseFolder;
    private readonly string? _database;

    /// <param name="executable">ASTAP to run; null or empty to look in the usual places.</param>
    /// <param name="databaseFolder">Where the star databases are; null for ASTAP's own folder (e.g. /opt/astap).</param>
    /// <param name="database">Which database, e.g. "d20"; null to let ASTAP choose from what's installed.</param>
    public AstapSolver(string? executable = null, string? databaseFolder = null, string? database = null)
    {
        _executable = string.IsNullOrWhiteSpace(executable) ? Candidates.FirstOrDefault(File.Exists) : executable;
        _database = string.IsNullOrWhiteSpace(database) ? null : database;
        _databaseFolder = !string.IsNullOrWhiteSpace(databaseFolder)
            ? databaseFolder
            // /usr/bin/astap is a link: the databases sit with the real program in /opt/astap.
            : _executable is null ? null : Path.GetDirectoryName(ResolveLink(_executable));
    }

    public string Name => "ASTAP";

    public string? Problem
    {
        get
        {
            if (_executable is null || !File.Exists(_executable))
                return "ASTAP isn't installed. Running nova setup installs it (on Arch: yay -S astap d20-star-db-astap)";
            if (_databaseFolder is null || !Directory.Exists(_databaseFolder) || !HasDatabase(_databaseFolder))
                return $"No ASTAP star database in {_databaseFolder}. Running nova setup installs one (on Arch: yay -S d20-star-db-astap, or d05/d50)";
            return null;
        }
    }

    public bool Available => Problem is null;

    public async Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        if (Problem is { } problem) return new PlateSolveResult(false, null, problem, clock.Elapsed);

        var (result, stars) = await RunAsync(request, ct);
        // ASTAP ignores "stars" smaller than 1.5″, sized using the field we tell it: a wrong focal length or pixel
        // size makes real stars look too small to count. Try again letting ASTAP work the field out itself.
        if (!result.Solved && request.FieldHeightDegrees is > 0 && IsNotEnoughStars(result.Error))
        {
            var (retry, retryStars) = await RunAsync(request with { FieldHeightDegrees = 0 }, ct);
            if (retry.Solved || retryStars > stars) (result, stars) = (retry, retryStars);
        }
        if (!result.Solved && stars is { } found && IsNotEnoughStars(result.Error))
            result = result with { Error = $"Not enough stars: ASTAP found {found} it could use (it needs about 10)." };
        return result with { Duration = clock.Elapsed };
    }

    private static bool IsNotEnoughStars(string? error) => error?.Contains("enough stars", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>One ASTAP run: its answer, and how many stars it found (from its log), when it says.</summary>
    private async Task<(PlateSolveResult Result, int? Stars)> RunAsync(PlateSolveRequest request, CancellationToken ct)
    {
        var ini = Path.ChangeExtension(request.FitsPath, ".ini");
        var log = Path.ChangeExtension(request.FitsPath, ".log");
        File.Delete(ini); // never read an answer left over from an earlier solve
        File.Delete(log);
        File.Delete(Path.ChangeExtension(request.FitsPath, ".wcs"));

        var start = new ProcessStartInfo(_executable!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in Arguments(request)) start.ArgumentList.Add(argument);
        // Qt builds of ASTAP want a display even on the command line; run them without one.
        start.Environment["QT_QPA_PLATFORM"] = "offscreen";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Couldn't start ASTAP");
        _ = process.StandardOutput.ReadToEndAsync(ct);
        _ = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            return (new PlateSolveResult(false, null, $"ASTAP took longer than {Timeout.TotalMinutes:0} minutes", TimeSpan.Zero), null);
        }

        var text = File.Exists(ini) ? await File.ReadAllTextAsync(ini, ct) : "";
        var stars = File.Exists(log) ? StarsFound(await File.ReadAllTextAsync(log, ct)) : null;
        try { File.Delete(log); } catch (IOException) { /* best effort */ }
        return (ParseResult(text, process.ExitCode), stars);
    }

    /// <summary>Stars ASTAP found, from its log: "Only 3 stars found in image" or "29 stars, 248 quads selected".</summary>
    public static int? StarsFound(string log)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(log, @"(?:Only )?(\d+) stars(?: found|,)");
        return matches.Count == 0 ? null : int.Parse(matches[^1].Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>The command line for a request (exposed for tests).</summary>
    public IReadOnlyList<string> Arguments(PlateSolveRequest request)
    {
        var args = new List<string> { "-f", request.FitsPath, "-r", F(request.SearchRadiusDegrees), "-z", "0" };
        if (request.Hint is { } hint)
        {
            args.AddRange(["-ra", F(hint.RaHours), "-spd", F(hint.DecDegrees + 90)]);
        }
        if (request.FieldHeightDegrees is >= 0 and var fov) args.AddRange(["-fov", F(fov)]); // 0: ASTAP works it out
        if (_databaseFolder is not null) args.AddRange(["-d", _databaseFolder]);
        if (_database is not null) args.AddRange(["-D", _database]);
        args.Add("-log"); // how many stars it found, for the error message
        return args;
    }

    /// <summary>Reads ASTAP's .ini answer (keys as written by write_ini in astap_main.pas).</summary>
    public static PlateSolveResult ParseResult(string ini, int exitCode)
    {
        var values = ini.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains('='))
            .Select(line => (Key: line[..line.IndexOf('=')].Trim(), Value: line[(line.IndexOf('=') + 1)..].Trim()))
            .GroupBy(kv => kv.Key)
            .ToDictionary(g => g.Key, g => g.First().Value);

        if (values.GetValueOrDefault("PLTSOLVD") != "T")
        {
            var error = values.GetValueOrDefault("ERROR") ?? exitCode switch
            {
                1 => "No solution found. Check the focal length and that the image has stars.",
                2 => "Not enough stars.",
                16 => "ASTAP couldn't read the image.",
                32 => "No star database found.",
                33 => "Error reading the star database.",
                _ => ini.Length == 0 ? $"ASTAP gave no answer (exit code {exitCode})." : "No solution found.",
            };
            return new PlateSolveResult(false, null, error, TimeSpan.Zero);
        }

        double N(string key) => values.TryGetValue(key, out var v) ? ParseNumber(v) : 0;
        var cd11 = N("CD1_1");
        var cd12 = N("CD1_2");
        var cd21 = N("CD2_1");
        var cd22 = N("CD2_2");
        var scale = Math.Abs(N("CDELT2")) * 3600;
        if (scale == 0) scale = Math.Sqrt(Math.Abs(cd11 * cd22 - cd12 * cd21)) * 3600;

        var (width, height) = (0.0, 0.0);
        var (pixelsWide, pixelsHigh) = (0, 0);
        if (values.TryGetValue("DIMENSIONS", out var dims) && dims.Split('x') is [var w, var h]
            && int.TryParse(w.Trim(), out var pw) && int.TryParse(h.Trim(), out var ph))
        {
            (width, height) = (pw * scale / 3600, ph * scale / 3600);
            (pixelsWide, pixelsHigh) = (pw, ph);
        }

        // Older ASTAP versions write only CDELT/CROTA: build the CD matrix from them.
        if (cd11 == 0 && cd12 == 0 && cd21 == 0 && cd22 == 0 && N("CDELT1") != 0)
        {
            var rho = N("CROTA2") * Math.PI / 180;
            (cd11, cd12) = (N("CDELT1") * Math.Cos(rho), -N("CDELT2") * Math.Sin(rho));
            (cd21, cd22) = (N("CDELT1") * Math.Sin(rho), N("CDELT2") * Math.Cos(rho));
        }
        var crpix1 = values.ContainsKey("CRPIX1") ? N("CRPIX1") : (pixelsWide + 1) / 2.0;
        var crpix2 = values.ContainsKey("CRPIX2") ? N("CRPIX2") : (pixelsHigh + 1) / 2.0;
        Wcs? wcs = cd11 * cd22 - cd12 * cd21 != 0 && (crpix1 > 0.5 || values.ContainsKey("CRPIX1"))
            ? new Wcs(crpix1, crpix2, N("CRVAL1"), N("CRVAL2"), cd11, cd12, cd21, cd22)
            : null;

        var ra = N("CRVAL1") / 15 % 24;
        var solution = new PlateSolution(
            new EquatorialCoordinates(ra < 0 ? ra + 24 : ra, N("CRVAL2")),
            Rotation: (N("CROTA2") % 360 + 360) % 360,
            ImageScale: scale,
            // A normal sky image has a negative CD determinant (east left with north up); positive means mirrored.
            Flipped: cd11 * cd22 - cd12 * cd21 > 0,
            FieldWidthDegrees: width,
            FieldHeightDegrees: height,
            Wcs: wcs);
        return new PlateSolveResult(true, solution, null, TimeSpan.Zero);
    }

    private static double ParseNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v
        : double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v
        : 0;

    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static bool HasDatabase(string folder) =>
        // ASTAP databases are many small files named like d20_0101.1476, w08_0101.001, g05_*.290 …
        Directory.EnumerateFiles(folder).Any(f => System.Text.RegularExpressions.Regex.IsMatch(
            Path.GetFileName(f), @"^[a-z]\d\d_\d{4}\.\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    private static string ResolveLink(string path)
    {
        try
        {
            return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        }
        catch
        {
            return path;
        }
    }
}
