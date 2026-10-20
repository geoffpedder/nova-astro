using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class NotificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Stands in for ntfy: records what was posted.</summary>
    private sealed class FakeNtfy(HttpStatusCode answer = HttpStatusCode.OK) : HttpMessageHandler
    {
        public readonly List<(Uri Url, string? Auth, JsonElement Body)> Posts = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            Posts.Add((request.RequestUri!, request.Headers.Authorization?.ToString(), body));
            return new HttpResponseMessage(answer);
        }
    }

    private NotificationService NewService(FakeNtfy ntfy) =>
        new(Opts.Create(new NovaOptions { ConfigDirectory = _root }),
            new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance),
            new ServiceCollection().BuildServiceProvider(), new HttpClient(ntfy));

    [Fact]
    public async Task PostsToTheTopicWithTheToken()
    {
        var ntfy = new FakeNtfy();
        var service = NewService(ntfy);
        service.Save(new NotificationSettings(Enabled: true, Server: "https://ntfy.example.org/", Topic: "nova-abc123"), "tk_secret");

        Assert.Null(await service.SendAsync(NotificationKind.SequenceDone, "Sequence 'M 31' finished", "M 31: 40 of 40 frames", CancellationToken.None));

        var post = Assert.Single(ntfy.Posts);
        Assert.Equal("https://ntfy.example.org/", post.Url.ToString());
        Assert.Equal("Bearer tk_secret", post.Auth);
        Assert.Equal("nova-abc123", post.Body.GetProperty("topic").GetString());
        Assert.Equal("Sequence 'M 31' finished", post.Body.GetProperty("title").GetString());
        Assert.Equal("M 31: 40 of 40 frames", post.Body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task SendsNothingWhenOffOrThatKindIsUnticked()
    {
        var ntfy = new FakeNtfy();
        var service = NewService(ntfy);
        await service.SendAsync(NotificationKind.SequenceFailed, "t", "m", CancellationToken.None); // off by default

        service.Save(new NotificationSettings(Enabled: true, Topic: "nova-abc123", GuidingLost: false, MeridianFlip: false), null);
        await service.SendAsync(NotificationKind.GuidingLost, "t", "m", CancellationToken.None);
        await service.SendAsync(NotificationKind.MeridianFlip, "t", "m", CancellationToken.None);
        Assert.Empty(ntfy.Posts);

        await service.SendAsync(NotificationKind.SequenceFailed, "t", "m", CancellationToken.None);
        Assert.Single(ntfy.Posts);
    }

    [Fact]
    public async Task SaysTheMiniPcIsReadyAfterABootOnly()
    {
        var ntfy = new FakeNtfy();
        var service = NewService(ntfy);
        service.Save(new NotificationSettings(Enabled: true, Topic: "nova-abc123"), null);

        service.Uptime = () => TimeSpan.FromMinutes(2); // just booted
        await service.AnnounceReadyAsync(TimeSpan.Zero, CancellationToken.None);
        var post = Assert.Single(ntfy.Posts);
        Assert.Equal("Mini PC ready", post.Body.GetProperty("title").GetString());
        Assert.Contains(".local:5000", post.Body.GetProperty("message").GetString());

        service.Uptime = () => TimeSpan.FromHours(3); // NOVA restarted (an update), the mini PC didn't
        await service.AnnounceReadyAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.Single(ntfy.Posts);

        service.Uptime = () => TimeSpan.FromMinutes(1);
        service.Save(service.Settings with { Ready = false }, null);
        await service.AnnounceReadyAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.Single(ntfy.Posts);
    }

    [Fact]
    public async Task KeepsTheTokenOutOfTheViewAndReportsFailures()
    {
        var ntfy = new FakeNtfy(HttpStatusCode.Forbidden);
        var service = NewService(ntfy);
        service.Save(new NotificationSettings(Enabled: true, Topic: "nova-abc123"), "tk_secret");
        Assert.True(service.View.HasToken);
        Assert.DoesNotContain("tk_secret", JsonSerializer.Serialize(service.View));

        service.Save(service.Settings with { SequenceDone = false }, null); // null keeps the token
        Assert.Equal("tk_secret", service.Settings.Token);

        var problem = await service.SendAsync(NotificationKind.Test, "t", "m", CancellationToken.None);
        Assert.Contains("403", problem);
        Assert.Contains("403", service.View.LastError);

        service.Save(service.Settings, ""); // "" removes it
        Assert.False(service.View.HasToken);
    }

    [Theory]
    [InlineData("ftp://ntfy.sh", "nova-abc")]
    [InlineData("https://ntfy.sh", "has spaces")]
    [InlineData("https://ntfy.sh", "")]
    public void RefusesABadServerOrTopic(string server, string topic) =>
        Assert.Throws<ArgumentException>(() =>
            NewService(new FakeNtfy()).Save(new NotificationSettings(Enabled: true, Server: server, Topic: topic), null));
}
