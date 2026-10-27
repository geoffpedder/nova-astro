using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Nova.Server.Services;

/// <param name="Available">The terminal can be opened (Linux, ttyd installed, a NOVA password set).</param>
/// <param name="Problem">Why not, in words, or null.</param>
/// <param name="Sessions">Browsers with the terminal open now.</param>
public sealed record TerminalStatus(bool Available, string? Problem, bool Running, int Sessions);

/// <summary>ttyd running (or a stand-in in tests).</summary>
public interface ITerminalProcess : IDisposable
{
    bool HasExited { get; }
    void Stop();
}

/// <summary>
/// A terminal on the mini PC in the browser (the Mini PC tab's Mini PC box): ttyd, started when a browser opens it
/// and stopped a minute after the last one closes, running a login shell as NOVA's user. It listens on a socket file
/// in a folder only NOVA's user can open (no network port, so nothing else on the mini PC can reach it), and NOVA
/// relays it through its own port, only to a signed-in browser, and only once a NOVA password is set: it's a shell.
/// </summary>
public sealed class TerminalService : IAsyncDisposable
{
    private readonly PasswordService _password;
    private readonly ActivityLog _log;
    private readonly object _sync = new();
    private ITerminalProcess? _process;
    private int _sessions;
    private CancellationTokenSource? _stopTimer;
    private readonly bool _linux;

    public TerminalService(IOptions<NovaOptions> options, PasswordService password, ActivityLog log)
    {
        _password = password;
        _log = log;
        _linux = OperatingSystem.IsLinux() && options.Value.Backend.Equals("Indi", StringComparison.OrdinalIgnoreCase);
        var folder = Path.Combine(options.Value.ResolveConfigDirectory(), "terminal");
        SocketPath = Path.Combine(folder, "ttyd.sock");
        Launch = StartTtyd;
        TtydPath = FindTtyd();
    }

    /// <summary>ttyd's socket file: in a folder only NOVA's user can open.</summary>
    public string SocketPath { get; }

    /// <summary>Where ttyd is installed, or null (replaceable in tests).</summary>
    public string? TtydPath { get; set; }

    /// <summary>Starts ttyd on the socket (replaceable in tests).</summary>
    public Func<string, ITerminalProcess> Launch { get; set; }

    /// <summary>On the mini PC (replaceable in tests, so the rules can be checked anywhere).</summary>
    public bool OnMiniPc
    {
        get => field || _linux;
        set;
    }

    /// <summary>How long ttyd keeps running after the last browser closes the terminal.</summary>
    public TimeSpan StopAfter { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Why the terminal can't be opened, or null.</summary>
    public string? Problem =>
        !OnMiniPc ? "The terminal is on the mini PC only (not on a Windows PC or with the simulator)"
        : !_password.PasswordSet ? "Set a NOVA password first (the power menu → Set a password…): the terminal is a shell on the mini PC, so it's only offered behind one"
        : TtydPath is null ? "ttyd isn't installed: nova setup (or: sudo apt install ttyd, sudo pacman -S ttyd)"
        : null;

    public TerminalStatus Status
    {
        get
        {
            var problem = Problem;
            lock (_sync) return new TerminalStatus(problem is null, problem, _process is { HasExited: false }, _sessions);
        }
    }

    /// <summary>Starts ttyd if it isn't running, and waits (briefly) for its socket.</summary>
    public async Task EnsureRunningAsync(CancellationToken ct)
    {
        if (Problem is { } problem) throw new InvalidOperationException(problem);
        lock (_sync)
        {
            _stopTimer?.Cancel();
            if (_process is { HasExited: false }) return;
            _process?.Dispose();
            var folder = Path.GetDirectoryName(SocketPath)!;
            Directory.CreateDirectory(folder);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (File.Exists(SocketPath)) File.Delete(SocketPath); // left by a ttyd that didn't stop cleanly
            _process = Launch(SocketPath);
        }
        for (var i = 0; i < 50 && !File.Exists(SocketPath); i++) await Task.Delay(100, ct);
        if (!File.Exists(SocketPath)) throw new InvalidOperationException("The terminal (ttyd) didn't start");
    }

    /// <summary>A browser has the terminal open, until the returned handle is disposed.</summary>
    public IDisposable Join(string who)
    {
        lock (_sync)
        {
            _sessions++;
            _stopTimer?.Cancel();
        }
        _log.Info($"Terminal opened from {who}");
        return new Session(this, who);
    }

    private void Leave(string who)
    {
        CancellationTokenSource timer;
        lock (_sync)
        {
            _sessions = Math.Max(0, _sessions - 1);
            if (_sessions > 0) return;
            _stopTimer?.Cancel();
            _stopTimer = timer = new CancellationTokenSource();
        }
        _log.Info($"Terminal closed ({who})");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StopAfter, timer.Token);
            }
            catch (OperationCanceledException)
            {
                return; // someone opened it again
            }
            lock (_sync)
            {
                if (_sessions > 0 || timer.IsCancellationRequested) return;
                _process?.Stop();
                _process?.Dispose();
                _process = null;
            }
        });
    }

    private sealed class Session(TerminalService service, string who) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) service.Leave(who);
        }
    }

    private static string? FindTtyd() =>
        new[] { "/usr/bin/ttyd", "/usr/local/bin/ttyd", "/bin/ttyd" }.FirstOrDefault(File.Exists);

    /// <summary>ttyd on the socket: writable, a login shell in the home folder, nothing else.</summary>
    private ITerminalProcess StartTtyd(string socket)
    {
        var info = new ProcessStartInfo(TtydPath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
        };
        foreach (var a in new[]
                 {
                     "--interface", socket, "--base-path", "/terminal", "--writable",
                     "-t", "fontSize=13", "-t", "disableLeaveAlert=true", "-t", "titleFixed=NOVA terminal",
                     "-t", """theme={"background":"#141414","foreground":"#d8d8d8","cursor":"#5e9fcf"}""",
                     "bash", "-l",
                 })
            info.ArgumentList.Add(a);
        var process = Process.Start(info) ?? throw new InvalidOperationException("ttyd didn't start");
        // Its log isn't needed, but must be read so it never blocks on a full pipe.
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return new TtydProcess(process);
    }

    private sealed class TtydProcess(Process process) : ITerminalProcess
    {
        public bool HasExited => process.HasExited;

        public void Stop()
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // gone already
            }
        }

        public void Dispose() => process.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _stopTimer?.Cancel();
            _process?.Stop();
            _process?.Dispose();
            _process = null;
        }
        return ValueTask.CompletedTask;
    }
}
