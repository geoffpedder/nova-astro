using Nova.Core.Astronomy;
using Nova.Core.Imaging;

namespace Nova.Tests;

public class SexagesimalTests
{
    [Theory]
    [InlineData("12.5", 12.5)]
    [InlineData("12:30:00", 12.5)]
    [InlineData("12 30 00", 12.5)]
    [InlineData("12h30m00s", 12.5)]
    [InlineData("-05:30", -5.5)]
    [InlineData("-05°30'00\"", -5.5)]
    [InlineData("+45", 45)]
    public void Parses(string text, double expected) =>
        Assert.Equal(expected, Sexagesimal.Parse(text), 9);

    [Fact]
    public void FormatsWithoutRoundingToSixty()
    {
        // 59.99s must carry into the next minute instead of printing "60.0s".
        Assert.Equal("05h36m00.0s", Sexagesimal.Format(5 + 35 / 60.0 + 59.99 / 3600, 'h'));
        Assert.Equal("-05°23'28\"", Sexagesimal.Format(-(5 + 23 / 60.0 + 28 / 3600.0), 'd', signed: true));
    }

    [Fact]
    public void RejectsOutOfRangeCoordinates()
    {
        Assert.Throws<FormatException>(() => EquatorialCoordinates.Parse("25", "0"));
        Assert.Throws<FormatException>(() => EquatorialCoordinates.Parse("1", "91"));
    }
}

public class FitsTests
{
    [Fact]
    public void RoundTripsUnsigned16BitData()
    {
        ushort[] pixels = [0, 1, 1000, 32767, 32768, 65535];
        var bytes = Fits.WriteUInt16(3, 2, pixels, [new FitsCard("EXPTIME", 2.5), new FitsCard("OBJECT", "M 42")]);

        Assert.Equal(0, bytes.Length % Fits.BlockSize);
        var image = Fits.Read(bytes);
        Assert.Equal(3, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(pixels.Select(p => (float)p), image.Pixels);
        Assert.Equal("2.5", image.Headers["EXPTIME"]);
        Assert.Equal("M 42", image.Headers["OBJECT"]);
    }

    [Fact]
    public void CardsAreEightyCharacters()
    {
        Assert.Equal(80, Fits.FormatCard(new FitsCard("NAXIS1", 1280, "width")).Length);
        Assert.Equal(80, Fits.FormatCard(new FitsCard("OBJECT", new string('x', 100))).Length);
    }
}

public class PreviewTests
{
    [Fact]
    public void StatisticsMatchSimpleData()
    {
        float[] pixels = [10, 20, 30, 40, 1000];
        var stats = Preview.ComputeStatistics(pixels);
        Assert.Equal(10, stats.Min);
        Assert.Equal(1000, stats.Max);
        Assert.Equal(220, stats.Mean, 3);
        Assert.Equal(30, stats.Median, 0);
        Assert.Equal(10, stats.Mad, 0);
    }

    [Fact]
    public void StretchPutsBackgroundNearTarget()
    {
        var random = new Random(1);
        var pixels = Enumerable.Range(0, 100_000).Select(_ => (float)(1000 + random.NextDouble() * 50)).ToArray();
        pixels[0] = 65535; // a saturated star
        var stats = Preview.ComputeStatistics(pixels);
        var stretched = Preview.AutoStretch(pixels, stats);

        var median = stretched.Order().ElementAt(stretched.Length / 2);
        Assert.InRange(median, 50, 80); // 0.25 * 255 ≈ 64
        Assert.Equal(255, stretched[0]);
    }

    [Fact]
    public void RendersPng()
    {
        var image = new RawImage(4000, 3000, new float[4000 * 3000], 16);
        var preview = Preview.Render(image, maxSize: 1600);
        Assert.Equal(3, preview.Binning);
        Assert.Equal(1333, preview.Width);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], preview.Png[..4]);
    }
}

public class SimulatorTests
{
    [Fact]
    public async Task OffersTwoCamerasWithTheirOwnSensorSizes()
    {
        await using var simulator = new Nova.Core.Simulation.SimulatorProvider();
        await simulator.ConnectAsync(CancellationToken.None);

        var cameras = simulator.Devices.Where(d => d.Kinds.Contains(Nova.Core.Equipment.DeviceKind.Camera)).ToList();
        Assert.Equal(["Simulated Camera", "Simulated Guide Camera"], cameras.Select(c => c.Name));
        Assert.Null(simulator.GetCamera("Simulated Mount"));

        var guide = simulator.GetCamera("Simulated Guide Camera")!;
        await guide.ConnectAsync(CancellationToken.None);
        var frame = await guide.ExposeAsync(new Nova.Core.Equipment.ExposureRequest(0), null, CancellationToken.None);
        Assert.Equal((640, 480), (frame.Image.Width, frame.Image.Height));
    }

