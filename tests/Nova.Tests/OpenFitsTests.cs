using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using Nova.Core.Imaging;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class FitsMetadataTests
{
    private static RawImage With(params (string Key, string Value)[] cards)
    {
        var image = new RawImage(1, 1, [0], 16);
        foreach (var (k, v) in cards) image.Headers[k] = v;
        return image;
    }

    [Fact]
    public void ScaleFromFocalLengthAndPixelSize()
    {
        Assert.Equal(1.999, FitsMetadata.ImageScale(With(("FOCALLEN", "388"), ("XPIXSZ", "3.76")))!.Value, 3);
        Assert.Equal(2.5, FitsMetadata.ImageScale(With(("PIXSCALE", "2.5"), ("FOCALLEN", "388"), ("XPIXSZ", "3.76"))));
        Assert.Null(FitsMetadata.ImageScale(With(("FOCALLEN", "388"))));
    }

    [Fact]
    public void CoordinatesFromDegreesOrSexagesimal()
    {
        var degrees = FitsMetadata.Coordinates(With(("RA", "83.82"), ("DEC", "-5.39")))!.Value;
        Assert.Equal(5.588, degrees.RaHours, 3);
        Assert.Equal(-5.39, degrees.DecDegrees, 3);

        var text = FitsMetadata.Coordinates(With(("OBJCTRA", "05 35 17.3"), ("OBJCTDEC", "-05 23 28")))!.Value;
        Assert.Equal(5.5881, text.RaHours, 3);
        Assert.Equal(-5.391, text.DecDegrees, 3);

        Assert.Null(FitsMetadata.Coordinates(With(("OBJECT", "M 42"))));
        Assert.Null(FitsMetadata.Coordinates(With(("OBJCTRA", "nonsense"), ("OBJCTDEC", "x"))));
    }

    [Fact]
    public void StartTime()
    {
        Assert.Equal(new DateTime(2026, 10, 1, 21, 30, 5, DateTimeKind.Utc),
            FitsMetadata.Started(With(("DATE-OBS", "2026-10-01T21:30:05"))));
    }
}

public class OpenFitsTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    [Fact]
    public async Task OpenedFileCanBeMeasuredAndSolved()
    {
        var options = Opts.Create(new NovaOptions
        {
            Backend = "Simulator", ConfigDirectory = _config, ImageDirectory = Path.Combine(_config, "images"),
        });
        var hub = new NullHubContext();
        var log = new ActivityLog(hub, NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var optics = new OpticsService(equipment, options, log);
        var imaging = new ImagingService(equipment, optics, log, hub, options);
        var solver = new PlateSolveService(equipment, imaging, optics, log, options);
        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.Mount!.UnparkAsync(CancellationToken.None);

        // A frame from "another night": captured, then gzipped as archives often are.
        var captured = await imaging.CaptureAsync(0, save: false, CancellationToken.None);
        var history = imaging.HfrHistory.Count;
        using var packed = new MemoryStream();
        await using (var gz = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
            await gz.WriteAsync(imaging.LatestFits);

        var opened = await imaging.OpenAsync(packed.ToArray(), "old.fits.gz", CancellationToken.None);

        Assert.Equal("old.fits.gz", opened.OpenedFile);
        Assert.NotEqual(captured.Id, opened.Id);
        Assert.Equal(captured.Width, opened.Width);
        Assert.Equal(captured.Stars.Count, opened.Stars.Count);
        Assert.Equal(captured.Stars.MedianHfr, opened.Stars.MedianHfr);
        Assert.Equal(history, imaging.HfrHistory.Count); // not part of the live focus trend

        var solve = await solver.SolveLatestAsync(CancellationToken.None);
        Assert.True(solve.Solved, solve.Error);
        Assert.Equal(opened.Id, solve.ImageId);
    }

    [Fact]
    public async Task RejectsFilesThatAreNotFits()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var hub = new NullHubContext();
        var log = new ActivityLog(hub, NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var imaging = new ImagingService(equipment, new OpticsService(equipment, options, log), log, hub, options);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            imaging.OpenAsync("hello, not a FITS file"u8.ToArray(), "notes.txt", CancellationToken.None));
        Assert.Contains("notes.txt", error.Message);
        Assert.Null(imaging.LatestImage);
    }
}
