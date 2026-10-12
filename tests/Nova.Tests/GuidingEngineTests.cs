using Nova.Core.Equipment;
using Nova.Core.Guiding;
using Nova.Core.Imaging;
using Nova.Core.Simulation;

namespace Nova.Tests;

public class GuidingEngineTests
{
    /// <summary>
    /// A pretend guide camera view: pulses move the star field along known directions at known rates (pixels per
    /// second), with optional Dec backlash, drift and periodic error.
    /// </summary>
    private sealed class FakeSky(double raAngleDeg, double raRate, double decAngleDeg, double decRate, double backlashPx = 0)
    {
        public double X = 320, Y = 240;
        private int _lastDec;
        private double _backlashLeft;

        public void Pulse(GuideDirection direction, int ms)
        {
            var seconds = ms / 1000.0;
            switch (direction)
            {
                case GuideDirection.West: Move(raAngleDeg, raRate * seconds); break;
                case GuideDirection.East: Move(raAngleDeg, -raRate * seconds); break;
                default:
                    var sign = direction == GuideDirection.North ? 1 : -1;
                    if (_lastDec != sign) _backlashLeft = backlashPx;
                    _lastDec = sign;
                    var move = decRate * seconds;
                    var taken = Math.Min(_backlashLeft, move);
                    _backlashLeft -= taken;
                    Move(decAngleDeg, sign * (move - taken));
                    break;
            }
        }

        public void Move(double angleDeg, double pixels)
        {
            X += pixels * Math.Cos(angleDeg * Math.PI / 180);
            Y += pixels * Math.Sin(angleDeg * Math.PI / 180);
        }

        /// <summary>The star field (lock star plus a few others at fixed offsets), with optional per-star noise.</summary>
        public List<DetectedStar> Stars(Random? noise = null, double sigma = 0)
        {
            double N() => noise is null ? 0 : (noise.NextDouble() * 2 - 1) * sigma;
            return
            [
                new(X + N(), Y + N(), 2, 50_000, 20_000, 80),
                new(X + 120 + N(), Y - 60 + N(), 2, 30_000, 12_000, 50),
                new(X - 150 + N(), Y + 80 + N(), 2, 20_000, 8_000, 40),
                new(X + 60 + N(), Y + 140 + N(), 2, 10_000, 4_000, 25),
            ];
        }
    }

    private static GuideCalibration Calibrate(FakeSky sky, double decDegrees = 0, PierSide? pier = PierSide.West, int stepMs = 500)
    {
        var calibrator = new GuideCalibrator(stepMs, decDegrees, pier);
        for (var i = 0; i < 500; i++)
        {
            var step = calibrator.Next(sky.X, sky.Y);
            if (step is null) break;
            sky.Pulse(step.Direction, step.Milliseconds);
        }
        Assert.True(calibrator.IsDone, calibrator.Error);
        return calibrator.Result!;
    }

    private static double Deg(double radians) => ((radians * 180 / Math.PI) % 360 + 360) % 360;

    [Theory]
    [InlineData(30, 4, 300, 4)]     // square axes, equal rates
    [InlineData(200, 6, 107, 3)]    // turned camera, 3° off square, different rates
    [InlineData(0, 2, 90, 2)]       // mirrored image (north the other way round)
    public void CalibrationRecoversTheAxes(double raAngle, double raRate, double decAngle, double decRate)
    {
        var sky = new FakeSky(raAngle, raRate, decAngle, decRate, backlashPx: 2);
        var (x0, y0) = (sky.X, sky.Y);
        var cal = Calibrate(sky);

        Assert.Equal(raAngle, Deg(cal.RaAngle), 0.5);
        Assert.Equal(decAngle, Deg(cal.DecAngle), 0.5);
        Assert.Equal(raRate, cal.RaRate, 0.05);
        Assert.Equal(decRate, cal.DecRate, 0.05);
        Assert.Equal(Math.Abs(90 - Math.Abs(((raAngle - decAngle) % 360 + 540) % 360 - 180)), cal.OrthogonalityError, 0.5);
        // It brings the star back near where it started (backlash clearing moves it a little north).
        Assert.True(Math.Sqrt((sky.X - x0) * (sky.X - x0) + (sky.Y - y0) * (sky.Y - y0)) < 6);
    }

    [Fact]
    public void CalibrationFailsWhenTheStarDoesNotMove()
    {
        var calibrator = new GuideCalibrator(500, 0, null, maxSteps: 10);
        CalibrationStep? step;
        var n = 0;
        do step = calibrator.Next(320, 240); while (step is not null && ++n < 100);
        Assert.True(calibrator.IsFailed);
        Assert.Contains("west", calibrator.Error);
    }

