using Nova.Core.Equipment;

namespace Nova.Core.Simulation;

/// <summary>A five-position LRGB + Ha wheel that takes about a second per slot.</summary>
internal sealed class SimulatedFilterWheel : IFilterWheel
{
    private static readonly string[] FilterNames = ["L", "R", "G", "B", "Ha"];

    /// <summary>
    /// Where each filter focuses relative to L (focuser steps), as with a real refractor: red and Ha a little further
    /// out, blue a little in. What NOVA's filter offsets measure.
    /// </summary>
    internal static readonly int[] FocusShifts = [0, 60, 0, -50, 90];

    /// <summary>The current filter's focus shift.</summary>
    internal int FocusShift => Position >= 1 && Position <= FocusShifts.Length ? FocusShifts[Position - 1] : 0;

    public string Name => "Simulated Filter Wheel";
    public bool IsConnected { get; private set; }
    public int Position { get; private set; } = 1;
    public int SlotCount => FilterNames.Length;
    public IReadOnlyList<string> Names => FilterNames;
    public bool IsMoving { get; private set; }

    public Task ConnectAsync(CancellationToken ct) { IsConnected = true; return Task.CompletedTask; }
    public Task DisconnectAsync(CancellationToken ct) { IsConnected = false; return Task.CompletedTask; }

    public async Task MoveToAsync(int slot, CancellationToken ct)
    {
        if (!IsConnected) throw new InvalidOperationException("Filter wheel is not connected");
        if (slot < 1 || slot > SlotCount) throw new ArgumentException($"Slot must be between 1 and {SlotCount}");

        IsMoving = true;
        try
        {
            // The wheel only turns one way, like most real ones.
            var slots = (slot - Position + SlotCount) % SlotCount;
            await Task.Delay(TimeSpan.FromSeconds(0.2 + 0.8 * slots), ct);
            Position = slot;
        }
        finally
        {
            IsMoving = false;
        }
    }
}

/// <summary>A flip-flat: a dimmable panel (0–255) on a cover that takes a couple of seconds to swing over.</summary>
internal sealed class SimulatedFlatPanel : IFlatPanel
{
    public string Name => "Simulated Flat Panel";
    public bool IsConnected { get; private set; }
    public bool HasLight => true;
    public bool LightOn { get; private set; }
    private double _brightness = 128;
    public NumberSetting? Brightness => new(_brightness, 0, 255, 1);
    public bool HasCover => true;
    public bool? CoverClosed { get; private set; } = false;
    public bool CoverMoving { get; private set; }

    /// <summary>How much light reaches the sensor, 0 (none) to 1 (full brightness): only with the cover closed and lit.</summary>
    public double Glow => IsConnected && LightOn && CoverClosed == true ? _brightness / 255 : 0;

    public Task ConnectAsync(CancellationToken ct) { IsConnected = true; return Task.CompletedTask; }
    public Task DisconnectAsync(CancellationToken ct) { IsConnected = false; return Task.CompletedTask; }

    public Task SetLightAsync(bool on, double? brightness, CancellationToken ct)
    {
        if (!IsConnected) throw new InvalidOperationException("Flat panel is not connected");
        if (brightness is { } b) _brightness = Math.Clamp(Math.Round(b), 0, 255);
        LightOn = on;
        return Task.CompletedTask;
    }

    public async Task SetCoverAsync(bool closed, CancellationToken ct)
    {
        if (!IsConnected) throw new InvalidOperationException("Flat panel is not connected");
        if (CoverClosed == closed) return;
        CoverMoving = true;
        try
        {
            await Task.Delay(1500, ct);
            CoverClosed = closed;
        }
        finally
        {
            CoverMoving = false;
        }
    }
}

/// <summary>A rotator turning at 10°/s along the shortest path.</summary>
internal sealed class SimulatedRotator : IRotator
{
    private const double DegreesPerSecond = 10;
    private CancellationTokenSource? _move;

    public string Name => "Simulated Rotator";
    public bool IsConnected { get; private set; }
    public double Angle { get; private set; } = 90;
    public bool IsMoving { get; private set; }

    public Task ConnectAsync(CancellationToken ct) { IsConnected = true; return Task.CompletedTask; }
    public Task DisconnectAsync(CancellationToken ct) { _move?.Cancel(); IsConnected = false; return Task.CompletedTask; }

    public async Task MoveToAsync(double angle, CancellationToken ct)
    {
        if (!IsConnected) throw new InvalidOperationException("Rotator is not connected");
        angle = (angle % 360 + 360) % 360;

        _move?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _move = cts;
        IsMoving = true;
        try
        {
            while (true)
            {
                await Task.Delay(50, cts.Token);
                var remaining = ((angle - Angle) % 360 + 540) % 360 - 180; // shortest signed difference
                var step = DegreesPerSecond * 0.05;
                if (Math.Abs(remaining) <= step)
                {
                    Angle = angle;
                    break;
                }
                Angle = ((Angle + Math.Sign(remaining) * step) % 360 + 360) % 360;
            }
        }
        finally
        {
            IsMoving = false;
            if (_move == cts) _move = null;
        }
    }

    public Task HaltAsync(CancellationToken ct)
    {
        _move?.Cancel();
        return Task.CompletedTask;
    }
}