    [Fact]
    public async Task CameraSettingsShapeTheFrame()
    {
        await using var simulator = new Nova.Core.Simulation.SimulatorProvider();
        var camera = simulator.GetCamera("Simulated Camera")!;
        await camera.ConnectAsync(CancellationToken.None);

        await camera.ApplyAsync(new Nova.Core.Equipment.CameraSettingsUpdate(Binning: 2, TargetTemperature: -10), CancellationToken.None);
        var controls = camera.Controls;
        Assert.True(controls.Cooler!.On); // setting a target starts the cooler
        Assert.Equal(-10, controls.Cooler.TargetTemperature);
        Assert.Equal(2, controls.Binning!.Value);

        var frame = await camera.ExposeAsync(new Nova.Core.Equipment.ExposureRequest(0), null, CancellationToken.None);
        Assert.Equal((640, 480), (frame.Image.Width, frame.Image.Height));
        Assert.Equal("2", frame.Image.Headers["XBINNING"]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            camera.ApplyAsync(new Nova.Core.Equipment.CameraSettingsUpdate(Binning: 9), CancellationToken.None));
    }
}

public class FileNamingTests
{
    private static readonly DateTime Time = new(2026, 10, 1, 22, 13, 46);

    [Fact]
    public void FollowsNinasStyle() =>
        Assert.Equal("2026-10-01_22-13-46_Light_Ha_-10.00_300.00s_0024.fits",
            FileNaming.FileName(new FrameDescription(Time, 300, Nova.Core.Equipment.FrameType.Light, "Ha", -10.004, FrameNumber: 24)));

    [Fact]
    public void SingleExposuresHaveNoNumber() =>
        Assert.Equal("2026-10-01_22-13-46_Light_L_-10.00_2.50s.fits",
            FileNaming.FileName(new FrameDescription(Time, 2.5, Nova.Core.Equipment.FrameType.Light, "L", -10)));

    [Fact]
    public void UsesNoneForAMissingFilterAndEmptyPartsForAnUnknownTypeOrTemperature()
    {
        Assert.Equal("2026-10-01_22-13-46__None__2.50s.fits", FileNaming.FileName(new FrameDescription(Time, 2.5, null, null, null)));
        Assert.Equal("2026-10-01_22-13-46_Dark_None_-5.00_60.00s_0001.fits",
            FileNaming.FileName(new FrameDescription(Time, 60, Nova.Core.Equipment.FrameType.Dark, "  ", -5, 1)));
        Assert.EndsWith("_12345.fits", FileNaming.FileName(new FrameDescription(Time, 1, null, "L", 0, 12345)));
    }

    [Theory]
    [InlineData("OIII 3nm", "OIII-3nm")]
    [InlineData("L/RGB", "L-RGB")]
    [InlineData("  Slot_1  ", "Slot-1")]
    [InlineData("Hα", "Hα")]
    [InlineData("///", null)]
    public void CleansFilterNames(string raw, string? expected) => Assert.Equal(expected, FileNaming.Clean(raw));

    [Fact]
    public void AvoidsNegativeZeroTemperature() =>
        Assert.Contains("_0.00_", FileNaming.FileName(new FrameDescription(Time, 1, null, null, -0.004)));

    [Theory]
    [InlineData("Light Frame", Nova.Core.Equipment.FrameType.Light)]
    [InlineData("Dark Frame", Nova.Core.Equipment.FrameType.Dark)]
    [InlineData("Flat Field", Nova.Core.Equipment.FrameType.Flat)]
    [InlineData("Bias Frame", Nova.Core.Equipment.FrameType.Bias)]
    public void ReadsFrameTypeFromFitsHeader(string header, Nova.Core.Equipment.FrameType expected) =>
        Assert.Equal(expected, FileNaming.FrameTypeFromHeader(header));

    [Fact]
    public void NeverOverwrites()
    {
        var folder = Directory.CreateTempSubdirectory("nova-names").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "a.fits"), "");
            File.WriteAllText(Path.Combine(folder, "a_2.fits"), "");
            Assert.Equal(Path.Combine(folder, "a_3.fits"), FileNaming.UniquePath(folder, "a.fits"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public class FitsHeaderTests
{
    [Fact]
    public void ReadsCardsInOrderWithComments()
    {
        var bytes = Fits.WriteUInt16(2, 1, [1, 2],
        [
            new FitsCard("FILTER", "Ha", "Filter"),
            new FitsCard("OBJECT", "It's M 42 / Orion"),
            new FitsCard("CCD-TEMP", -10.5, "sensor temperature [C]"),
        ]);
        var header = Fits.ReadHeader(bytes);

        Assert.Equal("SIMPLE", header[0].Key);
        var filter = header.Single(c => c.Key == "FILTER");
        Assert.Equal(("Ha", "Filter"), (filter.Value, filter.Comment));
        var obj = header.Single(c => c.Key == "OBJECT");
        Assert.Equal("It's M 42 / Orion", obj.Value); // a '/' inside a string isn't a comment
        Assert.Null(obj.Comment);
        Assert.Equal(("-10.5", "sensor temperature [C]"), (header.Single(c => c.Key == "CCD-TEMP").Value, header.Single(c => c.Key == "CCD-TEMP").Comment));
        Assert.DoesNotContain(header, c => c.Key == "END");
    }
}
