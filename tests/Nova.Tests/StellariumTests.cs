using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Nova.Core.Astronomy;
using Nova.Server.Services;
using Opts = Microsoft.Extensions.Options.Options;

namespace Nova.Tests;

public class StellariumProtocolTests
{
    [Fact]
    public void EncodesLikeStellarium()
    {
        var position = StellariumProtocol.Position(new EquatorialCoordinates(12, -45), DateTime.UnixEpoch.AddSeconds(1));
        Assert.Equal(24, BinaryPrimitives.ReadInt16LittleEndian(position));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(position.AsSpan(2)));
        Assert.Equal(1_000_000, BinaryPrimitives.ReadInt64LittleEndian(position.AsSpan(4)));     // µs
        Assert.Equal(0x80000000u, BinaryPrimitives.ReadUInt32LittleEndian(position.AsSpan(12))); // 12h = half circle
        Assert.Equal(-(1 << 29), BinaryPrimitives.ReadInt32LittleEndian(position.AsSpan(16)));   // −45° = −2³⁰/2
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(position.AsSpan(20)));
    }

    [Fact]
    public void GotoRoundTrips()
    {
        var target = new EquatorialCoordinates(23.9999, 89.5);
        var back = StellariumProtocol.ReadGoto(StellariumProtocol.Goto(target, DateTime.UtcNow));
        Assert.Equal(target.RaHours, back.RaHours, 6);
        Assert.Equal(target.DecDegrees, back.DecDegrees, 6);
    }
}

public class StellariumTelescopeServerTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "nova-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task<(EquipmentService Equipment, PlateSolveService Solver, StellariumTelescopeServer Server, int Port)> StartAsync(string mode)
    {
        var options = Opts.Create(new NovaOptions
        {
            Backend = "Simulator", ConfigDirectory = _config, StellariumPort = FreePort(), StellariumGoto = mode, SolveExposureSeconds = 0,
        });
        var hub = new NullHubContext();
        var log = new ActivityLog(hub, NullLogger<ActivityLog>.Instance);
        var equipment = new EquipmentService(options, log);
        var optics = new OpticsService(equipment, options, log);
        var imaging = new ImagingService(equipment, optics, log, hub, options);
        var solver = new PlateSolveService(equipment, imaging, optics, log, options);
        var sites = new SiteService(equipment, options, log);
        await sites.SetSunAvoidanceAsync(0, CancellationToken.None); // test GoTos can be anywhere, whatever the time of day
        var sequencer = new SequencerService(equipment, imaging, solver, new AutofocusService(equipment, imaging, log, options),
            new GuiderService(options, log), sites, log, options);
        var server = new StellariumTelescopeServer(equipment, solver, sites, imaging, sequencer, log, options, NullLogger<StellariumTelescopeServer>.Instance);
        await equipment.ConnectAsync(CancellationToken.None);
        await equipment.Mount!.UnparkAsync(CancellationToken.None);
        await server.StartAsync(CancellationToken.None);
        for (var i = 0; i < 50 && server.Port == 0; i++) await Task.Delay(20);
        return (equipment, solver, server, options.Value.StellariumPort);
    }

    private static async Task<EquatorialCoordinates> ReadPositionAsync(NetworkStream stream)
    {
        var message = new byte[24];
        await stream.ReadExactlyAsync(message).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(24, BinaryPrimitives.ReadInt16LittleEndian(message));
        return StellariumProtocol.ReadGoto(message); // same layout for RA/Dec
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), $"timed out waiting for {what}");
    }

    [Fact]
    public async Task ReportsTheMountAndSlewsOnGoto()
    {
        var (equipment, _, server, port) = await StartAsync("Slew");
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();

        // The reticle: the mount's position in J2000.
        var reported = await ReadPositionAsync(stream);
        var mountJ2000 = Precession.JNowToJ2000(equipment.Mount!.Coordinates, DateTime.UtcNow);
        Assert.True(Precession.Separation(reported, mountJ2000) < 0.01, $"reported {reported}, mount {mountJ2000}");
        await WaitFor(() => server.Status?.Clients == 1, "the client to be counted");

        // Ctrl+1 on M31.
        var m31 = new EquatorialCoordinates(0.7123, 41.269);
        await stream.WriteAsync(StellariumProtocol.Goto(m31, DateTime.UtcNow));
        await WaitFor(() => Precession.Separation(Precession.JNowToJ2000(equipment.Mount.Coordinates, DateTime.UtcNow), m31) < 0.01,
            "the mount to reach M31");

        client.Close();
        await WaitFor(() => server.Status?.Clients == 0, "the disconnect");
        await server.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CentresOnGotoWhenItCan()
    {
        var (_, solver, server, port) = await StartAsync("Center");
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var target = new EquatorialCoordinates(5.6, -5.0);
        await client.GetStream().WriteAsync(StellariumProtocol.Goto(target, DateTime.UtcNow));

        await WaitFor(() => solver.Centering is { Running: false }, "centring to finish");
        Assert.StartsWith("Centred", solver.Centering!.Message);
        await server.StopAsync(CancellationToken.None);
    }
}
