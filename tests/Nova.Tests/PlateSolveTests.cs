using Microsoft.Extensions.Logging.Abstractions;
using Nova.Core.Astronomy;
using Nova.Core.PlateSolving;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class PrecessionTests
{
    [Fact]
    public void MatchesMeeusExample21b()
    {
        // θ Persei, J2000 position corrected for proper motion, precessed to 2028 Nov 13.19 (JD 2462088.69).
        var j2000 = new EquatorialCoordinates(41.054063 / 15, 49.227750);
        var date = DateTime.FromOADate(2462088.69 - 2415018.5);
        var jnow = Precession.J2000ToJNow(j2000, DateTime.SpecifyKind(date, DateTimeKind.Utc));

        Assert.Equal(41.547214, jnow.RaHours * 15, 3);
        Assert.Equal(49.348483, jnow.DecDegrees, 3);
    }

    [Fact]
    public void RoundTripsAndMovesTheRightAmountSince2000()
    {
        var m42 = new EquatorialCoordinates(5.588139, -5.391111);
        var now = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
        var jnow = Precession.J2000ToJNow(m42, now);
        var back = Precession.JNowToJ2000(jnow, now);

        Assert.Equal(m42.RaHours, back.RaHours, 9);
        Assert.Equal(m42.DecDegrees, back.DecDegrees, 9);
        // ~26 years of precession near the equator moves things by roughly 0.3-0.4°.
        Assert.InRange(Precession.Separation(m42, jnow), 0.25, 0.45);
    }

    [Fact]
    public void MeasuresSeparation()
    {
        Assert.Equal(1.0, Precession.Separation(new(0, 0), new(0, 1)), 9);
        Assert.Equal(1.0 / 60, Precession.Separation(new(5, -5), new(5, -5 + 1.0 / 60)), 9);
        Assert.Equal(15.0, Precession.Separation(new(0, 0), new(1, 0)), 9); // 1 h of RA on the equator
    }
}

public class AstapTests
{
    // As written by ASTAP's write_ini (astap_main.pas) — FloatToStrE style numbers.
    private const string SolvedIni = """
        PLTSOLVD=T
        CRPIX1= 6.4050000000000000E+002
        CRPIX2= 4.8050000000000000E+002
        CRVAL1= 8.3822083333333330E+001
        CRVAL2=-5.3911111111111110E+000
        CDELT1=-5.3888888888888890E-004
        CDELT2= 5.3888888888888890E-004
        CROTA1= 1.2000000000000000E+000
        CROTA2= 1.2500000000000000E+000
        CD1_1=-5.3876000000000000E-004
        CD1_2= 1.1800000000000000E-005
        CD2_1= 1.1700000000000000E-005
        CD2_2= 5.3876000000000000E-004
        CMDLINE=astap -f latest.fits -r 30
        DIMENSIONS=1280 x 960
        """;

    [Fact]
    public void ReadsASolution()
    {
        var result = AstapSolver.ParseResult(SolvedIni, 0);
        var s = result.Solution!;
        Assert.True(result.Solved);
        Assert.Equal(83.822083 / 15, s.Center.RaHours, 6);
        Assert.Equal(-5.391111, s.Center.DecDegrees, 6);
        Assert.Equal(1.94, s.ImageScale, 2);           // 5.389e-4 ° × 3600
        Assert.Equal(1.25, s.Rotation, 6);
        Assert.False(s.Flipped);                        // negative CD determinant = normal sky image
        Assert.Equal(1280 * 1.94 / 3600, s.FieldWidthDegrees, 2);
    }

    [Theory]
    [InlineData("PLTSOLVD=F\nCMDLINE=x\nDIMENSIONS=1280 x 960\nERROR=Not enough stars.\n", 2, "Not enough stars.")]
    [InlineData("PLTSOLVD=F\n", 1, "No solution found. Check the focal length and that the image has stars.")]
    [InlineData("", 32, "No star database found.")]
    public void ReportsFailures(string ini, int exitCode, string expected)
    {
        var result = AstapSolver.ParseResult(ini, exitCode);
        Assert.False(result.Solved);
        Assert.Equal(expected, result.Error);
    }

