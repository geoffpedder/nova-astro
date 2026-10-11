using Nova.Core.Astronomy;
using Nova.Core.Catalogs;
using Nova.Core.Equipment;
using Nova.Server.Services;

namespace Nova.Tests;

public class SolarSystemTests
{
    private static readonly GeoLocation Victoria = new(48.5197, -123.4167, 0);

    [Fact]
    public void PutsTheSunAtTheSolsticesAndEquinox()
    {
        Assert.Equal(23.44, SolarSystem.Sun(new DateTime(2026, 6, 21, 8, 0, 0, DateTimeKind.Utc)).DecDegrees, 1);
        Assert.Equal(-23.44, SolarSystem.Sun(new DateTime(2026, 12, 21, 20, 0, 0, DateTimeKind.Utc)).DecDegrees, 1);
        var march = SolarSystem.Sun(new DateTime(2026, 3, 20, 14, 46, 0, DateTimeKind.Utc));
        Assert.InRange(march.DecDegrees, -0.1, 0.1);
        Assert.True(march.RaHours is < 0.05 or > 23.95);
    }

    [Fact]
    public void KnowsTheMoonsPhase()
    {
        // Full Moon 2026-09-26 16:49 UTC, new Moon 2026-10-10 15:50 UTC.
        Assert.True(SolarSystem.MoonIllumination(new DateTime(2026, 9, 26, 16, 49, 0, DateTimeKind.Utc)) > 0.98);
        Assert.True(SolarSystem.MoonIllumination(new DateTime(2026, 10, 10, 15, 50, 0, DateTimeKind.Utc)) < 0.02);
        Assert.False(SolarSystem.MoonWaxing(new DateTime(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc)));
        Assert.True(SolarSystem.MoonWaxing(new DateTime(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void FindsTonightsTwilight()
    {
        // Early October in Victoria, BC (the Dominion Astrophysical Observatory): sunset about 18:50 PDT (01:50 UTC), astronomical dark about 90 minutes later.
        var night = Night.Around(new DateTime(2026, 10, 5, 22, 0, 0, DateTimeKind.Utc), Victoria)!;
        Assert.InRange(night.Sunset, new DateTime(2026, 10, 6, 1, 15, 0, DateTimeKind.Utc), new DateTime(2026, 10, 6, 2, 10, 0, DateTimeKind.Utc));
        Assert.InRange(night.Sunrise, new DateTime(2026, 10, 6, 13, 50, 0, DateTimeKind.Utc), new DateTime(2026, 10, 6, 14, 45, 0, DateTimeKind.Utc));
        Assert.InRange((night.AstronomicalDusk!.Value - night.Sunset).TotalMinutes, 80, 120);
        Assert.True(night.NauticalDusk < night.AstronomicalDusk && night.AstronomicalDawn < night.NauticalDawn);

        // Asked in the middle of that night, it's the same night.
        var during = Night.Around(new DateTime(2026, 10, 6, 7, 0, 0, DateTimeKind.Utc), Victoria)!;
        Assert.Equal(night.Sunset, during.Sunset, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void ShortNorthernSummerNightsHaveNoAstronomicalDark()
    {
        var night = Night.Around(new DateTime(2026, 6, 21, 18, 0, 0, DateTimeKind.Utc), new GeoLocation(55, 0, 0))!;
        Assert.Null(night.AstronomicalDusk);
        Assert.Equal(night.NauticalDusk ?? night.Sunset, night.DarkStart);
    }
}

public class DeepSkyCatalogTests
{
    [Theory]
    [InlineData("m31", "M 31")]
    [InlineData("M 42", "M 42")]
    [InlineData("messier 45", "M 45")]
    [InlineData("m3", "M 3")]
    [InlineData("NGC7000", "NGC 7000")]
    [InlineData("north america", "NGC 7000")]
    [InlineData("c20", "NGC 7000")]
    [InlineData("sh2 129", "Sh2-129")]
    [InlineData("Sh2-155", "Caldwell 9")]
    [InlineData("heart", "IC 1805")]
    [InlineData("andromeda", "M 31")]
    [InlineData("andromda", "M 31")]             // a typo
    [InlineData("north amercia", "NGC 7000")]
    public void FindsTargetsByAnyName(string query, string expected)
    {
        Assert.Equal(expected, DeepSkyCatalog.Search(query).First().Label);
    }

    [Theory]
    [InlineData("vega", "Vega")]
    [InlineData("Polaris", "Polaris")]
    [InlineData("alpha lyr", "Vega")]
    [InlineData("α UMi", "Polaris")]
    [InlineData("epsilon lyr", "ε² Lyr")]
    [InlineData("HD 172167", "Vega")]
    [InlineData("albir", "Albireo")]
    [InlineData("alpha centauri", "Rigil Kentaurus")]
    [InlineData("alpha centuri", "Rigil Kentaurus")] // a typo
    [InlineData("beta orionis", "Rigel")]
    [InlineData("alpha ursae minoris", "Polaris")]
    [InlineData("betelguese", "Betelgeuse")]         // a common misspelling
    public void FindsStarsByNameOrDesignation(string query, string expected)
    {
        Assert.Equal(expected, StarCatalog.Search(query).First().Star.Label);
    }

    [Fact]
    public void DoesntBuryShortQueriesInFaintStars()
    {
        // "hd 1" could prefix thousands of HD numbers: only bright stars match partially.
        Assert.All(StarCatalog.Search("hd 1", 50), h => Assert.True(h.Rank == 0 || h.Star.Magnitude <= 6.5));
    }

    [Fact]
    public void KnowsSizesAndTypes()
    {
        var m31 = DeepSkyCatalog.Find("M 31")!;
        Assert.Equal("Galaxy", m31.TypeName);
        Assert.Equal("Andromeda Galaxy", m31.CommonName);
        Assert.Equal("Ring Nebula", DeepSkyCatalog.Find("M 57")!.CommonName);
        Assert.InRange(m31.MajorArcmin!.Value, 150, 200);
        Assert.Equal(0.712, m31.Position.RaHours, 2);
        Assert.InRange(DeepSkyCatalog.All.Count, 12_000, 13_000);
    }

    [Fact]
    public void ListsWhatsInAField()
    {
        var near = DeepSkyCatalog.Near(new EquatorialCoordinates(0.712, 41.27), 1.5);
        Assert.Contains(near, o => o.Label == "M 31");
        Assert.Contains(near, o => o.Label == "M 110");
        Assert.Equal("M 31", near[0].Label); // biggest first
    }

    [Fact]
    public void RatesWhatsUpTonight()
    {
        var site = new GeoLocation(48.5197, -123.4167, 0);
        var night = Night.Around(new DateTime(2026, 10, 5, 22, 0, 0, DateTimeKind.Utc), site)!;
        var step = TimeSpan.FromMinutes(15);
        var samples = TargetVisibility.Samples(night, site, step);
        var moon = SolarSystem.Moon(night.DarkStart);
        var m31 = TargetVisibility.For(DeepSkyCatalog.Find("M 31")!.Position, samples, site, 30, moon, step);
        var m42 = TargetVisibility.For(DeepSkyCatalog.Find("M 42")!.Position, samples, site, 30, moon, step);
        Assert.True(m31.HoursAbove > 7, $"M 31 up {m31.HoursAbove} h");
        Assert.Equal(82.7, m31.MaxAltitude, 0.3); // transits in the dark, 90 - (48.5 - 41.3) degrees up
        Assert.InRange(m42.HoursAbove, 0.5, 4);    // a pre-dawn object in October
    }
}

public class SkyTileTests
{
    [Fact]
    public void CountsTilesPerLevel()
    {
        Assert.Equal(12, SkyTileService.TilesIn(0));
        Assert.Equal(12288, SkyTileService.TilesIn(5));
        Assert.Equal(16380, SkyTileService.TilesUpTo(5));
    }

    [Fact]
    public async Task RefusesTilesThatDontExist()
    {
        var config = Path.Combine(Path.GetTempPath(), "nova-tiles-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new NovaOptions { ConfigDirectory = config });
            var log = new ActivityLog(new NullHubContext(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ActivityLog>.Instance);
            var tiles = new SkyTileService(options, log);
            Assert.Null(await tiles.GetTileAsync(10, 0, CancellationToken.None));          // deeper than the survey
            Assert.Null(await tiles.GetTileAsync(2, 192, CancellationToken.None));         // level 2 has 192 tiles: 0–191
            Assert.False(tiles.StartDownload(9, out var error));                          // too much to download
            Assert.NotNull(error);
            Assert.Equal(0, tiles.Status.StoredTiles);
        }
        finally
        {
            if (Directory.Exists(config)) Directory.Delete(config, recursive: true);
        }
    }
}

public class NicknameTests
{
    [Fact]
    public void RemembersRenamesAndClearsNicknames()
    {
        var config = Path.Combine(Path.GetTempPath(), "nova-nick-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new NovaOptions { ConfigDirectory = config });
            var log = new ActivityLog(new NullHubContext(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ActivityLog>.Instance);
            var names = new NicknameService(options, log);
            names.Set("ZWO CCD ASI120MM Mini", "  Guide cam ");
            Assert.Equal("Guide cam", names.All["ZWO CCD ASI120MM Mini"]);

            // Kept across restarts.
            Assert.Equal("Guide cam", new NicknameService(options, log).All["ZWO CCD ASI120MM Mini"]);

            names.Set("ZWO CCD ASI120MM Mini", "");
            Assert.False(names.All.ContainsKey("ZWO CCD ASI120MM Mini"));
            Assert.Throws<ArgumentException>(() => names.Set("ZWO AM5", new string('x', 41)));
        }
        finally
        {
            if (Directory.Exists(config)) Directory.Delete(config, recursive: true);
        }
    }
}

public class SunSafeRouteTests
{
    private static readonly RoutePoint NoonSun = new(0, -5); // due south at noon in October

    [Fact]
    public void GoesRoundTheSunWhenTheDirectPathCrossesIt()
    {
        // Across the southern sky from south-east to south-west, straight through the noon Sun.
        var from = new RoutePoint(-3, -5);
        var to = new RoutePoint(3, -5);
        Assert.False(SunSafeRoute.DirectIsSafe(from, to, NoonSun, 30));

        var route = SunSafeRoute.Plan(from, to, NoonSun, 30, latitude: 49)!;
        Assert.Equal(3, route.Count);
        Assert.Equal(to, route[^1]);
        Assert.Equal(SunSafeRoute.DetourDec, route[0].DecDegrees);
        // Every leg, taken straight, keeps 30° from the Sun.
        var legs = new[] { (from, route[0]), (route[0], route[1]), (route[1], route[2]) };
        Assert.All(legs, leg => Assert.All(SunSafeRoute.Linear(leg.Item1, leg.Item2), p => Assert.True(SunSafeRoute.Separation(p, NoonSun) >= 30)));
    }

    [Fact]
    public void SlewsStraightWhenThatsSafe()
    {
        var route = SunSafeRoute.Plan(new RoutePoint(-3, 60), new RoutePoint(-2, 45), NoonSun, 30, latitude: 49)!;
        Assert.Single(route);
    }

    [Fact]
    public void RefusesWhenTheSunIsBetweenTheTargetAndThePole()
    {
        // Straight below the Sun: any way up to the pole passes through it.
        Assert.Null(SunSafeRoute.Plan(new RoutePoint(-4, 50), new RoutePoint(0.2, -60), NoonSun, 30, latitude: 49));
    }

    [Fact]
    public void ChecksThePierFlipSwingOverThePole()
    {
        // A short hop across the meridian high up: straight it's fine, and over the pole too.
        Assert.True(SunSafeRoute.DirectIsSafe(new RoutePoint(-0.5, 70), new RoutePoint(0.5, 70), NoonSun, 30));
    }
}

public class PlanetTests
{
    [Fact]
    public void PutsVenusWhereMeeusDoes()
    {
        // Meeus, Astronomical Algorithms, example 33.a: Venus on 1992 December 20, 0h TD,
        // apparent RA 21h04m41.454s, Dec −18°53′16.84″ (of date). Ours is J2000 and approximate: within 0.1°.
        var utc = new DateTime(1992, 12, 19, 23, 59, 0, DateTimeKind.Utc);
        var venus = Planets.Find("Venus", utc)!;
        var ofDate = Precession.J2000ToJNow(venus.Position, utc);
        var expected = new EquatorialCoordinates(21 + 4 / 60.0 + 41.454 / 3600, -(18 + 53 / 60.0 + 16.84 / 3600));
        Assert.True(Precession.Separation(ofDate, expected) < 0.1, $"{ofDate} vs {expected}");
        Assert.InRange(venus.Magnitude, -4.7, -3.8);
        Assert.InRange(venus.DiameterArcsec, 15, 30);
    }

    [Fact]
    public void AThinVenusCrescentIsntOverbright()
    {
        // 2026 October: Venus nears inferior conjunction (24th), a crescent just past its greatest brilliancy.
        var venus = Planets.Find("Venus", new DateTime(2026, 10, 5, 20, 0, 0, DateTimeKind.Utc))!;
        Assert.InRange(venus.Magnitude, -4.9, -4.0); // near its greatest brilliancy (−4.8), not −5.7
        Assert.InRange(venus.DiameterArcsec, 45, 60);
    }

    [Fact]
    public void MarsAtItsCloseOpposition()
    {
        // 2003 August 27–28: Mars at RA about 22h38m, Dec −15.8°, magnitude −2.9, 25″ across.
        var mars = Planets.Find("Mars", new DateTime(2003, 8, 28, 0, 0, 0, DateTimeKind.Utc))!;
        Assert.True(Precession.Separation(mars.Position, new EquatorialCoordinates(22 + 38 / 60.0, -15.8)) < 0.5, $"{mars.Position}");
        Assert.InRange(mars.Magnitude, -3.2, -2.6);
        Assert.InRange(mars.DiameterArcsec, 24, 26.5);
    }
}
