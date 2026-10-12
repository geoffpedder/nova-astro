# Changelog

What changed in each release. The newest is first. A release's section here becomes its notes on GitHub.

## [Unreleased]

### Added
- **NOVA's own guider** (new; PHD2 stays the default): pick **NOVA** at the top of the Guider panel to guide with no
  PHD2 at all. Guide camera and output (the mount's pulse guiding, or a camera's ST4 port), Loop with a live view of
  the guide star, Calibrate (Dec backlash taken up first, the result kept and adjusted for Dec and meridian flips),
  Guide with PHD2's Hysteresis (RA) and Resist Switch (Dec) algorithms, multi-star averaging, Dither, settling, lost
  star recovery and the Dec drift warning. Sequences use whichever guider is chosen.

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

[Unreleased]: https://github.com/geoffpedder/nova-astro/compare/v0.3.5...HEAD
[0.3.5]: https://github.com/geoffpedder/nova-astro/releases/tag/v0.3.5
