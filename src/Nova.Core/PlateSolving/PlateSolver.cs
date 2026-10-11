using Nova.Core.Astronomy;

namespace Nova.Core.PlateSolving;

/// <param name="Hint">Roughly where the image is (J2000), e.g. from the mount; null for a blind solve.</param>
/// <param name="FieldHeightDegrees">Image height on the sky, from the optics; null to let the solver work it out.</param>
public sealed record PlateSolveRequest(
    string FitsPath, EquatorialCoordinates? Hint, double SearchRadiusDegrees, double? FieldHeightDegrees);

/// <param name="Center">Centre of the image, J2000.</param>
/// <param name="Rotation">Sky position angle of the image's "up", degrees (the solver's CROTA2).</param>
/// <param name="Flipped">The image is mirrored (e.g. through a diagonal or an OAG).</param>
/// <param name="Wcs">The exact sky ↔ pixel mapping, for annotating the image; null if the solver didn't give one.</param>
public sealed record PlateSolution(
    EquatorialCoordinates Center, double Rotation, double ImageScale, bool Flipped,
    double FieldWidthDegrees, double FieldHeightDegrees, Wcs? Wcs = null);

public sealed record PlateSolveResult(bool Solved, PlateSolution? Solution, string? Error, TimeSpan Duration);

public interface IPlateSolver
{
    string Name { get; }

    /// <summary>Whether the solver is installed and ready; <see cref="Problem"/> says why not.</summary>
    bool Available { get; }
    string? Problem { get; }

    Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken ct);
}
