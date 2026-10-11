using Nova.Core.Astronomy;
using Nova.Core.Equipment;
using Nova.Server.Services;

namespace Nova.Tests;

public class NightEndTests
{
    // The Dominion Astrophysical Observatory, Victoria, BC.
    private static readonly GeoLocation Victoria = new(48.5197, -123.4167, 0);
    private static readonly DateTime Evening = new(2026, 10, 6, 4, 0, 0, DateTimeKind.Utc); // 21:00 PDT on the 5th

    [Fact]
    public void StopsAtThatNightsAstronomicalDawn()
    {
        var night = Night.Around(Evening, Victoria)!;
        var end = SequenceEnd.NightEnd(SequenceEnd.AstronomicalDawn, Evening, Victoria)!.Value;

        Assert.Equal(night.AstronomicalDawn!.Value, end.Utc, TimeSpan.FromSeconds(1));
        Assert.StartsWith("astronomical dawn (", end.Label);
        Assert.True(SequenceEnd.NightEnd(SequenceEnd.NauticalDawn, Evening, Victoria)!.Value.Utc > end.Utc);
        Assert.Equal(night.Sunrise, SequenceEnd.NightEnd(SequenceEnd.Sunrise, Evening, Victoria)!.Value.Utc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void StartedAfterDawnRunsUntilTheNextOne()
    {
        var night = Night.Around(Evening, Victoria)!;
        var twilight = night.AstronomicalDawn!.Value.AddMinutes(10); // after dawn, before sunrise

        var end = SequenceEnd.NightEnd(SequenceEnd.AstronomicalDawn, twilight, Victoria)!.Value;

        Assert.InRange(end.Utc - twilight, TimeSpan.FromHours(20), TimeSpan.FromHours(26));
    }

    [Fact]
    public void NeverOrNoSiteMeansNoStop()
    {
        Assert.Null(SequenceEnd.NightEnd(SequenceEnd.Never, Evening, Victoria));
        Assert.Null(SequenceEnd.NightEnd(null, Evening, Victoria));
        Assert.Null(SequenceEnd.NightEnd(SequenceEnd.AstronomicalDawn, Evening, null));
    }

    [Fact]
    public void AClockTimeIsAlwaysAfterTheStart()
    {
        var now = DateTime.UtcNow;
        var local = now.ToLocalTime();
        var soon = local.AddMinutes(30).ToString("HH:mm");
        var earlier = local.AddMinutes(-30).ToString("HH:mm");

        var end = SequenceEnd.NightEnd(soon, now, null)!.Value;
        Assert.InRange(end.Utc - now, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));
        Assert.Contains(soon, end.Label);
        Assert.InRange(SequenceEnd.NightEnd(earlier, now, null)!.Value.Utc - now, TimeSpan.FromHours(23), TimeSpan.FromHours(24));
    }

    [Fact]
    public void ShortSummerNightsFallBackToALighterDawn()
    {
        // 55°N at midsummer: the Sun never gets 18° down, so "astronomical dawn" is nautical dawn or sunrise.
        var north = new GeoLocation(55, 0, 0);
        var evening = new DateTime(2026, 6, 21, 20, 0, 0, DateTimeKind.Utc);
        var night = Night.Around(evening, north)!;
        Assert.Null(night.AstronomicalDawn);

        var end = SequenceEnd.NightEnd(SequenceEnd.AstronomicalDawn, evening, north)!.Value;
        Assert.Equal(night.NauticalDawn ?? night.Sunrise, end.Utc, TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("AstronomicalDawn", true)]
    [InlineData("Never", true)]
    [InlineData("05:30", true)]
    [InlineData("", true)]
    [InlineData("dawnish", false)]
    [InlineData("25:00", false)]
    public void ValidatesTheStop(string stop, bool valid) => Assert.Equal(valid, SequenceEnd.IsValidStop(stop));
}
