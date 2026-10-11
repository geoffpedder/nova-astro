using System.Globalization;
using Nova.Core.Astronomy;
using Nova.Core.Imaging;
using Nova.Core.PlateSolving;

namespace Nova.Core.Simulation;

/// <summary>
/// Plate solver for NOVA's built-in simulator, whose star field is made up and can't be solved for real.
/// It reads where the simulated camera actually pointed (SIM-RA/SIM-DEC) — the answer a real solver
/// would find — so slew, sync and centring can be tested without hardware or a star database.
/// </summary>
public sealed class SimulatedSolver : IPlateSolver
{
    public string Name => "Simulator";
    public bool Available => true;
    public string? Problem => null;

    public async Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken ct)
    {
        await Task.Delay(300, ct); // feel like a solve
        var image = Fits.Read(await File.ReadAllBytesAsync(request.FitsPath, ct));
        if (!image.Headers.TryGetValue("SIM-RA", out var ra) || !image.Headers.TryGetValue("SIM-DEC", out var dec))
            return new PlateSolveResult(false, null, "Not a simulator frame: no SIM-RA/SIM-DEC", TimeSpan.FromMilliseconds(300));

        double Number(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        var scale = image.Headers.TryGetValue("PIXSCALE", out var p) ? Number(p) : 1;
        var center = new EquatorialCoordinates(Number(ra) / 15, Number(dec));
        var pa = image.Headers.TryGetValue("SIM-PA", out var a) ? Number(a) : 0;
        return new PlateSolveResult(true,
            new PlateSolution(center, Rotation: pa, scale, Flipped: false,
                image.Width * scale / 3600, image.Height * scale / 3600,
                Wcs.Rotated(center, scale, image.Width, image.Height, pa)),
            null, TimeSpan.FromMilliseconds(300));
    }
}
