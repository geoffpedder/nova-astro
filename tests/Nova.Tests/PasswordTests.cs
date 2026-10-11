using System.Security.Claims;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class PasswordTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private PasswordService NewService() => new(Opts.Create(new NovaOptions { ConfigDirectory = _root }));

    [Fact]
    public async Task WithoutAPasswordEveryoneIsSignedIn()
    {
        var service = NewService();
        Assert.False(service.PasswordSet);
        Assert.True(service.IsSignedIn(new ClaimsPrincipal()));
        Assert.True(await service.CheckAsync("anything", CancellationToken.None));
    }

    [Fact]
    public async Task ChecksThePasswordAndKeepsOnlyAHash()
    {
        var service = NewService();
        service.Set("orion-nebula");

        Assert.True(service.PasswordSet);
        Assert.False(service.IsSignedIn(new ClaimsPrincipal())); // a browser that hasn't signed in
        Assert.True(await service.CheckAsync("orion-nebula", CancellationToken.None));
        Assert.DoesNotContain("orion-nebula", File.ReadAllText(Path.Combine(_root, "auth.json")));

        var signedIn = service.Principal();
        Assert.True(service.IsSignedIn(signedIn));
        Assert.Throws<ArgumentException>(() => service.Set("short"));
    }

    [Fact]
    public void ANewPasswordSignsOtherBrowsersOutAndRemovingItOpensUp()
    {
        var service = NewService();
        service.Set("first-password");
        var browser = service.Principal();

        service.Set("second-password");
        Assert.False(service.IsSignedIn(browser));
        Assert.True(service.IsSignedIn(service.Principal()));

        service.Set(null);
        Assert.False(service.PasswordSet);
        Assert.False(File.Exists(Path.Combine(_root, "auth.json")));
        Assert.True(service.IsSignedIn(new ClaimsPrincipal()));
    }

    [Fact]
    public async Task PicksUpAPasswordSetFromTheCommandLine()
    {
        var running = NewService();
        Assert.False(running.PasswordSet);

        NewService().Set("from-nova-password"); // `nova password` writes the same file
        await Task.Delay(1100); // rechecked at most once a second

        Assert.True(running.PasswordSet);
        Assert.True(await running.CheckAsync("from-nova-password", CancellationToken.None));
    }
}