    [Fact]
    public void PassesHintsTheWayAstapExpects()
    {
        var solver = new AstapSolver("/opt/astap/astap", "/opt/astap", "d20");
        var args = solver.Arguments(new PlateSolveRequest("/tmp/x.fits", new EquatorialCoordinates(5.5, -5.25), 30, 0.62));
        Assert.Equal(
            ["-f", "/tmp/x.fits", "-r", "30", "-z", "0", "-ra", "5.5", "-spd", "84.75", "-fov", "0.62", "-d", "/opt/astap", "-D", "d20", "-log"],
            args);
    }

    [Fact]
    public void ReadsHowManyStarsAstapFound()
    {
        Assert.Equal(3, AstapSolver.StarsFound("22:32:32  Image height: 0.50 degrees\n22:32:32  Only 3 stars found in image. Abort\n"));
        Assert.Equal(29, AstapSolver.StarsFound("22:33:43  29 stars, 248 quads selected in the image. 88 database stars\n"));
        Assert.Null(AstapSolver.StarsFound("Using star database D80\n"));
    }

    [Fact]
    public void SaysHowToInstallWhenMissing()
    {
        var solver = new AstapSolver(Path.Combine(Path.GetTempPath(), "no-astap-here"));
        Assert.False(solver.Available);
        Assert.Contains("yay -S astap", solver.Problem);
    }
}

public class CenteringTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    [Fact]
    public async Task CentresTheSimulatorDespiteItsPointingError()
    {
        var options = Opts.Create(new NovaOptions
        {
            Backend = "Simulator", ConfigDirectory = _config, ImageDirectory = Path.Combine(_config, "images"),
            SolveExposureSeconds = 0, CenterToleranceArcmin = 1,
        });
        var hub = new NullHubContext();
        var log = new ActivityLog(hub, NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var optics = new OpticsService(equipment, options, log);
        var imaging = new ImagingService(equipment, optics, log, hub, options);
        var solver = new PlateSolveService(equipment, imaging, optics, log, options);
        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.Mount!.UnparkAsync(CancellationToken.None);

        // Before syncing, a plain slew misses: the simulated mount points ~15′ off.
        var target = new EquatorialCoordinates(5.6, -5.0);
        await equipment.Mount.SlewAsync(Precession.J2000ToJNow(target, DateTime.UtcNow), CancellationToken.None);
        await imaging.CaptureAsync(0, save: false, CancellationToken.None);
        var before = await solver.SolveLatestAsync(CancellationToken.None);
        var missBy = Precession.Separation(new(before.RaHours!.Value, before.DecDegrees!.Value), target) * 60;
        // How far depends on the time (pier side, simulated polar error); what matters is that it misses.
        Assert.InRange(missBy, 5, 90);

        Assert.True(solver.StartCentering(target, out var error), error);
        for (var i = 0; i < 300 && solver.Centering is not { Running: false }; i++) await Task.Delay(100);

        var result = solver.Centering!;
        Assert.Equal("Centred", result.Message.Split(',')[0]);
        Assert.True(result.ErrorArcmin <= 1, $"{result.ErrorArcmin}′ off");
        Assert.True(result.Attempt >= 2, "the first attempt should have needed a sync");
    }
}

