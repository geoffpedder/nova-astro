using System.Net.Sockets;
using System.Net.WebSockets;
using Nova.Server.Services;

namespace Nova.Server.Api;

/// <summary>
/// The terminal (ttyd) through NOVA's own port: its page and script at /terminal/, its WebSocket at /terminal/ws,
/// relayed to ttyd's socket file. Only for a signed-in browser (Program's guard) and only with a NOVA password set
/// (checked again here, so nothing else can open a shell).
/// </summary>
public static class TerminalApi
{
    public static void MapTerminal(this WebApplication app)
    {
        app.MapGet("/api/terminal", (TerminalService terminal) => terminal.Status);
        app.Map("/terminal/{**rest}", ProxyAsync);
    }

    /// <summary>Refuses the terminal unless a password is set and this browser has signed in with it.</summary>
    public static int? Refusal(HttpContext http, TerminalService terminal, PasswordService password) =>
        !password.PasswordSet || terminal.Problem is not null ? StatusCodes.Status403Forbidden
        : !password.IsSignedIn(http.User) ? StatusCodes.Status401Unauthorized
        : null;

    private static async Task ProxyAsync(HttpContext http, TerminalService terminal, PasswordService password, ActivityLog log)
    {
        if (Refusal(http, terminal, password) is { } status)
        {
            http.Response.StatusCode = status;
            await http.Response.WriteAsync(terminal.Problem ?? "Sign in to NOVA first", http.RequestAborted);
            return;
        }
        try
        {
            await terminal.EnsureRunningAsync(http.RequestAborted);
        }
        catch (InvalidOperationException ex)
        {
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await http.Response.WriteAsync(ex.Message, http.RequestAborted);
            return;
        }
        var path = http.Request.Path.Value + http.Request.QueryString.Value;
        if (http.WebSockets.IsWebSocketRequest) await RelayWebSocketAsync(http, terminal, path, log);
        else await ForwardAsync(http, terminal, path);
    }

    /// <summary>A connection to ttyd's socket file, for HTTP and the WebSocket alike.</summary>
    private static SocketsHttpHandler Handler(string socket) => new()
    {
        ConnectCallback = async (_, ct) =>
        {
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await s.ConnectAsync(new UnixDomainSocketEndPoint(socket), ct);
                return new NetworkStream(s, ownsSocket: true);
            }
            catch
            {
                s.Dispose();
                throw;
            }
        },
    };

    /// <summary>ttyd's page, script and token: a plain GET passed through.</summary>
    private static async Task ForwardAsync(HttpContext http, TerminalService terminal, string path)
    {
        using var client = new HttpClient(Handler(terminal.SocketPath)) { BaseAddress = new Uri("http://localhost") };
        using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
        http.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is { } type) http.Response.ContentType = type.ToString();
        http.Response.Headers.CacheControl = "no-store";
        await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
    }

    /// <summary>The terminal itself: messages passed both ways, as they are (ttyd's "tty" protocol), until either side closes.</summary>
    private static async Task RelayWebSocketAsync(HttpContext http, TerminalService terminal, string path, ActivityLog log)
    {
        var protocol = http.WebSockets.WebSocketRequestedProtocols.FirstOrDefault();
        using var ttyd = new ClientWebSocket();
        if (protocol is not null) ttyd.Options.AddSubProtocol(protocol);
        // Kept for the whole session: disposing it closes the connection to ttyd.
        using var invoker = new HttpMessageInvoker(Handler(terminal.SocketPath));
        try
        {
            await ttyd.ConnectAsync(new Uri("ws://localhost" + path), invoker, http.RequestAborted);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or SocketException)
        {
            log.Warn($"Terminal: couldn't reach ttyd ({ex.Message})");
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }
        using var browser = await http.WebSockets.AcceptWebSocketAsync(protocol);
        using var session = terminal.Join(http.Connection.RemoteIpAddress?.ToString() ?? "a browser");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        var up = Pump(browser, ttyd, cts.Token);
        var down = Pump(ttyd, browser, cts.Token);
        try
        {
            await Task.WhenAny(up, down);
        }
        finally
        {
            cts.Cancel();
            // Close both properly: answering a close that arrived (CloseReceived), or starting one (Open).
            foreach (var s in new WebSocket[] { browser, ttyd })
                if (s.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await s.CloseAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
                    }
                    catch
                    {
                        // gone
                    }
        }
    }

    private static async Task Pump(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var r = await from.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                await to.SendAsync(buffer.AsMemory(0, r.Count), r.MessageType, r.EndOfMessage, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // one side left
        }
    }
}
