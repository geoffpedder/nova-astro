using System.IO.Compression;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.ResponseCompression;
using Nova.Server.Api;
using Nova.Server.Hubs;
using Nova.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// `Nova.Server password [--clear]` (what `nova password` runs): set or remove the password, then exit.
if (args is ["password", ..])
{
    var novaOptions = Microsoft.Extensions.Options.Options.Create(builder.Configuration.GetSection("Nova").Get<NovaOptions>() ?? new NovaOptions());
    return PasswordService.RunCommand(args[1..], novaOptions);
}

builder.Services.Configure<NovaOptions>(builder.Configuration.GetSection("Nova"));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
// The web UI's scripts compress to a fraction of their size (the site map's towns and lakes especially): quicker
// over Wi-Fi. The fastest settings, so the mini PC hardly notices.
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
    o.MimeTypes = ["text/javascript", "application/javascript", "text/css", "text/html", "application/json", "image/svg+xml"];
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

// The optional password: signed-in browsers keep a cookie for 30 days. Its keys live in the config folder, so
// restarts and updates don't sign anyone out.
var configDirectory = (builder.Configuration.GetSection("Nova").Get<NovaOptions>() ?? new NovaOptions()).ResolveConfigDirectory();
// Made here, at the start, so every setting can be saved from the first one (a fresh user has no ~/.config yet).
Directory.CreateDirectory(configDirectory);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(configDirectory, "keys")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "nova.session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.ExpireTimeSpan = TimeSpan.FromDays(30);
    o.SlidingExpiration = true;
    // An API, not a website with a login page: answer 401 rather than redirecting.
    o.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
});
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<ActivityLog>();
builder.Services.AddSingleton<EquipmentService>();
builder.Services.AddSingleton<ImagingService>();
builder.Services.AddSingleton<NovaGuider>();
builder.Services.AddSingleton<GuiderService>();
builder.Services.AddSingleton<OpticsService>();
builder.Services.AddSingleton<CameraLinkService>();
builder.Services.AddSingleton<SimulatorService>();
builder.Services.AddSingleton<SiteService>();
builder.Services.AddSingleton<PlateSolveService>();
builder.Services.AddSingleton<AutofocusService>();
builder.Services.AddSingleton<FocusPresetService>();
builder.Services.AddSingleton<PolarAlignmentService>();
builder.Services.AddSingleton<StellariumTelescopeServer>();
builder.Services.AddSingleton<FlatsService>();
builder.Services.AddSingleton<DriverSetupService>();
builder.Services.AddSingleton<SequencerService>();
builder.Services.AddSingleton<MountMotionService>();
builder.Services.AddSingleton<PowerService>();
builder.Services.AddSingleton<NetworkService>();
builder.Services.AddSingleton<SkyTileService>();
builder.Services.AddSingleton<NicknameService>();
builder.Services.AddSingleton<SetupGuideService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<FocusOffsetService>();
builder.Services.AddSingleton<StorageService>();
builder.Services.AddSingleton<SystemStatsService>();
builder.Services.AddSingleton<WebcamService>();
builder.Services.AddSingleton<HotspotService>();
builder.Services.AddSingleton<TerminalService>();
builder.Services.AddSingleton<MountBluetoothService>();
builder.Services.AddSingleton<EquipmentProfileService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StorageService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<NotificationService>());
builder.Services.AddSingleton<StatusReporter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StatusReporter>());
builder.Services.AddHostedService<ProviderConnector>();
builder.Services.AddHostedService<GuiderConnector>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<HotspotService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<StellariumTelescopeServer>());

// Lets systemd know when the service has started and stops it cleanly.
builder.Host.UseSystemd();

var app = builder.Build();
// Created now rather than on first use, so it hears about solves from the start.
app.Services.GetRequiredService<SetupGuideService>();
// Created now, so a mount set to Bluetooth connects that way from the first Connect.
app.Services.GetRequiredService<MountBluetoothService>();
// Messages to your phone (ntfy) from the parts of a night that can need you.
var notifications = app.Services.GetRequiredService<NotificationService>();
app.Services.GetRequiredService<SequencerService>().Notifications = notifications;
app.Services.GetRequiredService<FlatsService>().Notifications = notifications;
app.Services.GetRequiredService<PlateSolveService>().Notifications = notifications;
app.Services.GetRequiredService<SequencerService>().FocusOffsets = app.Services.GetRequiredService<FocusOffsetService>();
// Frames go to the mini PC or a USB drive; a fall-back to the mini PC is worth a message to the phone.
var storage = app.Services.GetRequiredService<StorageService>();
app.Services.GetRequiredService<ImagingService>().Storage = storage;
app.Services.GetRequiredService<SequencerService>().Storage = storage;
app.Services.GetRequiredService<NovaGuider>().Storage = storage;
storage.FellBack += (_, why) => notifications.Notify(NotificationKind.Storage, "Frames are going to the mini PC", why);
// A new password (or none) drops every live connection; each reconnects only if still signed in.
app.Services.GetRequiredService<PasswordService>().Changed += Nova.Server.Hubs.NovaHub.DisconnectAll;

// The web UI is built into wwwroot (see web/vite.config.ts) and served from here,
// so the mini PC needs nothing besides this one process.
app.UseResponseCompression();
app.UseAuthentication();
// With a password set, everything that controls or shows the equipment needs a signed-in browser: the API (apart
// from signing in), live updates and PHD2's screen. The page's own files stay open so it can show the sign-in.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var guarded = (path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/auth"))
                  || path.StartsWithSegments("/hub") || path.StartsWithSegments("/phd2") || path.StartsWithSegments("/websockify")
                  || path.StartsWithSegments("/terminal");
    if (guarded && !context.RequestServices.GetRequiredService<PasswordService>().IsSignedIn(context.User))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UsePhd2Screen(); // PHD2's screen through NOVA's port (noVNC files and the VNC relay)

app.MapNovaApi();
app.MapTerminal(); // the terminal (ttyd) through NOVA's port, behind the password
app.MapHub<NovaHub>("/hub");
app.MapFallbackToFile("index.html");

var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<NovaOptions>>().Value;
app.Logger.LogInformation("Nova backend: {Backend}. Images go to {Directory}", options.Backend, options.ResolveImageDirectory());

app.Run();
return 0;
