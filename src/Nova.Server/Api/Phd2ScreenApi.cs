using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Nova.Server.Services;

namespace Nova.Server.Api;

/// <summary>
/// PHD2's virtual screen inside NOVA, through NOVA's own port: noVNC's web client is served from /phd2/novnc/ and
/// its connection relayed at /phd2/novnc/websockify straight to x11vnc on the mini PC (which only listens locally). So
/// the browser needs nothing but port 5000, and the PHD2 tab works wherever NOVA does.
/// </summary>
public static class Phd2ScreenApi
{
    private static readonly string[] NoVncFolders = ["/usr/share/webapps/novnc", "/usr/share/novnc", "/usr/share/noVNC"];

    /// <summary>Where noVNC's web client is installed, or null.</summary>
    public static string? NoVncFolder(NovaOptions options) =>
        (string.IsNullOrWhiteSpace(options.NoVncPath) ? NoVncFolders : [options.NoVncPath])
        .FirstOrDefault(d => File.Exists(Path.Combine(d, "vnc.html")));

    /// <summary>Serves noVNC's files at /phd2/novnc/ and accepts WebSockets (call before the API is mapped).</summary>
    public static void UsePhd2Screen(this WebApplication app)
    {
        app.UseWebSockets();
        var options = app.Services.GetRequiredService<IOptions<NovaOptions>>().Value;
        if (NoVncFolder(options) is { } folder)
            app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(folder), RequestPath = "/phd2/novnc" });
    }

    public static void MapPhd2Screen(this RouteGroupBuilder api, IEndpointRouteBuilder app)
    {
        // Whether the screen can be shown, and why not.
        api.MapGet("/phd2/screen", async (IOptions<NovaOptions> options, CancellationToken ct) =>
        {
            var o = options.Value;
            if (NoVncFolder(o) is null)
                return Results.Ok(new { Available = false, Problem = "noVNC isn't installed on the mini PC: run nova setup (Arch: yay -S novnc websockify, then install.sh again)" });
            if (!await CanReachAsync(o.Phd2VncPort, ct))
                return Results.Ok(new { Available = false, Problem = "PHD2's screen isn't being shared: is nova-screen running? (sudo systemctl status nova-screen)" });
            return Results.Ok(new { Available = true, Problem = (string?)null });
        });

        // The VNC stream: WebSocket (binary frames) <-> x11vnc's TCP port, both ways, until either end closes.
        // noVNC connects to "websockify" (its default path): next to its page in 1.6+ (/phd2/novnc/websockify),
        // at the site's root in older versions (/websockify). /phd2/websockify stays for anything using it directly.
        app.Map("/phd2/novnc/websockify", RelayAsync);
        app.Map("/websockify", RelayAsync);
        app.Map("/phd2/websockify", RelayAsync);
    }

    private static async Task RelayAsync(HttpContext http, IOptions<NovaOptions> options, ActivityLog log)
    {
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                log.Warn($"PHD2 screen: a request for the VNC relay that wasn't a WebSocket ({http.Request.Method}, {http.Request.Protocol})");
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var protocol = http.WebSockets.WebSocketRequestedProtocols.FirstOrDefault(p => p == "binary");
            using var socket = await http.WebSockets.AcceptWebSocketAsync(protocol);
            using var vnc = new TcpClient();
            try
            {
                await vnc.ConnectAsync("127.0.0.1", options.Value.Phd2VncPort, http.RequestAborted);
            }
            catch (SocketException ex)
            {
                log.Warn($"PHD2 screen: couldn't reach x11vnc on port {options.Value.Phd2VncPort} ({ex.Message})");
                await socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "PHD2's screen isn't being shared", CancellationToken.None);
                return;
            }
            log.Info($"PHD2 screen: browser connected (protocol '{protocol ?? "none"}'), relaying to x11vnc on port {options.Value.Phd2VncPort}");
            long toVncBytes = 0, toBrowserBytes = 0;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            var stream = vnc.GetStream();
            var toVnc = Task.Run(async () =>
            {
                var buffer = new byte[64 * 1024];
                while (!cts.IsCancellationRequested)
                {
                    var r = await socket.ReceiveAsync(buffer, cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    await stream.WriteAsync(buffer.AsMemory(0, r.Count), cts.Token);
                    Interlocked.Add(ref toVncBytes, r.Count);
                }
            });
            var toBrowser = Task.Run(async () =>
            {
                var buffer = new byte[64 * 1024];
                int n;
                while ((n = await stream.ReadAsync(buffer, cts.Token)) > 0)
                {
                    await socket.SendAsync(buffer.AsMemory(0, n), WebSocketMessageType.Binary, true, cts.Token);
                    Interlocked.Add(ref toBrowserBytes, n);
                }
            });
            Task? ended = null;
            try
            {
                ended = await Task.WhenAny(toVnc, toBrowser);
                await ended; // surface its error, if any
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Warn($"PHD2 screen: the relay stopped ({ex.GetType().Name}: {ex.Message})");
            }
            catch (OperationCanceledException)
            {
                // the browser left
            }
            finally
            {
                log.Info($"PHD2 screen: {(ended == toVnc ? "the browser" : "x11vnc")} closed the connection " +
                         $"({Interlocked.Read(ref toVncBytes):N0} bytes to x11vnc, {Interlocked.Read(ref toBrowserBytes):N0} to the browser)");
                cts.Cancel();
                if (socket.State == WebSocketState.Open)
                    try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { /* gone */ }
            }
        }
    }

    private static async Task<bool> CanReachAsync(int port, CancellationToken ct)
    {
        try
        {
            using var probe = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            await probe.ConnectAsync("127.0.0.1", port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }
}
