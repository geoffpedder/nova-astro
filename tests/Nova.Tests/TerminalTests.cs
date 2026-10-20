using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Nova.Server.Api;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class TerminalTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-terminal-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_config, recursive: true); } catch (IOException) { }
    }

    /// <summary>Stands in for ttyd: makes its socket file, and notes when it's stopped.</summary>
    private sealed class FakeTtyd : ITerminalProcess
    {
        public static int Started;
        public bool Stopped { get; private set; }
        public bool HasExited => Stopped;
        public FakeTtyd(string socket)
        {
            File.WriteAllText(socket, "");
            Interlocked.Increment(ref Started);
        }
        public void Stop() => Stopped = true;
        public void Dispose() { }
    }

    private (TerminalService Terminal, PasswordService Password, List<FakeTtyd> Launched) Make()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var password = new PasswordService(options, log);
        var launched = new List<FakeTtyd>();
        var terminal = new TerminalService(options, password, log)
        {
            OnMiniPc = true,
            TtydPath = "/usr/bin/ttyd",
            StopAfter = TimeSpan.FromMilliseconds(200),
            Launch = socket => { var t = new FakeTtyd(socket); launched.Add(t); return t; },
        };
        return (terminal, password, launched);
    }

    [Fact]
    public void OnlyWithAPasswordAndTtydAndOnlySignedIn()
    {
        var (terminal, password, _) = Make();
        Assert.Contains("Set a NOVA password first", terminal.Problem);
        var http = new DefaultHttpContext();
        Assert.Equal(StatusCodes.Status403Forbidden, TerminalApi.Refusal(http, terminal, password)); // no password: never

        password.Set("dark skies");
        Assert.Null(terminal.Problem);
        Assert.Equal(StatusCodes.Status401Unauthorized, TerminalApi.Refusal(http, terminal, password)); // not signed in

        terminal.TtydPath = null;
        Assert.Contains("ttyd isn't installed", terminal.Problem);
        Assert.Equal(StatusCodes.Status403Forbidden, TerminalApi.Refusal(http, terminal, password));

        terminal.OnMiniPc = false;
        terminal.TtydPath = "/usr/bin/ttyd";
        Assert.Contains("mini PC only", terminal.Problem);
    }

    [Fact]
    public async Task StartsWhenOpenedAndStopsAMinuteAfterTheLastCloses()
    {
        var (terminal, password, launched) = Make();
        password.Set("dark skies");

        await terminal.EnsureRunningAsync(CancellationToken.None);
        await terminal.EnsureRunningAsync(CancellationToken.None); // already running: not started twice
        Assert.Single(launched);
        var first = terminal.Join("a");
        var second = terminal.Join("b");
        Assert.Equal(2, terminal.Status.Sessions);

        first.Dispose();
        await Task.Delay(400);
        Assert.False(launched[0].Stopped); // one still open
        second.Dispose();
        second.Dispose(); // twice is once
        Assert.Equal(0, terminal.Status.Sessions);
        await Task.Delay(100);
        var again = terminal.Join("c"); // opened again before it stopped: carries on
        await Task.Delay(400);
        Assert.False(launched[0].Stopped);
        again.Dispose();
        await Task.Delay(500);
        Assert.True(launched[0].Stopped);
        Assert.False(terminal.Status.Running);

        await terminal.EnsureRunningAsync(CancellationToken.None); // and starts afresh
        Assert.Equal(2, launched.Count);
        await terminal.DisposeAsync();
    }

    /// <summary>Stands in for ttyd on its socket file: a page, and a WebSocket ("tty") that answers each message.</summary>
    private sealed class FakeTtydServer : ITerminalProcess
    {
        private readonly WebApplication _app;
        private bool _stopped;
        public FakeTtydServer(string socket)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(socket));
            _app = builder.Build();
            _app.UseWebSockets();
            _app.MapGet("/terminal/", () => Results.Text("ttyd page", "text/html"));
            _app.Map("/terminal/ws", async (HttpContext http) =>
            {
                using var ws = await http.WebSockets.AcceptWebSocketAsync(http.WebSockets.WebSocketRequestedProtocols.FirstOrDefault());
                var buffer = new byte[1024];
                while (true)
                {
                    var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    await ws.SendAsync(Encoding.UTF8.GetBytes("echo:" + Encoding.UTF8.GetString(buffer, 0, r.Count)), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            });
            _app.StartAsync().GetAwaiter().GetResult();
        }
        public bool HasExited => _stopped;
        public void Stop() { _stopped = true; _app.StopAsync().GetAwaiter().GetResult(); }
        public void Dispose() => _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task RelaysTtydsPageAndTerminalToASignedInBrowser()
    {
        var options = Opts.Create(new NovaOptions { Backend = "Simulator", ConfigDirectory = _config });
        var log = new ActivityLog(new NullHubContext(), NullLogger<ActivityLog>.Instance);
        var password = new PasswordService(options, log);
        password.Set("dark skies");
        await using var terminal = new TerminalService(options, password, log)
        {
            OnMiniPc = true, TtydPath = "/usr/bin/ttyd", Launch = socket => new FakeTtydServer(socket),
        };

        // NOVA's side: the relay, with a header standing in for the sign-in cookie.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(terminal).AddSingleton(password).AddSingleton(log);
        await using var nova = builder.Build();
        nova.UseWebSockets();
        nova.Use(async (http, next) =>
        {
            if (http.Request.Headers.ContainsKey("X-Signed-In")) http.User = password.Principal();
            await next();
        });
        nova.MapTerminal();
        await nova.StartAsync();
        var address = nova.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        using var http = new HttpClient { BaseAddress = new Uri(address) };
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await http.GetAsync("/terminal/")).StatusCode);
        http.DefaultRequestHeaders.Add("X-Signed-In", "1");
        Assert.Equal("ttyd page", await http.GetStringAsync("/terminal/"));

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("X-Signed-In", "1");
        ws.Options.AddSubProtocol("tty");
        await ws.ConnectAsync(new Uri(address.Replace("http", "ws") + "/terminal/ws"), CancellationToken.None);
        Assert.Equal("tty", ws.SubProtocol);
        await ws.SendAsync(Encoding.UTF8.GetBytes("ls"), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[1024];
        var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal("echo:ls", Encoding.UTF8.GetString(buffer, 0, r.Count));
        Assert.Equal(1, terminal.Status.Sessions);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        for (var i = 0; i < 50 && terminal.Status.Sessions > 0; i++) await Task.Delay(20);
        Assert.Equal(0, terminal.Status.Sessions);
        await nova.StopAsync();
    }
}
