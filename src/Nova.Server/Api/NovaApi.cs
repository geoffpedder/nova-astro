using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Nova.Core.Astronomy;
using Nova.Core.Equipment;
using Nova.Core.Guiding;
using Nova.Server.Services;

namespace Nova.Server.Api;

/// <summary>Kind is "Camera" or "Mount"; a null name means automatic (first found).</summary>
public sealed record SelectDeviceRequest(DeviceKind Kind, string? Name);

/// <summary>Element name to new value, e.g. { "values": { "PORT": "/dev/ttyACM0" } }.</summary>
public sealed record SetPropertyRequest(Dictionary<string, string> Values);

/// <summary>
/// Saves an optics preset for a camera (millimetres) and makes it active. Set <c>OriginalName</c> to rename one.
/// </summary>
public sealed record OpticsRequest(string Camera, string Name, double FocalLength, double Aperture, string? OriginalName = null);
public sealed record NicknameRequest(string Device, string? Nickname);
public sealed record SunAvoidanceRequest(double? Degrees = null, bool? Enabled = null);

/// <summary>Seconds the mount is left to settle after every GoTo (0 for none).</summary>
public sealed record SettleRequest(double Seconds);

public sealed record OpticsPresetRequest(string Camera, string Name);

/// <summary>Save frames to "local" (the mini PC) or "usb"; Device picks which drive when there's more than one.</summary>
public sealed record StorageTargetRequest(string Target, string? Device = null);

/// <summary>A FITS file in the image folders to open: Root "local" or "usb", Path relative to it.</summary>
public sealed record OpenFileRequest(string Root, string Path);

/// <summary>A night's folder (yyyy-MM-dd) on the mini PC to copy to the USB drive.</summary>
public sealed record StorageCopyRequest(string Night);

/// <summary>The gain and offset a camera is given whenever it connects; both null to leave the driver's own.</summary>
public sealed record CameraDefaultsRequest(string Camera, double? Gain, double? Offset);

public sealed record SimulatorsRequest(bool Enabled);

/// <summary>Latitude +N, longitude +E (-180..180), elevation in metres. Set OriginalName to rename.</summary>
public sealed record SiteRequest(string Name, double Latitude, double Longitude, double Elevation, string? OriginalName = null);

public sealed record SiteNameRequest(string Name);

public sealed record FocuserMoveRequest(int Position);

/// <summary>1-based slot, as on the wheel.</summary>
public sealed record FilterWheelMoveRequest(int Slot);

public sealed record RotatorMoveRequest(double Angle);

/// <param name="Brightness">Null keeps the panel's brightness.</param>
public sealed record FlatPanelLightRequest(bool On, double? Brightness = null);

public sealed record FlatPanelCoverRequest(bool Closed);

/// <summary>Settle: within <c>SettlePixels</c> for <c>SettleSeconds</c>, giving up after <c>SettleTimeout</c>.</summary>
public sealed record GuideRequest(bool Recalibrate = false, double SettlePixels = 1.5, double SettleSeconds = 10, double SettleTimeout = 60);

public sealed record GuiderProfileRequest(int Id);

/// <summary>"phd2" or "nova".</summary>
public sealed record GuiderBackendRequest(string Backend);

/// <summary>Dark exposures (seconds) for the guide camera, and how many darks to take at each.</summary>
public sealed record GuideDarksRequest(double[] Exposures, int Frames = 5);

/// <summary>A point of the guide frame, in its pixels.</summary>
public sealed record GuideStarRequest(double X, double Y);

public sealed record GuiderEquipmentRequest(bool Connected);

public sealed record DitherRequest(double Pixels = 5, bool RaOnly = false, double SettlePixels = 1.5, double SettleSeconds = 10, double SettleTimeout = 60);

/// <summary>Signed steps: negative moves in, positive moves out.</summary>
public sealed record FocuserStepRequest(int Steps);

/// <summary>
/// Saves a named focuser position; without <c>Position</c>, wherever the focuser is now (read on the server,
/// so it's exact even right after a move). Set <c>OriginalName</c> to rename one.
/// </summary>
public sealed record FocusPresetRequest(string Focuser, string Name, int? Position = null, string? OriginalName = null);
public sealed record FocusPresetDeleteRequest(string Focuser, string Name);

public sealed record SequenceNameRequest(string Name);

/// <summary>Restart or shut down the mini PC; optionally park the mount and turn the cooler off first.</summary>
public sealed record PowerRequest(bool Park = true, bool CoolerOff = true);
public sealed record SequenceCountRequest(int Row, int Count, int Target = 0);

/// <summary>
/// Manual motion: <c>Move</c> true starts or keeps moving (repeat it while the button is held; it stops by itself
/// about a second after the last one), false stops that axis. No direction with false stops everything.
/// </summary>
public sealed record MotionRequest(MotionDirection? Direction, bool Move);
public sealed record SlewRateRequest(string Name);

/// <summary>Simulator only: turn the virtual polar alignment knobs by this many arcminutes.</summary>
public sealed record KnobRequest(double Altitude, double Azimuth);

public sealed record ExposeRequest(double Duration, bool Save = true);

/// <summary>J2000 RA in hours and Dec in degrees, as decimal ("5.588") or sexagesimal ("05:35:17", "-05 23 28").</summary>
public sealed record SlewRequest(string Ra, string Dec);

