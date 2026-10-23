# Changelog

What changed in each release. The newest is first. A release's section here becomes its notes on GitHub.

## [Unreleased]

## [0.3.19] - 2026-10-23

### Fixed

- **Polar alignment on the pole, from home:** with the mount already at the pole (its home position), NOVA now
  takes the first frame there and turns RA once, by 90°. Before, it first made a GoTo to a point beside the pole,
  which could swing RA up to half a turn round to Polaris's side, and the 90° turn on top could take the scope
  down near the tripod.

## [0.3.18] - 2026-10-22

### Added

- **Equipment profiles:** save the equipment in use (the device for each role, the INDI drivers, the Simulators
  switch and how the mount connects) under a name, and load it later in one go, like the site and optics presets.
  In the Equipment panel under Connect equipment; loading one (while disconnected) starts its drivers, stops the
  others and chooses its devices, ready for Connect equipment.
- **NOVA on the Home Screen:** added to an iPad's or phone's Home Screen (Safari: Share, Add to Home Screen),
  NOVA opens as its own full-screen app, without Safari's bars or its swipe-down that leaves full screen by
  accident. The Full screen button hides there, as it's not needed.

### Changed

- **Bluetooth mounts are locked to your NOVA:** each NOVA has its own code for the mount, and the first time it
  connects the Mount panel asks you to press the mount's **BT** button; after that the mount remembers it. NOVA
  only connects to the mount you picked (no more "the first ZWO mount found"), so at a site with several ZWO
  mounts it can't take over someone else's, and another NOVA can't use yours without someone pressing its button.
  When the search finds several mounts, the panel says to pick yours by name.

## [0.3.17] - 2026-10-22

### Added

- **ZWO mounts over Bluetooth:** an AM3, AM5 or AM7 can connect without its USB cable. In the Mount panel set
  Connection to Bluetooth and pick the mount from the ones found in range; Connect equipment then connects it over
  Bluetooth, and NOVA keeps the link up (reconnecting if it drops) until Disconnect, which frees the mount for
  ZWO's phone app again. A small helper (`nova-mount-ble.py`) offers the mount's Bluetooth to INDI's
  `indi_lx200am5` driver as a serial port, so the driver works as it does over USB. `nova setup` installs BlueZ and
  Python's Bleak and turns Bluetooth on; `nova doctor` checks them.
- **Dew heater switch:** cameras with an anti-dew heater around the window (ZWO's cooled Pro cameras, such as the
  ASI2600MC Pro) get a Dew heater switch in the Camera panel, under the cooler. The INDI driver only offers it as
  an "AntiDewHeater" number among its controls, which was easy to miss.

### Changed

- **Camera cooler:** a switch instead of the "Cooler on" / "Cooler off" button, so it's clear at a glance whether
  the cooler is on.
- **Image tab:** the HFR trend's space under the image is kept from the first frame (and on frames with no stars), so
  the page no longer jumps when the chart appears.

### Fixed

- **The clock on networks that block Ubuntu's secure time servers:** Ubuntu's chrony only trusts its NTS
  servers, which need TCP port 4460 as well as UDP 123, so behind many home routers the clock was never set.
  `nova setup` adds the ordinary public servers (pool.ntp.org) as a fallback, used only while no secure server
  answers.

## [0.3.16] - 2026-10-20

### Changed