public class CameraRotationTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    /// <summary>A frame whose top points at position angle <paramref name="pa"/> (unmirrored, as NOVA displays it).</summary>
    private static Wcs Turned(EquatorialCoordinates centre, double pa, int width, int height)
    {
        var s = 2.0 / 3600;
        var t = pa * Math.PI / 180;
        return new Wcs((width + 1) / 2.0, (height + 1) / 2.0, centre.RaHours * 15, centre.DecDegrees,
            -s * Math.Cos(t), -s * Math.Sin(t), s * Math.Sin(t), -s * Math.Cos(t));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(147.5)]
    [InlineData(300)]
    public void MeasuresTheFramesAngle(double pa)
    {
        foreach (var dec in new[] { -40.0, 0, 65 })
            Assert.Equal(pa, CameraAngle.OfTop(Turned(new(7.6, dec), pa, 3000, 2000), 3000, 2000), 1);
        Assert.Equal(0, CameraAngle.OfTop(Wcs.Simple(new(1, 20), 2, 3000, 2000), 3000, 2000), 3);
    }

    [Fact]
    public void TurnsTheShortWayAllowingUpsideDown()
    {
        Assert.Equal((-12.0, false), Round(CameraAngle.Turn(35, 47)));
        Assert.Equal((20.0, false), Round(CameraAngle.Turn(10, 350)));      // across north
        Assert.Equal((-10.0, true), Round(CameraAngle.Turn(10, 200)));      // 170° = 10° the other way, upside down
        Assert.Equal((0.0, true), Round(CameraAngle.Turn(90, 270)));
        static (double, bool) Round((double D, bool U) t) => (Math.Round(t.D, 6), t.U);
    }

    private async Task<(PlateSolveService Solver, EquipmentService Equipment)> RigAsync()
    {
        var options = Opts.Create(new NovaOptions
        {
            Backend = "Simulator", ConfigDirectory = _config, ImageDirectory = Path.Combine(_config, "images"),
            SolveExposureSeconds = 0, CenterToleranceArcmin = 1,
        });
        var hub = new NullHubContext();
        var log = new ActivityLog(hub, NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var optics = new OpticsService(equipment, options, log);
        var imaging = new ImagingService(equipment, optics, log, hub, options);
        var solver = new PlateSolveService(equipment, imaging, optics, log, options);
        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.Mount!.UnparkAsync(CancellationToken.None);
        return (solver, equipment);
    }

    [Fact]
    public async Task TurnsARotatorToThePlannedAngleLearningWhichWayItGoes()
    {
        var (solver, equipment) = await RigAsync();
        Assert.NotNull(equipment.Rotator);
        var target = new EquatorialCoordinates(5.6, -5.0);

        async Task<RotationStatus> Run(double angle)
        {
            Assert.True(solver.StartMatchingRotation(target, angle, out var error), error);
            for (var i = 0; i < 600 && solver.Rotation is not { Running: false }; i++)
            {
                Assert.False(solver.Rotation?.Waiting == true, "a rotator shouldn't need anyone");
                await Task.Delay(50);
            }
            return solver.Rotation!;
        }

        // The simulator's rotator turns the frame the opposite way to its angle: the first move goes the wrong way,
        // the next ones put it right, and NOVA remembers.
        var first = await Run(30);
        Assert.StartsWith("Camera at", first.Message);
        Assert.InRange(Math.Abs(CameraAngle.Turn(30, first.CameraAngle!.Value).Degrees), 0, 1);
        Assert.Equal("Simulated Rotator", first.Rotator);
        Assert.Equal(-1, solver.RotatorSense("Simulated Rotator"));

        // Knowing that, another angle takes a single move: measure, turn, measure.
        var second = await Run(100);
        Assert.InRange(Math.Abs(CameraAngle.Turn(100, second.CameraAngle!.Value).Degrees), 0, 1);
        Assert.Equal(2, second.Checks);
    }

    /// <summary>How far the angle a message starts with ("Camera at 359.7°, …") is from 0°, either way.</summary>
    private static double NorthOffset(string message, string prefix)
    {
        Assert.StartsWith(prefix, message);
        var text = message[prefix.Length..message.IndexOf('°')];
        var angle = double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        return Math.Min(angle % 360, 360 - angle % 360);
    }

    [Fact]
    public async Task AsksForATurnChecksAgainAndCanKeepItAsItIs()
    {
        var options = Opts.Create(new NovaOptions
        {
            Backend = "Simulator", ConfigDirectory = _config, ImageDirectory = Path.Combine(_config, "images"),
            SolveExposureSeconds = 0, CenterToleranceArcmin = 1,
        });
        var hub = new NullHubContext();
        var log = new ActivityLog(hub, NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var optics = new OpticsService(equipment, options, log);
        var imaging = new ImagingService(equipment, optics, log, hub, options);
        var solver = new PlateSolveService(equipment, imaging, optics, log, options) { UseRotator = false }; // turned by hand
        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.Mount!.UnparkAsync(CancellationToken.None);
        var target = new EquatorialCoordinates(5.6, -5.0);

        async Task<RotationStatus> Until(Func<RotationStatus, bool> done)
        {
            for (var i = 0; i < 300; i++)
            {
                if (solver.Rotation is { } r && done(r)) return r;
                await Task.Delay(50);
            }
            throw new TimeoutException(solver.Rotation?.Message);
        }

        // The simulator's camera is north-up: a plan of 0.5° is already there.
        Assert.True(solver.StartMatchingRotation(target, 0.5, out var error), error);
        var ok = await Until(r => !r.Running);
        // North-up in today's coordinates, a few tenths of a degree off J2000's north (more with the solve's scatter).
        Assert.InRange(NorthOffset(ok.Message, "Camera at "), 0, 0.6);

        // 30° wanted: turn it 30° anticlockwise (a larger angle).
        Assert.True(solver.StartMatchingRotation(target, 30, out error), error);
        var ask = await Until(r => r.Waiting);
        Assert.Equal(30, ask.TurnDegrees!.Value, 0.6);
        Assert.Equal("anticlockwise", ask.Direction);
        Assert.Matches(@"Turn the camera 30(\.\d)?° anticlockwise", ask.Message);

        // "Turned it" (the simulator's camera can't turn): it measures again and asks again.
        Assert.True(solver.RotationTurned());
        var again = await Until(r => r.Waiting && r.Checks == 2);
        Assert.Equal(30, again.TurnDegrees!.Value, 0.6);

        // Keep it as it is: done, after re-centring (it was "turned").
        Assert.True(solver.KeepRotation());
        var kept = await Until(r => !r.Running);
        Assert.InRange(NorthOffset(kept.Message, "Keeping the camera at "), 0, 0.6);
        Assert.False(solver.KeepRotation()); // nothing waiting any more
    }
}


public class ImageHistoryTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    [Fact]
    public async Task KeepsTheLastFewToLookBackAtAndCompare()
    {
        var options = Opts.Create(new NovaOptions
        {
            Backend = "Simulator", ConfigDirectory = _config, ImageDirectory = Path.Combine(_config, "images"), SolveExposureSeconds = 0,
        });
        var hub = new NullHubContext();
        var log = new ActivityLog(hub, NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var optics = new OpticsService(equipment, options, log);
        var imaging = new ImagingService(equipment, optics, log, hub, options);
        var solver = new PlateSolveService(equipment, imaging, optics, log, options);
        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.Mount!.UnparkAsync(CancellationToken.None);

        Assert.Equal(5, imaging.HistorySize);
        imaging.SetHistorySize(3);
        Assert.Throws<ArgumentException>(() => imaging.SetHistorySize(2));

        var first = await imaging.CaptureAsync(0, save: false, CancellationToken.None);
        imaging.Pin(first.Id, true);
        var ids = new List<int> { first.Id };
        for (var i = 0; i < 3; i++) ids.Add((await imaging.CaptureAsync(0, save: false, CancellationToken.None)).Id);
        // Not kept: like autofocus and polar alignment frames.
        var notKept = await imaging.CaptureAsync(0, save: false, CancellationToken.None, keep: false);

        // Three kept, newest first; the pinned first one stayed, the one after it went.
        var history = imaging.History;
        Assert.Equal([ids[3], ids[2], first.Id], history.Select(h => h.Info.Id));
        Assert.True(history[2].Pinned);
        Assert.Equal(notKept.Id, imaging.LatestImage!.Id);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_config, "image-history"), "*.fits").Length);

        // An older image can be previewed, measured and solved like the latest.
        Assert.NotNull(imaging.ThumbnailOf(ids[2]));
        Assert.NotNull(await imaging.GetPreviewAsync("auto", true, 512, ids[2]));
        Assert.NotNull(imaging.StarsOf(ids[2]));
        Assert.Contains(imaging.HeaderOf(ids[2])!, c => c.Key == "SIM-RA");
        var solve = await solver.SolveLatestAsync(CancellationToken.None, imageId: ids[2]);
        Assert.True(solve.Solved);
        Assert.Equal(ids[2], solver.SolveOf(ids[2])!.ImageId);
        Assert.NotNull(solver.Frame(ids[2]));
        Assert.Null(solver.Frame(ids[3])); // not solved

        // A bigger history keeps more from now on; unpinning lets the old one go when it's the oldest.
        imaging.Pin(first.Id, false);
        await imaging.CaptureAsync(0, save: false, CancellationToken.None);
        Assert.DoesNotContain(imaging.History, h => h.Info.Id == first.Id);
    }
}
