using Microsoft.Extensions.Logging.Abstractions;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public sealed class UpdateCheckTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nova-update-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static string Release(string tag, string notes = "### Added\n\n- Something new") =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            tag_name = tag,
            html_url = $"https://github.com/{UpdateCheckService.Repo}/releases/tag/{tag}",
            body = notes,
            published_at = "2026-11-12T08:00:00Z",
        });

    private (UpdateCheckService Updates, ActivityLog Log) Service(string installed, Func<string> answer)
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _folder, Phd2AutoConnect = false });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var updates = new UpdateCheckService(options, log) { Installed = installed, Fetch = _ => Task.FromResult(answer()) };
        return (updates, log);
    }

    [Fact]
    public async Task ANewerReleaseIsAvailableAndSaidOnce()
    {
        var (updates, log) = Service("0.3.22", () => Release("v0.3.23"));
        Assert.False(updates.Status.Available); // nothing known before the first check

        var status = await updates.CheckAsync(CancellationToken.None);
        Assert.True(status.Available);
        Assert.Equal("0.3.23", status.Latest!.Version);
        Assert.Contains("Something new", status.Latest.Notes);
        Assert.Null(status.Problem);
        Assert.NotNull(status.CheckedUtc);

        await updates.CheckAsync(CancellationToken.None);
        Assert.Single(log.Recent, e => e.Message.Contains("NOVA 0.3.23 is out") && e.Message.Contains("nova update"));
    }

    [Theory]
    [InlineData("0.3.23", "v0.3.23")] // up to date
    [InlineData("0.4.0", "v0.3.23")]  // ahead of the latest release
    [InlineData("0.3.23+abc123", "v0.3.23")]
    public async Task TheSameOrAnOlderReleaseIsNot(string installed, string latest)
    {
        var (updates, log) = Service(installed, () => Release(latest));
        var status = await updates.CheckAsync(CancellationToken.None);
        Assert.False(status.Available);
        Assert.False(status.DevBuild);
        Assert.DoesNotContain(log.Recent, e => e.Message.Contains("is out"));
    }

    [Fact]
    public async Task ABuildFromSourceIsNeverToldToUpdate()
    {
        var (updates, _) = Service("0.0.0-dev", () => Release("v0.3.23"));
        var status = await updates.CheckAsync(CancellationToken.None);
        Assert.True(status.DevBuild);
        Assert.False(status.Available);
        Assert.Equal("0.3.23", status.Latest!.Version); // still shown, for interest
    }

    [Fact]
    public async Task NoInternetKeepsTheLastAnswerAndSaysWhy()
    {
        var offline = false;
        var (updates, _) = Service("0.3.22", () => offline ? throw new HttpRequestException("Name or service not known") : Release("v0.3.23"));
        await updates.CheckAsync(CancellationToken.None);
        offline = true;
        var status = await updates.CheckAsync(CancellationToken.None);
        Assert.True(status.Available);
        Assert.Contains("Couldn't reach GitHub", status.Problem);
    }

    [Fact]
    public async Task AnAnswerThatIsNotAReleaseIsAProblemNotAnUpdate()
    {
        var (updates, _) = Service("0.3.22", () => """{"message":"API rate limit exceeded"}""");
        var status = await updates.CheckAsync(CancellationToken.None);
        Assert.False(status.Available);
        Assert.NotNull(status.Problem);
    }

    [Fact]
    public void TheLinkIsOnlyEverToTheProjectsReleases()
    {
        var release = UpdateCheckService.Parse("""{"tag_name":"v1.2.3","html_url":"https://example.com/elsewhere","body":""}""");
        Assert.Equal($"https://github.com/{UpdateCheckService.Repo}/releases", release!.Url);
    }

    [Fact]
    public void TurningTheDailyCheckOffIsRemembered()
    {
        var (updates, _) = Service("0.3.22", () => Release("v0.3.23"));
        Assert.True(updates.Status.Enabled);
        updates.SetEnabled(false);
        var (again, _) = Service("0.3.22", () => Release("v0.3.23"));
        Assert.False(again.Status.Enabled);
    }

    [Theory]
    [InlineData("0.3.23", "0.3.22", true)]
    [InlineData("0.10.0", "0.9.9", true)]
    [InlineData("1.0.0", "0.99.99", true)]
    [InlineData("0.3.22", "0.3.22", false)]
    [InlineData("0.3.21", "0.3.22", false)]
    [InlineData("nonsense", "0.3.22", false)]
    public void VersionsCompareByNumber(string candidate, string installed, bool newer) =>
        Assert.Equal(newer, UpdateCheckService.IsNewer(candidate, installed));
}
