using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Nova.Core.Equipment;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class SequencerTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    private sealed record Rig(
        EquipmentService Equipment, ImagingService Imaging, GuiderService Guider, AutofocusService Autofocus,
        SequencerService Sequencer, string Images, PlateSolveService Solver);

    private async Task<Rig> SetUpAsync(int phd2Port = 0)
    {
        var images = Path.Combine(_config, "images");
        var options = Opts.Create(new NovaOptions
        {
            Backend = "Simulator", ConfigDirectory = _config, ImageDirectory = images, SolveExposureSeconds = 0,
            Phd2Host = "127.0.0.1", Phd2Port = phd2Port, Phd2AutoConnect = false,
        });
        var hub = new NullHubContext();
        var log = new ActivityLog(hub, NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var optics = new OpticsService(equipment, options, log);
        var imaging = new ImagingService(equipment, optics, log, hub, options);
        var solver = new PlateSolveService(equipment, imaging, optics, log, options) { UseRotator = false }; // turned by hand
        var autofocus = new AutofocusService(equipment, imaging, log, options);
        autofocus.UpdateSettings(new AutofocusSettings(StepSize: 300, StepsPerSide: 3, ExposureSeconds: 0.3));
        var guider = new GuiderService(options, log);
        var sequencer = new SequencerService(equipment, imaging, solver, autofocus, guider, new SiteService(equipment, options, log), log, options);
        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.Mount!.UnparkAsync(CancellationToken.None);
        return new Rig(equipment, imaging, guider, autofocus, sequencer, images, solver);
    }

    private static Sequence Plan(SequenceOrder order, params SequenceRow[] rows) => new(
        "Test", new SequenceTarget("M 42", "05:35:17", "-05:23:28"),
        new SequenceStart(CoolCamera: false, CenterTarget: false, Autofocus: false, StartGuiding: false),
        rows.ToList(), order, new SequenceDither(EveryFrames: 0), new SequenceAutofocus(TemperatureChange: null, HfrIncreasePercent: null));

    private static async Task<SequencerStatus> WaitForEnd(SequencerService sequencer, int seconds = 90)
    {
        for (var i = 0; i < seconds * 20 && sequencer.Running; i++) await Task.Delay(50);
        Assert.False(sequencer.Running, "the sequence didn't finish");
        return sequencer.Status!;
    }

    private static async Task WaitFor(Func<bool> condition, string what, int seconds = 30)
    {
        for (var i = 0; i < seconds * 20 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), $"timed out waiting for {what}");
    }

    private static List<string> Saved(string images) =>
        Directory.Exists(images) ? Directory.GetFiles(images, "*.fits", SearchOption.AllDirectories).ToList() : [];

    [Fact]
    public async Task WaitsForTheCameraToBeTurnedToThePlannedRotation()
    {
        var rig = await SetUpAsync();
        var plan = Plan(SequenceOrder.ByRow, new SequenceRow(null, 0.2, 1)) with
        {
            Target = new SequenceTarget("M 42", "05:35:17", "-05:23:28", Rotation: 30),
            Start = new SequenceStart(CoolCamera: false, CenterTarget: true, Autofocus: false, StartGuiding: false),
        };
        Assert.True(rig.Sequencer.Start(plan, out var error), error);

        // After centring it asks for the turn, and takes no frames until it's answered.
        await WaitFor(() => rig.Solver.Rotation is { Waiting: true }, "the request to turn the camera");
        Assert.Equal("Rotation", rig.Sequencer.Status!.Step);
        Assert.Matches(@"Turn the camera 30(\.\d)?° anticlockwise", rig.Sequencer.Status.Message);
        Assert.Equal(0, rig.Sequencer.Status.FramesDone);
        Assert.True(rig.Solver.Rotation!.FromSequence);

        Assert.True(rig.Solver.KeepRotation());
        var end = await WaitForEnd(rig.Sequencer);
        Assert.Equal("Done", end.State);
        Assert.Equal(1, end.FramesDone);
    }

    [Fact]
    public async Task WithARotatorTheRotationNeedsNobody()
    {
        var rig = await SetUpAsync();
        rig.Solver.UseRotator = true;
        var plan = Plan(SequenceOrder.ByRow, new SequenceRow(null, 0.2, 1)) with
        {
            Target = new SequenceTarget("M 42", "05:35:17", "-05:23:28", Rotation: 45),
            Start = new SequenceStart(CoolCamera: false, CenterTarget: true, Autofocus: false, StartGuiding: false),
        };
        Assert.True(rig.Sequencer.Start(plan, out var error), error);
        var end = await WaitForEnd(rig.Sequencer);
        Assert.Equal("Done", end.State);
        Assert.Equal(1, end.FramesDone);
        Assert.InRange(Math.Abs(Nova.Core.PlateSolving.CameraAngle.Turn(45, rig.Solver.Rotation!.CameraAngle!.Value).Degrees), 0, 1);
    }

    [Fact]
    public async Task CentresFocusesAndTakesTheFramesInCycleOrder()
    {
        var rig = await SetUpAsync();
        var plan = Plan(SequenceOrder.Cycle,
            new SequenceRow("L", 0.2, 2), new SequenceRow("R", 0.2, 1), new SequenceRow("G", 0.2, 0), new SequenceRow("B", 0.2, 1, Enabled: false))
            with
            {
                Start = new SequenceStart(CoolCamera: false, CenterTarget: true, Autofocus: true, StartGuiding: false),
            };

        Assert.True(rig.Sequencer.Start(plan, out var error), error);
        var status = await WaitForEnd(rig.Sequencer);

        Assert.Equal("Done", status.State);
        Assert.Equal(3, status.FramesDone);
        Assert.Equal(3, status.FramesTotal);
        Assert.Equal(1, status.Autofocuses);
        Assert.Equal([2, 1, 0, 0], status.Rows.Select(r => r.Done));

        // Saved in the night's folder, in a folder named after the target, numbered through the sequence, in L R L order.
        var files = Saved(rig.Images).Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(f => f[^4..]).ToList();
        Assert.Equal(3, files.Count);
        Assert.All(Saved(rig.Images), f => Assert.Equal("M 42", Path.GetFileName(Path.GetDirectoryName(f))));
        Assert.Equal(["0001", "0002", "0003"], files.Select(f => f[^4..]));
        Assert.Equal(["L", "R", "L"], files.Select(f => f.Split('_')[3]));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}_Light_L_-?\d+\.\d{2}_0\.20s_0001$", files[0]);
    }

    [Fact]
    public async Task RefocusesWhenTheFilterChanges()
    {
        var rig = await SetUpAsync();
        var plan = Plan(SequenceOrder.ByRow, new SequenceRow("L", 0.2, 1), new SequenceRow("Ha", 0.2, 1))
            with { Autofocus = new SequenceAutofocus(AfterFilterChange: true, TemperatureChange: null, HfrIncreasePercent: null) };

        Assert.True(rig.Sequencer.Start(plan, out var error), error);
        var status = await WaitForEnd(rig.Sequencer);
        Assert.Equal("Done", status.State);
        Assert.Equal(1, status.Autofocuses); // only before the Ha frame: no autofocus at the start in this plan
        Assert.Equal(5, rig.Equipment.FilterWheel!.Position); // Ha
    }

    [Fact]
    public async Task PausesResumesSkipsAndAborts()
    {
        var rig = await SetUpAsync();
        var plan = Plan(SequenceOrder.ByRow, new SequenceRow("L", 0.2, 100), new SequenceRow("R", 0.2, 2), new SequenceRow("G", 0.2, 100));
        Assert.True(rig.Sequencer.Start(plan, out var error), error);

        await WaitFor(() => rig.Sequencer.Status?.FramesDone >= 2, "two frames");
        rig.Sequencer.Pause();
        await WaitFor(() => rig.Sequencer.Status?.State == "Paused", "the pause");
        var frames = rig.Sequencer.Status!.FramesDone;
        await Task.Delay(700);
        Assert.Equal(frames, rig.Sequencer.Status!.FramesDone); // really paused

        rig.Sequencer.Skip();   // the rest of L
        rig.Sequencer.Resume();
        await WaitFor(() => rig.Sequencer.Status?.Rows[1].Done == 2, "the R frames");
        Assert.True(rig.Sequencer.Status!.Rows[0].Skipped);
        Assert.Equal(frames, rig.Sequencer.Status.Rows[0].Done);

        rig.Sequencer.Abort();
        var status = await WaitForEnd(rig.Sequencer);
        Assert.Equal("Aborted", status.State);
        Assert.True(status.Rows[2].Done < 100);
        Assert.False(rig.Imaging.IsBusy);
    }

    [Fact]
    public async Task GuidesAndDithers()
    {
        var dithers = 0;
        using var phd2 = new ScriptedServer(
            ["""{"Event":"Version","PHDVersion":"2.6.13","PHDSubver":"","MsgVersion":1}""", """{"Event":"AppState","State":"Stopped"}"""],
            request =>
            {
                var method = JsonNode.Parse(request)!["method"]!.GetValue<string>();
                string Reply(string result) => $$"""{"jsonrpc":"2.0","result":{{result}},"id":{{JsonNode.Parse(request)!["id"]}}}""";
                return method switch
                {
                    "get_app_state" => [Reply("\"Stopped\"")],
                    "get_pixel_scale" => [Reply("2.0")],
                    "get_profiles" => [Reply("""[{"id":1,"name":"Simulators"}]""")],
                    "get_profile" => [Reply("""{"id":1,"name":"Simulators"}""")],
                    "get_connected" => [Reply("true")],
                    "guide" => [Reply("0"), """{"Event":"StartGuiding"}""", """{"Event":"SettleDone","Status":0}"""],
                    "dither" => Dither(Reply("0")),
                    "stop_capture" => [Reply("0"), """{"Event":"GuidingStopped"}"""],
                    _ => [Reply("0")],
                };
            });
        IEnumerable<string> Dither(string reply)
        {
            Interlocked.Increment(ref dithers);
            return [reply, """{"Event":"SettleDone","Status":0}"""];
        }

        var rig = await SetUpAsync(phd2.Port);
        await rig.Guider.ConnectAsync(CancellationToken.None);
        var plan = Plan(SequenceOrder.ByRow, new SequenceRow("L", 0.2, 5)) with
        {
            Start = new SequenceStart(CoolCamera: false, CenterTarget: false, Autofocus: false, StartGuiding: true),
            Dither = new SequenceDither(EveryFrames: 2, Pixels: 3),
        };

        Assert.True(rig.Sequencer.Start(plan, out var error), error);
        var status = await WaitForEnd(rig.Sequencer);
        Assert.Equal("Done", status.State);
        Assert.Equal(5, status.FramesDone);
        Assert.Equal(2, status.Dithers); // before frames 3 and 5
        Assert.Equal(2, dithers);
        Assert.Contains(phd2.Received, l => l.Contains("\"method\":\"guide\""));
        Assert.Contains(phd2.Received, l => l.Contains("\"method\":\"stop_capture\"")); // end: stop guiding
    }

    [Fact]
    public async Task RefusesPlansItCannotRun()
    {
        var rig = await SetUpAsync();
        Assert.False(rig.Sequencer.Start(Plan(SequenceOrder.ByRow, new SequenceRow("OIII", 1, 1)), out var error));
        Assert.Contains("no filter called 'OIII'", error);
        Assert.False(rig.Sequencer.Start(Plan(SequenceOrder.ByRow), out error));
        Assert.Contains("at least one row", error);
        Assert.False(rig.Sequencer.Start(Plan(SequenceOrder.ByRow, new SequenceRow("L", 1, 1)) with
            { Target = new SequenceTarget("Nowhere", "25:99:00", "+95:00:00") }, out error));
        Assert.Contains("Target coordinates", error);
    }

    [Fact]
    public async Task SavesLoadsAndDeletes()
    {
        var rig = await SetUpAsync();
        var plan = Plan(SequenceOrder.Cycle, new SequenceRow("L", 180, 30, Gain: 100, Binning: 1), new SequenceRow("Ha", 300, 20, FrameType: FrameType.Light))
            with { Name = "M42 / LRGB" };
        rig.Sequencer.Save(plan);
        Assert.Equal(["M42 - LRGB"], rig.Sequencer.SavedNames());

        var loaded = rig.Sequencer.Load("M42 / LRGB")!;
        Assert.Equal(JsonSerializer.Serialize(plan, Sequence.Json), JsonSerializer.Serialize(loaded, Sequence.Json));
        Assert.Contains("\"order\": \"Cycle\"", File.ReadAllText(Directory.GetFiles(Path.Combine(_config, "sequences")).Single()));

        rig.Sequencer.Delete("M42 / LRGB");
        Assert.Empty(rig.Sequencer.SavedNames());
    }

    [Fact]
    public async Task OnlyTargetsTickedForLiveStackingAreStackedAndTheirStackIsSaved()
    {
        var rig = await SetUpAsync();
        var stacks = new LiveStackService(rig.Imaging, new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance));
        rig.Sequencer.LiveStack = stacks;
        var rows = new List<SequenceRow> { new("L", 0.5, 3) };
        var plan = Plan(SequenceOrder.ByRow, rows[0]) with
        {
            Targets =
            [
                new SequenceTargetPlan(new SequenceTarget("M 42", "05:35:17", "-05:23:28"), rows, LiveStack: true),
                new SequenceTargetPlan(new SequenceTarget("M 43", "05:35:31", "-05:16:03"), rows),
            ],
        };
        Assert.True(rig.Sequencer.Start(plan, out var error), error);
        await WaitForEnd(rig.Sequencer);

        var saved = Directory.GetFiles(rig.Images, "Stack_*.fits", SearchOption.AllDirectories);
        var only = Assert.Single(saved);
        Assert.Contains(Path.Combine("M 42", "Stacked"), only);
        Assert.Contains("_3x_", Path.GetFileName(only));
        Assert.Empty(stacks.Status.Stacks); // saved and cleared when the target ended
    }
}

