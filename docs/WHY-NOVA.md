# Why NOVA

How NOVA compares with the two usual ways of running a rig remotely: ZWO's ASIAIR, and a PC running N.I.N.A.
that you reach over Remote Desktop.

## NOVA and the ASIAIR

- **Not tied to one brand.** NOVA uses INDI, so it runs cameras, mounts, focusers and filter wheels from QHY,
  Player One, SVBony, Touptek, Celestron, Sky-Watcher (EQMod), iOptron and others, as well as ZWO's.
- **Any browser, no app.** A laptop, tablet or phone, on Windows, macOS, Linux, iOS or Android: nothing to install.
  Added to an iPad's or phone's Home Screen, it opens as its own full-screen app.
- **Several screens at once.** Any number of browsers show the same live session: a laptop indoors, a phone at
  the scope.
- **Cheap, capable hardware.** A used ex-office mini PC (a Lenovo ThinkCentre M710q or similar) often costs less
  than an ASIAIR and has far more processing power, storage and USB ports. A computer you already own works too.
- **Open and yours.** Free and open source: no licence, no paid tier, and no waiting on a vendor's update
  schedule. Everything can be read, changed and fixed.
- **An open HTTP API.** Build your own add-ons: a status screen on the telescope, a button box, scripts, a
  dashboard. See [Build on NOVA](../README.md#build-on-nova).
- **Your Bluetooth mount is locked to your NOVA.** ZWO AM3/AM5/AM7 mounts connect over Bluetooth with a pairing
  code of NOVA's own, confirmed once with the mount's BT button, and NOVA only ever connects to the mount you chose:
  safer at a shared dark site.
- **Its own guider, built for strain-wave mounts.** Predictive PEC, Dec backlash compensation, a guiding assistant
  and PHD2-format guide logs, with a guide camera that drops out reconnected without stopping guiding. PHD2 is there
  too, if you prefer it.
- **Polar alignment two ways:** three-point, or with the camera on the pole.
- **A real Linux computer underneath.** A terminal in the browser, `nova doctor` to check everything, and ordinary
  tools for USB drives, networks and storage.
- **The extras that make a night easier:** a webcam view of the rig, notifications on your phone (sequence done,
  needs you, guiding lost, the mini PC ready after a boot), a night's frames copied to a USB drive and checked, a
  flats wizard, and a Wi-Fi hotspot of your own naming when there's no network to join.

## NOVA and N.I.N.A. over Remote Desktop

- **Made for the browser, not a remote desktop.** Remote Desktop streams a whole Windows desktop: sluggish over a
  field Wi-Fi link, fiddly on a phone or tablet, and it needs a client app. NOVA sends only the data and images,
  and its page is laid out for touch and small screens.
- **Several people can watch.** Remote Desktop gives one person the session; NOVA shows the same live state to
  every browser.
- **No Windows to look after.** No Windows licence, no updates restarting the computer mid-session, no antivirus
  or driver conflicts. NOVA runs headless on Linux and needs less hardware to do it.
- **One command to set up, one to update.** `nova setup` installs INDI and your equipment's drivers, the ASTAP
  plate solver and a star database, PHD2 and NOVA itself; `nova update` updates it. Every release's setup is tested
  from scratch on Debian, Ubuntu and Arch.
- **Designed to run without a screen.** Its own hotspot when there's no network, `nova.local` to find it, a
  message when it has booted, a terminal in the browser, USB copying: nothing needs a monitor or a desktop.
- **Equipment profiles switch everything at once:** the drivers, the device for each role, how the mount
  connects, and the guider with its camera.
- **Safety built in.** Every move is kept clear of the Sun while it's up (slews go round it), and hand moves are
  checked to have really stopped.
- **Nothing lost when your laptop sleeps.** Everything runs on the mini PC; open any browser to pick up where it
  is.
