using System.Net;

namespace Nova.Server.Api;

/// <summary>
/// Reads the object selected in Stellarium's Remote Control plugin on the user's own PC. The browser tries
/// Stellarium directly first (needs CORS enabled there); this is the fallback: NOVA's server asks Stellarium
/// at the address the browser connected from, so only the PC's firewall has to let port 8090 through.
/// Only ever the requesting PC — never an arbitrary host. Also sets the Oculars frame rotation (from plate solves).
/// </summary>
public static class StellariumApi
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    public static void MapStellarium(this RouteGroupBuilder api)
    {
        api.MapGet("/stellarium/selection", async (HttpContext http, int? port, CancellationToken ct) =>
        {
            var p = port ?? 8090;
            if (p is < 1 or > 65535) return Results.Problem("Invalid port", statusCode: StatusCodes.Status400BadRequest);
            if (BrowserHost(http) is not { } host) return Results.Problem("Can't tell where the browser is", statusCode: StatusCodes.Status502BadGateway);
            var url = $"http://{host}:{p}/api/objects/info?format=json";

            try
            {
                using var response = await Http.GetAsync(url, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return Results.Problem("Nothing is selected in Stellarium: click an object first", statusCode: StatusCodes.Status404NotFound);
                if (!response.IsSuccessStatusCode)
                    return Results.Problem($"Stellarium answered {(int)response.StatusCode}: {body}", statusCode: StatusCodes.Status502BadGateway);
                return Results.Content(body, "application/json");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.Problem($"Couldn't reach Stellarium at {host}:{p} ({ex.Message})", statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Sets the Oculars sensor frame's rotation in the requesting PC's Stellarium. Only this property:
        // the relay mustn't become a way to change arbitrary Stellarium settings.
        api.MapPost("/stellarium/rotation", async (HttpContext http, StellariumRotationRequest request, CancellationToken ct) =>
        {
            if (request.Port is < 1 or > 65535) return Results.Problem("Invalid port", statusCode: StatusCodes.Status400BadRequest);
            if (!double.IsFinite(request.Angle)) return Results.Problem("Invalid angle", statusCode: StatusCodes.Status400BadRequest);
            if (BrowserHost(http) is not { } host) return Results.Problem("Can't tell where the browser is", statusCode: StatusCodes.Status502BadGateway);
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["id"] = RotationProperty,
                ["value"] = request.Angle.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            });
            try
            {
                using var response = await Http.PostAsync($"http://{host}:{request.Port}/api/stelproperty/set", form, ct);
                var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
                return response.IsSuccessStatusCode && body.StartsWith("ok", StringComparison.OrdinalIgnoreCase)
                    ? Results.NoContent()
                    : Results.Problem($"Stellarium said: {body} (is the Oculars plugin loaded?)", statusCode: StatusCodes.Status502BadGateway);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.Problem($"Couldn't reach Stellarium at {host}:{request.Port} ({ex.Message})", statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    public const string RotationProperty = "Oculars.selectedCCDRotationAngle";

    /// <summary>The address the browser connected from, as a URL host.</summary>
    private static string? BrowserHost(HttpContext http)
    {
        var client = http.Connection.RemoteIpAddress;
        if (client is null) return null;
        if (client.IsIPv4MappedToIPv6) client = client.MapToIPv4();
        return IPAddress.IsLoopback(client) ? "localhost"
            : client.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{client}]" : client.ToString();
    }
}

public sealed record StellariumRotationRequest(double Angle, int Port = 8090);
