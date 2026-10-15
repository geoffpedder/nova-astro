using Nova.Core.Equipment;
using Nova.Core.Focusing;

namespace Nova.Tests;

public class FocuserMovesTests
{
    /// <summary>A focuser that records where it was sent, with gear slack: the position it reports only follows the
    /// motor once the slack is taken up, as a real one with backlash does.</summary>
    private sealed class FakeFocuser(bool absolute = true, int slack = 0) : IFocuser
    {
        public List<int> Moves { get; } = [];
        public List<int> Steps { get; } = [];
        private int _motor = 5000, _lastDirection;
        public string Name => "Fake focuser";
        public bool IsConnected => true;
        public bool CanMoveAbsolute => absolute;
        public int Position { get; private set; } = 5000;
        /// <summary>Where the optics are: behind the motor by the slack when it last moved out, ahead when in.</summary>
        public double Optics => _motor - _lastDirection * slack / 2.0;
        public int MinPosition => 0;
        public int MaxPosition => 10_000;
        public bool IsMoving => false;
        public double? Temperature => null;
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task HaltAsync(CancellationToken ct) => Task.CompletedTask;

        public Task MoveToAsync(int position, CancellationToken ct)
        {
            Moves.Add(position);
            Go(position - _motor);
            return Task.CompletedTask;
        }

        public Task MoveRelativeAsync(int steps, CancellationToken ct)
        {
            Steps.Add(steps);
            Go(steps);
            return Task.CompletedTask;
        }

        private void Go(int steps)
        {
            if (steps != 0) _lastDirection = Math.Sign(steps);
            _motor += steps;
            Position = _motor;
        }
    }

    [Theory]
    [InlineData(5300, new[] { 5400, 5300 })] // out: past it and back in
    [InlineData(4700, new[] { 4700 })]       // in: straight there
    [InlineData(9950, new[] { 10_000, 9950 })] // the overshoot stops at the end of travel
    public void InwardApproachOvershootsOutwardMoves(int target, int[] moves)
    {
        var focuser = new FakeFocuser();
        FocuserMoves.MoveToAsync(focuser, target, 100, FocusApproach.Inward, CancellationToken.None).Wait();
        Assert.Equal(moves, focuser.Moves);
    }

    [Theory]
    [InlineData(4700, new[] { 4600, 4700 })] // in: past it and back out
    [InlineData(5300, new[] { 5300 })]       // out: straight there
    public void OutwardApproachOvershootsInwardMoves(int target, int[] moves)
    {
        var focuser = new FakeFocuser();
        FocuserMoves.MoveToAsync(focuser, target, 100, FocusApproach.Outward, CancellationToken.None).Wait();
        Assert.Equal(moves, focuser.Moves);
    }

    [Fact]
    public void NoBacklashMovesStraightThere()
    {
        var focuser = new FakeFocuser();
        FocuserMoves.MoveToAsync(focuser, 5300, 0, FocusApproach.Inward, CancellationToken.None).Wait();
        Assert.Equal([5300], focuser.Moves);
    }

    [Fact]
    public void RelativeMovesFinishTheSameWay()
    {
        var absolute = new FakeFocuser();
        FocuserMoves.MoveRelativeAsync(absolute, 200, 100, FocusApproach.Inward, CancellationToken.None).Wait();
        Assert.Equal([5300, 5200], absolute.Moves);

        var relative = new FakeFocuser(absolute: false);
        FocuserMoves.MoveRelativeAsync(relative, 200, 100, FocusApproach.Inward, CancellationToken.None).Wait();
        FocuserMoves.MoveRelativeAsync(relative, -50, 100, FocusApproach.Inward, CancellationToken.None).Wait();
        Assert.Equal([300, -100, -50], relative.Steps);
    }

    [Fact]
    public void APositionMeansTheSameFocusWhicheverWayItCameFrom()
    {
        // 40 steps of slack: without compensation the optics end up 40 steps apart for the same position.
        static double ArriveAt(int target, int from, int backlash)
        {
            var focuser = new FakeFocuser(slack: 40);
            FocuserMoves.MoveToAsync(focuser, from, 0, FocusApproach.Inward, CancellationToken.None).Wait();
            FocuserMoves.MoveToAsync(focuser, target, backlash, FocusApproach.Inward, CancellationToken.None).Wait();
            return focuser.Optics;
        }
        Assert.Equal(40, Math.Abs(ArriveAt(5000, 4000, 0) - ArriveAt(5000, 6000, 0)));
        Assert.Equal(0, ArriveAt(5000, 4000, 100) - ArriveAt(5000, 6000, 100));
    }
}
