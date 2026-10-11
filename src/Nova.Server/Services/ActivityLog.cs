using Microsoft.AspNetCore.SignalR;
using Nova.Server.Hubs;

namespace Nova.Server.Services;

public sealed record LogEntry(DateTime TimeUtc, string Level, string Message);

/// <summary>User-facing activity log: kept in memory for new clients and pushed live to browsers.</summary>
public sealed class ActivityLog(IHubContext<NovaHub> hub, ILogger<ActivityLog> logger)
{
    private const int Capacity = 300;
    private readonly Queue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Recent
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public void Info(string message) => Add("info", message, LogLevel.Information);
    public void Warn(string message) => Add("warn", message, LogLevel.Warning);

    public void Error(string message, Exception? exception = null)
    {
        logger.LogError(exception, "{Message}", message);
        Add("error", exception is null ? message : $"{message}: {exception.Message}", null);
    }

    private void Add(string level, string message, LogLevel? logLevel)
    {
        if (logLevel is { } l) logger.Log(l, "{Message}", message);
        var entry = new LogEntry(DateTime.UtcNow, level, message);
        lock (_entries)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity) _entries.Dequeue();
        }
        _ = hub.Clients.All.SendAsync(NovaHub.Events.Log, entry);
    }
}
