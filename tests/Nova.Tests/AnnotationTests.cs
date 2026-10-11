using Microsoft.Extensions.Logging.Abstractions;
using Nova.Core.Astronomy;
using Nova.Core.Catalogs;
using Nova.Core.PlateSolving;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class WcsTests
{
    [Fact]
    public void PixelAndSkyRoundTripForARotatedMirroredSolution()
    {
        // 2"/px, rotated 30°, mirrored, reference pixel off-centre — like a real ASTAP answer.
        var s = 2.0 / 3600;
        var rho = 30 * Math.PI / 180;
        var wcs = new Wcs(3000.5, 2000.5, 84.0, -1.5,
            s * Math.Cos(rho), -s * Math.Sin(rho), s * Math.Sin(rho), s * Math.Cos(rho));

        foreach (var (x, y) in new[] { (0.0, 0.0), (3000.0, 2000.0), (6247.0, 4175.0), (123.4, 3999.9) })
        {
            var sky = wcs.ToSky(x, y);
            var back = wcs.ToPixel(sky)!.Value;
            Assert.Equal(x, back.X, 6);
            Assert.Equal(y, back.Y, 6);
        }
        // The reference pixel (1-based 3000.5) is array position 2999.5 and sits exactly on CRVAL.
        var center = wcs.ToSky(2999.5, 1999.5);
        Assert.Equal(84.0, center.RaHours * 15, 9);
        Assert.Equal(-1.5, center.DecDegrees, 9);
    }

    [Fact]
    public void SimpleIsNorthUpEastLeft()
    {
        var wcs = Wcs.Simple(new EquatorialCoordinates(6, 20), arcsecPerPixel: 3.6, width: 1000, height: 800);
        var north = wcs.ToPixel(new EquatorialCoordinates(6, 20.1))!.Value;
        var east = wcs.ToPixel(new EquatorialCoordinates(6 + 0.1 / 15, 20))!.Value;
        Assert.Equal(499.5, north.X, 3);
        Assert.Equal(399.5 - 100, north.Y, 1); // 0.1° = 100 px, up
        Assert.True(east.X < 499.5);          // east is left
    }

    [Fact]
    public void ReadsTheWcsFromAstap()
    {
        var ini = "PLTSOLVD=T\nCRPIX1=3104.5\nCRPIX2=2088.5\nCRVAL1=83.82\nCRVAL2=-5.39\nCDELT1=-0.000555\nCDELT2=0.000555\n" +
                  "CROTA1=1.0\nCROTA2=1.0\nCD1_1=-0.000555\nCD1_2=-0.0000097\nCD2_1=-0.0000097\nCD2_2=0.000555\nDIMENSIONS=6248 x 4176\n";
        var wcs = AstapSolver.ParseResult(ini, 0).Solution!.Wcs!;
        Assert.Equal(3104.5, wcs.CrPix1);
        Assert.Equal(-0.0000097, wcs.Cd12);
        var center = wcs.ToSky(3103.5, 2087.5);
        Assert.Equal(83.82, center.RaHours * 15, 6);
    }
}

public class StarCatalogTests
{
    [Fact]
    public void FindsNamedStars()
    {
        Assert.True(StarCatalog.Count > 100_000);
        var orionBelt = StarCatalog.Near(new EquatorialCoordinates(5.6793, -1.9426), radiusDegrees: 0.5);
        Assert.Equal("Alnitak", orionBelt[0].Label);
        Assert.Contains("ζ Ori", orionBelt[0].Detail);
        Assert.Contains("HD 37742", orionBelt[0].Detail);
        Assert.True(orionBelt.Zip(orionBelt.Skip(1)).All(p => p.First.Magnitude <= p.Second.Magnitude), "brightest first");

        // Across RA 0h and near the pole too.
        Assert.Contains(StarCatalog.Near(new EquatorialCoordinates(2.53, 89.26), 0.2), s => s.Label == "Polaris");
        Assert.NotEmpty(StarCatalog.Near(new EquatorialCoordinates(23.99, 0), 1.0).Where(s => s.Position.RaHours < 1));
    }
}

public class AnnotationTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    [Fact]
    public async Task LabelsLandOnTheStarsInTheImage()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config, SolveExposureSeconds = 0 });
        var hub = new NullHubContext();
        var log = new ActivityLog(hub, NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var optics = new OpticsService(equipment, options, log);
        var imaging = new ImagingService(equipment, optics, log, hub, options);
        var solver = new PlateSolveService(equipment, imaging, optics, log, options);
        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.Mount!.UnparkAsync(CancellationToken.None);

        var alnitak = new EquatorialCoordinates(5.6793, -1.9426);
        await equipment.Mount.SlewAsync(Precession.J2000ToJNow(alnitak, DateTime.UtcNow), CancellationToken.None);
        await imaging.CaptureAsync(0.5, save: false, CancellationToken.None);
        Assert.Null(solver.Annotate()); // not solved yet

        // Centre first (a plain slew can miss by tens of arcminutes, more than this small field), then a real frame.
        await solver.CenterAndWaitAsync(alnitak, CancellationToken.None);
        await imaging.CaptureAsync(0.5, save: false, CancellationToken.None);
        Assert.True((await solver.SolveLatestAsync(CancellationToken.None)).Solved);

        var annotations = solver.Annotate()!;
        Assert.Equal(imaging.LatestImage!.Id, annotations.ImageId);
        Assert.Equal("Alnitak", annotations.Stars[0].Label);

        // Every label sits on a star: the pixel there is far above the sky background.
        var detected = imaging.LatestStars!.Stars;
        foreach (var star in annotations.Stars.Where(s => s.Magnitude < 9.5))
        {
            var near = detected.Any(d => Math.Abs(d.X - star.X) < 2 && Math.Abs(d.Y - star.Y) < 2);
            var bright = BrightAt(imaging, star.X, star.Y);
            Assert.True(near || bright, $"{star.Label} ({star.Magnitude}) at {star.X:0},{star.Y:0} isn't on a star");
        }
    }

    // Saturated stars aren't measured (no HFR), so for those check the pixels directly.
    private static bool BrightAt(ImagingService imaging, double x, double y)
    {
        var raw = typeof(ImagingService).GetField("_latestRaw", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(imaging) as Nova.Core.Imaging.RawImage;
        var pixels = raw!.Pixels;
        var median = pixels.OrderBy(p => p).ElementAt(pixels.Length / 2);
        var peak = 0f;
        for (var dy = -2; dy <= 2; dy++)
        for (var dx = -2; dx <= 2; dx++)
        {
            int px = (int)Math.Round(x) + dx, py = (int)Math.Round(y) + dy;
            if (px >= 0 && py >= 0 && px < raw.Width && py < raw.Height) peak = Math.Max(peak, pixels[py * raw.Width + px]);
        }
        return peak > median * 3;
    }
}
