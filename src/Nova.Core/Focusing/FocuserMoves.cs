using Nova.Core.Equipment;

namespace Nova.Core.Focusing;

/// <summary>Which way the focuser is moving when it arrives at a position: in (shorter) or out.</summary>
public enum FocusApproach { Inward, Outward }

/// <summary>
/// Focuser moves with backlash compensation by overshoot (as NINA's "Overshoot" method): every position is reached
/// moving the same way, so the slack in the gears is always taken up the same way and a position means the same
/// focus whichever way the focuser came from. A move that would arrive the other way goes <c>backlash</c> steps past
/// the target first and comes back. Used for every move: autofocus, filter offsets and moving by hand.
/// </summary>
public static class FocuserMoves
{
    /// <param name="backlash">Steps to overshoot by; 0 moves straight there.</param>
    /// <param name="approach">The direction every move finishes in.</param>
    public static async Task MoveToAsync(IFocuser focuser, int target, int backlash, FocusApproach approach, CancellationToken ct)
    {
        if (backlash > 0)
        {
            var from = focuser.Position;
            if (approach == FocusApproach.Inward && target > from)
                await focuser.MoveToAsync(Clamp(focuser, target + backlash), ct);
            else if (approach == FocusApproach.Outward && target < from)
                await focuser.MoveToAsync(Clamp(focuser, target - backlash), ct);
        }
        await focuser.MoveToAsync(target, ct);
    }

    /// <summary>A move by <paramref name="steps"/> (negative = in), finishing in the same direction as every other move.</summary>
    public static async Task MoveRelativeAsync(IFocuser focuser, int steps, int backlash, FocusApproach approach, CancellationToken ct)
    {
        if (focuser.CanMoveAbsolute)
        {
            await MoveToAsync(focuser, Clamp(focuser, focuser.Position + steps), backlash, approach, ct);
            return;
        }
        // A relative-only focuser: the same overshoot, in steps.
        var wrongWay = (approach == FocusApproach.Inward && steps > 0) || (approach == FocusApproach.Outward && steps < 0);
        if (backlash > 0 && wrongWay)
        {
            var over = Math.Sign(steps) * backlash;
            await focuser.MoveRelativeAsync(steps + over, ct);
            await focuser.MoveRelativeAsync(-over, ct);
            return;
        }
        await focuser.MoveRelativeAsync(steps, ct);
    }

    private static int Clamp(IFocuser focuser, int position) =>
        focuser.MaxPosition > focuser.MinPosition ? Math.Clamp(position, focuser.MinPosition, focuser.MaxPosition) : Math.Max(0, position);
}
