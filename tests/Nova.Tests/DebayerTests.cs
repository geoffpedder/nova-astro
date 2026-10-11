using Nova.Core.Equipment;
using Nova.Core.Imaging;
using Nova.Core.Simulation;

namespace Nova.Tests;

public class DebayerTests
{
    [Theory]
    [InlineData(BayerPattern.RGGB, 0, 0, BayerPattern.RGGB)]
    [InlineData(BayerPattern.RGGB, 1, 0, BayerPattern.GRBG)]
    [InlineData(BayerPattern.RGGB, 0, 1, BayerPattern.GBRG)]
    [InlineData(BayerPattern.RGGB, 1, 1, BayerPattern.BGGR)]
    [InlineData(BayerPattern.GBRG, 1, 1, BayerPattern.GRBG)]
    public void ShiftsPatternForOddOffsets(BayerPattern pattern, int dx, int dy, BayerPattern expected) =>
        Assert.Equal(expected, Debayer.Shift(pattern, dx, dy));

    [Fact]
    public void ReadsPatternAndOffsetsFromHeaders()
    {
        var image = new RawImage(2, 2, new float[4], 16);
        Assert.Null(Debayer.FromHeaders(image));

        image.Headers["BAYERPAT"] = "RGGB";
        image.Headers["XBAYROFF"] = "1";
        Assert.Equal(BayerPattern.GRBG, Debayer.FromHeaders(image));
    }

    [Fact]
    public void SuperPixelTakesEachColourFromItsCell()
    {
        // Two 2×2 RGGB cells side by side: R=10, G=20 and 30, B=40, then R=1, G=2 and 4, B=8.
        float[] mosaic =
        [
            10, 20, 1, 2,
            30, 40, 4, 8,
        ];
        var planes = Debayer.SuperPixel(new RawImage(4, 2, mosaic, 16), BayerPattern.RGGB);

        Assert.Equal((2, 1), (planes.Width, planes.Height));
        Assert.Equal([10f, 1f], planes.Red);
        Assert.Equal([25f, 3f], planes.Green);
        Assert.Equal([40f, 8f], planes.Blue);

        var bggr = Debayer.SuperPixel(new RawImage(4, 2, mosaic, 16), BayerPattern.BGGR);
        Assert.Equal([40f, 8f], bggr.Red);
    }

    [Fact]
    public void RendersRgbPng()
    {
        var random = new Random(3);
        var image = new RawImage(400, 300, Enumerable.Range(0, 400 * 300).Select(_ => (float)random.Next(900, 1100)).ToArray(), 16);
        var preview = Preview.RenderColor(image, BayerPattern.RGGB, maxSize: 100);

        Assert.Equal((100, 75), (preview.Width, preview.Height));
        Assert.Equal(4, preview.Binning); // 2 from the superpixel, 2 from downsampling
        Assert.Equal(2, preview.Png[25]); // IHDR colour type: RGB
    }

    [Theory]
    [InlineData(BayerPattern.RGGB)]
    [InlineData(BayerPattern.BGGR)]
    [InlineData(BayerPattern.GRBG)]
    [InlineData(BayerPattern.GBRG)]
    public void BilinearGivesEveryPixelAllThreeColours(BayerPattern pattern)
    {
        // A flat colour seen through the filter pattern (red 200, green 120, blue 40) must come back as that colour
        // at every pixel, edges and corners included.
        const int w = 6, h = 4;
        var cells = pattern.ToString();
        var mosaic = new float[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            mosaic[y * w + x] = cells[(y & 1) * 2 + (x & 1)] switch { 'R' => 200, 'G' => 120, _ => 40 };

        Func<float, byte> asIs = v => (byte)v;
        var rgb = Debayer.Bilinear(new RawImage(w, h, mosaic, 16), pattern, [asIs, asIs, asIs]);

        Assert.Equal(w * h * 3, rgb.Length);
        for (var i = 0; i < w * h; i++) Assert.Equal((200, 120, 40), (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]));
    }

    [Fact]
    public void FullResolutionColourPreviewKeepsEveryPixel()
    {
        var random = new Random(4);
        var image = new RawImage(400, 300, Enumerable.Range(0, 400 * 300).Select(_ => (float)random.Next(900, 1100)).ToArray(), 16);

        var full = Preview.RenderColor(image, BayerPattern.RGGB, maxSize: 400); // what "Full resolution" asks for
        Assert.Equal((400, 300, 1), (full.Width, full.Height, full.Binning));

        var half = Preview.RenderColor(image, BayerPattern.RGGB, maxSize: 200); // superpixels, as before
        Assert.Equal((200, 150, 2), (half.Width, half.Height, half.Binning));
    }

    [Fact]
    public async Task SimulatedColourCameraWritesBayerHeaderOnlyWhenUnbinned()
    {
        await using var simulator = new SimulatorProvider();
        var camera = simulator.GetCamera("Simulated Camera")!;
        await camera.ConnectAsync(CancellationToken.None);

        var unbinned = await camera.ExposeAsync(new ExposureRequest(0), null, CancellationToken.None);
        Assert.Equal(BayerPattern.RGGB, Debayer.FromHeaders(unbinned.Image));

        await camera.ApplyAsync(new CameraSettingsUpdate(Binning: 2), CancellationToken.None);
        var binned = await camera.ExposeAsync(new ExposureRequest(0), null, CancellationToken.None);
        Assert.Null(Debayer.FromHeaders(binned.Image));

        var guide = simulator.GetCamera("Simulated Guide Camera")!;
        await guide.ConnectAsync(CancellationToken.None);
        Assert.Null(Debayer.FromHeaders((await guide.ExposeAsync(new ExposureRequest(0), null, CancellationToken.None)).Image));
    }
}

public class LinearPreviewTests
{
    [Fact]
    public void UsesSensorFullScaleForSixteenBitData()
    {
        float[] pixels = [0, 32768, 65535, 70000];
        var image = new RawImage(4, 1, pixels, 16);
        var range = Preview.FullScale(image, Preview.ComputeStatistics(pixels));

        Assert.Equal((0d, 65535d), range);
        Assert.Equal([0, 128, 255, 255], Preview.Linear(pixels, range)); // a half-full pixel is mid-grey
    }

    [Fact]
    public void LinearPreviewDiffersFromStretched()
    {
        var random = new Random(5);
        var pixels = Enumerable.Range(0, 200 * 100).Select(_ => (float)random.Next(1000, 1100)).ToArray();
        var image = new RawImage(200, 100, pixels, 16);

        var stretched = Preview.Render(image, stretch: true);
        var linear = Preview.Render(image, stretch: false);
        Assert.NotEqual(stretched.Png, linear.Png);
        Assert.NotEqual(Preview.RenderColor(image, BayerPattern.RGGB).Png, Preview.RenderColor(image, BayerPattern.RGGB, stretch: false).Png);
    }
}
