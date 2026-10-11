using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Nova.Core.Astronomy;

namespace Nova.Server.Services;

/// <summary>
/// Stellarium's telescope server protocol: little-endian binary messages over TCP.
/// Stellarium → server (GoTo): length 20, type 0, time (µs, int64), RA (uint32, full circle = 2³²), Dec (int32, 90° = 2³⁰).
/// Server → Stellarium (position): length 24, type 0, time, RA, Dec, status (int32, 0 = OK).
/// </summary>
public static class StellariumProtocol
{
    public const int GotoLength = 20;
    public const int PositionLength = 24;

    public static byte[] Position(EquatorialCoordinates position, DateTime utc)
    {
        var message = new byte[PositionLength];
        var span = message.AsSpan();
        BinaryPrimitives.WriteInt16LittleEndian(span, PositionLength);
        BinaryPrimitives.WriteInt16LittleEndian(span[2..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(span[4..], (utc.ToUniversalTime() - DateTime.UnixEpoch).Ticks / 10);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)((long)Math.Round(position.RaHours / 24 * 4294967296.0) & 0xFFFFFFFF));
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], (int)Math.Round(Math.Clamp(position.DecDegrees, -90, 90) / 90 * 1073741824.0));
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], 0);
        return message;
    }

    /// <summary>The target of a GoTo message (the whole message, length prefix included).</summary>
    public static EquatorialCoordinates ReadGoto(ReadOnlySpan<byte> message)
    {
        var ra = BinaryPrimitives.ReadUInt32LittleEndian(message[12..]) * 24.0 / 4294967296.0;
        var dec = BinaryPrimitives.ReadInt32LittleEndian(message[16..]) * 90.0 / 1073741824.0;
        return new EquatorialCoordinates(ra, dec);
    }

    public static byte[] Goto(EquatorialCoordinates target, DateTime utc)
    {
        var message = new byte[GotoLength];
        Position(target, utc).AsSpan(0, GotoLength).CopyTo(message);
        BinaryPrimitives.WriteInt16LittleEndian(message, GotoLength);
        return message;
    }
}

public sealed record StellariumLinkStatus(int Port, int Clients);

/// <summary>
/// NOVA as a telescope for Stellarium (Telescope Control → "External software or a remote computer", this
/// machine's address and <see cref="NovaOptions.StellariumPort"/>). Stellarium's reticle follows the mount;
/// a GoTo (Ctrl+1) slews there and, by default, plate solves until centred.
/// </summary>
public sealed class StellariumTelescopeServer(
    EquipmentService equipment, PlateSolveService solver, SiteService sites, ImagingService imaging, SequencerService sequencer,
    ActivityLog log, IOptions<NovaOptions> options, ILogger<StellariumTelescopeServer> logger) : BackgroundService
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(500);
    private int _clients;
    private TcpListener? _listener;

    /// <summary>The port actually listened on (useful when configured as a free port in tests), or 0.</summary>
    public int Port => _listener?.LocalEndpoint is IPEndPoint ep ? ep.Port : 0;
    public StellariumLinkStatus? Status => _listener is null ? null : new StellariumLinkStatus(Port, _clients);

    private bool JNow => options.Value.StellariumEquinox.Equals("JNow", StringComparison.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = options.Value.StellariumPort;
        if (port <= 0) return;
        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
        }
        catch (SocketException ex)
        {
            _listener = null;
            log.Warn($"Stellarium telescope link unavailable: port {port} is in use ({ex.Message})");
            return;
        }
        logger.LogInformation("Stellarium telescope link on port {Port}", Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => ServeAsync(client, stoppingToken), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stoppingToken)
    {
        var who = client.Client.RemoteEndPoint?.ToString() ?? "?";
        Interlocked.Increment(ref _clients);
        log.Info($"Stellarium connected from {who}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            using (client)
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var sender = SendPositionsAsync(stream, cts.Token);
                try
                {
                    await ReceiveAsync(stream, cts.Token);
                }
                finally
                {
                    cts.Cancel();
                    try { await sender; } catch (Exception) { /* connection closing */ }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Stellarium went away or NOVA is stopping.
        }
        finally
        {
            Interlocked.Decrement(ref _clients);
            log.Info($"Stellarium at {who} disconnected");
        }
    }

    private async Task SendPositionsAsync(NetworkStream stream, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(UpdateInterval);
        do
        {
            if (equipment.Mount is { IsConnected: true } mount)
            {
                var now = DateTime.UtcNow;
                var position = JNow ? mount.Coordinates : Precession.JNowToJ2000(mount.Coordinates, now);
                await stream.WriteAsync(StellariumProtocol.Position(position, now), ct);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task ReceiveAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        while (true)
        {
            if (!await ReadExactlyAsync(stream, header, ct)) return; // closed
            var length = BinaryPrimitives.ReadInt16LittleEndian(header);
            var type = BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(2));
            if (length < 4) return; // garbage: drop the connection
            var message = new byte[length];
            header.CopyTo(message, 0);
            if (!await ReadExactlyAsync(stream, message.AsMemory(4), ct)) return;
            if (type == 0 && length >= StellariumProtocol.GotoLength) Goto(StellariumProtocol.ReadGoto(message));
        }
    }

    private void Goto(EquatorialCoordinates received)
    {
        var target = JNow ? Precession.JNowToJ2000(received, DateTime.UtcNow) : received;
        log.Info($"Stellarium GoTo {target} (J2000)");
        if ((Api.NovaApi.ManualMoveLock(imaging, sequencer) ?? sites.BelowHorizon(target) ?? sites.NearSun(target)) is { } refused)
        {
            log.Warn($"Stellarium GoTo not done: {refused}");
            return;
        }
        var center = options.Value.StellariumGoto.Equals("Center", StringComparison.OrdinalIgnoreCase)
                     && equipment.Camera is { IsConnected: true } && solver.Solver.Available;
        string? error;
        var started = center
            ? solver.StartCentering(target, out error)
            : equipment.StartMountOperation($"Slewing to {target} (J2000, from Stellarium)",
                (m, ct) => sites.SlewSafelyAsync(m, Precession.J2000ToJNow(target, DateTime.UtcNow), ct), out error);
        if (!started) log.Warn($"Stellarium GoTo not done: {error}");
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
