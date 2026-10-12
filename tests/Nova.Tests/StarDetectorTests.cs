using Nova.Core.Equipment;
using Nova.Core.Imaging;
using Nova.Core.Simulation;

namespace Nova.Tests;

public class StarDetectorTests
{
    /// <summary>A 16-bit frame with Gaussian stars of known size on a noisy background.</summary>
    private static RawImage Synthetic(int width, int height, double sigma, IEnumerable<(double X, double Y, double Flux)> stars,
        double background = 1000, double noise = 10, int seed = 1)
    {
        var random = new Random(seed);
        var pixels = new float[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            // Box-Muller
            var g = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
            pixels[i] = (float)(background + g * noise);
        }
        foreach (var (sx, sy, flux) in stars)
        {
            var norm = flux / (2 * Math.PI * sigma * sigma);
            for (var y = (int)(sy - 6 * sigma); y <= sy + 6 * sigma; y++)
            for (var x = (int)(sx - 6 * sigma); x <= sx + 6 * sigma; x++)
            {
                if (x < 0 || y < 0 || x >= width || y >= height) continue;
                var d2 = (x - sx) * (x - sx) + (y - sy) * (y - sy);
                pixels[y * width + x] += (float)(norm * Math.Exp(-d2 / (2 * sigma * sigma)));
            }
        }
        return new RawImage(width, height, pixels, 16);
    }

    private static IEnumerable<(double, double, double)> Grid(int width, int height, int step, double flux) =>
        from y in Enumerable.Range(1, height / step - 1)
        from x in Enumerable.Range(1, width / step - 1)
        select ((double)x * step + 0.3, (double)y * step + 0.6, flux);

    [Theory]
    [InlineData(1.5)]
    [InlineData(2.5)]
    [InlineData(4.0)]
    public void MeasuresHfrOfGaussianStars(double sigma)
    {
        var image = Synthetic(600, 400, sigma, Grid(600, 400, 80, 60_000));
        var result = StarDetector.Analyze(image);

        Assert.Equal(6 * 4, result.Stars.Count); // every star on the grid (6 × 4), nothing else
        // For a Gaussian, the half-flux radius is sigma × sqrt(2 ln 2) ≈ 1.1774 σ.
        Assert.Equal(1.1774 * sigma, result.MedianHfr!.Value, 1.1774 * sigma * 0.1);
        Assert.Equal(2 * result.MedianHfr.Value, result.MedianFwhm!.Value, 9);
    }

    [Fact]
    public void FindsCentresAccurately()
    {
        var image = Synthetic(200, 200, 2, [(100.3, 80.7, 50_000)]);
        var star = Assert.Single(StarDetector.Analyze(image).Stars);
        Assert.Equal(100.3, star.X, 0.1);
        Assert.Equal(80.7, star.Y, 0.1);
    }

    [Fact]
    public void IgnoresHotPixelsSaturatedStarsAndEdges()
    {
        var image = Synthetic(300, 300, 2,
        [
            (150, 150, 40_000),       // a good star
            (60, 60, 20_000_000),     // so bright it clips: saturated
            (4, 150, 40_000),         // at the edge
        ]);
        image.Pixels[200 * 300 + 220] = 60_000; // a hot pixel
        image.Pixels[220 * 300 + 90] = 30_000;  // another
        for (var i = 0; i < image.Pixels.Length; i++) image.Pixels[i] = Math.Min(image.Pixels[i], 65_535);

        var star = Assert.Single(StarDetector.Analyze(image).Stars);
        Assert.Equal(150, star.X, 0.2);
    }

    [Fact]
    public void FindsNothingInPureNoise()
    {
        var result = StarDetector.Analyze(Synthetic(500, 500, 2, []));
        Assert.Empty(result.Stars);
        Assert.Null(result.MedianHfr);
    }

    [Fact]
    public void HandlesColourFramesAndReportsFullResolutionPixels()
    {
        var mono = Synthetic(400, 300, 3, Grid(400, 300, 100, 80_000));
        var colour = new RawImage(mono.Width, mono.Height, mono.Pixels, 16);
        colour.Headers["BAYERPAT"] = "RGGB";

        var m = StarDetector.Analyze(mono);
        var c = StarDetector.Analyze(colour);
        Assert.Equal(m.Stars.Count, c.Stars.Count);
        Assert.Equal(m.MedianHfr!.Value, c.MedianHfr!.Value, m.MedianHfr.Value * 0.15); // same stars, same size
    }

    [Fact]
    public void FindsJustTheStarsInAQuantisedGuideFrame()
    {
        // A ZWO guide camera: 12-bit values in 16-bit (multiples of 16) on a quiet background, so most of the
        // background is one exact value. The noise must still be measured, or every speck above it is a "star".
        var image = Synthetic(640, 480, 1.5, Grid(640, 480, 120, 30_000), background: 400, noise: 6, seed: 3);
        for (var i = 0; i < image.Pixels.Length; i++) image.Pixels[i] = MathF.Round(image.Pixels[i] / 16) * 16;
        var expected = Grid(640, 480, 120, 30_000).Count();

        var analysis = StarDetector.Analyze(image);
        Assert.InRange(analysis.Stars.Count, expected - 2, expected);
    }

    [Fact]
    public async Task SimulatedStarsGrowWhenDefocused()
    {
        await using var simulator = new SimulatorProvider();
        var camera = simulator.GetCamera("Simulated Guide Camera")!; // mono, so no Bayer step
        var focuser = simulator.GetFocuser("Simulated Focuser")!;
        await camera.ConnectAsync(CancellationToken.None);
        await focuser.ConnectAsync(CancellationToken.None);

        // The guide camera isn't wired to the focuser; use the main camera for the focus check below.
        var main = simulator.GetCamera("Simulated Camera")!;
        await main.ConnectAsync(CancellationToken.None);
        await main.ApplyAsync(new CameraSettingsUpdate(Binning: 2), CancellationToken.None); // binned = mono

        async Task<double> HfrAt(int position)
        {
            await focuser.MoveToAsync(position, CancellationToken.None);
            var frame = await main.ExposeAsync(new ExposureRequest(2), null, CancellationToken.None);
            return StarDetector.Analyze(frame.Image).MedianHfr!.Value;
        }

        var focused = await HfrAt(25_000);
        var slightlyOut = await HfrAt(25_800);
        var farOut = await HfrAt(27_000);
        Assert.True(focused < slightlyOut && slightlyOut < farOut, $"{focused:0.00} < {slightlyOut:0.00} < {farOut:0.00}");
    }
}
