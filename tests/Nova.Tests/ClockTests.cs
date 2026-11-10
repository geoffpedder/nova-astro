using Microsoft.Extensions.Logging.Abstractions;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public sealed class ClockTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nova-clock-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>A clock service on a pretend timedatectl: what it was asked, and what it answers.</summary>
    private (ClockService Clock, List<string> Ran, ActivityLog Log, EquipmentService Equipment) Service(
        bool synchronized = false, bool ntp = true, int refusals = 0, string? setError = null)
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _folder, Phd2AutoConnect = false });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var ran = new List<string>();
        var clock = new ClockService(equipment, log)
        {
            Supported = () => true,
            Run = (file, arguments, _) =>
            {
                var command = string.Join(' ', arguments);
                ran.Add(command);
                (int, string) answer = command switch
                {
                    "show -p NTPSynchronized --value" => (0, synchronized ? "yes\n" : "no\n"),
                    "show -p NTP --value" => (0, ntp ? "yes\n" : "no\n"),
                    _ when command.StartsWith("set-time") && setError is not null => (1, setError),
                    _ when command.StartsWith("set-time") && refusals-- > 0 => (1, "Failed to set time: Automatic time synchronization is enabled"),
                    _ => (0, ""),
                };
                return Task.FromResult(answer);
            },
        };
        return (clock, ran, log, equipment);
    }

    [Fact]
    public async Task SetsTheClockWithNetworkTimeOffForTheMoment()
    {
        var (clock, ran, log, equipment) = Service(refusals: 2);
        await using var _ = equipment;
        var time = new DateTimeOffset(2026, 11, 10, 4, 15, 30, TimeSpan.Zero);

        Assert.Null(await clock.SetAsync(time, CancellationToken.None));

        Assert.Equal("set-ntp false --no-ask-password", ran[2]);
        // Refused twice while network time was still stopping, then set: in UTC, whatever the mini PC's time zone.
        var sets = ran.Where(c => c.StartsWith("set-time")).ToList();
        Assert.Equal(3, sets.Count);
        Assert.StartsWith("set-time 2026-11-10 04:15:3", sets[^1]);
        Assert.EndsWith(" UTC --no-ask-password", sets[^1]);
        Assert.Equal("set-ntp true --no-ask-password", ran[^1]); // so the internet corrects it again when there is some
        Assert.Contains(log.Recent, e => e.Message.Contains("set from the browser's device to 2026-11-10 04:15:3"));
    }

    [Fact]
    public async Task LeavesAClockSetFromTheInternetAlone()
    {
        var (clock, ran, _, equipment) = Service(synchronized: true);
        await using var __ = equipment;

        var status = await clock.StatusAsync(CancellationToken.None);
        Assert.True(status.Synchronized);
        Assert.False(status.CanSet);

        var problem = await clock.SetAsync(DateTimeOffset.UtcNow.AddHours(3), CancellationToken.None);
        Assert.Contains("set from the internet", problem);
        Assert.DoesNotContain(ran, c => c.StartsWith("set-"));
    }

    [Fact]
    public async Task RefusesWhileASequenceRunsAndForAnImpossibleDate()
    {
        var (clock, ran, _, equipment) = Service();
        await using var __ = equipment;

        Assert.Contains("can't be right", await clock.SetAsync(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None));
        clock.Busy = () => "A sequence is running: stop it before changing the clock";
        Assert.Contains("sequence is running", await clock.SetAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Empty(ran);
    }

    [Fact]
    public async Task SaysWhenItIsntAllowedAndPutsNetworkTimeBack()
    {
        var (clock, ran, _, equipment) = Service(setError: "Failed to set time: Interactive authentication required.");
        await using var __ = equipment;

        var problem = await clock.SetAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Contains("isn't allowed to set the clock yet", problem);
        Assert.Equal("set-ntp true --no-ask-password", ran[^1]);
    }

    [Fact]
    public async Task SendsTheNewTimeToAConnectedMount()
    {
        var (clock, _, log, equipment) = Service(ntp: false);
        await using var __ = equipment;
        await equipment.ConnectAsync(CancellationToken.None);

        Assert.Null(await clock.SetAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.InRange((equipment.Mount!.ClockUtc!.Value - DateTime.UtcNow).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.Contains(log.Recent, e => e.Message.EndsWith(": time sent"));
    }

    [Theory]
    [InlineData(-11520, "3 h 12 min behind")]
    [InlineData(95, "1 min 35 s ahead")]
    [InlineData(-40, "40 s behind")]
    [InlineData(864000, "10 days ahead")]
    public void DescribesHowFarOut(int seconds, string expected) =>
        Assert.Equal(expected, ClockService.Describe(TimeSpan.FromSeconds(seconds)));
}
