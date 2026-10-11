using Nova.Core.Guiding;

namespace Nova.Tests;

public class DecDriftTests
{
    [Fact]
    public void SpotsAPolarErrorFromOneWayDecCorrections()
    {
        // Like the night of 7 October: 2 s frames at 6.45″/px, every Dec correction north, about 30″ a minute.
        var monitor = new DecDriftMonitor();
        for (var i = 0; i <= 300; i++) monitor.Add(1000 + 2 * i, 0.155, i % 6 == 0 ? null : "North");
        var drift = monitor.Estimate(6.45)!;
        Assert.Equal("North", drift.Direction);
        Assert.Equal(1.0, drift.OneWay);
        Assert.Equal(10, drift.Minutes, 1);
        Assert.InRange(drift.ArcsecPerMinute, 24, 26);       // 5 of 6 frames × 0.155 px × 6.45″ × 30 frames/min
        Assert.InRange(drift.PolarErrorArcmin, 90, 100);     // ≈ 1.6°
        Assert.True(drift.Significant);
        Assert.Equal("1.6°", DecDriftMonitor.Describe(drift.PolarErrorArcmin));
    }

    [Fact]
    public void BalancedCorrectionsAreJustSeeing()
    {
        var monitor = new DecDriftMonitor();
        for (var i = 0; i <= 300; i++) monitor.Add(2 * i, 0.3, i % 2 == 0 ? "North" : "South");
        var drift = monitor.Estimate(6.45)!;
        Assert.False(drift.Significant);
        Assert.True(drift.ArcsecPerMinute < 1);
    }

    [Fact]
    public void NeedsFiveMinutesAndSkipsGapsAndSettling()
    {
        var monitor = new DecDriftMonitor();
        for (var i = 0; i < 100; i++) monitor.Add(2 * i, 0.2, "South");
        Assert.Null(monitor.Estimate(1)); // 200 s isn't enough

        // A minute without a star (a gap), then settling after a dither: neither counts.
        for (var i = 0; i < 100; i++) monitor.Add(260 + 2 * i, 3, "North", counts: false);
        Assert.Null(monitor.Estimate(1));
        for (var i = 0; i < 60; i++) monitor.Add(460 + 2 * i, 0.2, "South");
        var drift = monitor.Estimate(1)!;
        Assert.Equal("South", drift.Direction);
        Assert.Equal(6.0, drift.ArcsecPerMinute, 1); // 0.2″ every 2 s
        Assert.InRange(drift.Minutes, 5.2, 5.4);

        monitor.Reset();
        Assert.Null(monitor.Estimate(1));
    }

    [Fact]
    public void ASmallPolarErrorIsNotWorthMentioning()
    {
        // 2″ a minute: about 8′, fine for most setups.
        var monitor = new DecDriftMonitor();
        for (var i = 0; i <= 300; i++) monitor.Add(i, 2.0 / 60, "North");
        var drift = monitor.Estimate(1)!;
        Assert.Equal(1.0, drift.OneWay);
        Assert.False(drift.Significant);
        Assert.Equal("8′", DecDriftMonitor.Describe(drift.PolarErrorArcmin));
    }
}

public class Phd2DriftWarningTests
{
    [Fact]
    public void WarnsOncePerGuidingRunAboutPolarAlignment()
    {
        var client = new Phd2Client();
        var messages = new List<string>();
        client.Message += (_, m) => messages.Add(m);

        client.Handle(System.Text.Json.Nodes.JsonNode.Parse("""{"Event":"StartGuiding"}""")!.AsObject());
        // Offline, StartGuiding's pixel-scale refresh fails and clears it: set it afterwards, as PHD2 would answer.
        typeof(Phd2Client).GetProperty(nameof(Phd2Client.PixelScale))!.SetValue(client, 6.45);
        for (var i = 0; i <= 400; i++)
            client.Handle(System.Text.Json.Nodes.JsonNode.Parse(
                $$"""{"Event":"GuideStep","Timestamp":{{1000 + 2 * i}},"RADistanceRaw":0.1,"DECDistanceRaw":0.1,"DECDistanceGuide":0.15,"DECDirection":"North","ErrorCode":0}""")!.AsObject());

        var warning = Assert.Single(messages);
        Assert.Contains("polar alignment is at least 1.", warning);
        Assert.Contains("pushing north", warning);
        Assert.True(client.Drift!.Significant);

        client.Handle(System.Text.Json.Nodes.JsonNode.Parse("""{"Event":"StartGuiding"}""")!.AsObject());
        Assert.Null(client.Drift);
    }
}
