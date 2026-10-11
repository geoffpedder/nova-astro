using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Nova.Core.Astronomy;
using Nova.Core.Catalogs;
using Nova.Core.Equipment;
using Nova.Server.Services;

namespace Nova.Server.Api;

/// <param name="Ra">J2000, as "hh:mm:ss.s" (what the sequencer and slews take).</param>
public sealed record TargetInfo(
    string Label, string? CommonName, IReadOnlyList<string> Names, string Type, string TypeName, string Group,
    double RaHours, double DecDegrees, string Ra, string Dec,
    double? MajorArcmin, double? MinorArcmin, double? AngleDegrees, double? Magnitude, double? SurfaceBrightness,
    Visibility? Visibility = null);

public sealed record NightInfo(
    DateTime Sunset, DateTime Sunrise, DateTime? NauticalDusk, DateTime? NauticalDawn, DateTime? AstronomicalDusk,
    DateTime? AstronomicalDawn, DateTime DarkStart, DateTime DarkEnd, double MoonIllumination, bool MoonWaxing);

public sealed record AltitudePoint(DateTime Utc, double Altitude, double MoonAltitude, double SunAltitude);

public sealed record AltitudeCurve(NightInfo Night, IReadOnlyList<AltitudePoint> Points);

public sealed record FieldStar(double RaHours, double DecDegrees, double Magnitude, string? Label);

public sealed record SkyTileDownloadRequest(int MaxOrder);

public sealed record SkyField(IReadOnlyList<FieldStar> Stars, IReadOnlyList<TargetInfo> Objects);

