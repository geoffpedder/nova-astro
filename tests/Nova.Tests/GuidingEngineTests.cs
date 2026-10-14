using Nova.Core.Equipment;
using Nova.Core.Guiding;
using Nova.Core.Imaging;
using Nova.Core.Simulation;

namespace Nova.Tests;

public class GuidingEngineTests(Xunit.Abstractions.ITestOutputHelper output)
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
    public void CalibrationRecordsThePathForAGraph()
    {
        var sky = new FakeSky(30, 4, 300, 4, backlashPx: 2);
        var calibrator = new GuideCalibrator(500, 0, null);
        for (var step = calibrator.Next(sky.X, sky.Y); step is not null; step = calibrator.Next(sky.X, sky.Y))
            sky.Pulse(step.Direction, step.Milliseconds);
        Assert.True(calibrator.IsDone);

        var points = calibrator.Points;
        Assert.Equal(["West", "East", "North", "South"], points.Select(p => p.Phase).Distinct());
        Assert.Equal(new CalibrationPoint("West", 0, 0), points[0]); // measured from where the West steps began
        // The West points lie along the RA direction (30°), the North ones along Dec (300°), from where they started.
        var west = points.Where(p => p.Phase == "West").Last();
        Assert.Equal(30, Math.Atan2(west.Y, west.X) * 180 / Math.PI, 1);
        var north = points.Where(p => p.Phase == "North").ToList();
        var angle = Math.Atan2(north[^1].Y - north[0].Y, north[^1].X - north[0].X) * 180 / Math.PI;
        Assert.Equal(-60, angle, 1);
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

    [Theory]
    [InlineData(0)]
    [InlineData(1.5)]
    [InlineData(4)]
    public void CalibrationMeasuresDecBacklash(double backlash)
    {
        var sky = new FakeSky(30, 4, 300, 4, backlashPx: backlash);
        var cal = Calibrate(sky);
        Assert.Equal(backlash, cal.DecBacklashPixels, 0.05);
        Assert.InRange(cal.DecBacklashMs, backlash / 4 * 1000 - 15, backlash / 4 * 1000 + 15);
        Assert.Equal(4, cal.DecRate, 0.05); // the north steps still give the rate (the backlash was cleared first)
    }

    /// <summary>
    /// Dec only: the star wanders north and south (a slow wobble, so corrections keep reversing) through a mount with
    /// <paramref name="backlashPx"/> of Dec backlash. Returns the Dec RMS over the last half and the controller.
    /// </summary>
    private static (double Rms, GuideController Controller) GuideDecThroughBacklash(
        bool compensate, double backlashPx, double measuredPx, int frames = 600)
    {
        var sky = new FakeSky(0, 4, 270, 4, backlashPx);
        var cal = new GuideCalibration(0, 4, -Math.PI / 2, 4, 0, PierSide.West, DateTime.UtcNow, measuredPx);
        var controller = new GuideController(new GuideAlgorithmSettings(DecBacklashCompensation: compensate));
        var (homeX, homeY) = (sky.X, sky.Y);
        var noise = new Random(5);
        var late = new List<double>();
        double last = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            var t = frame * 2.0; // 2 s frames
            var wobble = 4 * Math.Sin(2 * Math.PI * t / 240); // ±4 px over 4 minutes: Dec reverses twice a cycle
            sky.Move(270, wobble - last);
            last = wobble;
            var dy = sky.Y - homeY + (noise.NextDouble() - 0.5) * 0.2;
            var c = controller.Correct(sky.X - homeX, dy, cal);
            if (frame >= frames / 2) late.Add(sky.Y - homeY);
            if (c.Dec is { } dec) sky.Pulse(dec.Direction, dec.Milliseconds);
        }
        return (Math.Sqrt(late.Average(v => v * v)), controller);
    }

    [Fact]
    public void DecBacklashCompensationCutsTheLagAfterReversals()
    {
        var (off, _) = GuideDecThroughBacklash(compensate: false, backlashPx: 3, measuredPx: 3);
        var (on, controller) = GuideDecThroughBacklash(compensate: true, backlashPx: 3, measuredPx: 3);
        output.WriteLine($"Dec RMS with 3 px of backlash: {off:0.000} px uncompensated, {on:0.000} px compensated ({100 * (1 - on / off):0}% lower); " +
            $"pulse {controller.Backlash.PulseMs:0} ms (truth 750), {controller.Backlash.Applied} reversals");
        Assert.True(on < off * 0.85, $"compensated {on:0.000} px vs {off:0.000} px");
        Assert.True(controller.Backlash.Applied > 5);
        Assert.InRange(controller.Backlash.PulseMs, 450, 1100);
    }

    [Theory]
    [InlineData(1.5)] // measured too little: it grows
    [InlineData(5.5)] // too much: it shrinks
    public void DecBacklashCompensationAdjustsTowardsTheTruth(double measuredPx)
    {
        var (_, controller) = GuideDecThroughBacklash(compensate: true, backlashPx: 3, measuredPx: measuredPx, frames: 1500);
        var start = measuredPx / 4 * 1000;
        output.WriteLine($"measured {start:0} ms, now {controller.Backlash.PulseMs:0} ms (truth 750): {controller.Backlash.Increases} up, {controller.Backlash.Decreases} down");
        Assert.True(Math.Abs(controller.Backlash.PulseMs - 750) < Math.Abs(start - 750), $"{controller.Backlash.PulseMs:0} ms");
    }

    [Fact]
    public void NoBacklashMeansNoCompensation()
    {
        var (rms, controller) = GuideDecThroughBacklash(compensate: true, backlashPx: 0, measuredPx: 0.3); // under the noise floor
        Assert.Equal(0, controller.Backlash.MeasuredMs);
        Assert.Equal(0, controller.Backlash.Applied);
        var (plain, _) = GuideDecThroughBacklash(compensate: false, backlashPx: 0, measuredPx: 0);
        Assert.Equal(plain, rms, 6);
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
    public void WritesAGuideLogInPhd2sFormat()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nova-guidelog-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "night", "NOVA_GuideLog.txt");
        try
        {
            var log = new GuideLog(() => path);
            var start = new DateTime(2026, 10, 12, 4, 0, 0, DateTimeKind.Utc);
            log.Info("not written: no run going on");
            log.BeginCalibration(start, ["Camera = Guide"]);
            var points = new List<CalibrationPoint> { new("West", 0, 0), new("West", 3, 4), new("East", 1.5, 2), new("North", 1.5, 2), new("North", 1.5, -3) };
            log.EndCalibration(start.AddSeconds(30), points, new GuideCalibration(0.9273, 5, -1.5708, 5, 10, null, start), null, 2);
            log.BeginGuiding(start.AddSeconds(40), ["Exposure = 2000 ms"]);
            log.Step(start.AddSeconds(42), new GuideLogStep(0.1, -0.2, 0.15, -0.25, 0.1, 0,
                new GuidePulse(GuideDirection.East, 120), null, 52000, 80.123));
            log.StarLost(start.AddSeconds(44), "Lost the guide star");
            log.Info("DITHER by 1.000, 2.000, new lock pos = 3.000, 4.000");
            log.EndGuiding(start.AddSeconds(46));

            var lines = File.ReadAllLines(path);
            Assert.StartsWith("PHD2 version", lines[0]);
            Assert.Single(lines, l => l.StartsWith("PHD2 version")); // once per file
            Assert.DoesNotContain(lines, l => l.Contains("not written"));
            Assert.Contains("West,1,3.000,4.000,3.000,4.000,5.000", lines);
            Assert.Contains("North,1,0.000,-5.000,1.500,-3.000,5.000", lines);
            Assert.Contains("West calibration complete. Angle = 53.1 deg, Rate = 5.000 px/sec, Parity = +", lines);
            Assert.Contains("North calibration complete. Angle = -90.0 deg, Rate = 5.000 px/sec, Parity = +", lines);
            Assert.Contains("Calibration guide speeds: RA = 10.0 a-s/s, Dec = 10.0 a-s/s", lines);
            Assert.Contains("1,2.000,\"Mount\",0.100,-0.200,0.150,-0.250,0.100,0.000,120,E,0,,,,52000,80.12,0", lines);
            Assert.Contains("2,4.000,\"DROP\",,,,,,,,,,,,,0,0.00,1,\"Lost the guide star\"", lines);
            Assert.Contains("INFO: DITHER by 1.000, 2.000, new lock pos = 3.000, 4.000", lines);
            Assert.StartsWith("Guiding Ends at", lines[^1]);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

    [Fact]
    public void GuidingAssistantSeparatesSeeingDriftAndPeriodicError()
    {
        // Two minutes of 2 s frames, unguided: seeing 0.3 px RMS per axis, Dec drifting 0.5 px a minute, RA a slow
        // ±1.5 px wave (periodic error), at 2″ a pixel.
        var random = new Random(7);
        var assistant = new GuidingAssistant();
        for (var t = 0.0; t <= 120; t += 2)
            assistant.Add(t, 1.5 * Math.Sin(2 * Math.PI * t / 120) + 0.3 * Gauss(random), 0.5 * t / 60 + 0.3 * Gauss(random));

        var result = assistant.Result(DateTime.UtcNow, 2, calibrated: true, exposureSeconds: 2)!;
        Assert.Equal("arcsec", result.Units);
        Assert.Equal("RA/Dec", result.Axes);
        Assert.InRange(result.JitterRa, 0.45, 0.75);        // 0.3 px × 2″
        Assert.InRange(result.JitterDec, 0.45, 0.75);
        Assert.InRange(result.DriftDec, 0.8, 1.2);          // 0.5 px/min × 2″
        Assert.InRange(result.PeriodicRa, 3.0, 6.5);        // the ±1.5 px wave: 6″ peak to peak, less smoothing
        Assert.InRange(result.PolarErrorArcmin!.Value, 3, 5); // 1″/min ÷ 0.26″/min per arcminute ≈ 4′
        Assert.Empty(result.PePeriods!); // the 120 s wave needs a 4-minute run to be told from drift
        Assert.InRange(result.RecommendedRaMinMove, 0.12, 0.3);
        Assert.Null(result.RecommendedExposure); // good seeing: no reason to change the exposure
        Assert.Contains("Seeing jitter", result.Summary);

        // A longer run sees the periodic error's period.
        var longer = new GuidingAssistant();
        for (var t = 0.0; t <= 300; t += 2) longer.Add(t, 1.5 * Math.Sin(2 * Math.PI * t / 120) + 0.3 * Gauss(random), 0.3 * Gauss(random));
        var withPeriod = longer.Result(DateTime.UtcNow, 2, calibrated: true, exposureSeconds: 2)!;
        Assert.InRange(Assert.Single(withPeriod.PePeriods!), 112, 128);
        Assert.Contains("RA period", withPeriod.Summary);

        // Poor seeing (1.2″ a frame) asks for longer exposures to average it.
        var poor = new GuidingAssistant();
        for (var t = 0.0; t <= 120; t += 2) poor.Add(t, 0.6 * Gauss(random), 0.6 * Gauss(random));
        Assert.Equal(3, poor.Result(DateTime.UtcNow, 2, true, 2)!.RecommendedExposure);

        Assert.Null(new GuidingAssistant().Result(DateTime.UtcNow, 2, true, 2)); // too few frames
    }

    [Fact]
    public void DiagnosisTellsSeeingFromSettings()
    {
        var random = new Random(11);
        List<GuidePoint> Series(Func<int, double> ra, Func<int, double> dec) =>
            Enumerable.Range(0, 60).Select(i => new GuidePoint(ra(i), dec(i))).ToList();

        // Random errors the size of the measured seeing: nothing to gain.
        var seeing = Series(_ => 0.5 * Gauss(random), _ => 0.5 * Gauss(random));
        var d = GuidingDiagnosis.Diagnose(seeing, "arcsec", 0.5, 0.5)!;
        Assert.StartsWith("Seeing-limited", d.Summary);
        Assert.Equal(2.355 * Math.Sqrt(d.Axes[0].Rms * d.Axes[0].Rms + d.Axes[1].Rms * d.Axes[1].Rms), d.BlurArcsec!.Value, 1);

        // Same errors without a seeing measurement: random, run the assistant.
        Assert.Contains("guiding assistant", GuidingDiagnosis.Diagnose(seeing, "arcsec", null, null)!.Summary);

        // RA see-saws (overshooting): flagged with the advice, whatever the seeing.
        var seesaw = Series(i => (i % 2 == 0 ? 1 : -1) * (1 + 0.2 * Gauss(random)), _ => 0.3 * Gauss(random));
        var c = GuidingDiagnosis.Diagnose(seesaw, "arcsec", 0.3, 0.3)!;
        Assert.Equal("chasing", c.Axes[0].Verdict);
        Assert.Contains("lower aggressiveness", c.Summary);

        // RA wanders slowly (corrections too timid).
        var wander = Series(i => 1.5 * Math.Sin(i / 6.0) + 0.1 * Gauss(random), _ => 0.3 * Gauss(random));
        var l = GuidingDiagnosis.Diagnose(wander, "arcsec", 0.3, 0.3)!;
        Assert.Equal("lagging", l.Axes[0].Verdict);
        Assert.Contains("higher aggressiveness", l.Summary);

        // Tiny slow wander within the min move: left alone on purpose, so not "lagging".
        var tiny = Series(i => 0.15 * Math.Sin(i / 6.0), i => 0.1 * Math.Sin(i / 8.0));
        Assert.Equal("lagging", GuidingDiagnosis.Diagnose(tiny, "arcsec", null, null)!.Axes[0].Verdict);
        var fine = GuidingDiagnosis.Diagnose(tiny, "arcsec", null, null, minMoveRa: 0.6, minMoveDec: 0.8)!;
        Assert.Equal(["fine", "fine"], fine.Axes.Select(a => a.Verdict));
        Assert.Contains("nothing to gain", fine.Summary);

        // Settling steps don't count, and too few steps give no verdict.
        Assert.Null(GuidingDiagnosis.Diagnose(seeing.Take(20).ToList(), "arcsec", 0.5, 0.5));
        Assert.Null(GuidingDiagnosis.Diagnose(seeing.Select(p => p with { Settling = true }).ToList(), "arcsec", 0.5, 0.5));
    }

    [Theory]
    [InlineData("ZWO AM5", true)]
    [InlineData("ZWO AM3", true)]
    [InlineData("ZWO  AM5 (USB)", true)]
    [InlineData("AM5", true)]
    [InlineData("EQMod Mount", false)]
    [InlineData("LX200 GPS", false)]
    [InlineData("Simulated Mount", false)]
    [InlineData(null, false)]
    public void RecognisesZwoAmMounts(string? name, bool am)
    {
        Assert.Equal(am, GuidePresets.IsZwoAm(name));
        var defaults = GuidePresets.DefaultsFor(name);
        Assert.Equal(am ? 2.5 : 5, defaults.DitherPixels);
        Assert.Equal(am ? 0.7 : 1.5, defaults.SettlePixels);
    }

    [Fact]
    public void PeriodogramFindsOneAndTwoPeriodsInUnevenNoisyData()
    {
        var random = new Random(5);
        var t = new List<double>();
        for (var time = 0.0; time < 1800; time += 1.5 + random.NextDouble()) t.Add(time); // 1.5–2.5 s apart
        var one = t.Select(x => 6 * Math.Sin(2 * Math.PI * x / 300) + 0.02 * x + 0.5 * Gauss(random)).ToList();
        var peaks = Periodogram.Peaks(t, one, 20, 900);
        Assert.Equal(300, peaks[0].Seconds, 300 * 0.03);
        Assert.True(peaks[0].Power > 0.8);

        var two = t.Select(x => 6 * Math.Sin(2 * Math.PI * x / 300) + 3 * Math.Sin(2 * Math.PI * x / 110 + 1) + 0.5 * Gauss(random)).ToList();
        var both = Periodogram.Peaks(t, two, 20, 900).Select(p => p.Seconds).Order().ToList();
        Assert.Equal(2, both.Count);
        Assert.Equal(110, both[0], 110 * 0.03);
        Assert.Equal(300, both[1], 300 * 0.03);

        // Noise alone: nothing strong.
        var noise = t.Select(_ => Gauss(random)).ToList();
        Assert.All(Periodogram.Peaks(t, noise, 20, 900), p => Assert.True(p.Power < 0.1));
    }

    /// <summary>
    /// An hour of 2 s guide frames along RA: the mount's periodic error (two waves) plus seeing noise; corrections
    /// move the star back at once. Returns the RA RMS over the last 20 minutes and the controller.
    /// </summary>
    private static (double Rms, GuideController Controller) GuideAgainstPeriodicError(RaAlgorithm algorithm, bool withPe = true, int seed = 9)
    {
        var random = new Random(seed);
        var cal = new GuideCalibration(0, 4, -Math.PI / 2, 4, 0, null, DateTime.UtcNow);
        var controller = new GuideController(new GuideAlgorithmSettings(RaAlgorithm: algorithm));
        double Gear(double t) => withPe ? 8 * Math.Sin(2 * Math.PI * t / 300) + 3 * Math.Sin(2 * Math.PI * t / 100 + 0.7) : 0;
        double corrected = 0;
        var late = new List<double>();
        for (var t = 0.0; t < 3600; t += 2)
        {
            var x = Gear(t) - corrected;                 // where the star is, along RA (px)
            var measured = x + 0.15 * Gauss(random);     // seeing
            var c = controller.Correct(measured, 0.15 * Gauss(random), cal, t);
            if (c.Ra is { } p) corrected += p.Milliseconds / 1000.0 * cal.RaRate * (p.Direction == GuideDirection.East ? 1 : -1);
            if (t >= 2400) late.Add(x);
        }
        return (Math.Sqrt(late.Average(v => v * v)), controller);
    }

    [Fact]
    public void PredictivePecBeatsHysteresisOnPeriodicError()
    {
        var (hysteresis, _) = GuideAgainstPeriodicError(RaAlgorithm.Hysteresis);
        var (pec, controller) = GuideAgainstPeriodicError(RaAlgorithm.PredictivePec);
        output.WriteLine($"RA RMS over the last 20 min: Hysteresis {hysteresis:0.000} px, Predictive PEC {pec:0.000} px ({100 * (1 - pec / hysteresis):0}% lower)");
        Assert.True(pec < hysteresis * 0.7, $"PEC {pec:0.000} px vs Hysteresis {hysteresis:0.000} px RMS");

        var status = controller.Pec.Status;
        Assert.Equal(PecState.Predicting, status.State);
        Assert.Contains(status.Periods, p => Math.Abs(p - 300) < 15);
        Assert.True(status.Quality > 0.8, $"quality {status.Quality}");
        Assert.InRange(status.AmplitudePixels, 7, 13);
    }

    [Fact]
    public void PredictivePecDoesNoHarmWithoutPeriodicError()
    {
        var (hysteresis, _) = GuideAgainstPeriodicError(RaAlgorithm.Hysteresis, withPe: false);
        var (pec, controller) = GuideAgainstPeriodicError(RaAlgorithm.PredictivePec, withPe: false);
        Assert.True(pec <= hysteresis * 1.1, $"PEC {pec:0.000} px vs Hysteresis {hysteresis:0.000} px RMS");
        Assert.Equal(PecState.Learning, controller.Pec.Status.State); // nothing worth predicting
    }

    [Fact]
    public void PecPredictorLearnsKeepsDithersSmoothAndResets()
    {
        var p = new PeriodicErrorPredictor();
        double Gear(double t) => 5 * Math.Sin(2 * Math.PI * t / 120);
        for (var t = 0.0; t < 200; t += 2) p.Add(t, Gear(t));
        Assert.Equal(PecState.Learning, p.Status.State); // under two cycles
        Assert.Null(p.Predict(200, 2));

        // A dither shifts the lock by 3 px along RA: the error jumps, but the recorded motion carries on.
        p.Applied(3);
        for (var t = 200.0; t < 400; t += 2) p.Add(t, Gear(t) - 3);
        Assert.Equal(PecState.Predicting, p.Status.State);
        Assert.InRange(p.Status.Periods[0], 117, 123);
        Assert.Equal(Gear(402) - Gear(400), p.Predict(400, 2)!.Value, 0.05);

        // A fixed period works from the same data.
        var fixedPeriod = new PeriodicErrorPredictor { PeriodOverride = 120 };
        for (var t = 0.0; t < 260; t += 2) fixedPeriod.Add(t, Gear(t));
        Assert.Equal([120.0], fixedPeriod.Status.Periods);

        // A long gap starts it over.
        string? why = null;
        p.Restarted += reason => why = reason;
        p.Add(500, 0);
        Assert.Contains("learning again", why);
        Assert.Equal(PecState.Learning, p.Status.State);
        p.Reset();
        Assert.Equal(0, p.Status.Minutes);
    }

    [Fact]
    public void DitherSettlingIsMarkedAndLeftOutOfTheRms()
    {
        var history = new GuideHistory(10);
        history.Add(0.5, -0.5, settling: false);
        history.Add(-0.5, 0.5, settling: false);
        history.MarkDither();
        history.Add(8, 6, settling: true);   // the star walking to its new place
        history.Add(3, 2, settling: true);
        history.Add(0.5, 0.5, settling: false);

        var stats = history.Stats(2); // 2″ a pixel
        Assert.Equal(3, stats.Samples);
        Assert.Equal(1.0, stats.RmsRa, 6);
        Assert.Equal(1.0, stats.PeakRa, 6);
        Assert.Equal("arcsec", stats.Units);

        var points = history.Points(2);
        Assert.Equal([false, false, true, false, false], points.Select(p => p.Dither));
        Assert.Equal([false, false, true, true, false], points.Select(p => p.Settling));
        Assert.Equal(16, points[2].Ra);

        for (var i = 0; i < 20; i++) history.Add(0, 0, false);
        Assert.Equal(10, history.Count);
        Assert.Equal("px", history.Stats(null).Units);
    }

    [Fact]
    public void DarksTakeOutHotPixelsAndKeepTheBackground()
    {
        static RawImage Frame(params (int I, float V)[] extra)
        {
            var pixels = Enumerable.Repeat(1000f, 100).ToArray();
            foreach (var (i, v) in extra) pixels[i] = v;
            return new RawImage(10, 10, pixels, 16);
        }

        // Median of three: a cosmic ray in one dark goes, the hot pixel (in all three) stays.
        var master = DarkFrames.Combine([Frame((5, 5000), (50, 9000)), Frame((5, 5000)), Frame((5, 5200))]);
        Assert.Equal(5000, master.Pixels[5]);
        Assert.Equal(1000, master.Pixels[50]);
        Assert.Equal(1, DarkFrames.HotPixels(master));

        // A light frame: the hot pixel and a star; subtracting leaves the star and the 1000 background.
        var light = Frame((5, 5000), (60, 3000));
        light.Headers["EXPTIME"] = "2";
        var clean = DarkFrames.Subtract(light, master);
        Assert.Equal(1000, clean.Pixels[5]);
        Assert.Equal(3000, clean.Pixels[60]);
        Assert.Equal(1000, clean.Pixels[0]);
        Assert.Equal("2", clean.Headers["EXPTIME"]);

        // Twice the dark's exposure: twice its hot signal.
        var longer = DarkFrames.Subtract(Frame((5, 9000)), master, 2);
        Assert.Equal(1000, longer.Pixels[5]);

        Assert.Throws<ArgumentException>(() => DarkFrames.Subtract(new RawImage(5, 20, new float[100], 16), master));
    }

    [Fact]
    public void ChoosesTheMatchingOrNearestDark()
    {
        Assert.Null(DarkFrames.Choose([], 2));
        Assert.Equal((1, 1.0), DarkFrames.Choose([1, 2, 4], 2));
        var (index, ratio) = DarkFrames.Choose([1, 2, 4], 3)!.Value; // nearer 4 than 2 (by ratio)
        Assert.Equal(2, index);
        Assert.Equal(0.75, ratio, 6);
        Assert.Equal((0, 0.5), DarkFrames.Choose([1, 2, 4], 0.5));
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
