using Nova.Core.Equipment;
using Nova.Core.Guiding;

namespace Nova.Tests;

/// <summary>Automatic exposure, the star brightness check, Lowpass2, an off-axis guider's turning calibration and comet tracking.</summary>
public class GuiderFeatureTests
{
    private static GuideCalibration Cal(double raDeg, double decDeg, double rate = 4) =>
        new(raDeg * Math.PI / 180, rate, decDeg * Math.PI / 180, rate, 0, PierSide.West, DateTime.UtcNow);

    [Fact]
    public void AutoExposureLengthensForAFaintStarAndShortensForABrightOne()
    {
        var auto = new GuideExposureController();
        auto.Configure(1, 4, 20, 2);
        Assert.Equal(2, auto.Exposure);

        // Faint: judged over three frames, then one step longer; three more before the next.
        Assert.Null(auto.Add(8));
        Assert.Null(auto.Add(9));
        Assert.Equal(2.5, auto.Add(10));
        Assert.Null(auto.Add(10));
        Assert.Null(auto.Add(10));
        Assert.Equal(3, auto.Add(10));

        // Near the target: left alone.
        for (var i = 0; i < 10; i++) Assert.Null(auto.Add(25));

        // Bright: shorter, never below the minimum.
        for (var i = 0; i < 30; i++) auto.Add(200);
        Assert.Equal(1, auto.Exposure);
    }

    [Fact]
    public void AutoExposureLengthensWhenTheStarIsLostUpToTheMaximum()
    {
        var auto = new GuideExposureController();
        auto.Configure(0.5, 3, 20, 1);
        for (var i = 0; i < 40; i++) auto.Add(null);
        Assert.Equal(3, auto.Exposure);
        auto.Configure(0.5, 3, 20, 10); // a start outside the range is brought in
        Assert.Equal(3, auto.Exposure);
    }

    [Fact]
    public void StarMassCheckSkipsSuddenChangesThenAcceptsALastingOne()
    {
        var mass = new StarMassMonitor { Tolerance = 0.5 };
        for (var i = 0; i < 8; i++) Assert.True(mass.Add(10_000 + i * 100, out _));

        // A satellite crossing: much brighter for a frame.
        Assert.False(mass.Add(25_000, out var change));
        Assert.True(change > 1);
        Assert.True(mass.Add(10_200, out _));

        // Cloud arriving and staying: skipped for a few frames, then taken as the star's brightness now.
        var results = Enumerable.Range(0, StarMassMonitor.AcceptAfter).Select(i => mass.Add(4_000, out _)).ToList();
        Assert.All(results.Take(StarMassMonitor.AcceptAfter - 1), r => Assert.False(r));
        Assert.True(results[^1]);
        Assert.True(mass.Add(4_100, out _));

        // Off: everything goes.
        var off = new StarMassMonitor { Tolerance = 0 };
        for (var i = 0; i < 5; i++) off.Add(10_000, out _);
        Assert.True(off.Add(90_000, out _));
    }

    [Fact]
    public void Lowpass2FollowsADriftAndSmoothsTheJitter()
    {
        var lowpass = new Lowpass2Algorithm(aggressiveness: 0.8, minMove: 0.1);
        var random = new Random(3);
        var position = 0.0;
        var corrections = new List<double>();
        for (var i = 0; i < 200; i++)
        {
            position += 0.05; // steady drift
            var seen = position + (random.NextDouble() * 2 - 1) * 0.4; // seeing
            var correction = lowpass.Correction(seen);
            Assert.True(Math.Abs(correction) <= Math.Abs(seen) + 1e-9); // never more than the error
            if (Math.Abs(seen) < 0.1) Assert.Equal(0, correction);
            position -= correction;
            corrections.Add(correction);
        }
        // It keeps up with the drift (the star stays near home) …
        Assert.True(Math.Abs(position) < 0.5, $"{position}");
        // … with corrections much steadier than the jitter (±0.4).
        var late = corrections.Skip(100).Where(c => c != 0).ToList();
        var mean = late.Average();
        Assert.True(Math.Sqrt(late.Average(c => (c - mean) * (c - mean))) < 0.15);
    }

    [Fact]
    public void TheControllerUsesLowpass2ForDecWhenChosen()
    {
        var calibration = Cal(0, 90);
        var controller = new GuideController(new GuideAlgorithmSettings(DecAlgorithm: DecAlgorithm.Lowpass2, DecMinMove: 0.1, DecBacklashCompensation: false));
        var first = controller.Correct(0, 1.0, calibration);
        Assert.Equal(GuideDirection.South, first.Dec!.Direction); // first frames correct straight away
        // Resist Switch would refuse an immediate reversal; Lowpass2 follows its fitted line.
        controller.Correct(0, 0.8, calibration);
        controller.Correct(0, 0.6, calibration);
        var reversed = controller.Correct(0, -1.5, calibration);
        Assert.True(reversed.Dec is null || reversed.Dec.Direction == GuideDirection.North);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(-110)]
    public void ATurnedCalibrationMatchesTheTurnedCamera(double turn)
    {
        var before = Cal(30, 120);
        var turned = before.Rotated(turn * Math.PI / 180);
        var truth = Cal(30 + turn, 120 + turn);
        // A star offset in the turned camera splits into the same axes either way.
        foreach (var (dx, dy) in new[] { (3.0, 1.0), (-2.0, 4.0) })
        {
            var (ra, dec) = turned.ToAxes(dx, dy);
            var (raTrue, decTrue) = truth.ToAxes(dx, dy);
            Assert.Equal(raTrue, ra, 6);
            Assert.Equal(decTrue, dec, 6);
        }
        Assert.Same(before, before.Rotated(0));
    }

    [Fact]
    public void SkyMotionMovesTheLockTheWayTheObjectMovesAgainstTheStars()
    {
        // A West pulse moves stars along +x, a North pulse along +y.
        var calibration = Cal(0, 90);
        // 3600″/h east for 10 s at 2″/px: 10″ = 5 px, the way stars go when the scope moves west.
        var (dx, dy) = calibration.SkyMotion(3600, 0, 10, 2);
        Assert.Equal(5, dx, 6);
        Assert.Equal(0, dy, 6);
        // North on the sky is the other way from where a North pulse moves the stars.
        (dx, dy) = calibration.SkyMotion(0, 7200, 10, 2);
        Assert.Equal(0, dx, 6);
        Assert.Equal(-10, dy, 6);
        Assert.Equal((0, 0), calibration.SkyMotion(3600, 3600, 10, 0));
    }
}
