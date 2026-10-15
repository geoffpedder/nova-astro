# Installing NOVA

NOVA runs on a small Linux computer at the telescope (a mini PC or a Raspberry Pi) and you use it from a
browser on any computer, tablet or phone on the same network.

- [What you need](#what-you-need)
- [Choosing a computer](#choosing-a-computer)
- [Which system to use](#which-system-to-use)
- [Quick setup](#quick-setup)
- [First run](#first-run)
- [Updating, and the `nova` command](#updating-and-the-nova-command)
- [Network, ports and security](#network-ports-and-security)
- [Troubleshooting](#troubleshooting)
- [Installing by hand (Arch)](#installing-by-hand-arch)
- [Headless PHD2](#headless-phd2)
- [Uninstalling](#uninstalling)

## What you need

- **A 64-bit Linux computer:** a mini PC (x86-64), or a Raspberry Pi 4 or 5 running 64-bit Raspberry Pi OS.
  32-bit systems aren't supported.
- **One of these systems:**

  | System | Where INDI comes from |
  |---|---|
  | Arch, Omarchy, EndeavourOS, Manjaro | Arch's `libindi`; each maker's drivers from the AUR (`setup.sh` installs `yay` if needed) |
  | Debian 13 (trixie) | [Astroberry's repository](https://www.indilib.org/download/raspberry-pi.html), with every INDI driver |
  | Raspberry Pi OS, 64-bit, trixie (Pi 4 or 5) | Astroberry's repository |
  | Ubuntu 26.04 or newer | INDI's own repository (`ppa:mutlaqja/ppa`); PHD2 from `ppa:pch/phd2` |

  Older Debian and Ubuntu releases only offer INDI 1.9, which has no driver for ZWO's AM3/AM5 mounts, so
  setup refuses them.
- **Equipment with INDI drivers:** mounts, cameras, focusers, filter wheels, rotators and flat panels. NOVA
  talks to them through [INDI](https://www.indilib.org/).
- **A browser** on the computer, tablet or phone you'll use NOVA from. Nothing is installed there.

## Choosing a computer

**A used ex-office mini PC is the best value for NOVA.** Raspberry Pis have become expensive: by the time a Pi 5
has enough memory, a case, a cooler, a power supply and decent storage, it often costs more than a second-hand
business mini PC that is faster in every way. Offices replace these "tiny" PCs in bulk, so they're plentiful and
cheap on the used market:

- **Lenovo ThinkCentre M710q, M720q, M920q** (the "Tiny" range)
- **Dell OptiPlex Micro** (3060, 5060, 7060 and similar)
- **HP EliteDesk / ProDesk Mini** (G3, G4 and similar)

What makes them a good fit:

- **Fast enough for everything at once:** a 6th to 9th generation Intel Core i3 or i5 runs INDI, PHD2 and
  NOVA together with room to spare, and plate solves in a second or two.
- **A proper SSD,** not an SD card that can wear out or corrupt when the power drops in the field.
- **Every INDI driver:** x86-64 gets all of them, and every Linux package, with nothing to compile.
- **Small and frugal:** about the size of a paperback, drawing around 10–15 W while imaging. Most run from a
  19–20 V laptop-style power brick, so at the telescope a 12 V battery works through a DC-DC step-up converter
  or a 12 V laptop adapter.

What to look for in a listing:

- **8 GB of memory** (4 GB works; more doesn't help NOVA).
- **An SSD** of 128 GB or more (captures can go to a USB drive instead).
- **A Wi-Fi card,** if you want Wi-Fi and the [hotspot](USER-GUIDE.md#wi-fi-hotspot-no-wi-fi-at-the-site). Some come
  without one; an Intel card (8265, 9260, AX200/AX210) can be fitted cheaply. Ask for the antennas to be included.
  `nova doctor` says whether yours can run a hotspot.
- **USB 3 ports** for the imaging camera; plug the guide camera and a webcam into separate ports.

A Raspberry Pi 4 or 5 still works if you already have one (64-bit Raspberry Pi OS, with an SSD rather than an
SD card if possible).

## Which system to use

**Ubuntu Server 26.04 LTS is the one to choose for a mini PC** (64-bit Raspberry Pi OS on a Raspberry Pi).

- **It stays put.** An LTS release gets security updates for five years without its packages changing under
  you. On a rolling system such as Arch, a routine update can move INDI on and leave the AUR drivers built for
  the old version until they're rebuilt (see [Troubleshooting](#troubleshooting)): not what you want to find
  at the telescope.
- **INDI comes from INDI itself.** Its own Ubuntu packages are built by the INDI developers and carry every
  driver in one package, so any maker's equipment works without choosing drivers at install time. PHD2 has its
  own Ubuntu packages too.
- **Nothing runs that NOVA doesn't need.** NOVA is used from a browser and PHD2 runs on a virtual screen, so
  the mini PC needs no desktop. The server edition boots quickly, leaves the memory and processor to imaging,
  and has no firewall switched on to get in the way.

Arch, Omarchy, Debian and Ubuntu Desktop all work as well, with the same setup; pick one of them if you also
want to use the mini PC with its own screen and keyboard.

**Installing Ubuntu Server for NOVA:**

1. Download Ubuntu Server 26.04 LTS from [ubuntu.com](https://ubuntu.com/download/server) and write it to a
   USB stick (with [balenaEtcher](https://etcher.balena.io/) or Rufus).
2. In the installer: join your network (a cable is simplest; the network step can join Wi-Fi too), give the
   mini PC a short name (NOVA is then at `http://<name>.local:5000`), and tick **Install OpenSSH server** so
   you can reach it from your laptop.
3. After the first boot, check the clock with `timedatectl`: setup fails if it's far out (see
   [Troubleshooting](#troubleshooting)). **If the mini PC ran Windows before,** check the clock in its BIOS
   (UEFI) setup first, usually reached with Del, F2 or F10 as it starts, and set it to the current time.
   Windows keeps local time there while Linux expects UTC, so the clock can start hours out; apt then
   refuses Ubuntu's package lists as "not valid yet", and the install stops partway.
4. **Give Ubuntu the whole drive.** Ubuntu Server's installer sets up the drive with LVM but only uses part of
   it (often 100 GB, or half the drive) for the system and your images, and leaves the rest unused, so a big
   SSD can fill up after a few nights. Check with `df -h /`: if its size is well short of the drive's, take the
   rest:

   ```bash
   sudo lvextend -r -l +100%FREE /dev/ubuntu-vg/ubuntu-lv
   ```

   `-r` grows the filesystem too, and it's safe while running. (`sudo vgs` shows how much was left unused.) To do
   it during the install instead, at the storage summary edit `ubuntu-lv` and set its size to the maximum.
5. Then follow [Quick setup](#quick-setup) below.

## Quick setup

`setup.sh` sets up a fresh computer in one go: INDI and your equipment's drivers, the ASTAP plate solver with
a star database, PHD2 for guiding (optional), Avahi (so `<name>.local` works) and polkit, then the latest
NOVA release. It asks a few questions first, shows what it will install, then runs by itself (apart from
sudo's password).

```bash
sudo apt install git             # Arch: sudo pacman -S --needed git
git clone https://github.com/geoffpedder/nova-astro.git && cd nova-astro
deploy/setup.sh                  # as yourself, not with sudo
```

**It asks:**

- **Whose equipment you have** (Arch only): ZWO, QHY, Player One, ToupTek, SVBONY, DSLRs, Atik, Moravian,
  Starlight Xpress, EQMod mounts, Celestron AUX mounts. On Arch each maker's drivers are a separate AUR
  package, built from source. Drivers for ZWO AM, iOptron, Pegasus and LX200-type mounts come with INDI
  itself. On Debian, Raspberry Pi OS and Ubuntu every driver comes in one package, so there's no question.
- **Which star database** for plate solving: d05 (100 MB, wide fields), d20 (400 MB, suits most
  telescopes) or d50 (900 MB, long focal lengths).
- **Whether to set up PHD2** for guiding. It runs without a screen; you see it in NOVA's PHD2 tab.
- **Whether to install the GSC star catalogue** (Debian and Ubuntu): only INDI's CCD Simulator uses it.

**Options:**

| Option | |
|---|---|
| `--yes` | take the suggested answers: ZWO drivers, the d20 database, PHD2, no GSC |
| `--brands zwo,qhy,…` | on Arch, the makers whose drivers to install |
| `--database d05\|d20\|d50` | the star database; later, `nova setup --database d50` adds another alongside the first |
| `--no-phd2` | leave guiding out |
| `--release-file nova-….tar.gz` | install that downloaded release instead of fetching the latest |
| `--build` | build NOVA from the source instead of downloading a release (needs the .NET 10 SDK and Node) |

It's safe to run again: it only adds what's missing (for example PHD2, or another maker's drivers). It
finishes by running `nova doctor`, which checks everything.

Arch's AUR driver packages require exactly the version of INDI that Arch ships, so `setup.sh` always updates
the whole system (`pacman -Syu`) before installing them.

## First run

Open `http://<name>.local:5000` from any computer on the same network (`<name>` is the mini PC's host name;
`nova status` prints the address).

A **Getting started** checklist leads the way: the site, the drivers, connecting, the camera's optics and a
test plate solve (plus PHD2, optional). Each step ticks itself and has a button that opens the place where
it's done. Below the steps it lists anything the mini PC is missing, with the command that fixes it. See the
[user guide](USER-GUIDE.md#getting-started).

## Updating, and the `nova` command

Installing NOVA also installs a `nova` command on the mini PC:

| Command | What it does |
|---|---|
| `nova update` | installs the newest release (`nova update v0.2.0` for a particular one, also to go back; `--force` reinstalls) |
| `nova doctor` | checks everything NOVA needs and prints the fix for anything missing |
| `nova setup` | runs `setup.sh` again, e.g. to add PHD2 or another maker's drivers |
| `nova status` | the version, the services and the web address |
| `nova logs [service]` | follows a service's log (`nova` by default; also `indiserver`, `phd2`, …) |
| `nova restart` | restarts INDI and NOVA (and PHD2's screen) |
| `nova password` | sets NOVA's password (asked twice); `nova password --clear` removes it |

`nova update` downloads the build for the computer (x64 or arm64), checks its checksum, and installs it.
Nothing is compiled. Settings, sites, sequences and images are kept across updates.

**Without internet at the scope:** download the release for the mini PC (`nova-<version>-linux-x64.tar.gz`, or
`linux-arm64` for a Raspberry Pi, with its `.sha256`) elsewhere, bring it over on a USB stick, and run
`nova update --file nova-<version>-linux-x64.tar.gz`. A first install can do the same:
`deploy/setup.sh --release-file nova-<version>-linux-x64.tar.gz` (setup still needs internet for INDI and the
other packages).

Download releases with `nova update` rather than a browser: a browser may warn that a new, rarely
downloaded program is dangerous. That warning is about how many people have downloaded the file, not
about what's in it; `nova update` checks the file's checksum instead.

Run `nova doctor` after system updates too: it notices drivers or programs that went missing.

## Network, ports and security

| Port | What |
|---|---|
| 5000 | NOVA's web page and API |
| 7624 | INDI (only NOVA needs it; it can stay local) |
| 10001 | Stellarium's telescope link (optional; `Nova__StellariumPort=0` in `/etc/nova/nova.env` turns it off) |
| 6080 | noVNC's own page for PHD2's screen (optional; NOVA's PHD2 tab goes through port 5000) |
| 4400 | PHD2's event server (local only) |

If the mini PC runs a firewall (`ufw`, which Omarchy turns on, or `firewalld`), the install opens TCP 5000 and 10001
and mDNS (UDP 5353, for `.local` names) in it. Any other firewall needs those opened by hand.

**A password is optional.** Without one, anyone on the same network can open NOVA and control the telescope.
That's fine at home or in a field with your own Wi-Fi; on shared networks (star parties, campsites) set one:
⏻ → **Set a password…** in NOVA, or `nova password` on the mini PC. Then each browser signs in once and stays
signed in for 30 days; ⏻ → **Sign out of this browser** signs it out, and changing the password signs every
other browser out. The password is kept as a salted hash, never as itself, and wrong guesses are answered
more and more slowly.

With a password set, NOVA's API, live updates and PHD2 tab all need a signed-in browser, and noVNC's own page
on port 6080 only answers on the mini PC itself (from the next restart, or `nova restart`). Stellarium's
telescope link on port 10001 can't ask for a password; turn it off with `Nova__StellariumPort=0` if that
matters. Forgotten the password? `nova password --clear` on the mini PC removes it.

Even with a password, don't expose port 5000 to the internet: NOVA uses plain HTTP, made for a local network.

Settings are in `/etc/nova/nova.env` (environment variables such as `Nova__AstapDatabase=d20`); the chosen
INDI drivers are in `/etc/nova/indi.env`. After editing either by hand, run `nova restart`.

## Troubleshooting

**Start with `nova doctor`.** It checks INDI and the chosen drivers, ASTAP and its star database, PHD2, the
services and the ports, and prints a fix for each problem. `nova logs` (or `nova logs indiserver`) shows
what's happening right now.

- **`http://<name>.local:5000` doesn't open.** Use the mini PC's IP address instead (`nova status`, or your
  router's device list). `.local` names need Avahi on the mini PC (`setup.sh` installs it) and work on most
  computers and phones, though not always on older Android. If the IP address doesn't open either, check the
  mini PC's firewall: the install opens NOVA's ports in `ufw` and `firewalld` (`nova doctor` says which is
  on), but any other firewall needs TCP 5000 opened by hand.
- **It's at `<name>-2.local` instead.** Avahi renames itself when it thinks another computer has its name, which
  it can wrongly think at boot or after a network change (its own announcement coming back, e.g. repeated by mesh
  access points). The install adds a check (`nova-mdns.timer`) that sets the name back within 30 seconds; the
  journal says when it did (`journalctl -u nova-mdns`). If it keeps happening, another device may really have the
  name: give the mini PC another (`sudo hostnamectl set-hostname …`).
- **Setup stops with *Release file … is not valid yet* (Debian, Ubuntu).** The mini PC's clock is behind, often
  after Windows was on the machine. Turn on time sync with `sudo timedatectl set-ntp true` and check with
  `timedatectl` (*System clock synchronized: yes*), then run setup again. Ubuntu 26.04 syncs securely (NTS),
  which needs outgoing TCP 4460 as well as UDP 123; if your network blocks it, add a plain source:
  `echo "pool pool.ntp.org iburst" | sudo tee /etc/chrony/sources.d/plain-ntp.sources` then
  `sudo chronyc reload sources`. NOVA needs the right time too: for the mount, plate solving and dawn.
- **A driver is chosen but not installed.** NOVA's Getting started list and `nova doctor` name the package.
  On Arch, `yay` may say *No AUR package found for libindi=2.2.5*: the system is out of date, so the AUR
  package can't find the INDI version it needs. Update first: `sudo pacman -Syu && yay -S libindi-eqmod`
  (with that maker's package).
- **Drivers stop working after a system update (Arch).** AUR drivers are built against the INDI version
  installed at the time. Rebuild them: `yay -S --rebuild libindi-asi-git` (and any other `libindi-…`
  packages), then `nova restart`.
- **The mount refuses to slew.** NOVA sends the site and time to the mount when it connects; make sure a site
  is set (Mount panel → Site). NOVA also refuses targets below the horizon or too near the Sun while it's up
  (Sun protection).
- **Plate solving fails.** Check that the camera has an optics preset with the right focal length (Camera →
  Optics), that the star database suits the field of view (d05 for wide fields, d50 for long focal lengths:
  `nova setup --database d50` adds it, then put `Nova__AstapDatabase=d50` in `/etc/nova/nova.env` and run
  `nova restart` so ASTAP uses it),
  and that the frame shows stars: in focus, long enough, and not clouded over.
- **The PHD2 tab is blank.** `nova status` should show `phd2` and `nova-screen` active. If PHD2 isn't
  installed, `nova setup` adds it.
- **A serial device (mount, focuser) can't be opened.** The install adds NOVA's user to the serial group
  (`uucp` on Arch, `dialout` on Debian and Ubuntu); `nova restart` picks that up. NOVA tries every serial
  port the driver lists when the expected one fails; if none works, check the cable and the device's settings
  (the ⚙ next to it in the Equipment panel).
- **Saving to a USB drive doesn't work.** `nova doctor` checks udisks2 and NOVA's permission to mount drives
  (on a mini PC set up before 0.3.1: install `udisks2`, then `nova update --force`). The drive's format matters
  too: exFAT, FAT32 or NTFS work as they are (see the user guide's *Camera and optics*).

## Installing by hand (Arch)

What `setup.sh` installs, written out for Arch so it can be done by hand:

| What | Install | Why |
|---|---|---|
| Build tools | `sudo pacman -S --needed git github-cli dotnet-sdk nodejs npm` | `deploy/publish.sh` builds NOVA (.NET 10, web UI); not needed for releases |
| INDI | `sudo pacman -S libindi` | indiserver, the ZWO AM3/AM5 mount driver (`indi_lx200am5`), simulators |
| ZWO drivers | `yay -S libindi-asi-git` | ASI cameras, EFW, EAF, CAA (`indi_asi_*`). The plain `libindi-asi` package can be pinned to an older libindi and refuse to install |
| Other makers' drivers | `yay -S libindi-qhy` (and so on) | one AUR package per maker |
| Plate solving | ASTAP's command-line version from [SourceForge](https://sourceforge.net/projects/astap-program/files/linux_installer/) (`astap_cli` into /opt/astap), then `yay -S d20-star-db-astap` | the plate solver and its star database in /opt/astap (d05/d50 suit other fields of view). The AUR's `astap` builds from a source download that has gone missing before |
| Headless PHD2 | `sudo pacman -S --needed xorg-server-xvfb x11vnc icewm python-build` then `yay -S phd2 novnc websockify` | guiding on a virtual display, its screen in the browser; websockify needs `python-build` to build |
| Restart / shut down from the web page | `sudo pacman -S --needed polkit` (usually already there) | lets the ⏻ menu restart or power off the mini PC; the install adds a polkit rule allowing NOVA's user to do only that |
| Webcam view | `sudo pacman -S --needed ffmpeg v4l-utils` | shows a USB webcam (ffmpeg) with its brightness, exposure and other controls (v4l-utils) in the Webcam tab, to watch the rig from indoors; the install adds NOVA's user to the `video` group |
| Saving to USB drives | `sudo pacman -S --needed udisks2` | mounts and ejects USB drives for NOVA (Camera panel → Storage); the install adds a polkit rule allowing NOVA's user to mount and eject them |
| Wi-Fi hotspot (optional) | `sudo pacman -S --needed hostapd` | the mini PC's own Wi-Fi network when there's none to join (Network dialog, from the header); hostapd is needed with systemd-networkd (NetworkManager and iwd run it themselves); the install adds `nova-hotspot.service` and a polkit rule letting NOVA's user start and stop only that |
| Wi-Fi details (optional) | `sudo pacman -S --needed iw` | the header's network name and signal; `iw` also spots when the mini PC runs a hotspot |
| Network names (optional) | `sudo pacman -S avahi nss-mdns` and `sudo systemctl enable --now avahi-daemon` | `http://<name>.local:5000` |
| GSC catalogue (optional) | AUR `gsc`, built by hand (below) | stars for INDI's CCD Simulator only; real cameras don't need it |

Then build and install NOVA on the mini PC itself:

```bash
git clone https://github.com/geoffpedder/nova-astro.git && cd nova-astro
deploy/publish.sh linux-x64        # linux-arm64 on a Raspberry Pi
sudo deploy/install.sh out/linux-x64
```

To update a build made this way: `git pull && deploy/publish.sh linux-x64 && sudo deploy/install.sh out/linux-x64`.
(`nova update` switches it to the latest release instead.)

**Building on another machine** works too (Git Bash on Windows): the build is self-contained, so the mini PC
needs no .NET.

```bash
deploy/publish.sh linux-x64
scp -r out/linux-x64 deploy user@minipc:~/nova/
ssh user@minipc 'cd ~/nova && sudo deploy/install.sh linux-x64'
```

**GSC by hand:** the AUR `gsc` package doesn't build as-is. Download `GSC_1.2.tar.gz` yourself (the
PKGBUILD's download tends to fail halfway) and set the PKGBUILD's `source` to that local file. GCC 16 also
rejects the old C, so build with `make CC="gcc -std=gnu89 -fpermissive -I."` (the `-I.` finds `gsc.h`, which
sits in the archive's `src/` folder), then `makepkg -Cfsi`. It installs to /usr/share/GSC, where
`indiserver.service` points INDI. On Debian and Ubuntu it's simply `sudo apt install gsc`.

## Headless PHD2

With PHD2 installed, the install runs it at boot on a virtual display (the `nova-display`, `nova-wm` and
`phd2` services). IceWM fills the screen with PHD2's window, gives every window a title bar to move it by, and
lists them all in a taskbar along the bottom: click one to bring it to the front. If PHD2 is closed, it starts
again. NOVA connects to it by
itself, and **Connect equipment** also connects PHD2's camera and mount.

PHD2's window shows in NOVA's **PHD2** tab, through NOVA's own port: NOVA serves noVNC's web client and
relays it to x11vnc, which only listens on the mini PC itself. It's red-filtered in night mode, and only
connected while the tab is open. (noVNC's own page, `http://<name>.local:6080/vnc.html`, works too.)

Once, in that tab: create a PHD2 profile with *INDI Camera* (your guide camera) and *INDI Mount*, and tick
**Tools → Enable Server**. After that, pick the profile in NOVA's Guider panel.

## Uninstalling

```bash
sudo systemctl disable --now nova indiserver nova-display nova-wm phd2 nova-screen
sudo rm -f /etc/systemd/system/{nova,indiserver,nova-display,nova-wm,phd2,nova-screen}.service
sudo rm -f /etc/polkit-1/rules.d/49-nova-power.rules /usr/local/bin/nova
sudo rm -rf /opt/nova
sudo systemctl daemon-reload
```

That leaves your settings and images. To remove those too: `/etc/nova` (driver choice and settings),
`~/.config/nova` (sites, optics, sequences, the sky survey cache) and `~/Pictures/Nova` (images). INDI,
ASTAP and PHD2 stay installed; remove them with the system's package manager if you like.
