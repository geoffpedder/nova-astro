# NOVA

**Astrophotography from your browser.** NOVA runs on a small Linux computer at the telescope (a mini PC or a
Raspberry Pi) and controls your mount, cameras, focuser, filter wheel, rotator and flat panel through
[INDI](https://www.indilib.org/). You use it from a browser on any computer, tablet or phone on the same
network, with nothing to install there. It's inspired by [N.I.N.A.](https://nighttime-imaging.eu/), for Linux.

Website: [nova-astro.app](https://nova-astro.app)

![NOVA's Image tab showing a plate-solved 300 s frame of M 31 with named stars, its image history, and the mount in 3D](docs/images/desktop-image.webp)

## What it does

- **Imaging:** exposures with cooling, gain, offset and binning; stretched colour previews; stars, HFR and
  FWHM for every frame; image history with side-by-side and blink comparison; optional live stacking, to watch a
  target build up. FITS files are saved on the mini PC or straight to a USB drive plugged into it.
- **Plate solving** with ASTAP: sync, slew and centre on a target to within an arcminute; named stars on
  solved frames.
- **Targets:** offline deep-sky and star catalogues, tonight's best targets for your site, a Stellarium-style
  sky chart with survey imagery, framing with rotation and mosaics, and the sky at any other time to plan the
  night before it's dark.
- **Sequences:** a whole night unattended, for several targets: centring, autofocus with per-filter offsets,
  guiding, dithering, meridian flips, flats, the camera's rotation matched for each target, and a stop at dawn.
- **Focusing** (V-curve autofocus), **polar alignment** (three-point, or with the camera on the pole),
  **guiding** with NOVA's own guider (predictive PEC for strain-wave mounts, Dec backlash compensation, a guiding
  assistant and PHD2-style logs) or PHD2 (which runs on the mini PC and shows in a tab), a **flats wizard**, and a
  **Stellarium** link.
- **Equipment:** INDI drivers chosen on the page, equipment profiles to switch between setups (or the simulators)
  in one go, and ZWO AM3/AM5/AM7 mounts over USB or Bluetooth, the Bluetooth link locked to your NOVA.
- **Watch the rig from indoors** with a USB webcam on the mini PC, and pop the webcam, the latest image or the guiding
  graph out into small windows that stay on screen whichever tab you're on. A terminal on the mini PC, in the browser,
  for the odd command.
- **Built for the field:** no internet needed, a red night mode, and work that carries on when your laptop
  sleeps or the Wi-Fi drops, because everything runs on the mini PC. Optional messages to your phone when a
  sequence finishes or needs you. Frames can be saved straight to a USB drive. With no Wi-Fi at the site, the mini PC
  starts its own hotspot so a phone or tablet can still reach it.
- **Made for phones and tablets too:** a compact layout with the image full screen and the controls floating over
  it, chosen automatically on touch screens. Added to an iPad's or phone's Home Screen, it opens as its own
  full-screen app.

## Screenshots

<table>
  <tr>
    <td width="33%"><a href="docs/images/desktop-targets-now.webp"><img src="docs/images/desktop-targets-now.webp" alt="The Targets tab: the sky chart with survey imagery, the camera's frame on M 31 and the altitude chart for the night"></a></td>
    <td width="33%"><a href="docs/images/desktop-targets-time-menu.webp"><img src="docs/images/desktop-targets-time-menu.webp" alt="The Targets tab in its planning view: the Time menu open, the sky shown at 00:43 tonight, a dashed border round the chart"></a></td>
    <td width="33%"><a href="docs/images/desktop-targets-sky.webp"><img src="docs/images/desktop-targets-sky.webp" alt="The Targets tab zoomed out to the whole sky, with tonight's list of targets down the side"></a></td>
  </tr>
  <tr>
    <td><b>Targets:</b> a sky chart with survey imagery, the camera's frame on the target, and its altitude through the night.</td>
    <td><b>Plan ahead:</b> the sky at any other time, clearly marked, with the mount's buttons off until you're back to now.</td>
    <td><b>Tonight:</b> the whole sky and the best targets for your site.</td>
  </tr>
  <tr>
    <td width="33%"><a href="docs/images/desktop-plan.webp"><img src="docs/images/desktop-plan.webp" alt="The Plan tab: M 31 then M 33, each with its frames, times and live stacking"></a></td>
    <td width="33%"><a href="docs/images/desktop-image-side.webp"><img src="docs/images/desktop-image-side.webp" alt="The Image tab comparing two frames side by side, with a table of what changed between them"></a></td>
    <td width="33%"><a href="docs/images/desktop-popouts.webp"><img src="docs/images/desktop-popouts.webp" alt="The image full width with the panels hidden, and the 3D mount view and guiding graph popped out over it"></a></td>
  </tr>
  <tr>
    <td><b>Plan:</b> a night's imaging over several targets, with dithering, refocusing, the meridian flip and what happens at the end.</td>
    <td><b>Compare:</b> two frames side by side or blinked, with what changed between them.</td>
    <td><b>Pop-outs:</b> the mount in 3D and the guiding graph in small windows over any tab.</td>
  </tr>
  <tr>
    <td width="33%"><a href="docs/images/desktop-guiding.webp"><img src="docs/images/desktop-guiding.webp" alt="The Guiding tab: the guide star, the graph at about 1 arcsecond RMS, and the calibration"></a></td>
    <td width="33%"><a href="docs/images/desktop-polar.webp"><img src="docs/images/desktop-polar.webp" alt="The Polar alignment tab part way through adjusting: the error in altitude and azimuth, and a target to bring the dot into"></a></td>
    <td width="33%"><a href="docs/images/desktop-autofocus.webp"><img src="docs/images/desktop-autofocus.webp" alt="The Focuser panel after an autofocus run, with its V-curve"></a></td>
  </tr>
  <tr>
    <td><b>Guiding:</b> NOVA's own guider, with its graph, target and calibration.</td>
    <td><b>Polar alignment:</b> the error live as you turn the knobs (here in the simulator).</td>
    <td><b>Autofocus:</b> a V-curve and the best focus position (here in the simulator).</td>
  </tr>
  <tr>
    <td width="33%"><a href="docs/images/desktop-night.webp"><img src="docs/images/desktop-night.webp" alt="The Image tab in night mode, everything in red"></a></td>
    <td width="33%"><a href="docs/images/phd2-tab-v2.webp"><img src="docs/images/phd2-tab-v2.webp" alt="The PHD2 tab: PHD2's own window, running headless on the mini PC"></a></td>
    <td width="33%"><a href="docs/images/webcam-tab-v2.webp"><img src="docs/images/webcam-tab-v2.webp" alt="The Webcam tab: a USB webcam's view of the telescope in the back yard at night"></a></td>
  </tr>
  <tr>
    <td><b>Night mode:</b> everything in red, to keep your night vision.</td>
    <td><b>PHD2:</b> PHD2 runs on the mini PC without a screen, and shows in a tab (or use NOVA's own guider).</td>
    <td><b>Webcam:</b> watch the rig from indoors, full size or popped out over any tab.</td>
  </tr>
</table>

**On a tablet** (the compact layout, chosen automatically on touch screens):

<table>
  <tr>
    <td width="33%"><a href="docs/images/ipad-image.webp"><img src="docs/images/ipad-image.webp" alt="The compact layout on a tablet: the image of M 31 full screen, icons down the left, the shutter down the right and a small guiding graph"></a></td>
    <td width="33%"><a href="docs/images/ipad-targets.webp"><img src="docs/images/ipad-targets.webp" alt="The compact layout's Targets page in its planning view, with the camera's frame on M 31"></a></td>
    <td width="33%"><a href="docs/images/ipad-plan.webp"><img src="docs/images/ipad-plan.webp" alt="The compact layout's Plan page with two targets"></a></td>
  </tr>
  <tr>
    <td><b>The image, full screen:</b> icons for the equipment on one side, the shutter on the other.</td>
    <td><b>Targets:</b> the same planner, here showing tonight at 00:43.</td>
    <td><b>Plan:</b> the night's sequence.</td>
  </tr>
  <tr>
    <td width="33%"><a href="docs/images/ipad-mount.webp"><img src="docs/images/ipad-mount.webp" alt="The Mount panel slid out over the image, with the 3D view and the direction buttons"></a></td>
    <td width="33%"><a href="docs/images/ipad-camera.webp"><img src="docs/images/ipad-camera.webp" alt="The Camera panel slid out over the image"></a></td>
    <td width="33%"><a href="docs/images/ipad-guide.webp"><img src="docs/images/ipad-guide.webp" alt="The Guider panel slid out over the image, with the guide star and calibration"></a></td>
  </tr>
  <tr>
    <td><b>Mount</b></td>
    <td><b>Camera</b></td>
    <td><b>Guider</b></td>
  </tr>
  <tr>
    <td width="33%"><a href="docs/images/ipad-flats.webp"><img src="docs/images/ipad-flats.webp" alt="The Flat panel's controls slid out over the image: light, brightness and cover"></a></td>
    <td></td>
    <td></td>
  </tr>
  <tr>
    <td><b>Flat panel</b></td>
    <td></td>
    <td></td>
  </tr>
</table>

**On a phone:**

<table>
  <tr>
    <td width="25%"><a href="docs/images/phone-image.webp"><img src="docs/images/phone-image.webp" alt="A phone: the image of M 31, the guiding graph, the shutter and the icons along the bottom"></a></td>
    <td width="25%"><a href="docs/images/phone-targets.webp"><img src="docs/images/phone-targets.webp" alt="A phone: the Targets page in its planning view with the camera's frame on M 31"></a></td>
    <td width="25%"><a href="docs/images/phone-targets-altitude.webp"><img src="docs/images/phone-targets-altitude.webp" alt="A phone: the Targets page with the altitude chart open"></a></td>
    <td width="25%"><a href="docs/images/phone-targets-framing.webp"><img src="docs/images/phone-targets-framing.webp" alt="A phone: the Framing panel open over the chart"></a></td>
  </tr>
  <tr>
    <td><b>Image</b></td>
    <td><b>Targets</b></td>
    <td><b>Altitude</b></td>
    <td><b>Framing</b></td>
  </tr>
  <tr>
    <td width="25%"><a href="docs/images/phone-mount.webp"><img src="docs/images/phone-mount.webp" alt="A phone: the Mount panel with the 3D view"></a></td>
    <td width="25%"><a href="docs/images/phone-camera.webp"><img src="docs/images/phone-camera.webp" alt="A phone: the Camera panel"></a></td>
    <td width="25%"><a href="docs/images/phone-guide.webp"><img src="docs/images/phone-guide.webp" alt="A phone: the Guider panel"></a></td>
    <td></td>
  </tr>
  <tr>
    <td><b>Mount</b></td>
    <td><b>Camera</b></td>
    <td><b>Guider</b></td>
    <td></td>
  </tr>
</table>

<p align="center"><a href="docs/images/polar-aligning.webp"><img src="docs/images/polar-aligning.webp" width="70%" alt="At night: the refractor on its mount in red light, and a laptop showing NOVA's polar alignment"></a><br><em>Polar aligning with NOVA: the only time you need to be at the telescope.</em></p>

## Taken with NOVA

Captured with NOVA and guided by NOVA's own guider (ZWO AM3, ASI2600MC Pro).

<table>
  <tr>
    <td width="41%"><a href="docs/images/m33.webp"><img src="docs/images/m33.webp" alt="M 33, the Triangulum Galaxy: a face-on spiral with blue arms and pink star-forming regions"></a></td>
    <td width="41%"><a href="docs/images/cave-nebula.webp"><img src="docs/images/cave-nebula.webp" alt="Sh2-155, the Cave Nebula: a glowing red cloud edged by dark dust in a dense star field"></a></td>
    <td width="18%"><a href="docs/images/heart-nebula.webp"><img src="docs/images/heart-nebula.webp" alt="IC 1805, the Heart Nebula: a wide heart-shaped loop of red hydrogen with a bright knot at its centre"></a></td>
  </tr>
  <tr>
    <td><b>M 33</b>, the Triangulum Galaxy</td>
    <td><b>Sh2-155</b>, the Cave Nebula</td>
    <td><b>IC 1805</b>, the Heart Nebula</td>
  </tr>
</table>

## Install

<img src="docs/images/mini-pc.webp" width="240" align="right" alt="A Lenovo ThinkCentre Tiny mini PC labelled nova.local:5000">

A used ex-office mini PC (a Lenovo ThinkCentre M710q or similar, like the one on the right) is cheap, fast and
ideal: cheaper than a Raspberry Pi kit these days. See [Choosing a computer](docs/INSTALL.md#choosing-a-computer).

<br clear="all"><br>

<p align="center"><a href="docs/images/mini-pc-on-rig.webp"><img src="docs/images/mini-pc-on-rig.webp" width="70%" alt="A small mini PC strapped to the top of a refractor, between the guide scope and the focuser, with the cameras and focuser cabled to it and a label giving its address, the optics and the hotspot name"></a><br><em>A mini PC riding on the telescope. Small intel N95 pc's like this run on 12v power directly and are ideal for Nova</em></p>

On a 64-bit Arch, Debian 13, Raspberry Pi OS (trixie) or Ubuntu 26.04 system:

```bash
sudo apt install git             # Arch: sudo pacman -S --needed git
git clone https://github.com/geoffpedder/nova-astro.git && cd nova-astro
deploy/setup.sh
```

`setup.sh` installs INDI and your equipment's drivers, the ASTAP plate solver, PHD2 and NOVA itself. Then
open `http://<name>.local:5000`, where a **Getting started** checklist takes you through the rest. Later,
`nova update` installs new releases (NOVA says on its page when there's one) and `nova doctor` checks everything.

## Build on NOVA

Everything NOVA's page does goes through a plain HTTP API on the mini PC, so it's easy to make your own
things that work with it: a status screen, a button box, a script that runs at dusk, a dashboard in another
app. This little touchscreen (an ESP32 "Cheap Yellow Display" in a printed case) sits on the telescope and shows
the mount, camera, sequence and guiding by reading NOVA's status over Wi-Fi, with no changes to NOVA at all.
The [API](docs/DEVELOPING.md#api) lists the endpoints.

<p align="center"><a href="docs/images/status-monitor.webp"><img src="docs/images/status-monitor.webp" width="70%" alt="A small red touchscreen on the telescope next to the mini PC, showing NOVA's mount parked, the camera exposing 6 of 60 s, the sequence not running and guiding stopped"></a><br><em>A home-made status monitor on the telescope, reading NOVA's API.</em></p>

## Documentation

- [Installing NOVA](docs/INSTALL.md): systems, setup options, updating, troubleshooting, installing by hand
- [Using NOVA](docs/USER-GUIDE.md): every part of the page, from the first run to unattended sequences
- [Developing NOVA](docs/DEVELOPING.md): building, tests, releases and the API
- [Why NOVA](docs/WHY-NOVA.md): how it compares with an ASIAIR, or N.I.N.A. over Remote Desktop
- [Changelog](CHANGELOG.md)

## Status

NOVA is young. It's developed with a ZWO AM3 mount and an ASI2600MC Pro on Ubuntu Server and Arch, and tested
against INDI's simulators; every release's setup is also tested from scratch on Arch, Debian and Ubuntu in
containers. Raspberry Pi OS hasn't been tried on real hardware yet. Reports of how it works with other equipment are very welcome; please include the output
of `nova doctor`.

**Password:** optional. Without one, anyone on the same network can control the telescope, which is fine at
home or in a field; on shared networks set one (⏻ → Set a password…). Either way, don't expose NOVA to the
internet.

## Licence

NOVA is free software: you can use, study, change and share it under the terms of the
[GNU General Public License](LICENSE), version 3 or (at your option) any later version. If you distribute a
changed version, it must be under the same licence, with its source.

Copyright (C) 2026 Geoff Pedder.

## Credits

NOVA uses the HYG star database, OpenNGC, GeoNames, Natural Earth, d3-celestial's constellation figures, and
the Inter and JetBrains Mono fonts; see [NOTICE](NOTICE) for their authors and licences. It contains no code
from N.I.N.A. and isn't affiliated with it.