public sealed record HistorySizeRequest(int Size);

public sealed record DriversRequest(List<string> Drivers);
public sealed record SetupHiddenRequest(bool Hidden);
public sealed record MeasureOffsetsRequest(int? Passes);
public sealed record FocusOffsetsRequest(string Focuser, bool Enabled, string? Reference, Dictionary<string, int> Offsets);
public sealed record NotificationsRequest(NotificationSettings Settings, string? Token);
public sealed record PasswordRequest(string? Password);
public sealed record ChangePasswordRequest(string? Current, string? New);

public sealed record PinRequest(bool Pinned);

/// <param name="Angle">Position angle of the frame's top, degrees.</param>
public sealed record RotationMatchRequest(string Ra, string Dec, double Angle);

public static class NovaApi
{
    public static void MapNovaApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", (StatusReporter status) => status.GetSnapshot());
        api.MapGet("/log", (ActivityLog log) => log.Recent);

        // Equipment
        api.MapPost("/equipment/connect", async (EquipmentService equipment, GuiderService guider, ActivityLog log, CancellationToken ct) =>
        {
            try
            {
                await equipment.ConnectAsync(ct);
            }
            catch (Exception ex)
            {
                log.Error($"Could not connect to {equipment.Provider.Name}", ex);
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }

            // One button for everything: also have PHD2 connect its guide camera and mount.
            if (guider.Backend == "phd2" && guider.IsConnected && !guider.Status.EquipmentConnected)
            {
                try
                {
                    await guider.SetEquipmentConnectedAsync(true, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.Error("PHD2 couldn't connect its equipment (check its profile)", ex);
                }
            }
            return Results.NoContent();
        });
        api.MapPost("/equipment/disconnect", async (EquipmentService equipment, GuiderService guider, ActivityLog log, CancellationToken ct) =>
        {
            if (guider.Backend == "nova") await guider.StopAsync(ct);
            else if (guider.IsConnected && guider.Status.EquipmentConnected)
            {
                try
                {
                    await guider.StopAsync(ct);
                    await guider.SetEquipmentConnectedAsync(false, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.Error("Could not disconnect PHD2's equipment", ex);
                }
            }
            await equipment.DisconnectAsync(ct);
            return Results.NoContent();
        });

        api.MapPost("/site", (SiteRequest r, SiteService sites, CancellationToken ct) =>
            BadRequestOnArgument(() => sites.SaveAsync(new Site(r.Name, r.Latitude, r.Longitude, r.Elevation), r.OriginalName, ct)));
        // Sun protection: { "enabled": false } switches it off (keeping the degrees), { "degrees": 30 } sets the distance.
        api.MapPost("/site/sun", (SunAvoidanceRequest request, SiteService sites, CancellationToken ct) =>
            BadRequestOnArgument(async () =>
            {
                if (request.Degrees is { } degrees) await sites.SetSunAvoidanceAsync(degrees, ct);
                if (request.Enabled is { } enabled) await sites.SetSunProtectionAsync(enabled, ct);
            }));
        api.MapPost("/mount/settle", (SettleRequest request, SiteService sites, CancellationToken ct) =>
            BadRequestOnArgument(() => sites.SetSettleSecondsAsync(request.Seconds, ct)));
        api.MapPost("/site/select", (SiteNameRequest r, SiteService sites, CancellationToken ct) =>
            BadRequestOnArgument(() => sites.SelectAsync(r.Name, ct)));
        api.MapPost("/site/delete", (SiteNameRequest r, SiteService sites, CancellationToken ct) =>
            BadRequestOnArgument(() => sites.DeleteAsync(r.Name, ct)));

        api.MapPost("/equipment/simulators", async (SimulatorsRequest request, SimulatorService simulators, CancellationToken ct) =>
        {
            await simulators.SetEnabledAsync(request.Enabled, ct);
            return Results.NoContent();
        });

        api.MapPost("/equipment/select", async (SelectDeviceRequest request, EquipmentService equipment,
            ImagingService imaging, CancellationToken ct) =>
        {
            if (request.Kind == DeviceKind.Camera && imaging.IsBusy)
                return Results.Problem("Can't change camera during an exposure", statusCode: StatusCodes.Status409Conflict);
            try
            {
                await equipment.SelectAsync(request.Kind, string.IsNullOrWhiteSpace(request.Name) ? null : request.Name, ct);
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // Raw driver settings ("everything the driver exposes")
        api.MapGet("/devices/{device}/properties", (string device, EquipmentService equipment) =>
            equipment.Provider.Settings is { } settings
                ? Results.Ok(settings.GetProperties(device))
                : Results.Problem("Driver settings need an INDI backend", statusCode: StatusCodes.Status404NotFound));
        api.MapPost("/devices/{device}/properties/{property}", async (string device, string property,
            SetPropertyRequest request, EquipmentService equipment, ActivityLog log, CancellationToken ct) =>
        {
            if (equipment.Provider.Settings is not { } settings)
                return Results.Problem("Driver settings need an INDI backend", statusCode: StatusCodes.Status404NotFound);
            try
            {
                await settings.SetPropertyAsync(device, property, request.Values, ct);
                log.Info($"{device}: set {property} {string.Join(", ", request.Values.Select(v => $"{v.Key}={v.Value}"))}");
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        api.MapPost("/equipment/nickname", (NicknameRequest request, NicknameService nicknames) =>
        {
            try
            {
                nicknames.Set(request.Device, request.Nickname);
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });
        api.MapPost("/optics", (OpticsRequest request, OpticsService optics, CancellationToken ct) =>
            BadRequestOnArgument(() => optics.SavePresetAsync(request.Camera,
                new OpticsPreset(request.Name, request.FocalLength, request.Aperture), request.OriginalName, ct)));
        api.MapPost("/optics/select", (OpticsPresetRequest request, OpticsService optics, CancellationToken ct) =>
            BadRequestOnArgument(() => optics.SelectPresetAsync(request.Camera, request.Name, ct)));
        api.MapPost("/optics/delete", (OpticsPresetRequest request, OpticsService optics, CancellationToken ct) =>
            BadRequestOnArgument(() => optics.DeletePresetAsync(request.Camera, request.Name, ct)));
        // Where frames are saved: the mini PC or a USB drive.
        api.MapGet("/storage", (StorageService storage) => storage.Status);
        api.MapPost("/storage/target", (StorageTargetRequest request, StorageService storage, CancellationToken ct) =>
            BadRequestOnArgument(() => storage.SetTargetAsync(request.Target, request.Device, ct)));
        api.MapPost("/storage/eject", (StorageService storage, CancellationToken ct) => ConflictOnRefusal(async () =>
        {
            try
            {
                await storage.EjectAsync(ct);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"Couldn't eject the drive: {ex.Message}");
            }
        }));
        api.MapPost("/storage/copy", (StorageCopyRequest request, StorageService storage) =>
        {
            try
            {
                storage.StartCopy(request.Night);
                return Results.Accepted();
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });
        api.MapPost("/storage/copy/cancel", (StorageService storage) =>
        {
            storage.CancelCopy();
            return Results.NoContent();
        });
        api.MapPost("/camera/defaults", (CameraDefaultsRequest request, OpticsService optics) =>
            BadRequestOnArgument(() =>
            {
                optics.SetConnectDefaults(request.Camera, request.Gain, request.Offset);
                return Task.CompletedTask;
            }));

        // Camera
        api.MapPost("/camera/settings", async (CameraSettingsUpdate update, EquipmentService equipment,
            ImagingService imaging, ActivityLog log, CancellationToken ct) =>
        {
            if (equipment.Camera is not { IsConnected: true } camera)
                return Results.Problem("No camera connected", statusCode: StatusCodes.Status409Conflict);
            // Cooling can change any time; anything that alters the frame waits for the exposure to end.
            var altersFrame = update.Gain is not null || update.Offset is not null || update.Binning is not null || update.FrameType is not null;
            if (altersFrame && imaging.IsBusy)
                return Results.Problem("Can't change that during an exposure", statusCode: StatusCodes.Status409Conflict);
            try
            {
                await camera.ApplyAsync(update, ct);
                log.Info($"{camera.Name}: {Describe(update)}");
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });
        api.MapPost("/camera/expose", (ExposeRequest request, ImagingService imaging) =>
            imaging.TryStartExposure(request.Duration, request.Save, out var error)
                ? Results.Accepted()
                : Results.Problem(error, statusCode: StatusCodes.Status409Conflict));
        api.MapPost("/camera/abort", (ImagingService imaging) =>
        {
            imaging.Abort();
            return Results.NoContent();
        });

        // Images
        // ?mode=mono | auto | RGGB | BGGR | GRBG | GBRG, &stretch=false for linear, &size=1600 (longest side; 0 = full).
        // The rendering actually used is in X-Nova-Preview.
        // ?id= picks an image in the history (the latest when it's not given, or gone).
        api.MapGet("/images/latest/preview.png", async (string? mode, bool? stretch, int? size, int? id, ImagingService imaging, HttpResponse response) =>
        {
            try
            {
                if (await imaging.GetPreviewAsync(mode, stretch ?? true, size, id) is not { } preview) return Results.NotFound();
                response.Headers["X-Nova-Preview"] = preview.Mode;
                return Results.File(preview.Png, "image/png");
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });
        // Open a FITS file as the latest image: the raw file is the request body (up to 1 GB), name in ?name=.
        api.MapPost("/images/open", async (HttpContext http, string? name, ImagingService imaging, CancellationToken ct) =>
        {
            if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = 1L << 30; // full-frame FITS are often 50-120 MB
            using var body = new MemoryStream();
            await http.Request.Body.CopyToAsync(body, ct);
            if (body.Length == 0) return Results.Problem("No file sent", statusCode: StatusCodes.Status400BadRequest);
            try
            {
                return Results.Ok(await imaging.OpenAsync(body.ToArray(), Path.GetFileName(name ?? "image.fits"), ct));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });
        // Open… → From the mini PC: browse the image folders (and a mounted USB drive), and open a file from there.
        api.MapGet("/files", (string? root, string? path, StorageService storage) =>
        {
            try
            {
                return Results.Ok(storage.Browse(root ?? "local", path));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });
        api.MapPost("/images/open-file", async (OpenFileRequest request, StorageService storage, ImagingService imaging, CancellationToken ct) =>
        {
            try
            {
                var file = storage.FitsFile(request.Root, request.Path);
                return Results.Ok(await imaging.OpenAsync(await File.ReadAllBytesAsync(file.FullName, ct), file.Name, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });
        // Named catalog stars in the latest image (needs a plate solve of it): ?count= brightest, default 40.
        api.MapGet("/images/latest/annotations", (int? count, int? id, PlateSolveService solver) =>
            solver.Annotate(Math.Clamp(count ?? 40, 1, 500), id) is { } annotations
                ? Results.Ok(annotations)
                : Results.Problem("Plate solve the image first", statusCode: StatusCodes.Status404NotFound));
        api.MapGet("/images/latest/frame", (int? id, PlateSolveService solver) =>
            solver.Frame(id) is { } frame ? Results.Ok(frame) : Results.NotFound());
        api.MapGet("/images/latest/stars", (int? id, ImagingService imaging) =>
            imaging.StarsOf(id) is { } stars ? Results.Ok(stars) : Results.NotFound());
        api.MapGet("/images/latest/header", (int? id, ImagingService imaging) =>
            imaging.HeaderOf(id) is { } header ? Results.Ok(header) : Results.NotFound());
        api.MapGet("/images/latest/fits", (int? id, ImagingService imaging) =>
            imaging.FitsOf(id) is { } fits ? Results.File(fits, "application/fits", imaging.FileNameOf(id)) : Results.NotFound());

        // The optional password: whether one is set and this browser is signed in; sign in and out; set, change or
        // remove it (the current password is needed while one is set).
        api.MapGet("/auth", (HttpContext context, PasswordService password) => Results.Ok(password.Status(context.User)));
        api.MapPost("/auth/login", async (PasswordRequest request, HttpContext context, PasswordService password, CancellationToken ct) =>
        {
            if (!await password.CheckAsync(request.Password, ct))
                return Results.Problem("That's not the password", statusCode: StatusCodes.Status401Unauthorized);
            if (password.PasswordSet)
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, password.Principal(),
                    new AuthenticationProperties { IsPersistent = true });
            return Results.NoContent();
        });
        api.MapPost("/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });
        api.MapPost("/auth/password", async (ChangePasswordRequest request, HttpContext context, PasswordService password, CancellationToken ct) =>
        {
            if (password.PasswordSet && !await password.CheckAsync(request.Current, ct))
                return Results.Problem("The current password isn't right", statusCode: StatusCodes.Status400BadRequest);
            try
            {
                password.Set(request.New);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            // This browser stays signed in with the new password; every other one has to sign in again.
            if (password.PasswordSet)
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, password.Principal(),
                    new AuthenticationProperties { IsPersistent = true });
            else
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        // Notifications to your phone through ntfy: the settings (the token is never sent back), and a test message.
        api.MapGet("/notifications", (NotificationService notifications) => Results.Ok(notifications.View));
        api.MapPost("/notifications", (NotificationsRequest request, NotificationService notifications) =>
        {
            try
            {
                notifications.Save(request.Settings, request.Token);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            return Results.Ok(notifications.View);
        });
        api.MapPost("/notifications/test", async (NotificationService notifications, CancellationToken ct) =>
            await notifications.SendAsync(NotificationKind.Test, "NOVA test", "Notifications from NOVA reach this phone.", ct) is { } problem
                ? Results.Problem($"Couldn't send it: {problem}", statusCode: StatusCodes.Status502BadGateway)
                : Results.NoContent());

        // The first-run guide: what the mini PC is missing, and whether the guide is put away.
        api.MapGet("/setup", (SetupGuideService setup) => Results.Ok(setup.Check()));
        api.MapPost("/setup/hidden", (SetupHiddenRequest request, SetupGuideService setup) =>
        {
            setup.SetHidden(request.Hidden);
            return Results.NoContent();
        });

        // Which INDI drivers indiserver runs (saved to indi.env, started and stopped now when possible).
        api.MapGet("/drivers", (DriverSetupService drivers) => Results.Ok(drivers.Setup()));
        api.MapPost("/drivers", async (DriversRequest request, DriverSetupService drivers, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await drivers.ApplyAsync(request.Drivers, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        // The image history: thumbnails, how many to keep, pins.
        api.MapGet("/images/{id:int}/thumbnail.png", (int id, ImagingService imaging, HttpResponse response) =>
        {
            if (imaging.ThumbnailOf(id) is not { } png) return Results.NotFound();
            response.Headers.CacheControl = "private, max-age=86400"; // an image never changes
            return Results.File(png, "image/png");
        });
        api.MapPost("/images/history", (HistorySizeRequest request, ImagingService imaging) =>
            BadRequestOnArgument(() =>
            {
                imaging.SetHistorySize(request.Size);
                return Task.CompletedTask;
            }));
        api.MapPost("/images/{id:int}/pin", (int id, PinRequest request, ImagingService imaging) =>
            BadRequestOnArgument(() =>
            {
                imaging.Pin(id, request.Pinned);
                return Task.CompletedTask;
            }));

        MapFocuser(api);
        api.MapStellarium();
        api.MapTargets();
        api.MapPhd2Screen(app);
        MapAccessories(api);

        // Mount. Targets are J2000 (catalogue coordinates); the mount is sent JNow.
        api.MapPost("/mount/slew", async (SlewRequest request, EquipmentService equipment, SiteService sites,
            ImagingService imaging, SequencerService sequencer) =>
        {
            if (ManualMoveLock(imaging, sequencer) is { } locked) return Results.Problem(locked, statusCode: StatusCodes.Status409Conflict);
            if (!TryParseTarget(request, out var target, out var problem)) return problem;
            await sites.EnsureMountLocationAsync();
            if (sites.BelowHorizon(target) is { } low) return Results.Problem(low, statusCode: StatusCodes.Status409Conflict);
            if (sites.NearSun(target) is { } sun) return Results.Problem(sun, statusCode: StatusCodes.Status409Conflict);
            return OperationResult(equipment.StartMountOperation($"Slewing to {target} (J2000)",
                (m, ct) => sites.SlewSafelyAsync(m, Precession.J2000ToJNow(target, DateTime.UtcNow), ct), out var error), error);
        });
        api.MapPost("/mount/motion", async (MotionRequest request, MountMotionService motion, ImagingService imaging,
            SequencerService sequencer, CancellationToken ct) =>
        {
            try
            {
                // Stopping is always allowed; starting a move isn't while an image or a sequence would be spoiled.
                if (request is { Move: true } && ManualMoveLock(imaging, sequencer) is { } locked)
                    return Results.Problem(locked, statusCode: StatusCodes.Status409Conflict);
                if (request is { Move: true, Direction: { } direction }) await motion.MoveAsync(direction, ct);
                else await motion.StopAsync(request.Direction, ct);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });
        api.MapPost("/mount/rate", (SlewRateRequest request, MountMotionService motion, CancellationToken ct) =>
            BadRequestOnArgument(() => motion.SetRateAsync(request.Name, ct)));
        api.MapPost("/mount/center", async (SlewRequest request, PlateSolveService solver, SiteService sites,
            ImagingService imaging, SequencerService sequencer) =>
        {
            if (ManualMoveLock(imaging, sequencer) is { } locked) return Results.Problem(locked, statusCode: StatusCodes.Status409Conflict);
            if (!TryParseTarget(request, out var target, out var problem)) return problem;
            await sites.EnsureMountLocationAsync();
            if (sites.BelowHorizon(target) is { } low) return Results.Problem(low, statusCode: StatusCodes.Status409Conflict);
            if (sites.NearSun(target) is { } sun) return Results.Problem(sun, statusCode: StatusCodes.Status409Conflict);
            return OperationResult(solver.StartCentering(target, out var error), error);
        });

        // Turning the camera by hand to the planned rotation (no rotator): measure, ask, check again.
        api.MapPost("/rotation/match", async (RotationMatchRequest request, PlateSolveService solver, SiteService sites,
            ImagingService imaging, SequencerService sequencer) =>
        {
            if (ManualMoveLock(imaging, sequencer) is { } locked) return Results.Problem(locked, statusCode: StatusCodes.Status409Conflict);
            if (!double.IsFinite(request.Angle)) return Results.Problem("Invalid angle", statusCode: StatusCodes.Status400BadRequest);
            if (!TryParseTarget(new SlewRequest(request.Ra, request.Dec), out var target, out var problem)) return problem;
            await sites.EnsureMountLocationAsync();
            return OperationResult(solver.StartMatchingRotation(target, request.Angle, out var error), error);
        });
        api.MapPost("/rotation/turned", (PlateSolveService solver) =>
            solver.RotationTurned() ? Results.NoContent() : Results.Problem("Nothing is waiting for the camera to be turned", statusCode: StatusCodes.Status409Conflict));
        api.MapPost("/rotation/keep", (PlateSolveService solver) =>
            solver.KeepRotation() ? Results.NoContent() : Results.Problem("Nothing is waiting for the camera to be turned", statusCode: StatusCodes.Status409Conflict));
        api.MapPost("/rotation/cancel", (PlateSolveService solver) =>
        {
            solver.CancelRotation();
            return Results.NoContent();
        });

        // Plate solving
        api.MapPost("/solve", async (int? id, PlateSolveService solver, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await solver.SolveLatestAsync(ct, imageId: id));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });
        api.MapPost("/solve/sync", (PlateSolveService solver, CancellationToken ct) =>
            ConflictOnRefusal(() => solver.SyncToLastAsync(ct)));
        api.MapPost("/mount/park", (EquipmentService equipment) =>
            OperationResult(equipment.StartMountOperation("Parking", (m, ct) => m.ParkAsync(ct), out var error), error));
        api.MapPost("/mount/unpark", (EquipmentService equipment) =>
            OperationResult(equipment.StartMountOperation("Unparking", (m, ct) => m.UnparkAsync(ct), out var error), error));
        api.MapPost("/mount/abort", async (EquipmentService equipment, PlateSolveService solver, CancellationToken ct) =>
        {
            solver.CancelCentering();
            await equipment.AbortMountAsync(ct);
            return Results.NoContent();
        });
    }

    private static void MapFocuser(RouteGroupBuilder api)
    {
        api.MapPost("/focuser/move", (FocuserMoveRequest request, EquipmentService equipment) =>
        {
            if (equipment.Focuser is { IsConnected: true } f && f.MaxPosition > f.MinPosition
                && (request.Position < f.MinPosition || request.Position > f.MaxPosition))
                return Results.Problem($"Position must be between {f.MinPosition} and {f.MaxPosition}", statusCode: StatusCodes.Status400BadRequest);
            return OperationResult(equipment.StartFocuserOperation($"Focuser to {request.Position}",
                (focuser, ct) => focuser.MoveToAsync(request.Position, ct), out var error), error);
        });
        api.MapPost("/focuser/step", (FocuserStepRequest request, EquipmentService equipment) =>
            OperationResult(equipment.StartFocuserOperation(
                $"Focuser {(request.Steps < 0 ? "in" : "out")} {Math.Abs(request.Steps)} steps",
                (focuser, ct) => focuser.MoveRelativeAsync(request.Steps, ct), out var error), error));
        api.MapPost("/focuser/presets", (FocusPresetRequest request, FocusPresetService presets, EquipmentService equipment) =>
            BadRequestOnArgument(() =>
            {
                var position = request.Position
                    ?? (equipment.Provider.GetFocuser(request.Focuser) is { IsConnected: true, IsMoving: false } f
                        ? f.Position
                        : throw new ArgumentException($"{request.Focuser} isn't connected, or is still moving"));
                presets.Save(request.Focuser, new FocusPreset(request.Name, position), request.OriginalName);
                return Task.CompletedTask;
            }));
        api.MapPost("/focuser/presets/delete", (FocusPresetDeleteRequest request, FocusPresetService presets) =>
            BadRequestOnArgument(() =>
            {
                presets.Delete(request.Focuser, request.Name);
                return Task.CompletedTask;
            }));
        // The mini PC itself.
        api.MapPost("/system/restart", async (PowerRequest request, PowerService power, CancellationToken ct) =>
            await power.ShutdownAsync(reboot: true, request.Park, request.CoolerOff, ct) is { } problem
                ? Results.Problem(problem, statusCode: StatusCodes.Status409Conflict)
                : Results.Accepted());
        api.MapPost("/system/shutdown", async (PowerRequest request, PowerService power, CancellationToken ct) =>
            await power.ShutdownAsync(reboot: false, request.Park, request.CoolerOff, ct) is { } problem
                ? Results.Problem(problem, statusCode: StatusCodes.Status409Conflict)
                : Results.Accepted());

        // Sequencer: saved sequences, and running one.
        api.MapGet("/sequences", (SequencerService sequencer) => sequencer.SavedNames());
        api.MapGet("/sequences/{name}", (string name, SequencerService sequencer) =>
            sequencer.Load(name) is { } sequence ? Results.Json(sequence, Sequence.Json) : Results.NotFound());
        api.MapPost("/sequences", (Sequence sequence, SequencerService sequencer) =>
            BadRequestOnArgument(() =>
            {
                sequencer.Save(sequence);
                return Task.CompletedTask;
            }));
        api.MapPost("/sequences/delete", (SequenceNameRequest request, SequencerService sequencer) =>
            BadRequestOnArgument(() =>
            {
                sequencer.Delete(request.Name);
                return Task.CompletedTask;
            }));
        api.MapPost("/sequence/start", (Sequence sequence, SequencerService sequencer) =>
            OperationResult(sequencer.Start(sequence, out var error), error));
        api.MapPost("/sequence/pause", (SequencerService sequencer) =>
        {
            sequencer.Pause();
            return Results.NoContent();
        });
        api.MapPost("/sequence/resume", (SequencerService sequencer) =>
        {
            sequencer.Resume();
            return Results.NoContent();
        });
        api.MapPost("/sequence/count", (SequenceCountRequest request, SequencerService sequencer) =>
        {
            try
            {
                sequencer.SetCount(request.Target, request.Row, request.Count);
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });
        api.MapPost("/sequence/skip-target", (SequencerService sequencer) =>
        {
            sequencer.SkipTarget();
            return Results.NoContent();
        });
        api.MapPost("/sequence/skip", (SequencerService sequencer) =>
        {
            sequencer.Skip();
            return Results.NoContent();
        });
        api.MapPost("/sequence/abort", (SequencerService sequencer) =>
        {
            sequencer.Abort();
            return Results.NoContent();
        });

        api.MapPost("/polar/start", (PolarAlignmentService polar) => OperationResult(polar.Start(out var error), error));
        api.MapPost("/polar/continue", (PolarAlignmentService polar) =>
            polar.Continue() ? Results.NoContent() : Results.Problem("Not waiting for RA to be turned", statusCode: StatusCodes.Status409Conflict));
        api.MapPost("/polar/stop", (PolarAlignmentService polar) =>
        {
            polar.Stop();
            return Results.NoContent();
        });
        api.MapPost("/polar/settings", (PolarAlignmentSettings settings, PolarAlignmentService polar) =>
            polar.UpdateSettings(settings) is { } problem
                ? Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest)
                : Results.NoContent());
        api.MapPost("/simulator/polar-knobs", (KnobRequest request, PolarAlignmentService polar) =>
            polar.TurnSimulatorKnobs(request.Altitude, request.Azimuth) is { } problem
                ? Results.Problem(problem, statusCode: StatusCodes.Status409Conflict)
                : Results.NoContent());
        api.MapPost("/autofocus/start", (AutofocusService autofocus) =>
            OperationResult(autofocus.Start(out var error), error));
        api.MapPost("/autofocus/cancel", (AutofocusService autofocus) =>
        {
            autofocus.Cancel();
            return Results.NoContent();
        });
        api.MapPost("/autofocus/settings", (AutofocusSettings settings, AutofocusService autofocus) =>
            autofocus.UpdateSettings(settings) is { } problem
                ? Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest)
                : Results.NoContent());
        // Flats with a panel put on by hand: start, then answer what it asks (panel on, dimmer, covered…).
        api.MapPost("/flats/start", (FlatsRequest request, FlatsService flats, SequencerService sequencer) =>
            sequencer.Running
                ? Results.Problem("A sequence is running: it takes its own flats", statusCode: StatusCodes.Status409Conflict)
                : OperationResult(flats.Start(request, out var error), error));
        api.MapPost("/flats/continue", (FlatsService flats) =>
            flats.Continue() ? Results.NoContent() : Results.Problem("The flats aren't waiting for anything", statusCode: StatusCodes.Status409Conflict));
        api.MapPost("/flats/skip", (FlatsService flats) =>
            flats.Skip() ? Results.NoContent() : Results.Problem("The flats aren't waiting for anything", statusCode: StatusCodes.Status409Conflict));
        api.MapPost("/flats/cancel", (FlatsService flats) =>
        {
            flats.Cancel();
            return Results.NoContent();
        });
        api.MapPost("/flats/settings", (FlatsSettings settings, FlatsService flats) =>
            settings.Validate() is { } problem
                ? Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest)
                : Run(() => flats.UpdateSettings(settings)));
        api.MapPost("/focuser/halt", async (EquipmentService equipment, CancellationToken ct) =>
        {
            await equipment.HaltFocuserAsync(ct);
            return Results.NoContent();
        });
    }

    private static void MapAccessories(RouteGroupBuilder api)
    {
        api.MapPost("/filterwheel/move", (FilterWheelMoveRequest request, EquipmentService equipment, FocusOffsetService offsets) =>
        {
            var name = equipment.FilterWheel?.Names.ElementAtOrDefault(request.Slot - 1) ?? $"slot {request.Slot}";
            return OperationResult(equipment.StartFilterWheelOperation($"Filter {name}",
                (wheel, ct) => offsets.ChangeFilterAsync(wheel, request.Slot, ct), out var error), error);
        });

        // Per-filter focus offsets: typed in, or measured by autofocusing on each filter.
        api.MapPost("/focuser/offsets", (FocusOffsetsRequest request, FocusOffsetService offsets) =>
        {
            try
            {
                offsets.Save(request.Focuser, request.Enabled, request.Reference, request.Offsets);
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });
        api.MapPost("/focuser/offsets/measure", (MeasureOffsetsRequest? request, FocusOffsetService offsets) =>
            offsets.StartMeasure(out var error, request?.Passes ?? FocusOffsetService.DefaultPasses)
                ? Results.Accepted()
                : Results.Problem(error, statusCode: StatusCodes.Status409Conflict));
        api.MapPost("/focuser/offsets/cancel", (FocusOffsetService offsets) =>
        {
            offsets.CancelMeasure();
            return Results.NoContent();
        });

        // The flat panel: its light (and brightness), and the cover of a flip-flat.
        api.MapPost("/flatpanel/light", (FlatPanelLightRequest request, EquipmentService equipment) =>
            OperationResult(equipment.StartFlatPanelOperation(request.On ? "Flat panel light on" : "Flat panel light off",
                (panel, ct) => panel.SetLightAsync(request.On, request.Brightness, ct), out var error), error));
        api.MapPost("/flatpanel/cover", (FlatPanelCoverRequest request, EquipmentService equipment) =>
            OperationResult(equipment.StartFlatPanelOperation(request.Closed ? "Closing the flat panel cover" : "Opening the flat panel cover",
                (panel, ct) => panel.SetCoverAsync(request.Closed, ct), out var error), error));
        api.MapPost("/rotator/move", (RotatorMoveRequest request, EquipmentService equipment) =>
            OperationResult(equipment.StartRotatorOperation($"Rotator to {request.Angle:0.##}°",
                (rotator, ct) => rotator.MoveToAsync(request.Angle, ct), out var error), error));
        api.MapPost("/rotator/halt", async (EquipmentService equipment, CancellationToken ct) =>
        {
            await equipment.HaltRotatorAsync(ct);
            return Results.NoContent();
        });

        // Guider (PHD2)
        api.MapPost("/guider/connect", async (GuiderService guider, ActivityLog log, CancellationToken ct) =>
        {
            try
            {
                await guider.ConnectAsync(ct);
                return Results.NoContent();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Error("Could not connect to PHD2 (is it running with Tools ▸ Enable Server on?)", ex);
                return Results.Problem($"Could not connect to PHD2: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        });
        api.MapPost("/guider/disconnect", async (GuiderService guider) =>
        {
            await guider.DisconnectAsync();
            return Results.NoContent();
        });
        api.MapPost("/guider/guide", (GuideRequest request, GuiderService guider) =>
            OperationResult(guider.StartGuiding(new SettleSettings(request.SettlePixels, request.SettleSeconds, request.SettleTimeout),
                request.Recalibrate, out var error), error));
        api.MapPost("/guider/dither", (DitherRequest request, GuiderService guider) =>
            OperationResult(guider.Dither(request.Pixels, request.RaOnly,
                new SettleSettings(request.SettlePixels, request.SettleSeconds, request.SettleTimeout), out var error), error));
        api.MapPost("/guider/profile", (GuiderProfileRequest request, GuiderService guider, CancellationToken ct) =>
            ConflictOnRefusal(() => guider.SelectProfileAsync(request.Id, ct)));
        api.MapPost("/guider/equipment", (GuiderEquipmentRequest request, GuiderService guider, CancellationToken ct) =>
            ConflictOnRefusal(() => guider.SetEquipmentConnectedAsync(request.Connected, ct)));
        api.MapPost("/guider/stop", async (GuiderService guider, CancellationToken ct) =>
        {
            await guider.StopAsync(ct);
            return Results.NoContent();
        });

        // Guider: PHD2 or NOVA's own
        api.MapPost("/guider/backend", (GuiderBackendRequest request, GuiderService guider, CancellationToken ct) =>
            ConflictOnRefusal(() => guider.SetBackendAsync(request.Backend, ct)));
        api.MapPost("/guider/nova/settings", (NovaGuiderSettings settings, GuiderService guider) =>
        {
            if (guider.Nova is not { } nova) return Results.NotFound();
            try
            {
                nova.UpdateSettings(settings);
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });
        api.MapPost("/guider/loop", (GuiderService guider) => OperationResult(guider.Loop(out var error), error));
        api.MapPost("/guider/calibrate", (GuiderService guider) => OperationResult(guider.Calibrate(out var error), error));
        api.MapPost("/guider/calibration/clear", (GuiderService guider) =>
        {
            guider.Nova?.ClearCalibration();
            return Results.NoContent();
        });
        api.MapPost("/guider/darks/build", (GuideDarksRequest request, GuiderService guider) =>
            OperationResult(guider.BuildDarks(request.Exposures, request.Frames, out var error), error));
        api.MapPost("/guider/darks/clear", (GuiderService guider) =>
        {
            guider.Nova?.ClearDarks();
            return Results.NoContent();
        });
        api.MapPost("/guider/select", (GuideStarRequest request, GuiderService guider) =>
            ConflictOnRefusal(() =>
            {
                (guider.Nova ?? throw new InvalidOperationException("NOVA's own guider isn't available")).SelectStar(request.X, request.Y);
                return Task.CompletedTask;
            }));
        api.MapGet("/guider/logs", (GuiderService guider) => guider.Nova?.GuideLogs() ?? []);
        api.MapGet("/guider/logs/{name}", (string name, bool? drive, GuiderService guider) =>
            guider.Nova?.GuideLogPath(name, drive ?? false) is { } path
                ? Results.File(File.OpenRead(path), "text/plain", name)
                : Results.NotFound());
        api.MapGet("/guider/frame.png", (string? stretch, GuiderService guider) =>
            guider.Nova?.FramePng(stretch) is { } png ? Results.File(png, "image/png") : Results.NotFound());
    }

    /// <summary>
    /// Why the user can't move the mount right now (an exposure or a sequence would be spoiled), or null.
    /// Stop, park and abort stay allowed.
    /// </summary>
    public static string? ManualMoveLock(ImagingService imaging, SequencerService sequencer) =>
        sequencer.Running ? "The mount is locked while a sequence is running (paused too): stop the sequence first (Stop and Park still work)"
        : imaging.IsBusy ? "The mount is locked while the camera is exposing: wait for the image or abort it first (Stop and Park still work)"
        : null;

    private static bool TryParseTarget(SlewRequest request, out EquatorialCoordinates target, out IResult problem)
    {
        try
        {
            target = EquatorialCoordinates.Parse(request.Ra, request.Dec);
            problem = Results.Empty;
            return true;
        }
        catch (FormatException ex)
        {
            target = default;
            problem = Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            return false;
        }
    }

    private static async Task<IResult> BadRequestOnArgument(Func<Task> action)
    {
        try
        {
            await action();
            return Results.NoContent();
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>Runs a request, turning refusals (e.g. PHD2 "camera not found", "solve an image first") into a 409 with the message.</summary>
    private static async Task<IResult> ConflictOnRefusal(Func<Task> call)
    {
        try
        {
            await call();
            return Results.NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static IResult OperationResult(bool started, string? error) =>
        started ? Results.Accepted() : Results.Problem(error, statusCode: StatusCodes.Status409Conflict);

    private static string Describe(CameraSettingsUpdate u) => string.Join(", ", new[]
    {
        u.CoolerOn is { } on ? $"cooler {(on ? "on" : "off")}" : null,
        u.TargetTemperature is { } t ? $"target {t:0.#} °C" : null,
        u.Gain is { } g ? $"gain {g:0.##}" : null,
        u.Offset is { } o ? $"offset {o:0.##}" : null,
        u.Binning is { } b ? $"bin {b}×{b}" : null,
        u.FrameType is { } f ? $"{f.ToString().ToLowerInvariant()} frames" : null,
    }.Where(s => s is not null));

    private static IResult Run(Action action)
    {
        action();
        return Results.NoContent();
    }
}