/// <summary>
/// The target picker and framing tool: searching the offline deep-sky catalog, rating what's well placed
/// tonight, altitude curves, what's in a field (for drawing it offline), and sky survey images (DSS, from
/// CDS's hips2fits service — fetched by the mini PC and cached on disk, so frames planned at home still
/// show in the field without internet).
/// </summary>
public static class TargetsApi
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Surveys the page may ask for (never an arbitrary URL).</summary>
    private static readonly HashSet<string> Surveys = ["CDS/P/DSS2/color", "CDS/P/DSS2/red", "CDS/P/Mellinger/color"];
    private static readonly HashSet<string> Projections = ["TAN", "STG"];

    public static void MapTargets(this RouteGroupBuilder api)
    {
        // Deep-sky objects and stars together: exact matches (of either) first, then the rest in catalog order.
        api.MapGet("/targets/search", (string? q, int? limit) =>
        {
            var n = Math.Clamp(limit ?? 20, 1, 100);
            var query = q ?? "";
            var exactKey = DeepSkyCatalog.Normalize(query);
            var objects = DeepSkyCatalog.Search(query, n)
                .Select((o, i) => (Rank: o.Names.Any(x => DeepSkyCatalog.Normalize(x) == exactKey) ? 0 : 1, Order: i, Info: Info(o)));
            var stars = StarCatalog.Search(query, n)
                .Select((s, i) => (Rank: s.Rank == 0 ? 0 : 1, Order: i + 1000, Info: Info(s.Star)));
            // The planets and the Moon, where they are now (they move: the position is for this moment).
            var now = DateTime.UtcNow;
            var bodies = Planets.At(now).Select(pl => Info(pl))
                .Append(MoonInfo(now))
                .Where(b => exactKey.Length > 0 && DeepSkyCatalog.Normalize(b.Label).StartsWith(exactKey, StringComparison.Ordinal))
                .Select(b => (Rank: DeepSkyCatalog.Normalize(b.Label) == exactKey ? 0 : 1, Order: -1, Info: b));
            return Results.Ok(bodies.Concat(objects).Concat(stars).OrderBy(h => h.Rank).ThenBy(h => h.Order).Take(n).Select(h => h.Info).ToList());
        });

        api.MapGet("/targets/night", (SiteService sites, EquipmentService equipment) =>
        {
            if (Site(sites, equipment) is not { } site) return NoSite();
            return Night.Around(DateTime.UtcNow, site) is { } night
                ? Results.Ok(ToInfo(night))
                : Results.Problem("The Sun doesn't set (or rise) here today", statusCode: StatusCodes.Status409Conflict);
        });

        // What's well placed tonight: hours of darkness above minAltitude, within the size and type limits.
        api.MapGet("/targets/tonight", (SiteService sites, EquipmentService equipment, string? groups, double? minSize,
            double? maxSize, double? maxMagnitude, double? minAltitude, double? minHours, double? minMoonDistance,
            string? sort, int? limit) =>
        {
            if (Site(sites, equipment) is not { } site) return NoSite();
            if (Night.Around(DateTime.UtcNow, site) is not { } night)
                return Results.Problem("The Sun doesn't set (or rise) here today", statusCode: StatusCodes.Status409Conflict);
            var wanted = (groups ?? "galaxy,nebula,cluster,planetary").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            var step = TimeSpan.FromMinutes(15);
            var samples = TargetVisibility.Samples(night, site, step);
            var middle = night.DarkStart + (night.DarkEnd - night.DarkStart) / 2;
            var moon = Precession.JNowToJ2000(SolarSystem.Moon(middle), middle);
            var low = minAltitude ?? 30;

            var rated = new List<(DeepSkyObject Object, Visibility Visibility)>();
            foreach (var o in DeepSkyCatalog.All)
            {
                if (!wanted.Contains(DeepSkyCatalog.Group(o.Type))) continue;
                var size = o.MajorArcmin ?? 0;
                if (size < (minSize ?? 0) || (maxSize is { } most && size > most)) continue;
                if (maxMagnitude is { } faintest && o.Magnitude is { } m && m > faintest) continue;
                // Objects with no magnitude are mostly faint galaxies; nebulae rarely have one, so keep those.
                if (maxMagnitude is not null && o.Magnitude is null && DeepSkyCatalog.Group(o.Type) == "galaxy") continue;
                var visibility = TargetVisibility.For(o.Position, samples, site, low, moon, step);
                if (visibility.HoursAbove < (minHours ?? 1)) continue;
                if (visibility.MoonSeparation < (minMoonDistance ?? 0)) continue;
                rated.Add((o, visibility));
            }

            IEnumerable<(DeepSkyObject Object, Visibility Visibility)> ordered = sort switch
            {
                "size" => rated.OrderByDescending(r => r.Object.MajorArcmin ?? 0),
                "brightness" => rated.OrderBy(r => r.Object.Magnitude ?? 99),
                "transit" => rated.OrderBy(r => r.Visibility.BestUtc),
                "altitude" => rated.OrderByDescending(r => r.Visibility.MaxAltitude),
                // "best": famous and big things that are up all night first.
                _ => rated.OrderByDescending(r => r.Visibility.HoursAbove * (4 - r.Object.Fame)
                                                   * Math.Log(2 + (r.Object.MajorArcmin ?? 1))),
            };
            return Results.Ok(new
            {
                Night = ToInfo(night),
                Count = rated.Count,
                Targets = ordered.Take(Math.Clamp(limit ?? 100, 1, 500)).Select(r => Info(r.Object, r.Visibility)).ToList(),
            });
        });

        // A target's altitude through the night (sunset to sunrise, with an hour either side), and the Moon's.
        api.MapGet("/targets/altitude", (SiteService sites, EquipmentService equipment, string ra, string dec) =>
        {
            if (Site(sites, equipment) is not { } site) return NoSite();
            if (!TryParse(ra, dec, out var target, out var problem)) return problem;
            if (Night.Around(DateTime.UtcNow, site) is not { } night)
                return Results.Problem("The Sun doesn't set (or rise) here today", statusCode: StatusCodes.Status409Conflict);
            var points = new List<AltitudePoint>();
            for (var t = night.Sunset.AddHours(-1); t <= night.Sunrise.AddHours(1); t = t.AddMinutes(10))
            {
                var jnow = Precession.J2000ToJNow(target, t);
                points.Add(new AltitudePoint(t,
                    Math.Round(Horizon.AltAz(Horizon.ToVector(jnow, t, site)).Altitude, 2),
                    Math.Round(Night.MoonAltitude(t, site), 2),
                    Math.Round(Night.SunAltitude(t, site), 2)));
            }
            return Results.Ok(new AltitudeCurve(ToInfo(night), points));
        });

        // Stars and deep-sky objects around a point: enough to draw a field when there's no survey image.
        // Stars and deep-sky objects around a point, for drawing the sky chart. A radius of 180 is the whole sky.
        // objects=famous keeps Messier, Caldwell and named objects (for wide views).
        api.MapGet("/sky/field", (string ra, string dec, double? radius, double? magnitude, int? maxStars, string? objects, int? maxObjects) =>
        {
            if (!TryParse(ra, dec, out var center, out var problem)) return problem;
            var r = Math.Clamp(radius ?? 2, 0.05, 180);
            // Fainter stars for smaller fields, but never so many the page struggles.
            var faintest = magnitude ?? Math.Clamp(12.5 - 2.5 * Math.Log10(r * r), 5, 12.5);
            var stars = StarCatalog.Near(center, r, faintest).Take(Math.Clamp(maxStars ?? 3000, 1, 20_000))
                .Select(s => new FieldStar(Math.Round(s.Position.RaHours, 5), Math.Round(s.Position.DecDegrees, 4), s.Magnitude,
                    s.Magnitude < 6.5 ? s.Label : null))
                .ToList();
            var famous = objects == "famous";
            var found = DeepSkyCatalog.Near(center, Math.Min(180, r * 1.2))
                .Where(o => !famous || o.Fame <= 2)
                .Take(Math.Clamp(maxObjects ?? 200, 0, 3000))
                .Select(o => Info(o))
                .ToList();
            return Results.Ok(new SkyField(stars, found));
        });

        // The stored sky survey (HiPS tiles), for the sky chart: served from disk, else fetched once and kept.
        api.MapGet("/sky/tiles/{order:int}/{npix:long}.jpg", async (HttpContext http, SkyTileService tiles, int order, long npix, CancellationToken ct) =>
            await tiles.GetTileAsync(order, npix, ct) is { } bytes ? Tile(http, bytes) : Results.NotFound());
        api.MapGet("/sky/tiles/allsky.jpg", async (HttpContext http, SkyTileService tiles, CancellationToken ct) =>
            await tiles.GetAllskyAsync(ct) is { } bytes ? Tile(http, bytes) : Results.NotFound());
        api.MapGet("/sky/tiles/status", (SkyTileService tiles) => Results.Ok(tiles.Status));
        api.MapPost("/sky/tiles/download", (SkyTileDownloadRequest request, SkyTileService tiles) =>
            tiles.StartDownload(request.MaxOrder, out var error)
                ? Results.Accepted()
                : Results.Problem(error, statusCode: StatusCodes.Status409Conflict));
        api.MapPost("/sky/tiles/cancel", (SkyTileService tiles) =>
        {
            tiles.CancelDownload();
            return Results.NoContent();
        });

        // The Sun and Moon now (J2000), for the sky chart.
        api.MapGet("/sky/bodies", () =>
        {
            var now = DateTime.UtcNow;
            var sun = Precession.JNowToJ2000(SolarSystem.Sun(now), now);
            var moon = Precession.JNowToJ2000(SolarSystem.Moon(now), now);
            return Results.Ok(new
            {
                Utc = now,
                Planets = Planets.At(now).Select(pl => new
                {
                    pl.Name, pl.Position.RaHours, pl.Position.DecDegrees, pl.Magnitude, pl.DiameterArcsec, pl.DistanceAu,
                }),
                Sun = new { sun.RaHours, sun.DecDegrees },
                Moon = new { moon.RaHours, moon.DecDegrees, Illumination = Math.Round(SolarSystem.MoonIllumination(now), 3), Waxing = SolarSystem.MoonWaxing(now) },
            });
        });

        // A survey image, square, north up, gnomonic (TAN) or stereographic (STG) projection: fov is the side in degrees.
        api.MapGet("/sky/survey", async (IOptions<NovaOptions> options, ActivityLog log, double ra, double dec, double fov,
            int? size, string? survey, string? projection, CancellationToken ct) =>
        {
            var hips = survey ?? "CDS/P/DSS2/color";
            if (!Surveys.Contains(hips)) return Results.Problem("Unknown survey", statusCode: StatusCodes.Status400BadRequest);
            var proj = projection ?? "TAN";
            if (!Projections.Contains(proj)) return Results.Problem("Unknown projection", statusCode: StatusCodes.Status400BadRequest);
            if (ra is < 0 or >= 24 || dec is < -90 or > 90 || fov is <= 0 or > 180)
                return Results.Problem("Invalid position or field", statusCode: StatusCodes.Status400BadRequest);
            var pixels = Math.Clamp(size ?? 1024, 256, 2048);
            string F(double v, int places) => Math.Round(v, places).ToString(CultureInfo.InvariantCulture);
            var query = $"hips={Uri.EscapeDataString(hips)}&width={pixels}&height={pixels}&fov={F(fov, 4)}&projection={proj}" +
                        $"&coordsys=icrs&ra={F(ra * 15, 5)}&dec={F(dec, 5)}&format=jpg";

            var folder = Path.Combine(options.Value.ResolveConfigDirectory(), "survey-cache");
            var file = Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query)))[..32] + ".jpg");
            if (File.Exists(file)) return Results.File(await File.ReadAllBytesAsync(file, ct), "image/jpeg");

            try
            {
                using var response = await Http.GetAsync("https://alasky.cds.unistra.fr/hips-image-services/hips2fits?" + query, ct);
                if (!response.IsSuccessStatusCode)
                    return Results.Problem($"The survey service answered {(int)response.StatusCode}", statusCode: StatusCodes.Status502BadGateway);
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                Directory.CreateDirectory(folder);
                await File.WriteAllBytesAsync(file, bytes, ct);
                return Results.File(bytes, "image/jpeg");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // No internet (normal in the field): the page draws the catalog stars instead.
                return Results.Problem($"No survey image ({ex.Message})", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }

    // Tiles never change: let the browser keep them.
    private static IResult Tile(HttpContext http, byte[] bytes)
    {
        http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return Results.File(bytes, "image/jpeg");
    }

    private static GeoLocation? Site(SiteService sites, EquipmentService equipment) =>
        sites.ActiveSite?.Location ?? (equipment.Mount is { IsConnected: true } mount ? mount.Location : null);

    private static IResult NoSite() =>
        Results.Problem("Set your site (latitude and longitude) first", statusCode: StatusCodes.Status409Conflict);

    private static bool TryParse(string ra, string dec, out EquatorialCoordinates target, out IResult problem)
    {
        try
        {
            target = EquatorialCoordinates.Parse(ra, dec);
            problem = Results.Empty;
            return true;
        }
        catch (FormatException ex)
        {
            target = default;
            problem = Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            return false;
        }
    }

    public static TargetInfo Info(DeepSkyObject o, Visibility? visibility = null) => new(
        o.Label, o.CommonName, o.Names, o.Type, o.TypeName, DeepSkyCatalog.Group(o.Type),
        Math.Round(o.Position.RaHours, 6), Math.Round(o.Position.DecDegrees, 5),
        Sexagesimal.Format(o.Position.RaHours, 'h'), Sexagesimal.Format(o.Position.DecDegrees, 'd', signed: true),
        o.MajorArcmin, o.MinorArcmin, o.AngleDegrees, o.Magnitude, o.SurfaceBrightness, visibility);

    /// <summary>A planet as a target (its position now), sized by its disc.</summary>
    public static TargetInfo Info(PlanetPosition pl) => new(
        pl.Name, null, [pl.Name], "Planet", pl.Name == "Pluto" ? "Dwarf planet" : "Planet", "planet",
        Math.Round(pl.Position.RaHours, 6), Math.Round(pl.Position.DecDegrees, 5),
        Sexagesimal.Format(pl.Position.RaHours, 'h'), Sexagesimal.Format(pl.Position.DecDegrees, 'd', signed: true),
        Math.Round(pl.DiameterArcsec / 60, 3), null, null, pl.Magnitude, null);

    private static TargetInfo MoonInfo(DateTime utc)
    {
        var moon = Precession.JNowToJ2000(SolarSystem.Moon(utc), utc);
        return new TargetInfo("Moon", $"{Math.Round(SolarSystem.MoonIllumination(utc) * 100)}% lit", ["Moon"], "Moon", "Moon", "planet",
            Math.Round(moon.RaHours, 6), Math.Round(moon.DecDegrees, 5),
            Sexagesimal.Format(moon.RaHours, 'h'), Sexagesimal.Format(moon.DecDegrees, 'd', signed: true),
            31, null, null, -12.7, null);
    }

    /// <summary>A star as a target: its familiar name, with its first other designation as the "common name".</summary>
    public static TargetInfo Info(CatalogStar s)
    {
        var names = new[] { s.Label }.Concat(s.Detail.Split(" · ", StringSplitOptions.RemoveEmptyEntries)).ToList();
        return new TargetInfo(s.Label, names.Skip(1).FirstOrDefault(), names, "*", "Star", "star",
            Math.Round(s.Position.RaHours, 6), Math.Round(s.Position.DecDegrees, 5),
            Sexagesimal.Format(s.Position.RaHours, 'h'), Sexagesimal.Format(s.Position.DecDegrees, 'd', signed: true),
            null, null, null, s.Magnitude, null);
    }

    private static NightInfo ToInfo(NightTimes n) => new(n.Sunset, n.Sunrise, n.NauticalDusk, n.NauticalDawn,
        n.AstronomicalDusk, n.AstronomicalDawn, n.DarkStart, n.DarkEnd, n.MoonIllumination, n.MoonWaxing);
}