- **Optics presets are shared by every camera:** another camera on the same telescope just picks the same
  preset instead of having it typed in again. Each camera still remembers its own choice, the list says which
  other cameras use a preset, and changing one changes it for all of them. Presets saved per camera are pooled
  (a name two cameras used for different optics gets the camera's name added).

## [0.3.15] - 2026-10-20

### Added

- `nova doctor` checks the clock: says when the time zone is still UTC (Ubuntu Server's default) and how to
  set yours, and whether the clock is synced from the internet.
- INDI CCD Simulator: a "Sensor like" list in its driver settings (Simulator Settings) sets the resolution and
  pixel size of a ZWO cooled camera (ASI2600, ASI2400, ASI6200, ASI071, ASI294, ASI1600, ASI183, ASI533, ASI585).

## [0.3.14] - 2026-10-20

### Added
- **"Mini PC ready" notification:** after a boot, once NOVA is up and the network with it, your phone is told,
  with the address to open (Notifications: *the mini PC has started and NOVA is ready*, on by default).

## [0.3.13] - 2026-10-20

### Fixed
- **`nova update` from NOVA's own terminal** finishes: the install stops NOVA, which ended the terminal and the
  install with it, half way. Run from there, the installer now carries on as a job of its own
  (`journalctl -fu nova-install-…` follows it); NOVA restarts and the page comes back.

## [0.3.12] - 2026-10-20

### Added
- **A terminal on the mini PC** in the Activity tab's Mini PC box: run `nova doctor`, `journalctl`, `sudo apt …` from
  the browser, with colours, resizing and programs like `htop`. Only once a NOVA password is set, and only for a
  browser signed in with it. It uses ttyd (setup installs it), started while a terminal is open and listening on a
  file only NOVA's user can open, never on the network.

### Fixed
- **A fresh install couldn't save anything** (settings, sites, optics, equipment, even frames: "Access to the path
  '/opt/nova/nova' is denied"). With no `~/.config` folder yet (a new Ubuntu Server user), .NET gave no settings
  folder, so NOVA used `nova` relative to `/opt/nova`. It now always uses `~/.config/nova`, made at start.

## [0.3.11] - 2026-10-15

### Fixed
- **The mini PC stays at `<name>.local`:** Avahi sometimes renamed itself `<name>-2.local` (at boot, or after a
  network change) and never changed back. A check every 30 seconds (`nova-mdns.timer`, added by the install) now
  sets the name back.

## [0.3.10] - 2026-10-15

### Added
- **Focuser backlash for every move:** the autofocus settings' backlash now applies to all focuser moves (filter
  offsets, In/Out, Go to and saved positions, not just autofocus), and *Final approach* chooses whether moves finish
  inward (as before) or outward, for a focuser whose load pushes it in. Like NINA's overshoot method.
- **Change refocus and end-of-night settings while a sequence runs** (*Refocus when* and *At the end*, including
  when the night ends), as well as the frame counts.
- **Clearer meridian flip timing:** the Sequence tab shows when the flip will actually happen (flips wait for the
  frame running then: "flip ≈ 22:31, after this frame (latest 22:41)"), and the log warns about 5 minutes ahead, as
  does a phone notification (Notifications: *a meridian flip is about 5 minutes away*, on by default).

### Changed
- **NOVA's own guider is the default on a new install** (PHD2 is still installed and one click away in the
  Guider panel). An install that's been used already keeps the guider it had.
