# Developing NOVA

- [How it fits together](#how-it-fits-together)
- [Building and running](#building-and-running)
- [Tests](#tests)
- [Releases](#releases)
- [Deploy scripts](#deploy-scripts)
- [Bundled data](#bundled-data)
- [API](#api)

## How it fits together

```
 Browser (any PC / tablet / phone)
        │  REST /api/*  +  SignalR /hub (live push)
 Mini PC ─ Nova.Server (ASP.NET Core, serves the UI too)
             ├─ Nova.Core   equipment interfaces, FITS, stretch/preview, simulator
             └─ Nova.Indi   INDI client  ──TCP 7624──▶  indiserver + drivers ──USB──▶ hardware
```

**Design rules**

- The server connects to the INDI server at startup and keeps reconnecting, so devices can be listed, chosen
  and configured (e.g. serial port) before pressing **Connect equipment**.
- The server owns all state and runs all work. The browser only sends commands and shows state, so a
  sequence keeps running if your laptop sleeps or the Wi-Fi drops.
- Commands are plain REST. State is pushed over a SignalR WebSocket, with a full snapshot every second.
- The browser never gets raw FITS files. The server sends a downsampled, auto-stretched PNG.
- No CDN or internet dependencies, so it works in a field with no router.

| Path | What |
|---|---|
| `src/Nova.Core` | Device interfaces (`ICamera`, `IMount`, …), coordinates, FITS read/write, statistics, auto-stretch, PNG encoder, catalogues, built-in simulator |
| `src/Nova.Indi` | INDI XML protocol client written from scratch (no native libindi), plus INDI device implementations |
| `src/Nova.Server` | Web API, SignalR hub, imaging, equipment, sequencing and other services. Serves the built UI from `wwwroot/` |
| `web/` | Svelte 5 + TypeScript UI (Vite) |
| `tests/Nova.Tests` | xUnit tests, including the INDI client against a fake indiserver |
| `deploy/` | Install, setup, update and doctor scripts, the `nova` command, systemd units |
| `tools/` | Python scripts that build the bundled catalogues and map data |

## Building and running

Requires the .NET 10 SDK and Node 20 or newer. Works on Windows, macOS and Linux, with no hardware: NOVA
has a built-in simulator.

```bash
cd web && npm install && npm run build && cd ..
dotnet run --project src/Nova.Server --launch-profile simulator
```

Open http://localhost:5000, click **Connect equipment**, **Unpark**, **Slew**, then **Expose**. The simulator
draws a star field around wherever the mount points, and plate solves it.

For live UI reloading, keep the server running and in a second terminal run `cd web && npm run dev`, then
open http://localhost:5173. Vite proxies `/api` and `/hub` to port 5000. To develop the UI against the mini PC
instead, start Vite with `NOVA_BACKEND=http://minipc.local:5000`.

**Against real INDI** (Linux):

```bash
sudo pacman -S libindi             # indiserver + simulator drivers (Debian/Ubuntu: see INSTALL.md)
deploy/start-indi-simulators.sh    # or: indiserver indi_asi_ccd indi_eqmod_telescope
dotnet run --project src/Nova.Server --launch-profile indi
```

NOVA keeps its state in `~/.config/nova` (`%APPDATA%
ova` on Windows); `Nova__ConfigDirectory` points it
elsewhere, e.g. for a throwaway instance.

## Tests

```bash
dotnet test tests/Nova.Tests       # the server, INDI client, astronomy and services
cd web && npm run check            # type-checks the UI
```

**Setup test** (`tests/setup/test-setup.sh`): runs `deploy/setup.sh --yes` as a new user in a clean container
running systemd, installing a given release file, then checks the services, the version NOVA reports and
`nova doctor`. GitHub Actions runs it on Debian 13, Ubuntu 26.04 and Arch before every release and every Monday
(`.github/workflows/setup-test.yml`; Arch failures don't block a release, since its AUR drivers can lag behind
an INDI update). To run it yourself on Linux with Docker:

```bash
deploy/publish.sh linux-x64 && deploy/package.sh linux-x64 0.0.0-local
tests/setup/test-setup.sh debian:trixie out/nova-0.0.0-local-linux-x64.tar.gz
```

Tests that drive the simulator in real time leave generous margins, and the camera simulator's seeing can be
held steady (`SimulatedCamera.SeeingVariation = 0`) where a test needs a predictable result. The release
workflow still retries a failed test run once, as a safety net.

## Releases

Releases are built by GitHub Actions (`.github/workflows/release.yml`):

1. Add the release's changes under a new heading in `CHANGELOG.md`, e.g. `## [0.2.0] - 2026-11-01`.
2. Tag and push: `git tag v0.2.0 && git push origin v0.2.0`. A tag with a hyphen (`v0.2.0-beta.1`) makes a
   pre-release.
3. The workflow runs the tests and the setup test on each system, builds self-contained `linux-x64` and `linux-arm64` downloads
   (`nova-<version>-<rid>.tar.gz` with a `.sha256`, each holding the app, the deploy scripts and the README)
   and publishes them as a GitHub release, with that version's CHANGELOG section as its notes.

Running the workflow by hand (Actions → Release → Run workflow) makes the same builds as artifacts, without
a release. The version comes from the tag (`NOVA_VERSION` → `-p:Version`); local builds are `0.0.0-dev` and
show "dev build" in the header.

## Deploy scripts

| Script | What it does |
|---|---|
| `deploy/setup.sh` | sets up a fresh computer: dependencies for each system, then the latest release (or `--build`) |
| `deploy/update.sh` | downloads, checks and installs a release (`nova update`) |
| `deploy/install.sh` | installs a build as systemd services, the maintenance scripts and the `nova` command |
| `deploy/doctor.sh` | checks everything and prints the fixes (`nova doctor`) |
| `deploy/publish.sh` | builds the web UI and a self-contained build for a runtime (`linux-x64`, `linux-arm64`) |
| `deploy/package.sh` | packs a build into a release download |
| `deploy/nova` | the `nova` command |

When a feature needs a new package on the mini PC, add it to `setup.sh` (each system), `doctor.sh`,
NOVA's setup guide (`SetupGuideService`) and the table in [INSTALL.md](INSTALL.md#installing-by-hand-arch).

## Bundled data

The catalogues and map data are built from their sources by the scripts in `tools/` (each script's header
says where to download its inputs, into `tools/data/`). Their licences are in [NOTICE](../NOTICE).

| Data | File | Rebuild |
|---|---|---|
| Stars (HYG 4.1) | `src/Nova.Core/Catalogs/stars.tsv.gz` | `python tools/build_star_catalog.py hygdata_v41.csv` |
| Deep-sky objects (OpenNGC, Sharpless) | `src/Nova.Core/Catalogs/dso.tsv.gz` | `python tools/build_dso_catalog.py tools/data` |
| Constellation figures (d3-celestial) | `web/src/lib/constellations.json` | `python tools/build_constellations.py tools/data` |
| Site map (Natural Earth, GeoNames) | `web/src/lib/map/` | `python tools/build_site_map.py` |

Planet positions use JPL's *Keplerian Elements for Approximate Positions of the Major Planets* (E. M. Standish;
good to a few arcminutes for 1800–2050), with magnitudes from Mallama & Hilton (2018).

## API

Everything the page does goes through plain REST, so it can be scripted with `curl` too, or used by your own
devices and programs (the README shows a small touchscreen that polls `/api/status`). State is pushed over
SignalR at `/hub`; a simple client can poll `GET /api/status` once a second instead. While a password is set,
sign in first with `POST /api/auth/login` and send the `nova.session` cookie it sets. The main endpoints:

| Method | Path | Body |
|---|---|---|
| GET | `/api/status` | Full state snapshot |
| GET · POST | `/api/auth` · `/api/auth/login` `{ "password": "…" }` · `/api/auth/logout` | The optional password: `{ passwordSet, signedIn }`; sign in (sets the `nova.session` cookie) and out. While a password is set, everything else under `/api`, `/hub` and `/phd2` answers 401 without that cookie |
| POST | `/api/auth/password` | `{ "current": "…", "new": "…" }`: set, change or (empty `new`) remove the password |
| POST | `/api/focuser/offsets` · `/measure` `{ "passes": 3 }` · `/cancel` | Per-filter focus offsets: save `{ focuser, enabled, reference, offsets: { "R": 60 } }`; measure them (autofocus on each filter); progress and results in the status under `focuser.offsets` |
| GET · POST | `/api/hotspot` · `/api/hotspot/settings` · `/api/hotspot/start` · `/api/hotspot/stop` | The Wi-Fi hotspot fallback: `{ status, settings }` (state, devices joined, method, problem; name, password, fallback, retry), save `{ ssid, password, fallback, retry }` (also written to `/etc/nova/hotspot.conf` for `nova-hotspot.service`), start it now (back to Wi-Fi after 3 minutes if nobody joins), stop it |
| GET · POST | `/api/notifications` · `/api/notifications/test` | ntfy settings (the token is never returned: `hasToken`); save `{ settings, token }` (token null keeps it, "" removes it); send a test |
| GET | `/api/setup` | The setup guide's check of the mini PC: `{ solvedOnce, hidden, driversApplicable, driversChosen, driversInstalled, problems: [{ text, fix, optional }] }` |
| POST | `/api/setup/hidden` | `{ "hidden": true }`: put the setup guide away (or bring it back) |
| GET · POST | `/api/drivers` | The INDI drivers (installed, chosen, running) · `{ "drivers": ["indi_lx200am5", …] }` saves the choice and starts/stops them |
| POST | `/api/equipment/connect` · `/disconnect` | |
| POST | `/api/equipment/profiles` · `/load` · `/delete` | `{ "name": "Askar 71F", "originalName": null }` — save what's in use now (the device for each role, `"(none)"` for an empty one; INDI drivers; mount connection; guider and guide camera) as a profile, replacing or renaming `originalName`; load one (equipment disconnected) or delete it. Status: `equipmentProfiles` |
| POST | `/api/equipment/select` | `{ "kind": "Camera", "name": "ZWO CCD ASI2600MC Pro" }` — kind is Camera, Mount or Focuser; `null` name = automatic |
| POST | `/api/camera/settings` | `{ "coolerOn": true, "targetTemperature": -10, "gain": 100, "offset": 50, "binning": 2, "frameType": "Dark" }` (any subset) |
| POST | `/api/optics` | `{ "camera": "ZWO CCD ASI2600MC Pro", "name": "RedCat + 0.8x", "focalLength": 200, "aperture": 51, "originalName": null }` — add a preset (`originalName` null; a name already used is refused) or change/rename one (presets are shared by every camera, so this changes it for each using it), and make it this camera's |
| POST | `/api/optics/select` · `/delete` | `{ "camera": "…", "name": "RedCat 51" }` — the active preset is sent to the driver whenever that camera connects |
| POST | `/api/camera/defaults` | `{ "camera": "…", "gain": 100, "offset": 50 }` — set whenever that camera connects (both null: the driver's own; a ZWO imaging camera then gets 100/50) |
| POST | `/api/camera/expose` | `{ "duration": 5, "save": true }` |
| POST | `/api/camera/abort` | |
| GET | `/api/images/latest/preview.png?mode=auto&stretch=true&size=1600` · `/fits` | `mode`: `mono`, `auto` (debayer if FITS has `BAYERPAT`), or `RGGB`/`BGGR`/`GRBG`/`GBRG`; `stretch=false` for linear; `size` = longest side, `0` = full |
| GET | `/api/images/latest/annotations?count=40` | Named catalog stars in the latest image, placed by its plate solve: `{ imageId, width, height, stars: [{ x, y, label, detail, magnitude }] }` (404 until solved) |
| GET | `/api/images/latest/stars` | Detected stars of the latest frame `{ imageId, width, height, stars: [{ x, y, hfr }] }` |
| GET | `/api/images/latest/header` | FITS header cards `[{ key, value, comment }]` in file order |
| POST | `/api/images/open?name=old.fits` | Body: the raw FITS file (`.fits.gz` too, up to 1 GB). Becomes the latest image: preview, stars and plate solving (hinted by its own header) work on it; not added to the HFR trend |
| POST | `/api/livestack` | `{ "enabled": true }` — stack Expose and Loop frames (the Image tab's switch). Status (stacks, frames, left out and why) under `liveStack` |
| GET | `/api/livestack/preview.png?key=…&size=1600` | The stack (`key` from the status; the current one when left out), stretched; 404 before its first frame |
| POST | `/api/livestack/reset` · `/save` | `{ "key": "…" }` (or `{}` for the current stack) — start it again, or save it (32-bit FITS and PNG in the night's `Stacked` folder): answers `{ path }` |
| GET · POST | `/api/files?root=local&path=2026-10-09` · `/api/images/open-file` `{ "root": "local", "path": "2026-10-09/M 31/a.fits" }` | Browse the mini PC's image folders (`root` `local`, or `usb` for a mounted drive) and open a FITS file from them; nothing outside them is listed or read |
| GET · POST | `/api/storage` · `/api/storage/target` `{ "target": "usb", "device": null }` · `/eject` · `/copy` `{ "night": "2026-10-09" }` · `/copy/cancel` | Where frames are saved (the mini PC or a USB drive), ejecting the drive, and copying a night to it; status also under `storage` |
| GET | `/api/log` | The recent activity log `[{ timeUtc, level, message }]` |
| GET | `/api/devices/{device}/properties` | Every driver property (INDI only) |
| POST | `/api/devices/{device}/properties/{property}` | `{ "values": { "PORT": "/dev/ttyACM0" } }` |
| POST | `/api/mount/slew` | `{ "ra": "05:35:17", "dec": "-05:23:28" }` J2000 (decimal also accepted); NOVA converts to the mount's JNow |
| POST | `/api/mount/park` · `/unpark` · `/abort` | |
| POST | `/api/mount/bluetooth` · `/scan` | ZWO AM mounts over Bluetooth: `{ "enabled": true, "mount": "AM3_68bc8d", "address": "…" }` (a mount is required: never "the first found"), `{ "enabled": false }` for USB; `scan` searches for a few seconds. Status: `mountBluetooth` (state `Confirming` while the mount waits for its BT button) |
| POST | `/api/mount/settle` | `{ "seconds": 3 }` — how long the mount settles after every GoTo (0 = none) |
| POST | `/api/site/sun` | `{ "degrees": 30 }` or `{ "enabled": false }` — Sun protection's distance, or switching it off |
| POST | `/api/mount/center` | `{ "ra": "05:35:17", "dec": "-05:23:28" }` (J2000) — slew, solve, sync until centred; `/api/mount/abort` stops it |
| POST | `/api/solve` · `/api/solve/sync` | Plate solve the latest frame · sync the mount to the last solve |
| POST | `/api/site` | `{ "name": "Home", "latitude": 51.48, "longitude": -0.0015, "elevation": 46, "originalName": null }` — add/update/rename a site and make it active |
| POST | `/api/site/select` · `/delete` | `{ "name": "Home" }` — the active site and the mini PC's time are sent to the mount on every connect |
| POST | `/api/focuser/move` | `{ "position": 25000 }` |
| POST | `/api/focuser/step` | `{ "steps": -100 }` (negative = in) |
| POST | `/api/focuser/halt` | |
| POST | `/api/focuser/presets` · `/api/focuser/presets/delete` | Named focuser positions `{ focuser, name, position? }` (no position = where it is now), per focuser in `focus-presets.json` |
| GET | `/api/stellarium/selection?port=8090` | The object selected in Stellarium's Remote Control plugin on the PC making the request (fallback when the browser can't reach Stellarium directly because CORS is off) |
| POST | `/api/stellarium/rotation` | `{ angle, port }`: set the Oculars sensor-frame rotation in the requesting PC's Stellarium |
| GET | `/api/targets/search?q=heart&limit=20` | Deep-sky objects by name or designation, best first |
| GET | `/api/targets/tonight?groups=galaxy,nebula&minSize=10&minAltitude=30&minHours=2&sort=best&limit=100` | Tonight's well-placed objects with `visibility { maxAltitude, bestUtc, hoursAbove, moonSeparation }`, plus `night` |
| GET | `/api/targets/night` · `/api/targets/altitude?ra=&dec=` | Tonight's sunset, twilight, sunrise and Moon · a target's altitude through the night (with the Sun and Moon) |
| GET | `/api/sky/field?ra=&dec=&radius=2&magnitude=&maxStars=&objects=famous&maxObjects=` | Stars and objects around a point (radius 180 = the whole sky; `objects=famous`: Messier, Caldwell and named only) |
| GET | `/api/sky/survey?ra=&dec=&fov=&survey=CDS/P/DSS2/color&projection=STG` | A square, north-up survey image, `TAN` or `STG` projection (cached) |
| GET | `/api/sky/tiles/{order}/{npix}.jpg` · `/api/sky/tiles/allsky.jpg` | The stored survey's HiPS tiles (fetched from CDS and kept, the first time) |
| GET · POST | `/api/sky/tiles/status` · `/api/sky/tiles/download` `{ "maxOrder": 5 }` · `/api/sky/tiles/cancel` | How much of the survey is stored; download whole levels for offline use |
| GET | `/api/sky/bodies` | The Sun and Moon now (J2000), with the Moon's illumination |
| GET | `/api/images/latest/frame` | The latest image's outline on the sky (J2000 corners), once solved |
| GET/POST | `/api/sequences` · `/api/sequences/{name}` · `/api/sequences/delete` | Saved sequences: list, load, save (the sequence JSON), delete `{ name }` |
| POST | `/api/sequence/start` · `/pause` · `/resume` · `/skip` · `/skip-target` · `/abort` · `/count` | Run a sequence (body: the sequence JSON) and control it; progress in the status under `sequencer` |
| POST | `/api/system/restart` · `/api/system/shutdown` | `{ "park": true, "coolerOff": true }`: stop what's running, optionally park and turn the cooler off, then restart / power off the mini PC |
| POST | `/api/polar/start` · `/continue` · `/stop` | Polar alignment (three-point, or with the camera on the pole); `continue` after turning RA by hand (manual mode), `stop` when done adjusting. Live error in the status under `polarAlignment` |
| POST | `/api/polar/settings` | `{ "method": "ThreePoint", "stepDegrees": 10, "direction": "East", "exposureSeconds": 3, "refraction": false, "manual": false, "startAutomatically": true, "altitudeOffset": 2, "azimuthOffset": 1, "poleTurnDegrees": 90 }` (`method`: `ThreePoint` or `Pole`) |
| POST | `/api/simulator/polar-knobs` | Simulator only: turn the virtual knobs `{ "altitude": -5, "azimuth": 2 }` (arcminutes) |
| POST | `/api/autofocus/start` · `/api/autofocus/cancel` | Run (or cancel) a V-curve autofocus; progress, points and fit are in the status under `autofocus` |
| POST | `/api/autofocus/settings` | `{ "stepSize": 100, "stepsPerSide": 4, "exposureSeconds": 3, "backlash": 0 }` (saved in `autofocus.json`) |
| POST | `/api/filterwheel/move` | `{ "slot": 5 }` (1-based) |
| POST | `/api/rotator/move` · `/halt` | `{ "angle": 135 }` |
| POST | `/api/guider/connect` · `/disconnect` · `/stop` | PHD2 at `Nova:Phd2Host`:`Nova:Phd2Port` (default localhost:4400) |
| POST | `/api/guider/guide` | `{ "recalibrate": false, "settlePixels": 1.5, "settleSeconds": 10, "settleTimeout": 60 }` |
| POST | `/api/guider/profile` · `/equipment` | `{ "id": 2 }` · `{ "connected": true }` |
| POST | `/api/guider/dither` | `{ "pixels": 5, "raOnly": false }` (+ settle fields) |
| POST | `/api/guider/backend` | `{ "backend": "nova" }` (`phd2` or `nova`; saved in `guider.json`). Guide, dither, stop and the status follow it |
| POST | `/api/guider/nova/settings` | NOVA's guider: `{ "camera": null, "output": null, "exposureSeconds": 2, "binning": 1, "calibrationStepMs": 0, "reverseDecAfterFlip": false, "useDarks": true, "gain": null, "guideLog": true, "algorithms": { "raHysteresis": 0.1, "raAggressiveness": 0.7, "raMinMove": 0.15, "decAggressiveness": 1, "decMinMove": 0.2, "decFastSwitch": true, "decMode": "Auto", "maxRaPulseMs": 2500, "maxDecPulseMs": 2500, "raAlgorithm": "Hysteresis", "pecPeriodSeconds": 0, "pecPredictionGain": 0.8, "pecReactiveAggressiveness": 0.5 } }` (`camera`/`output` null: automatic / the mount) |
| POST | `/api/guider/loop` · `/calibrate` · `/calibration/clear` | NOVA's guider: loop the guide camera, calibrate, forget the calibration. State in the status under `guider.nova` |
| POST | `/api/guider/darks/build` · `/darks/clear` | NOVA's guider: take a dark library for the guide camera `{ "exposures": [1, 2, 3], "frames": 5 }` (scope covered; Stop cancels), or delete it. In the status under `guider.nova.darks` / `darkProgress` |
| POST | `/api/guider/select` | NOVA's guider: guide on the star nearest `{ "x": 312, "y": 140 }` (guide frame pixels; while looping or guiding) |
| POST | `/api/guider/nova/profiles/save` · `/load` · `/delete` | NOVA's guider: guide settings profiles `{ "name": "Windy" }`; `load` also takes the built-in presets (*NOVA defaults*, *ZWO AM mount*, *Poor seeing*). The list and the matching one are in the status (`guider.nova.profiles`, `profile`); the mount's dither/settle defaults under `guider.defaults` |
| POST | `/api/guider/assistant` · `/assistant/apply` | NOVA's guider: the guiding assistant `{ "seconds": 120 }` (30–900; corrections off; Stop ends it), and taking its suggested min moves and exposure. Result in the status under `guider.nova.assistant`; the verdict on recent guiding (both guiders) under `guider.diagnosis` |
| GET | `/api/webcam` · POST `/api/webcam/settings` | The webcam view: devices (from /sys/class/video4linux; `sim` in the simulator), settings `{ "device": null, "width": 640, "height": 480, "fps": 5 }`, state, viewers, fps |
| GET | `/api/webcam/controls` · POST `/api/webcam/controls` · `/controls/reset` | The webcam's V4L2 controls (via v4l2-ctl: type, range, menu, value, inactive), set one `{ "name": "brightness", "value": 20 }` (kept per webcam in `webcam-controls.json` and set again when it starts), or all back to defaults |
| GET | `/api/webcam/stream` · `/api/webcam/snapshot` | Live MJPEG (`multipart/x-mixed-replace`, for an `<img>`; ffmpeg runs only while one is open, ~10 s more) and one picture |
| GET · POST | `/api/backup` · `/api/backup/restore` | All of NOVA's settings as a zip (`nova-backup.json`, `config/…`, `drivers.txt`, `hotspot.json`); restore one by POSTing the zip as the body: it's put aside in `restore-pending/`, NOVA exits with code 75 so systemd restarts it, and the settings are swapped in at startup before any service reads them. Answers `{ info, restarting }` |
| GET | `/api/diagnostics` | A zip for looking into a problem: the activity log, NOVA's and indiserver's journals and the kernel's USB messages (36 hours), `nova doctor`, system info and the settings (secrets and site coordinates blanked, `auth.json` left out) |
| GET | `/api/terminal` · `/terminal/` | The terminal: `{ available, problem, running, sessions }`; ttyd's page and its WebSocket (`/terminal/ws`), relayed to ttyd's socket file (`~/.config/nova/terminal/ttyd.sock`). Refused (403) unless a NOVA password is set, and only for a signed-in browser |
| GET | `/api/system/stats` | The mini PC: CPU %, cores, load, temperature, memory used/total, NOVA's memory, image disks (total/free), uptime (from /proc and /sys on Linux) |
| GET | `/api/guider/logs` · `/api/guider/logs/{name}?drive=false` | NOVA's guider: the guide logs (one per night, PHD2's format, in each night's image folder; `drive` for those on the USB drive), and one as a download |
| GET | `/api/guider/frame.png` | NOVA's guider: the latest guide frame, stretched (`?stretch=low`, `medium` (default) or `high`). Its tracked stars are in the status under `guider.nova.stars` |

Hub events on `/hub`: `status`, `exposureProgress`, `imageReady`, `log`, `logHistory`.
