using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Nova.Server.Services;

namespace Nova.Server.Hubs;

/// <summary>
/// Server-to-browser push channel. Browsers send commands over REST (/api/...) and receive
/// state here, so commands stay easy to call from scripts and curl as well as the UI.
/// </summary>
public sealed class NovaHub(StatusReporter status, ActivityLog log) : Hub
{
    /// <summary>Event names; keep in sync with web/src/lib/hub.ts.</summary>
    public static class Events
    {
        public const string Status = "status";
        public const string ExposureProgress = "exposureProgress";
        public const string ImageReady = "imageReady";
        public const string Log = "log";
        public const string LogHistory = "logHistory";
    }

    private static readonly ConcurrentDictionary<string, HubCallerContext> Connected = new();

    /// <summary>
    /// Drops every browser's live connection, e.g. when the password changes: each reconnects with its own
    /// sign-in, so one signed out by the change stops receiving updates straight away.
    /// </summary>
    public static void DisconnectAll()
    {
        foreach (var context in Connected.Values) context.Abort();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        Connected.TryRemove(Context.ConnectionId, out _);
        return base.OnDisconnectedAsync(exception);
    }

    public override async Task OnConnectedAsync()
    {
        Connected[Context.ConnectionId] = Context;
        // Bring a newly connected browser up to date immediately.
        await Clients.Caller.SendAsync(Events.Status, status.GetSnapshot());
        await Clients.Caller.SendAsync(Events.LogHistory, log.Recent);
        await base.OnConnectedAsync();
    }
}