    [Fact]
    public void CalibrationWarnsWhenTheAxesAreFarFromSquare()
    {
        var cal = new GuideCalibrator(500, 0, null);
        var sky = new FakeSky(0, 4, 60, 4);
        for (var step = cal.Next(sky.X, sky.Y); step is not null; step = cal.Next(sky.X, sky.Y)) sky.Pulse(step.Direction, step.Milliseconds);
        Assert.True(cal.IsDone);
        Assert.Contains("from square", cal.Warning);
    }

    [Fact]
    public void StepLengthCoversTheTargetInAboutTwelveSteps()
    {
        // 6.45″/px guide camera, 0.5× guide rate at Dec 0: 25 px = 161″ at 7.5″/s → 21.4 s / 12 ≈ 1.8 s.
        Assert.Equal(1800, GuideCalibrator.StepMilliseconds(6.45, 0.5, 0));
        // Near the pole RA moves less sky, so steps get longer (capped).
        Assert.True(GuideCalibrator.StepMilliseconds(6.45, 0.5, 70) > 1800);
        Assert.Equal(5000, GuideCalibrator.StepMilliseconds(6.45, 0.5, 89));
    }

    [Fact]
    public void CalibrationAdaptsToDecAndPierSide()
    {
        var cal = new GuideCalibration(0.3, 4, 0.3 - Math.PI / 2, 4, 0, PierSide.West, DateTime.UtcNow);

        var at60 = cal.For(60, PierSide.West);
        Assert.Equal(2, at60.RaRate, 3);        // cos 60° = ½
        Assert.Equal(4, at60.DecRate, 3);
        Assert.Equal(cal.RaAngle, at60.RaAngle, 6);

        var flipped = cal.For(0, PierSide.East);
        Assert.Equal(Deg(cal.RaAngle + Math.PI), Deg(flipped.RaAngle), 3);
        Assert.Equal(Deg(cal.DecAngle + Math.PI), Deg(flipped.DecAngle), 3);
        var flippedDecKept = cal.For(0, PierSide.East, reverseDecAfterFlip: true);
        Assert.Equal(Deg(cal.DecAngle), Deg(flippedDecKept.DecAngle), 3);
    }

    [Fact]
    public void OffsetsSplitIntoAxesEvenWhenNotSquare()
    {
        // West along +x, North 80° from it (10° off square).
        var cal = new GuideCalibration(0, 4, 80 * Math.PI / 180, 4, 0, null, DateTime.UtcNow);
        var (nx, ny) = (Math.Cos(cal.DecAngle), Math.Sin(cal.DecAngle));
        var (ra, dec) = cal.ToAxes(3 + 2 * nx, 2 * ny);
        Assert.Equal(3, ra, 6);
        Assert.Equal(2, dec, 6);
    }

    [Fact]
    public void HysteresisBlendsAndIgnoresSmallErrors()
    {
        var h = new HysteresisAlgorithm(hysteresis: 0.1, aggressiveness: 0.7, minMove: 0.15);
        Assert.Equal(0.9 * 1.0 * 0.7, h.Correction(1.0), 6);
        Assert.Equal((0.9 * 1.0 + 0.1 * 0.63) * 0.7, h.Correction(1.0), 6);
        Assert.Equal(0, h.Correction(0.1));
        h.Reset();
        Assert.Equal(0.9 * -2 * 0.7, h.Correction(-2), 6);
    }

    [Fact]
    public void ResistSwitchIsSlowToReverse()
    {
        var r = new ResistSwitchAlgorithm(aggressiveness: 1, minMove: 0.2, fastSwitch: true);
        Assert.Equal(0.5, r.Correction(0.5));  // first side picked
        Assert.Equal(0, r.Correction(-0.3));   // one frame the other way: resisted
        Assert.Equal(0, r.Correction(-0.3));
        Assert.Equal(-0.3, r.Correction(-0.3)); // three in a row: switches
        Assert.Equal(0, r.Correction(0.4));
        Assert.Equal(0.7, r.Correction(0.7), 6); // fast switch: a big jump (> 3 × min move) switches at once
        Assert.Equal(0, r.Correction(0.1));     // under min move
    }

