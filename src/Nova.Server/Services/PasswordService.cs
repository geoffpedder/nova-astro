using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nova.Server.Services;

/// <param name="PasswordSet">NOVA asks for a password (otherwise anyone on the network can use it).</param>
/// <param name="SignedIn">This browser may use NOVA: signed in, or no password is set.</param>
public sealed record AuthStatus(bool PasswordSet, bool SignedIn);

/// <summary>
/// NOVA's optional password, kept in auth.json in the config folder as a salted PBKDF2 hash, never the password
/// itself. The file only exists while a password is set (deploy/nova-screen.sh checks that too, to keep PHD2's
/// own page local). Signed-in browsers carry a cookie with the password's stamp, which changes with every new
/// password, so changing or removing it signs every other browser out. `nova password` on the mini PC writes the
/// same file, so the service rereads it when it changes.
/// </summary>
public sealed class PasswordService
{
    public const int MinimumLength = 6;
    public const string StampClaim = "nova-password-stamp";
    private const int Iterations = 210_000;

    private readonly string _file;
    private readonly ActivityLog? _log;
    private readonly object _sync = new();
    private Stored? _stored;
    private DateTime _loadedWriteTime;
    private DateTime _checkedUtc;
    private int _failures;

    private sealed record Stored(string Hash, string Salt, int Iterations, string Stamp);

    public PasswordService(IOptions<NovaOptions> options, ActivityLog? log = null)
    {
        _file = Path.Combine(options.Value.ResolveConfigDirectory(), "auth.json");
        _log = log;
        Reload(force: true);
    }

    public bool PasswordSet => Current is not null;

    /// <summary>The password was set, changed or removed (here or by `nova password`).</summary>
    public event Action? Changed;

    private Stored? Current
    {
        get
        {
            Reload(force: false);
            return _stored;
        }
    }

    public AuthStatus Status(ClaimsPrincipal user) => new(PasswordSet, IsSignedIn(user));

    /// <summary>Whether this browser may use NOVA: no password, or signed in since the password was last set.</summary>
    public bool IsSignedIn(ClaimsPrincipal user) =>
        Current is not { } stored
        || (user.Identity?.IsAuthenticated == true && user.FindFirst(StampClaim)?.Value == stored.Stamp);

    public ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity([new Claim(StampClaim, Current?.Stamp ?? "")], "nova-password"));

    /// <summary>Checks a password. Wrong guesses are answered more slowly each time (up to 30 s), so guessing is slow.</summary>
    public async Task<bool> CheckAsync(string? password, CancellationToken ct)
    {
        if (Current is not { } stored) return true;
        if (Matches(stored, password ?? ""))
        {
            Interlocked.Exchange(ref _failures, 0);
            return true;
        }
        var failures = Interlocked.Increment(ref _failures);
        _log?.Warn($"Wrong password entered ({failures} in a row)");
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, failures)), ct);
        return false;
    }

    /// <summary>Sets the password, or removes it when <paramref name="password"/> is empty.</summary>
    public void Set(string? password)
    {
        lock (_sync)
        {
            if (string.IsNullOrEmpty(password))
            {
                if (File.Exists(_file)) File.Delete(_file);
                _stored = null;
                _log?.Info("Password removed: anyone on the network can use NOVA");
                Changed?.Invoke();
                return;
            }
            if (password.Length < MinimumLength) throw new ArgumentException($"Use at least {MinimumLength} characters");

            var salt = RandomNumberGenerator.GetBytes(16);
            var stored = new Stored(
                Convert.ToBase64String(Hash(password, salt, Iterations)), Convert.ToBase64String(salt), Iterations,
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(stored));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _stored = stored;
            _loadedWriteTime = File.GetLastWriteTimeUtc(_file);
            _log?.Info("Password set: NOVA now asks for it");
        }
        Changed?.Invoke();
    }

    private static bool Matches(Stored stored, string password)
    {
        try
        {
            var expected = Convert.FromBase64String(stored.Hash);
            var actual = Hash(password, Convert.FromBase64String(stored.Salt), stored.Iterations);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Hash(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>Picks up `nova password` changes: rereads the file when it changed (checked at most once a second).</summary>
    private void Reload(bool force)
    {
        if (!force && DateTime.UtcNow - _checkedUtc < TimeSpan.FromSeconds(1)) return;
        var before = _stored?.Stamp;
        lock (_sync) ReloadLocked(force);
        if (!force && _stored?.Stamp != before) Changed?.Invoke();
    }

    private void ReloadLocked(bool force)
    {
        _checkedUtc = DateTime.UtcNow;
        if (!File.Exists(_file))
        {
            _stored = null;
            return;
        }
        var written = File.GetLastWriteTimeUtc(_file);
        if (!force && written == _loadedWriteTime) return;
        _loadedWriteTime = written;
        try
        {
            _stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_file));
        }
        catch (Exception ex)
        {
            // Unreadable: keep asking for a password rather than opening up; `nova password --clear` fixes it.
            _log?.Error($"Couldn't read {_file}; clear it with: nova password --clear", ex);
            _stored ??= new Stored("", "", Iterations, Guid.NewGuid().ToString());
        }
    }

    /// <summary>
    /// `Nova.Server password` (and `nova password` on the mini PC): sets the password, asking twice, or with
    /// --clear removes it, e.g. when it's forgotten.
    /// </summary>
    public static int RunCommand(string[] args, IOptions<NovaOptions> options)
    {
        var service = new PasswordService(options);
        if (args.Contains("--clear"))
        {
            service.Set(null);
            Console.WriteLine("Password removed: anyone on the network can use NOVA.");
            return 0;
        }
        var first = ReadSecret("New password: ");
        if (string.IsNullOrEmpty(first))
        {
            Console.Error.WriteLine("No password entered; nothing changed. (--clear removes the password.)");
            return 1;
        }
        if (ReadSecret("Again: ") != first)
        {
            Console.Error.WriteLine("They don't match; nothing changed.");
            return 1;
        }
        try
        {
            service.Set(first);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"{ex.Message}; nothing changed.");
            return 1;
        }
        Console.WriteLine("Password set. Browsers will ask for it (once each; they stay signed in for 30 days).");
        return 0;
    }

    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
            else if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.WriteLine();
        return text.ToString();
    }
}
