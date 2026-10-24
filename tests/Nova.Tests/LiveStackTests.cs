using Nova.Core.Stacking;
using Nova.Server.Services;

namespace Nova.Tests;

public class LiveStackTests
{
    // A field of stars in the stack's pixels: positions and brightnesses.
    private static List<StarPoint> Field(int count, int width, int height, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count)
            .Select(_ => new StarPoint(20 + random.NextDouble() * (width - 40), 20 + random.NextDouble() * (height - 40), 200 + random.NextDouble() * 5000))
            .ToList();
    }

    // The same stars as a frame turned and shifted (frame = inverse(T)(reference)), with centroid jitter.
    private static List<StarPoint> Seen(List<StarPoint> reference, Similarity frameToReference, int seed, double jitter = 0.15)
    {
        var toFrame = frameToReference.Inverse();
        var random = new Random(seed);
        return reference.Select(s =>
        {
            var (x, y) = toFrame.Apply(s.X, s.Y);
            return new StarPoint(x + (random.NextDouble() - 0.5) * jitter, y + (random.NextDouble() - 0.5) * jitter, s.Flux);
        }).ToList();
    }

    private static Similarity Turn(double degrees, double tx, double ty) =>
        new(Math.Cos(degrees * Math.PI / 180), Math.Sin(degrees * Math.PI / 180), tx, ty);

    [Theory]
    [InlineData(0, 13.4, -7.2)]     // drift
    [InlineData(1.5, -40, 25)]      // a dither and a little field rotation
    [InlineData(180, 1600, 1050)]   // a meridian flip
    public void FindsHowAFrameIsShiftedAndTurned(double degrees, double tx, double ty)
    {
        var reference = Field(120, 1560, 1040, seed: 1);
        var truth = Turn(degrees, tx, ty);
        var frame = Seen(reference, truth, seed: 2);

        var match = StarMatcher.Match(reference, frame);
        Assert.NotNull(match.Transform);
        Assert.True(match.Matched >= 60, $"{match.Matched} matched");
        var fit = match.Transform!.Value;
        // Anywhere in the frame lands within a tenth of a pixel of where it should.
        foreach (var (x, y) in new[] { (0.0, 0.0), (1560.0, 0.0), (0.0, 1040.0), (780.0, 520.0) })
        {
            var (ax, ay) = fit.Apply(x, y);
            var (bx, by) = truth.Apply(x, y);
            Assert.True(Math.Abs(ax - bx) < 0.1 && Math.Abs(ay - by) < 0.1, $"({x},{y}): {ax:0.00},{ay:0.00} vs {bx:0.00},{by:0.00}");
        }
    }

    [Fact]
    public void CopesWithStarsLostOrAddedAndRefusesAStrangerField()
    {
        var reference = Field(100, 1560, 1040, seed: 3);
        var truth = Turn(0.7, 22, -15);
        // Thin cloud: a third of the stars gone, and some that aren't in the reference (noise, a satellite).
        var frame = Seen(reference, truth, seed: 4).Where((_, i) => i % 3 != 0).Concat(Field(10, 1560, 1040, seed: 5)).ToList();
        var match = StarMatcher.Match(reference, frame);
        Assert.NotNull(match.Transform);
        Assert.True(Math.Abs(match.Transform!.Value.Tx - truth.Tx) < 0.3);

        var stranger = StarMatcher.Match(reference, Field(100, 1560, 1040, seed: 99));
        Assert.True(stranger.Transform is null || stranger.Matched < 10, $"a stranger field matched {stranger.Matched} stars");
    }

    // A frame in the stack's pixels: sky, Gaussian stars at the given positions, noise.
    private static StackFrame Render(List<StarPoint> stars, int width, int height, int seed, double sky = 1000, double noise = 20)
    {
        var random = new Random(seed);
        var plane = new float[width * height];
        for (var i = 0; i < plane.Length; i++)
        {
            // Box–Muller noise.
            var g = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
            plane[i] = (float)(sky + noise * g);
        }
        foreach (var s in stars)
            for (var y = (int)s.Y - 6; y <= s.Y + 6; y++)
            for (var x = (int)s.X - 6; x <= s.X + 6; x++)
                if (x >= 0 && y >= 0 && x < width && y < height)
                    plane[y * width + x] += (float)(s.Flux / 10 * Math.Exp(-((x - s.X) * (x - s.X) + (y - s.Y) * (y - s.Y)) / (2 * 1.5 * 1.5)));
        return new StackFrame([plane], width, height, false);
    }

    [Fact]
    public void StackingLowersTheNoiseAndLeavesOutASatellite()
    {
        const int w = 300, h = 200;
        var stars = Field(40, w, h, seed: 6);
        var stack = new LiveStack(w, h, 1);
        for (var i = 0; i < 16; i++)
        {
            var shift = new Similarity(1, 0, (i % 4) * 3.3, (i / 4) * -2.1); // dithered
            var frame = Render(Seen(stars, shift, seed: 10 + i, jitter: 0), w, h, seed: 100 + i);
            if (i == 9)
                for (var x = 0; x < w; x++) frame.Planes[0][100 * w + x] += 5000; // a satellite trail across row 100
            stack.Add(frame, shift);
        }
        Assert.Equal(16, stack.Frames);
        var result = stack.Result()[0];

        // Noise in a starless corner: 20 per frame, about 20/√16 = 5 stacked (a little more for interpolation).
        var corner = Enumerable.Range(0, 400).Select(i => result[(150 + i / 20) * w + 270 + i % 20]).Where(v => v < 1100).ToArray();
        var mean = corner.Average();
        var sd = Math.Sqrt(corner.Average(v => (v - mean) * (v - mean)));
        Assert.InRange(sd, 2, 9);

        // The trail is gone: row 100 is no brighter than the rows beside it.
        double Row(int y) => Enumerable.Range(10, w - 20).Average(x => (double)result[y * w + x]);
        Assert.True(Row(100) - (Row(90) + Row(110)) / 2 < 50, $"trail left {Row(100) - (Row(90) + Row(110)) / 2:0}");
    }

    [Fact]
    public void RemovesHotPixelsButNotStars()
    {
        const int w = 60, h = 40;
        var frame = Render([new StarPoint(30, 20, 20000)], w, h, seed: 7);
        var plane = frame.Planes[0];
        var starPeak = plane[20 * w + 30];
        plane[10 * w + 10] = 60000; // a hot pixel
        LiveStack.RemoveHotPixels(plane, w, h);
        Assert.InRange(plane[10 * w + 10], 900, 1100);
        Assert.Equal(starPeak, plane[20 * w + 30]);
    }
}

