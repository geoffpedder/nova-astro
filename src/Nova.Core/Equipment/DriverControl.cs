namespace Nova.Core.Equipment;

/// <summary>Starts and stops device drivers on the server (INDI: indiserver's control pipe).</summary>
public interface IDriverControl
{
    /// <summary>False when the server wasn't started with a control pipe; devices can still be used.</summary>
    bool Available { get; }

    /// <summary>The driver program serving a device (e.g. "indi_simulator_ccd"), if the driver says.</summary>
    string? DriverOf(string device);

    Task StartAsync(string driver, CancellationToken ct);
    Task StopAsync(string driver, CancellationToken ct);
}
