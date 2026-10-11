using Nova.Core.Equipment;

namespace Nova.Core.Simulation;

/// <summary>
/// An absolute focuser (EAF-like range). The main simulated camera blurs its stars by
/// <see cref="Defocus"/>, so focusing has a visible, measurable effect — handy for autofocus later.
/// </summary>
internal sealed class SimulatedFocuser : IFocuser
{
    private const int StepsPerSecond = 2_000;
    private const int BestFocus = 25_000;
    private const double DepthOfFocusSteps = 600; // blur doubles about this far from best focus

    private readonly object _sync = new();
    private CancellationTokenSource? _move;
    private double _position = 23_800; // start a little out of focus

    public string Name => "Simulated Focuser";
    public bool IsConnected { get; private set; }
    public bool CanMoveAbsolute => true;
    public int MinPosition => 0;
    public int MaxPosition => 60_000;
    public int Position { get { lock (_sync) return (int)Math.Round(_position); } }
    public bool IsMoving { get; private set; }

    // A slow, gentle drift like a real probe at night.
    public double? Temperature => Math.Round(11.5 - DateTime.UtcNow.TimeOfDay.TotalHours % 1 * 0.5, 1);

    /// <summary>Star size multiplier: 1 at best focus, growing hyperbolically either side.</summary>
    public double Defocus => DefocusWith(0);

    /// <summary>The same, through a filter that focuses <paramref name="shift"/> steps from the reference.</summary>
    public double DefocusWith(int shift)
    {
        var off = (Position - BestFocus - shift) / DepthOfFocusSteps;
        return Math.Sqrt(1 + off * off);
    }

    public Task ConnectAsync(CancellationToken ct) { IsConnected = true; return Task.CompletedTask; }
    public Task DisconnectAsync(CancellationToken ct) { _move?.Cancel(); IsConnected = false; return Task.CompletedTask; }

    public async Task MoveToAsync(int position, CancellationToken ct)
    {
        if (!IsConnected) throw new InvalidOperationException("Focuser is not connected");
        if (position < MinPosition || position > MaxPosition)
            throw new ArgumentException($"Position must be between {MinPosition} and {MaxPosition}");

        _move?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _move = cts;
        IsMoving = true;
        try
        {
            while (true)
            {
                await Task.Delay(50, cts.Token);
                lock (_sync)
                {
                    var remaining = position - _position;
                    var step = StepsPerSecond * 0.05;
                    if (Math.Abs(remaining) <= step)
                    {
                        _position = position;
                        break;
                    }
                    _position += Math.Sign(remaining) * step;
                }
            }
        }
        finally
        {
            IsMoving = false;
            if (_move == cts) _move = null;
        }
    }

    public Task MoveRelativeAsync(int steps, CancellationToken ct) =>
        MoveToAsync(Math.Clamp(Position + steps, MinPosition, MaxPosition), ct);

    public Task HaltAsync(CancellationToken ct)
    {
        _move?.Cancel();
        return Task.CompletedTask;
    }
}