public sealed class LiveStackServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nova-livestack-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private async Task<(ImagingService Imaging, LiveStackService Stacks, ActivityLog Log)> SetUpAsync()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new Nova.Server.Services.NovaOptions
        {
            Backend = "Simulator", ConfigDirectory = Path.Combine(_folder, "config"), ImageDirectory = Path.Combine(_folder, "images"),
        });
        var hub = new NullHubContext();
        var log = new Nova.Server.Services.ActivityLog(hub, Microsoft.Extensions.Logging.Abstractions.NullLogger<Nova.Server.Services.ActivityLog>.Instance);
        var equipment = new Nova.Server.Services.EquipmentService(options, log);
        var imaging = new Nova.Server.Services.ImagingService(equipment, new Nova.Server.Services.OpticsService(equipment, options, log), log, hub, options);
        var stacks = new LiveStackService(imaging, log);
        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.Mount!.UnparkAsync(CancellationToken.None);
        return (imaging, stacks, log);
    }

    [Fact]
    public async Task StacksExposeFramesOnlyWhileSwitchedOnAndSavesTheStack()
    {
        var (imaging, stacks, _) = await SetUpAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        await imaging.CaptureAsync(0.5, save: false, timeout.Token, stackAs: LiveStackService.Manual); // switched off
        await imaging.CaptureAsync(0.5, save: false, timeout.Token);                                   // autofocus, centring...
        await stacks.WaitForQueueAsync(timeout.Token);
        Assert.Null(stacks.Status.Current);

        stacks.SetEnabled(true);
        for (var i = 0; i < 3; i++) await imaging.CaptureAsync(0.5, save: false, timeout.Token, stackAs: LiveStackService.Manual);
        await stacks.WaitForQueueAsync(timeout.Token);
        var current = stacks.Status.Current!;
        Assert.True(current.Frames == 3, $"{current.Frames} stacked, {current.Rejected} left out: {current.LastRejected}");
        Assert.Equal(1.5, current.IntegrationSeconds, 3);
        Assert.NotNull(stacks.Preview(null, 800));

        var path = await stacks.SaveAsync(null);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(Path.ChangeExtension(path, ".png")));
        var fits = Nova.Core.Imaging.Fits.Read(File.ReadAllBytes(path!));
        Assert.Equal(current.Width, fits.Width);
    }

    [Fact]
    public async Task ASequenceTargetsStackIsSavedAndClearedWhenItEnds()
    {
        var (imaging, stacks, _) = await SetUpAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        for (var i = 0; i < 2; i++) await imaging.CaptureAsync(0.5, save: true, timeout.Token, "M 31", stackAs: "M 31");
        await stacks.FinishTargetAsync("M 31");
        Assert.Empty(stacks.Status.Stacks);
        Assert.Contains(Directory.EnumerateFiles(Path.Combine(_folder, "images"), "Stack_*.fits", SearchOption.AllDirectories),
            f => f.Contains(Path.Combine("M 31", "Stacked")));
    }
}