public class LiveCountTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    private async Task<SequencerService> StartAsync(params SequenceRow[] rows)
    {
        var options = Opts.Create(new NovaOptions
        {
            Backend = "Simulator", ConfigDirectory = _config, ImageDirectory = Path.Combine(_config, "images"), Phd2AutoConnect = false,
        });
        var hub = new NullHubContext();
        var log = new ActivityLog(hub, NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var optics = new OpticsService(equipment, options, log);
        var imaging = new ImagingService(equipment, optics, log, hub, options);
        var solver = new PlateSolveService(equipment, imaging, optics, log, options);
        var sequencer = new SequencerService(equipment, imaging, solver, new AutofocusService(equipment, imaging, log, options),
            new GuiderService(options, log), new SiteService(equipment, options, log), log, options);
        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.Mount!.UnparkAsync(CancellationToken.None);
        var plan = new Sequence("Live", null, new SequenceStart(CoolCamera: false, CenterTarget: false, Autofocus: false, StartGuiding: false),
            rows.ToList(), Dither: new SequenceDither(EveryFrames: 0), Autofocus: new SequenceAutofocus(TemperatureChange: null, HfrIncreasePercent: null),
            MeridianFlip: new SequenceMeridianFlip(Enabled: false));
        Assert.True(sequencer.Start(plan, out var error), error);
        return sequencer;
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 600 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), $"timed out waiting for {what}");
    }

    [Fact]
    public async Task MoreFramesCanBeAddedWhileItRuns()
    {
        var sequencer = await StartAsync(new SequenceRow("L", 0.2, 3), new SequenceRow("R", 0.2, 1));
        await WaitFor(() => sequencer.Status?.FramesDone >= 1, "the first frame");

        sequencer.SetCount(0, 0, 6);
        Assert.Equal(7, sequencer.Status!.FramesTotal);
        await WaitFor(() => !sequencer.Running, "the end");
        Assert.Equal("Done", sequencer.Status!.State);
        Assert.Equal([6, 1], sequencer.Status.Rows.Select(r => r.Done));
        Assert.Throws<InvalidOperationException>(() => sequencer.SetCount(0, 0, 10)); // finished
    }

    [Fact]
    public async Task FewerFramesButNotBelowWhatIsTaken()
    {
        var sequencer = await StartAsync(new SequenceRow("L", 0.2, 100), new SequenceRow("R", 0.2, 2));
        await WaitFor(() => sequencer.Status?.Rows[0].Done >= 2, "two frames");
        var done = sequencer.Status!.Rows[0].Done;

        Assert.Throws<ArgumentException>(() => sequencer.SetCount(0, 0, done - 1));
        sequencer.SetCount(0, 0, sequencer.Status!.Rows[0].Done); // finish L now
        await WaitFor(() => !sequencer.Running, "the end");
        Assert.True(sequencer.Status!.Rows[0].Done <= done + 1); // at most the frame already under way
        Assert.Equal(2, sequencer.Status.Rows[1].Done);
    }

    [Fact]
    public async Task RefocusAndTheEndCanChangeWhileItRuns()
    {
        var sequencer = await StartAsync(new SequenceRow("L", 0.2, 200));
        await WaitFor(() => sequencer.Status?.FramesDone >= 1, "the first frame");

        // Stop at a set time and park at the end, decided after it started.
        var stopAt = DateTime.Now.AddHours(3).ToString("HH:mm");
        sequencer.UpdateLiveSettings(new SequenceAutofocus(TemperatureChange: 3), new SequenceEnd(StopAt: stopAt, Park: true));
        Assert.Equal($"the night's stop time ({stopAt})", sequencer.Status!.NightEnd);
        Assert.Throws<ArgumentException>(() => sequencer.UpdateLiveSettings(new SequenceAutofocus(EveryFrames: -1), new SequenceEnd()));

        sequencer.SetCount(0, 0, sequencer.Status!.Rows[0].Done); // finish now
        await WaitFor(() => !sequencer.Running, "the end");
        Assert.Equal("Done", sequencer.Status!.State);
        Assert.Throws<InvalidOperationException>(() => sequencer.UpdateLiveSettings(new SequenceAutofocus(), new SequenceEnd()));
    }
}