    [Fact]
    public void SettlesAfterStayingCloseLongEnough()
    {
        var start = new DateTime(2026, 10, 1, 22, 0, 0, DateTimeKind.Utc);
        var settle = new SettleTracker(new SettleSettings(1.5, 10, 60), start);
        Assert.False(settle.Add(start.AddSeconds(2), 3));
        Assert.False(settle.Add(start.AddSeconds(4), 1));
        Assert.False(settle.Add(start.AddSeconds(8), 2));    // out again: starts over
        Assert.False(settle.Add(start.AddSeconds(10), 1));
        Assert.False(settle.Add(start.AddSeconds(16), null)); // lost frame: starts over
        Assert.False(settle.Add(start.AddSeconds(18), 1));
        Assert.True(settle.Add(start.AddSeconds(28), 1.2));

        var never = new SettleTracker(new SettleSettings(1.5, 10, 20), start);
        Assert.False(never.Add(start.AddSeconds(10), 5));
        Assert.Throws<TimeoutException>(() => never.Add(start.AddSeconds(21), 5));
    }

    [Fact]
    public void TrackerAveragesStarsLosesThemAndRecovers()
    {
        var sky = new FakeSky(0, 4, 90, 4);
        var tracker = new GuideStarTracker();
        Assert.True(tracker.Select(sky.Stars(), 640, 480));
        Assert.Equal(4, tracker.Count);
        Assert.Equal((sky.X, sky.Y), tracker.LockHome);

        sky.Move(0, 3);
        var m = tracker.Measure(sky.Stars())!;
        Assert.Equal(3, m.Dx, 6);
        Assert.Equal(0, m.Dy, 6);
        Assert.Equal(4, m.Found);

        // Clouds: nothing found → lost; the field drifts a bit meanwhile and comes back.
        Assert.Null(tracker.Measure([]));
        sky.Move(90, 2);
        m = tracker.Measure(sky.Stars())!;
        Assert.NotNull(m);
        Assert.Equal(3, m.Dx, 6);
        Assert.Equal(2, m.Dy, 6);

        // Lock star gone but the references are there: still measures, from them.
        var withoutLock = sky.Stars().Skip(1).ToList();
        m = tracker.Measure(withoutLock)!;
        Assert.Equal(3, m.Found);
        Assert.Equal([false, true, true, true], tracker.Stars.Select(s => s.Found));
        Assert.Equal(sky.X, tracker.Stars[0].X, 6); // expected where the others say
        Assert.Equal(0, m.Snr);
        Assert.Equal(3, m.Dx, 6);

        // A stray object near a star doesn't pull the average.
        var stray = sky.Stars();
        stray[1] = stray[1] with { X = stray[1].X + 8 };
        m = tracker.Measure(stray)!;
        Assert.Equal(3, m.Dx, 6);
    }

    [Fact]
    public void TrackerFollowsADitherAndCanLockOnAClickedStar()
    {
        var sky = new FakeSky(0, 4, 90, 4);
        var tracker = new GuideStarTracker();
        Assert.True(tracker.SelectAt(sky.Stars(), 640, 480, sky.X + 118, sky.Y - 58));
        Assert.Equal((sky.X + 120, sky.Y - 60), tracker.LockHome);

        tracker.ShiftHome(2, -1);
        var m = tracker.Measure(sky.Stars())!;
        Assert.Equal(-2, m.Dx, 6);  // the stars are now 2 px left of home
        Assert.Equal(1, m.Dy, 6);
    }

    [Fact]
    public void GuidingHoldsTheStarAgainstDriftAndPeriodicError()
    {
        // Camera turned 35°, 4 px/s on both axes, Dec backlash; the sky drifts in Dec (polar error) and RA wobbles.
        var sky = new FakeSky(35, 4, 305, 4, backlashPx: 1);
        var cal = Calibrate(sky);
        var tracker = new GuideStarTracker();
        tracker.Select(sky.Stars(), 640, 480);
        var controller = new GuideController();
        var noise = new Random(3);

        var unguided = new FakeSky(35, 4, 305, 4) { X = sky.X, Y = sky.Y };
        var (ux0, uy0) = (unguided.X, unguided.Y);
        var guided = new List<double>();
        var unguidedDistances = new List<double>();
        var decPulses = new List<GuideDirection>();
        for (var frame = 0; frame < 300; frame++) // 2 s frames: 10 minutes
        {
            var t = frame * 2.0;
            var drift = 0.6 * 2 / 60;                                   // 0.6 px a minute in Dec
            var pe = 3 * Math.Cos(2 * Math.PI * t / 300) * 2 * Math.PI / 300 * 2; // 3 px over a 5-minute period
            foreach (var s in new[] { sky, unguided })
            {
                s.Move(305, drift);
                s.Move(35, pe);
            }

            var m = tracker.Measure(sky.Stars(noise, 0.1))!;
            guided.Add(m.Distance);
            unguidedDistances.Add(Math.Sqrt((unguided.X - ux0) * (unguided.X - ux0) + (unguided.Y - uy0) * (unguided.Y - uy0)));
            var c = controller.Correct(m.Dx, m.Dy, cal);
            if (c.Ra is { } ra) sky.Pulse(ra.Direction, ra.Milliseconds);
            if (c.Dec is { } dec) { sky.Pulse(dec.Direction, dec.Milliseconds); decPulses.Add(dec.Direction); }
        }

        var rms = Math.Sqrt(guided.Skip(30).Average(d => d * d));
        Assert.True(rms < 0.5, $"guided RMS {rms:0.00} px");
        Assert.True(unguidedDistances.Max() > 5, $"unguided drifted {unguidedDistances.Max():0.0} px");
        // Steady one-way Dec drift: the corrections stay one way (Resist Switch).
        Assert.True(decPulses.Count(d => d == GuideDirection.South) >= decPulses.Count * 0.9);
    }