- New sequences refocus when the temperature moves 2 °C (was 1 °C).
- *Reverse Dec after flip* is on for ZWO AM mounts (NOVA's guider), including setups that already had the AM defaults.
- The guiding verdict (seeing-limited, chasing…) is worked out every 30 seconds instead of every second, so it
  doesn't keep changing.

### Fixed
- **Copying a night to a USB drive could leave damaged files.** Linux kept much of each copy in memory to write
  later, so the copy "finished" early, and a drive unplugged before then kept files of the right size with only their
  start in them; a later copy then skipped them as already there. Every file written to the drive (copies, and frames
  saved straight to it) is now flushed to the drive before it counts as done, and a file only counts as already
  copied if its size, time and first and last 64 KB match. Copy the night again: damaged files are replaced.
- Night mode: the guide frame (Guiding tab, its close-up, and the Guider panel's) is red too.
- The Camera panel's **Storage** (USB drive, copying a night's frames) shows with no camera connected too.
- The Guiding tab fits the window without a few pixels of scrolling: the guide frame takes whatever height is left.
- The Image tab's Abort button keeps one width while it counts down (and while downloading).

## [0.3.9] - 2026-10-14

### Added
- **Dec backlash compensation** (NOVA's guider, on by default): calibration measures the Dec backlash (how far
  short the South steps come of the North ones), and when a Dec correction reverses, guiding adds that much pulse to
  take up the slack, adjusting it as it goes (more if the star still lags, less if it overshoots). The Guiding tab
  shows the backlash and the pulse; the guide log records it. In the simulation tests it cut Dec RMS by about a
  quarter on reversing Dec corrections with 3 px of backlash.
- **Exposure time on the Image tab:** a seconds box joined to **Expose**, the same setting as the Camera panel's.
- **Docs:** choosing a computer (a used ex-office mini PC such as a ThinkCentre M710q is cheaper than a Raspberry
  Pi kit and ideal for NOVA), giving Ubuntu Server the whole drive (its installer leaves part of it unused), and new
  screenshots.

### Changed
- Sites show by name only (the Site line, the setup guide and the activity log), so screenshots don't give your
  location away; the coordinates show only while the Site section is open.
- The altitude chart's hour labels thin out (every 4, 6 or 12 hours) when it's too narrow for every 2.

### Fixed
- Night mode: text box placeholders, colour emoji (network, pin, folder, lock, map), the direction pad's stop
  square, the pop-out pictures and the zoomed image's overview are all red now.
- Filter wheel buttons fit long names (Luminance, H_Alpha) instead of the text running over.
- The Sequence tab no longer shows a sideways scroll bar that moves a pixel or so.

## [0.3.8] - 2026-10-14

### Added
- **Wi-Fi hotspot fallback:** when the mini PC finds no Wi-Fi to join for 90 seconds, it starts its own network
  (name and password set in the new **Network** dialog: click the network name in the header), reachable at
  http://10.42.0.1:5000. While nobody is on it, it tries Wi-Fi again every 5 minutes; **Start hotspot now** tries it
  out (back to Wi-Fi in 3 minutes if nobody joins) and **Back to Wi-Fi** ends it. Works with Ubuntu Server's
  netplan (systemd-networkd, with hostapd, which setup now installs), NetworkManager and iwd; `nova doctor` checks it.
- **An icon:** a four-pointed star in the browser tab, and for an iPhone or iPad home screen (Share → Add to Home Screen).

### Fixed
- A popped-out window (webcam, image, guiding) no longer ends up off screen when the browser window is made smaller: it stays inside, and goes back where you put it when the window grows again.
- The cut corner of dialogs and pop-outs is see-through (it was a patch of the page's colour over the dimmed page or a
  picture), and the FITS header, driver settings and map dialogs open above any pop-outs.

## [0.3.7] - 2026-10-13

### Added
- **Webcam view:** a USB webcam on the mini PC in a **Webcam** tab, to watch the rig from indoors, with **Pop out** to
  keep a floating view (moved and resized as you like) over the other tabs, and **Snapshot**. It runs only while watched and passes the
  webcam's MJPEG on unconverted, so it costs next to nothing. Setup installs ffmpeg; the install adds NOVA's user to
  the `video` group. **Controls** beside it sets the webcam's own brightness, exposure, white balance, focus and so
  on (v4l2-ctl), kept per webcam and set again when it starts.
- **Image and guiding pop-outs:** **Pop out** on the Image tab keeps the latest image in a floating window over the
  tabs, updated with each frame; on the Guiding tab (or under the Guider panel's graph, PHD2 too) the guiding graph and
  RMS. Moved and resized as you like, and opened without leaving the tab you're on.
- **Targets tab panels:** the target list, framing controls and altitude chart can be moved and resized over the
  sky chart (double-click a heading, or **Layers → Put panels back**, to return them).
- **Drivers dialog filter:** *All installed*, *Ticked* (your equipment's drivers) or *Running* (a device connected
  now), with counts, next to the search.
- **Predictive PEC** (NOVA's guider, RA): learns the mount's periodic error while guiding (its period found
  automatically, or set) and corrects it ahead of time, with a gentler reactive part; for strain-wave mounts like the
  AM3. In the simulation tests it cut RA RMS by about 65% against Hysteresis on a strong periodic error. The Guiding
  tab shows it learning or predicting; the guide log records it; the guiding assistant reports the PE period; built-in
  profile *ZWO AM mount + PEC*.

### Fixed
- The Targets tab's sky chart no longer shows stretched for a moment when opened after the browser window was resized.
- The whole page no longer scrolls a little when the mouse wheel turns over the header: the main area now takes
  exactly the height the header leaves (it was a fixed guess that a slightly taller header overran).

## [0.3.6] - 2026-10-13

### Added
- **NOVA's own guider** (new; PHD2 stays the default): pick **NOVA** at the top of the Guider panel to guide with no
  PHD2 at all. Guide camera and output (the mount's pulse guiding, or a camera's ST4 port), Loop with a live view of
  the guide star, Calibrate (Dec backlash taken up first, the result kept and adjusted for Dec and meridian flips),
  Guide with PHD2's Hysteresis (RA) and Resist Switch (Dec) algorithms, multi-star averaging, Dither, settling, lost
  star recovery and the Dec drift warning. Sequences use whichever guider is chosen.
- **Dark library for the guide camera** (NOVA's guider): take darks once with the guide scope covered, and they're
  subtracted from every guide frame, so hot pixels and warm clusters aren't taken for guide stars. Kept per camera,
  binning and gain; the nearest exposure is scaled when there's no exact match.
- **Guide frames shown less noisy:** a gentler stretch by default, with Low / Med / High in the Guiding tab.
- **Calibration graph** (NOVA's guider), as PHD2 draws it: the star's path through the West/East and North/South
  steps with the fitted directions, to see at a glance how square the calibration is. In the Guiding tab and behind
  **Graph** in the Guider panel.
- **Guide camera gain** in the Guider panel (NOVA's guider), with the camera's range; set before every guide frame
  and for darks, instead of digging in the driver settings.
- **Guiding tab** for NOVA's guider: the whole guide frame with the guide star and the stars averaged with it marked
  (click another star to guide on it), the guide star close up, RMS and peaks, the calibration, a large graph of the
  last 300 steps, and all the buttons.
- **Mini PC stats** at the top of the Activity tab: CPU use, load and temperature, memory in use (and NOVA's own),
  free space on the image disk (and the USB drive when frames go there), uptime. Refreshed every few seconds while
  the tab is open.
- **Guide profiles** (NOVA's guider): save the guide settings under a name and switch between them; built-in
  *NOVA defaults*, *ZWO AM mount* and *Poor seeing*.
- **ZWO AM mount defaults:** with an AM3/AM5, NOVA's guider starts at 1.5 s exposures and gain 60 (unless already
  changed), and dithers default to 2.5 guide pixels, settling within 0.7 px for 8 s (panels and new sequences).
- **Seeing or settings?** Under the guiding graphs, a verdict on recent guiding: seeing-limited, within the min move,
  see-sawing (lower the aggressiveness) or lagging (raise it), and how much guiding adds to star size next to the
  latest frame's FWHM.
- **Guiding assistant** (NOVA's guider), like PHD2's: corrections off for a few minutes to measure the seeing, drift,
  polar error and periodic error; suggests min moves (and longer exposures in poor seeing), with **Apply**.
- **Exposure progress in the header:** while a sequence runs, a thin bar along the bottom of its status box fills as
  the current exposure goes.
- **Guide logs** for NOVA's guider, in PHD2's guide-log format (so PHD2's log viewers can open them): every
  calibration step and guide frame, dithers, settling, lost stars and setting changes. One per night in the night's
  image folder (the USB drive when frames go there); the Guiding tab lists them to download.

### Fixed
- The guide camera's Dark library section opens by itself only once the camera is connected.
- **Dithers no longer spoil the guiding RMS and graph** (PHD2 and NOVA's guider): the steps while guiding settles
  after a dither (or after starting) are left out of the RMS and peaks, and the graph marks the dither with a dashed
  line and shades the settling instead of drawing a spike that rescaled everything.
- **Noise measured on coarse frames:** a ZWO guide camera's 12-bit values (multiples of 16) on a quiet background
  left most pixels on one exact value, so NOVA measured no noise at all. Star detection then took hundreds of noise
  specks for stars, and the stretch showed the frame as white noise whatever the setting. Affected any frame like
  that, guide frames most.

## [0.3.5] - 2026-10-11

### Added
- **Activity tab:** the whole log, with *Warnings and errors only*, and a dot on the tab when one came in. Under the
  main view the log is now always a single line with the latest entry; click it to open the tab.
- **Expose in the Image tab's toolbar** (Abort while exposing), with the Camera panel's settings.
- **Settle after slews** (Mount panel, 3 s by default): after every GoTo NOVA waits that long before exposing, solving
  or moving on. Centring used to start its solve frame the moment the slew ended.
- **Pinch to zoom** on a phone or tablet: the image (smoothly from fitted up to 16×, around the fingers, and two
  fingers pan) and the Targets tab's sky chart.

## [0.3.4] - 2026-10-11

### Added
- **More room on a tablet:** the panel column folds away to a strip (◂ Hide panels), and the Activity log folds down
  to one line showing its latest message. Each browser remembers its layout.

## [0.3.3] - 2026-10-11

### Added
- **Open ▾ → From the mini PC** (Image tab): browse the mini PC's image folders, and a mounted USB drive, night by
  night, and open a frame saved there. **From this computer** is the old Open….

### Changed
- **From Stellarium frames the object in the Targets tab** (the yellow frame) as well as filling the Mount panel's
  boxes.
- The Polar alignment tab shows only its own frames, not an image opened from the computer (or taken elsewhere) since.

## [0.3.2] - 2026-10-11

### Added
- **Guide scope optics in the Guider panel:** the guide camera's focal length and aperture, sent to its driver when
  PHD2 connects it, so INDI stops warning that they're missing.

### Changed
- **ZWO imaging cameras start at gain 100, offset 50** (the driver's own gain is 200), unless you've chosen other
  defaults for the camera. A ZWO guide camera is left to PHD2.
- The Camera panel's cooling target starts at -10 °C.
- `nova setup --database d50` adds that star database even when another is installed (it used to do nothing).
- **PHD2's screen uses IceWM** instead of Openbox: a taskbar along the bottom lists PHD2's windows (click one to
  bring it to the front), and every window has a title bar to move it by. PHD2 starts again if its window is
  closed. New installs get IceWM; a mini PC set up before keeps Openbox until it's installed (Arch:
  `sudo pacman -S icewm`, Debian/Ubuntu: `sudo apt install icewm`), then `nova update --force`.

### Fixed
- Default gain and offset could be sent before the driver had defined them, and then weren't set at all.
- Clicks in the PHD2 tab could stop working after Alt+Tab away from the browser (its Alt release was lost, and the
  window manager took every click as Alt+click). Alt+click no longer does anything there, and keys left held are
  cleared when the screen starts.
- **PHD2's INDI camera window opened behind the setup wizard** (choosing *INDI Camera*), leaving PHD2 waiting on a
  window that couldn't be seen.

## [0.3.1] - 2026-10-10

### Added
- **Save frames to a USB drive** (Camera panel → Storage), as an ASIAIR does: the drive is mounted when it's
  plugged in, a sequence warns if it won't fit, and frames go to the mini PC instead if the drive is missing, full
  or pulled out (with a phone notification, if those are on). **Eject** unmounts it safely, and a night saved on
  the mini PC can be copied to the drive afterwards. New installs get udisks2 for this; on a mini PC set up
  before, install `udisks2` (Arch: `sudo pacman -S udisks2`, Debian/Ubuntu: `sudo apt install udisks2`), then
  `nova update --force`.

### Changed
- **The install opens NOVA's ports in the firewall** when `ufw` (on in Omarchy) or `firewalld` is running: 5000, 10001
  and mDNS for `.local` names. Before, browsers couldn't connect until port 5000 was opened by hand.
- **A mount (or other serial device) on another port connects anyway:** when it fails on the port its driver expects
  (say it was plugged in after INDI started), NOVA rescans the ports and tries each, as the driver's Scan Ports does.
- **Moving the mount by hand starts at 720x** (the fastest up to 720x), set as soon as the mount connects. In ZWO's
  Heavy Duty Mode it used to start at 60x.
- **The framing rotation starts at 0°** each time the page opens.

### Fixed
- **Clicks in the PHD2 tab did nothing on a fresh install:** PHD2's New Profile Wizard opened behind its own
  full-screen window, and while it waits PHD2 ignores every other click. Its dialogs now stay on top.
- Setup on Arch installs ASTAP's command-line version from SourceForge (the AUR package's source download had
  gone missing).

## [0.3.0] - 2026-10-08

### Added
- **Sequences stop at dawn:** astronomical dawn by default, or nautical dawn, sunrise, a set time or never (At
  the end → Stop the night at). No frame is started that would run past it; then the end steps run.
- **Per-filter focus offsets** (Focuser panel): measured by autofocusing on each filter (averaged over several
  passes, corrected for drift) or typed in; filter changes then move the focuser by the difference instead of
  refocusing.
- **Notifications to your phone** through ntfy (⏻ → Notifications…): when a sequence finishes or fails, a
  target is skipped, NOVA is waiting for you (flat panel, turning the camera), or guiding loses its star.
- **Polar alignment check while guiding:** when PHD2's Dec corrections keep going one way, NOVA says how fast
  the star drifts and the least polar error that explains it, in the Guider panel and the activity log.
- **Default gain and offset per camera** (Camera panel → Make these the default): set every time the camera
  connects, instead of whatever the driver starts with (the INDI ASI driver's is 200).
- **Calibration area** layer on the Targets chart: where PHD2 calibrates best (near Dec 0 and the meridian).
- **Polar alignment on the pole** (as SharpCap): two frames with Polaris in view and RA turned 90° between them;
  the live frame marks the mount's axis, the pole and Polaris, and you turn the knobs to bring the pole into the
  axis rings.
- **Pause, resume and abort a running sequence** from its status in the header.

### Changed
- **Polar alignment has its own tab,** with the error in degrees, minutes and seconds and a target chart that
  zooms in as it shrinks (rings at 30″, 1′ and 5′).
- **Three-point polar alignment starts just above the pole** (as N.I.N.A. does: 2° above, 1° east, turning RA 10°
  east), so the field no longer climbs to the zenith by the third point, and there's no longer a limit on starting
  near the pole.
- The Guider panel's graph scrolls left as steps come in, instead of stretching to fill the graph.
- Bug reports ask for `nova doctor`'s output.

### Fixed
- Town names on the site map were drawn backwards with gaps between the letters.
- Releases attached the setup test's builds as well as the release builds.

## [0.2.0] - 2026-10-07

The first public release.

### Added
- **One-step setup** (`deploy/setup.sh`) for Arch (and Omarchy, EndeavourOS, Manjaro), Debian 13, 64-bit
  Raspberry Pi OS and Ubuntu 26.04: INDI and your equipment's drivers, ASTAP with a star database, headless
  PHD2, Avahi and polkit, then the latest release. On Arch it asks whose equipment you have and installs
  those makers' drivers.
- **The `nova` command** on the mini PC: `nova update`, `nova doctor`, `nova setup`, `nova status`,
  `nova logs` and `nova restart`.
- **Getting started** checklist in the page: site, drivers, connecting, the camera's optics and a test
  plate solve (plus PHD2), each ticked by itself with a button that opens the place where it's done, and
  what the mini PC is missing with the command that fixes it.
- **Full screen** button in the header.
- **An optional password** (⏻ → Set a password…, or `nova password` on the mini PC): browsers sign in once and
  stay signed in for 30 days; it guards the API, live updates and PHD2's screen.
- Documentation: install guide, user guide, developer guide, NOTICE.
- NOVA is licensed under the GNU GPL, version 3 or later.

### Changed
- `nova doctor` works on Debian, Raspberry Pi OS and Ubuntu as well as Arch, treats PHD2 as optional, and
  names the package for a missing driver (updating Arch first, so pinned AUR drivers install).
- The Drivers dialog names the right package for each system, including ToupTek-family cameras and
  Celestron AUX mounts.

### Fixed
- The target lists ran a catalogue name into its common name ("M 31Andromeda Galaxy").
- A full-resolution preview of a colour camera's frame was only half size: the colours are now filled in at
  every pixel instead of combining each 2×2 block.

## [0.1.0] - 2026-10-07

The first release: ready-made downloads for 64-bit PCs (`linux-x64`) and the Raspberry Pi 4 and 5
(`linux-arm64`), installed and updated with `deploy/update.sh`.

- **Equipment:** an INDI client written from scratch; choosing drivers from the page (Equipment →
  Drivers…), devices, nicknames and every driver setting; simulators.
- **Camera:** exposure, gain, offset, binning, frame type, cooling; FITS files with full headers; a
  stretched, debayered preview; stars, HFR and FWHM for every frame; optics presets.
- **Mount:** J2000 and JNow, the site and time sent on connect, Sun protection, moving by hand, parking;
  the 720× slew limit in ZWO's Heavy Duty Mode.
- **Plate solving** with ASTAP: solve, sync, slew and centre; named stars on solved frames.
- **Focuser:** presets and V-curve autofocus. **Filter wheel**, **rotator** and **flat panel** (light box
  and flip-flat) panels.
- **Polar alignment:** three-point, with live adjustment.
- **Guiding** through PHD2, which runs headless on the mini PC and shows in a tab.
- **Targets:** offline deep-sky and star catalogues, tonight's best targets, a Stellarium-style sky chart
  with stored DSS imagery, framing with rotation and mosaics, matching framing to any image, and a
  rotation helper (by hand or with a rotator).
- **Sequences:** several targets with start and stop times and altitude limits; cooling, centring,
  autofocus, guiding, dithering, meridian flips; flats before the camera is turned and at the end.
- **Flats wizard:** automatic exposure, dark flats, by hand or with a controllable panel.
- **Image history:** the last few images with pins, and side-by-side or blink comparison.
- **Stellarium:** take its selection, act as its telescope, and turn its Oculars frame to the camera's angle.
- **The mini PC:** restart and shut down from the page, network status, night mode.

[Unreleased]: https://github.com/geoffpedder/nova-astro/compare/v0.3.19...HEAD
[0.3.19]: https://github.com/geoffpedder/nova-astro/releases/tag/v0.3.19
[0.3.18]: https://github.com/geoffpedder/nova-astro/releases/tag/v0.3.18
[0.3.17]: https://github.com/geoffpedder/nova-astro/releases/tag/v0.3.17
[0.3.16]: https://github.com/geoffpedder/nova-astro/releases/tag/v0.3.16
[0.3.15]: https://github.com/geoffpedder/nova-astro/releases/tag/v0.3.15
[0.3.14]: https://github.com/geoffpedder/nova-astro/releases/tag/v0.3.14
[0.3.13]: https://github.com/geoffpedder/nova-astro/releases/tag/v0.3.13
[0.3.12]: https://github.com/geoffpedder/nova-astro/releases/tag/v0.3.12
[0.3.11]: https://github.com/geoffpedder/nova-astro/releases/tag/v0.3.11
[0.3.10]: https://github.com/geoffpedder/nova-astro/releases/tag/v0.3.10
[0.3.9]: https://github.com/geoffpedder/nova-astro/releases/tag/v0.3.9
[0.3.8]: https://github.com/geoffpedder/nova-astro/releases/tag/v0.3.8
[0.3.7]: https://github.com/geoffpedder/nova-astro/releases/tag/v0.3.7
[0.3.6]: https://github.com/geoffpedder/nova-astro/releases/tag/v0.3.6
[0.3.5]: https://github.com/geoffpedder/nova-astro/releases/tag/v0.3.5
