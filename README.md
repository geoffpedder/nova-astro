# NOVA

**Astrophotography from your browser.** NOVA runs on a small Linux computer at the telescope (a mini PC or a
Raspberry Pi) and controls your mount, cameras, focuser, filter wheel, rotator and flat panel through
[INDI](https://www.indilib.org/). You use it from a browser on any computer, tablet or phone on the same
network, with nothing to install there. It's inspired by [N.I.N.A.](https://nighttime-imaging.eu/), for Linux.

![NOVA's Image tab showing a 300 s frame of M 31, with its image history](docs/images/image-tab-v2.webp)

## What it does

- **Imaging:** exposures with cooling, gain, offset and binning; stretched colour previews; stars, HFR and
  FWHM for every frame; image history with side-by-side and blink comparison; optional live stacking, to watch a
  target build up. FITS files are saved on the mini PC or straight to a USB drive plugged into it.
- **Plate solving** with ASTAP: sync, slew and centre on a target to within an arcminute; named stars on
  solved frames.
- **Targets:** offline deep-sky and star catalogues, tonight's best targets for your site, a Stellarium-style
  sky chart with survey imagery, framing with rotation and mosaics.
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
- **Works on a tablet:** the panels fold away, and the image and sky chart pinch to zoom. Added to an iPad's or
  phone's Home Screen, it opens as its own full-screen app.

## Screenshots

<table>
  <tr>
    <td width="33%"><a href="docs/images/targets-tab-v2.webp"><img src="docs/images/targets-tab-v2.webp" alt="The Targets tab: tonight's best targets, the sky chart with DSS imagery, and the camera's frame on IC 1396, with the panels moved and the altitude chart"></a></td>
    <td width="33%"><a href="docs/images/sequence-tab-v2.webp"><img src="docs/images/sequence-tab-v2.webp" alt="The Sequence tab: IC 1396 with its frames, dithering, refocusing, meridian flip and end-of-night steps"></a></td>
    <td width="33%"><a href="docs/images/image-tab-v2.webp"><img src="docs/images/image-tab-v2.webp" alt="The Image tab: the Andromeda Galaxy, with stars, HFR and FWHM, and the image history"></a></td>
  </tr>
  <tr>
    <td><b>Targets:</b> tonight's best targets, a sky chart with survey imagery, and framing the camera on IC 1396.</td>
    <td><b>Sequence:</b> a night's imaging, with dithering, refocusing, the meridian flip and what happens at the end.</td>
    <td><b>Image:</b> every frame stretched in colour, with its stars, HFR and FWHM, and the night's history.</td>
  </tr>
  <tr>
    <td><a href="docs/images/phd2-tab-v2.webp"><img src="docs/images/phd2-tab-v2.webp" alt="The PHD2 tab: PHD2's own window, running headless on the mini PC"></a></td>
    <td><a href="docs/images/webcam-tab-v2.webp"><img src="docs/images/webcam-tab-v2.webp" alt="The Webcam tab: a USB webcam's view of the telescope in the back yard at night"></a></td>
    <td></td>
  </tr>
  <tr>
    <td><b>PHD2:</b> PHD2 runs on the mini PC without a screen, and shows in a tab (or use NOVA's own guider).</td>
    <td><b>Webcam:</b> watch the rig from indoors, full size or popped out over any tab.</td>
    <td></td>
  </tr>
</table>

<p align="center"><a href="docs/images/polar-aligning.webp"><img src="docs/images/polar-aligning.webp" width="70%" alt="At night: the refractor on its mount in red light, and a laptop showing NOVA's polar alignment"></a><br><em>Polar aligning with NOVA: the only time you need to be at the telescope.</em></p>

## Taken with NOVA

Captured with NOVA and guided by NOVA's own guider (ZWO AM3, ASI2600MC Pro).

<table>
  <tr>
    <td width="50%"><a href="docs/images/m33.webp"><img src="docs/images/m33.webp" alt="M 33, the Triangulum Galaxy: a face-on spiral with blue arms and pink star-forming regions"></a></td>
    <td width="50%"><a href="docs/images/cave-nebula.webp"><img src="docs/images/cave-nebula.webp" alt="Sh2-155, the Cave Nebula: a glowing red cloud edged by dark dust in a dense star field"></a></td>
  </tr>
  <tr>
    <td><b>M 33</b>, the Triangulum Galaxy</td>
    <td><b>Sh2-155</b>, the Cave Nebula</td>
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
`nova update` installs new releases and `nova doctor` checks everything.

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