    [Fact]
    public void DecModeLimitsCorrections()
    {
        var cal = new GuideCalibration(0, 4, -Math.PI / 2, 4, 0, null, DateTime.UtcNow);
        var north = new GuideController(new GuideAlgorithmSettings(DecMode: DecGuideMode.North));
        // Star 2 px along the North-pulse direction (up, y −) → needs a South pulse: not allowed.
        Assert.Null(north.Correct(0, -2, cal).Dec);
        Assert.Equal(GuideDirection.North, north.Correct(0, 2, cal).Dec!.Direction);

        var off = new GuideController(new GuideAlgorithmSettings(DecMode: DecGuideMode.Off));
        Assert.Null(off.Correct(0, 5, cal).Dec);

        // Pulse length: 2 px × 0.7 × 0.9 at 4 px/s = 315 ms east; capped at the max.
        var c = new GuideController().Correct(2, 0, cal);
        Assert.Equal(new GuidePulse(GuideDirection.East, 315), c.Ra);
        Assert.Equal(2500, new GuideController().Correct(100, 0, cal).Ra!.Milliseconds);
    }

    [Fact]
    public void DitherStaysWithinTheAmount()
    {
        var cal = new GuideCalibration(0.5, 4, 0.5 - Math.PI / 2, 4, 0, null, DateTime.UtcNow);
        var random = new Random(1);
        for (var i = 0; i < 50; i++)
        {
            var (dx, dy) = GuideController.DitherOffset(random, 3, raOnly: false, cal);
            var (ra, dec) = cal.ToAxes(dx, dy);
            Assert.InRange(Math.Abs(ra), 0, 3.0001);
            Assert.InRange(Math.Abs(dec), 0, 3.0001);
            var (rx, ry) = GuideController.DitherOffset(random, 3, raOnly: true, cal);
            Assert.Equal(0, cal.ToAxes(rx, ry).Dec, 6);
        }
    }

    [Fact]
    public async Task SimulatedMountMovesWithGuidePulsesAndHasBacklash()
    {
        await using var simulator = new SimulatorProvider();
        var mount = (SimulatedMount)simulator.GetMount("Simulated Mount")!;
        await mount.ConnectAsync(CancellationToken.None);
        mount.DecBacklashArcsec = 3;
        var now = DateTime.UtcNow;
        var before = mount.TrueCoordinatesAt(now);

        // 1 s north at 0.5× sidereal = 7.5″.
        await mount.PulseAsync(GuideDirection.North, TimeSpan.FromSeconds(1), CancellationToken.None);
        var north = mount.TrueCoordinatesAt(now);
        Assert.Equal(7.52, (north.DecDegrees - before.DecDegrees) * 3600, 0.05);

        // Reversing loses the backlash: 7.5″ south comes back only 4.5″.
        await mount.PulseAsync(GuideDirection.South, TimeSpan.FromSeconds(1), CancellationToken.None);
        var south = mount.TrueCoordinatesAt(now);
        Assert.Equal(-4.52, (south.DecDegrees - north.DecDegrees) * 3600, 0.05);

        // East moves RA up (by the sky distance ÷ cos Dec).
        await mount.PulseAsync(GuideDirection.East, TimeSpan.FromMilliseconds(500), CancellationToken.None);
        var east = mount.TrueCoordinatesAt(now);
        var cosDec = Math.Cos(east.DecDegrees * Math.PI / 180);
        Assert.Equal(3.76, (east.RaHours - south.RaHours) * 15 * 3600 * cosDec, 0.05);
    }
}
