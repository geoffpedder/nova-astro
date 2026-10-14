using Microsoft.Extensions.Logging.Abstractions;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class WebcamTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nova-webcam-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static byte[] Jpeg(byte fill, int length) =>
        [0xFF, 0xD8, .. Enumerable.Repeat(fill, length), 0xFF, 0xD9];

    [Fact]
    public void SplitsMjpegIntoFramesAcrossReads()
    {
        var splitter = new MjpegSplitter();
        byte[] stream = [0x00, 0x13, .. Jpeg(1, 10), .. Jpeg(2, 5), 0x42, .. Jpeg(3, 7)];
        var frames = new List<byte[]>();
        // Fed in awkward pieces: markers split between reads, junk between frames.
        foreach (var size in new[] { 1, 3, 2, 7, 1, 1, 9, 100 })
        {
            var take = Math.Min(size, stream.Length);
            if (take == 0) break;
            frames.AddRange(splitter.Add(stream.AsSpan(0, take)));
            stream = stream[take..];
        }
        Assert.Equal(3, frames.Count);
        Assert.Equal(Jpeg(1, 10), frames[0]);
        Assert.Equal(Jpeg(2, 5), frames[1]);
        Assert.Equal(Jpeg(3, 7), frames[2]);
    }

    [Fact]
    public void ListsWebcamsFromSys()
    {
        // A webcam shows up as a capture node (index 0) and a metadata node (index 1); only the first is a picture.
        void Node(string name, string label, string index)
        {
            var dir = Directory.CreateDirectory(Path.Combine(_folder, "sys", name)).FullName;
            File.WriteAllText(Path.Combine(dir, "name"), label + "\n");
            File.WriteAllText(Path.Combine(dir, "index"), index + "\n");
        }
        Node("video0", "HD USB Camera", "0");
        Node("video1", "HD USB Camera", "1");
        Node("video2", "Logitech C270", "0");

        var devices = WebcamService.ListDevices(Path.Combine(_folder, "sys")).ToList();
        Assert.Equal([new WebcamDevice("/dev/video0", "HD USB Camera"), new WebcamDevice("/dev/video2", "Logitech C270")], devices);
        Assert.Empty(WebcamService.ListDevices(Path.Combine(_folder, "nothing")));
    }

    /// <summary>A webcam that counts its starts and stops and sends numbered frames.</summary>
    private sealed class FakeSource : IWebcamSource
    {
        public int Starts, Stops;

        public async Task RunAsync(string device, WebcamSettings settings, Action<WebcamFrame> frame, Action<string> note, CancellationToken ct)
        {
            Interlocked.Increment(ref Starts);
            try
            {
                for (byte i = 0; !ct.IsCancellationRequested; i++)
                {
                    frame(new WebcamFrame([0xFF, 0xD8, i, 0xFF, 0xD9], "image/jpeg", DateTime.UtcNow));
                    await Task.Delay(20, ct);
                }
            }
            finally
            {
                Interlocked.Increment(ref Stops);
            }
        }
    }

    private WebcamService Service(FakeSource source)
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _folder });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        return new WebcamService(options, log, Path.Combine(_folder, "no-sys"))
        {
            TestCard = source, Webcams = source, StopAfter = TimeSpan.FromMilliseconds(300),
        };
    }

    [Fact]
    public async Task CapturesOnlyWhileWatched()
    {
        var source = new FakeSource();
        await using var webcam = Service(source);
        Assert.Equal("Idle", webcam.Status.State);
        Assert.Equal("sim", Assert.Single(webcam.Status.Devices).Path); // the simulator's test card

        // Two viewers share one capture.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = await webcam.Watch(cts.Token).Take(3).ToListAsync(cts.Token);
        Assert.Equal(3, first.Count);
        Assert.All(first, f => Assert.Equal("image/jpeg", f.ContentType));
        Assert.Equal(1, source.Starts);

        // The viewer went: it carries on briefly (a tab switch or reload), then stops.
        var again = await webcam.Watch(cts.Token).Take(1).ToListAsync(cts.Token);
        Assert.Single(again);
        Assert.Equal(1, source.Starts); // came back in time: the same capture
        await Task.Delay(800, cts.Token);
        Assert.Equal(1, source.Stops);
        Assert.Equal("Idle", webcam.Status.State);

        // A snapshot with nobody watching starts it just for the picture.
        var snapshot = await webcam.SnapshotAsync(cts.Token);
        Assert.NotNull(snapshot);
        Assert.Equal(2, source.Starts);
    }

    [Fact]
    public async Task SavesAndChecksSettings()
    {
        await using (var webcam = Service(new FakeSource()))
        {
            webcam.UpdateSettings(new WebcamSettings("sim", 320, 240, 10));
            Assert.Throws<ArgumentException>(() => webcam.UpdateSettings(new WebcamSettings("sim", 640, 480, 0)));
            Assert.Throws<ArgumentException>(() => webcam.UpdateSettings(new WebcamSettings("/dev/video9")));
        }
        await using var reopened = Service(new FakeSource());
        Assert.Equal(new WebcamSettings("sim", 320, 240, 10), reopened.Settings);
    }

    [Fact]
    public async Task WaitsForAWebcamToBePluggedInAndCopesWithFailures()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Indi", ConfigDirectory = _folder });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var sys = Path.Combine(_folder, "sys");
        var source = new FakeSource();
        await using var webcam = new WebcamService(options, log, sys) { Webcams = source, RetryAfter = TimeSpan.FromMilliseconds(100) };

        // Watched with nothing plugged in: it waits, saying so.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var frames = webcam.Watch(cts.Token).GetAsyncEnumerator(cts.Token);
        var first = frames.MoveNextAsync().AsTask();
        await Task.Delay(300, cts.Token);
        Assert.Equal("Waiting", webcam.Status.State);
        Assert.Contains("No webcam found", webcam.Status.Error);
        Assert.False(first.IsCompleted);

        // Plugged in: it starts by itself and the waiting viewer gets frames.
        var dir = Directory.CreateDirectory(Path.Combine(sys, "video0")).FullName;
        File.WriteAllText(Path.Combine(dir, "name"), "USB Camera");
        File.WriteAllText(Path.Combine(dir, "index"), "0");
        Assert.True(await first.WaitAsync(cts.Token));
        Assert.Equal("Streaming", webcam.Status.State);
        Assert.Null(webcam.Status.Error);
        await frames.DisposeAsync();
    }

    [Fact]
    public async Task TriesAgainAfterTheWebcamFails()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _folder });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var flaky = new FlakySource();
        await using var webcam = new WebcamService(options, log, Path.Combine(_folder, "no-sys"))
        {
            TestCard = flaky, RetryAfter = TimeSpan.FromMilliseconds(100),
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // The first start fails ("busy"); the next works.
        var frame = await webcam.Watch(cts.Token).FirstAsync(cts.Token);
        Assert.NotNull(frame);
        Assert.Equal(2, flaky.Starts);
        Assert.Equal("Streaming", webcam.Status.State);
    }

    private sealed class FlakySource : IWebcamSource
    {
        public int Starts;

        public async Task RunAsync(string device, WebcamSettings settings, Action<WebcamFrame> frame, Action<string> note, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Starts) == 1) throw new InvalidOperationException("/dev/video0 is busy");
            while (!ct.IsCancellationRequested)
            {
                frame(new WebcamFrame([0xFF, 0xD8, 0xFF, 0xD9], "image/jpeg", DateTime.UtcNow));
                await Task.Delay(20, ct);
            }
        }
    }
}
