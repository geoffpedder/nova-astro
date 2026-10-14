#!/usr/bin/env bash
# Sets up a computer to run NOVA, starting from a fresh system: INDI and the ZWO drivers, ASTAP and a star database,
# headless PHD2 and the other pieces NOVA uses, then the latest NOVA release.
#   deploy/setup.sh             asks a few questions first (once NOVA is installed: nova setup)
#   --yes                       takes the suggested answers (d20 star database, headless PHD2, no GSC)
#   --database d05|d20|d50      the ASTAP star database, without asking; --no-phd2 leaves out guiding
#   --brands zwo,qhy,...        on Arch, the equipment makers whose drivers to install (others get them all)
#   --build                     builds NOVA from a copy of the source instead of downloading a release
#   --release-file nova-….tar.gz  installs that downloaded release instead (no internet needed for NOVA itself)
# Works on Arch (and Omarchy, EndeavourOS, Manjaro), Debian 13, Raspberry Pi OS (64-bit, trixie) and Ubuntu 26.04.
# Run it as yourself (not with sudo): it asks for sudo when it needs it. Safe to run again: it only adds what's missing.
set -euo pipefail
REPO="${NOVA_REPO:-geoffpedder/nova-astro}"
HERE=""
[ -f "${BASH_SOURCE[0]:-}" ] && HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

assume_yes=no
build=no
release_file=""
database_choice=""
phd2_choice=""
brands_choice=""
while [ $# -gt 0 ]; do
  arg="$1"; shift
  case "$arg" in
    --yes|-y) assume_yes=yes ;;
    --build) build=yes ;;
    --release-file) release_file="${1:-}"; shift || true ;;
    --database) database_choice="${1:-}"; shift || true ;;
    --no-phd2) phd2_choice=no ;;
    --brands) brands_choice="${1:-}"; shift || true ;;
    -h|--help) sed -n '2,11p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown option: $arg (try --help)" >&2; exit 1 ;;
  esac
done
if [ -n "$release_file" ]; then
  [ -f "$release_file" ] || { echo "--release-file: no such file: $release_file" >&2; exit 1; }
  release_file="$(cd "$(dirname "$release_file")" && pwd)/$(basename "$release_file")"
fi
case "$database_choice" in ""|d05|d20|d50) ;; *) echo "--database takes d05, d20 or d50" >&2; exit 1 ;; esac

if [ "$(id -u)" -eq 0 ]; then
  echo "Run this as yourself, not with sudo: NOVA runs as the user who sets it up, and it asks for sudo when needed." >&2
  exit 1
fi

say() { printf '\n\e[1m%s\e[0m\n' "$*"; }
has() { command -v "$1" >/dev/null 2>&1; }
# ask "Question?" y|n: the answer, or the suggested one with --yes or when nobody's there to answer.
ask() {
  local reply
  if [ "$assume_yes" = yes ] || [ ! -r /dev/tty ]; then [ "$2" = y ]; return; fi
  if [ "$2" = y ]; then read -r -p "$1 [Y/n] " reply </dev/tty || reply=""; else read -r -p "$1 [y/N] " reply </dev/tty || reply=""; fi
  [[ "${reply:-$2}" =~ ^[Yy] ]]
}

# --- Which system is this? ---
. /etc/os-release
family=""
case " ${ID:-} ${ID_LIKE:-} " in
  *" arch "*) family=arch ;;
  *" ubuntu "*) family=ubuntu ;;
  *" debian "*|*" raspbian "*) family=debian ;;
esac
codename="${UBUNTU_CODENAME:-${VERSION_CODENAME:-}}"

if [ "$family" = arch ]; then
  arch="$(uname -m)"
else
  arch="$(dpkg --print-architecture 2>/dev/null || uname -m)"
fi
case "$arch" in
  x86_64|amd64) rid=linux-x64 ;;
  aarch64|arm64) rid=linux-arm64 ;;
  armhf|armv7l)
    echo "This is a 32-bit system. NOVA needs a 64-bit one: on a Raspberry Pi 4 or 5, install the 64-bit Raspberry Pi OS." >&2
    exit 1 ;;
  *) echo "There's no NOVA build for $arch. It needs a 64-bit PC or a Raspberry Pi 4 or 5." >&2; exit 1 ;;
esac

case "$family" in
  arch) ;;
  debian)
    # INDI 2 (which has the ZWO AM3/AM5 driver) comes from Astroberry's repository, built for Debian 13 only.
    if [ "$codename" != trixie ]; then
      echo "NOVA's setup needs Debian 13 (trixie) or Raspberry Pi OS based on it; this is ${PRETTY_NAME:-$codename}." >&2
      echo "Older releases ship INDI 1.9, which has no driver for ZWO's AM3/AM5 mounts." >&2
      exit 1
    fi ;;
  ubuntu)
    # INDI's own Ubuntu packages (ppa:mutlaqja/ppa) are only kept up to date for recent releases.
    index="https://ppa.launchpadcontent.net/mutlaqja/ppa/ubuntu/dists/$codename/main/binary-amd64/Packages.xz" ;;
  *)
    echo "This setup knows Arch, Debian, Raspberry Pi OS and Ubuntu; this is ${PRETTY_NAME:-unknown}." >&2
    echo "On other systems install INDI 2, ASTAP and (optionally) PHD2 yourself, then: deploy/update.sh" >&2
    exit 1 ;;
esac

# A minimal Debian or Ubuntu may lack the download tools the checks below use.
if [ "$family" != arch ] && { ! has curl || ! has xz; }; then
  sudo apt-get update && sudo apt-get install -y curl xz-utils
fi
# (grep -c reads the whole list: grep -q would stop early, and pipefail would take xz's broken pipe as a failure.)
if [ "$family" = ubuntu ] && [ "$(curl -fsSL "$index" 2>/dev/null | xz -dc 2>/dev/null | grep -c '^Package: indi-bin$')" = 0 ]; then
  echo "INDI's Ubuntu packages aren't built for ${PRETTY_NAME:-$codename}. Use Ubuntu 26.04 or newer." >&2
  exit 1
fi

# --- Questions, all up front, so the rest runs by itself ---
say "Setting up NOVA on ${PRETTY_NAME:-this computer} ($rid)"
if [ "$assume_yes" = no ] && [ -r /dev/tty ]; then
  echo "Questions first; then it installs without stopping (apart from sudo's password)."
fi

# Debian and Ubuntu get every INDI driver in one package; on Arch each maker's drivers are their own AUR package
# (built from source), so only the ones you need. Mounts like ZWO AM, iOptron, Pegasus and LX200 types are in INDI itself.
# key|AUR packages|what for
brand_table="zwo|libindi-asi-git|ZWO: ASI cameras, EFW filter wheels, EAF focusers, CAA rotators
qhy|libindi-qhy|QHY cameras and filter wheels
playerone|libindi-playerone|Player One cameras
touptek|libindi-toupcam|ToupTek cameras
svbony|libindi-svbony libindi-svbonycam|SVBONY cameras
dslr|libindi-gphoto|Canon, Nikon and other DSLR or mirrorless cameras
atik|libindi-atik|Atik cameras
moravian|libindi-mi|Moravian cameras
sx|libindi-sx|Starlight Xpress cameras and filter wheels
eqmod|libindi-eqmod|Sky-Watcher and Orion EQ mounts (EQMod)
celestron|libindi-celestronaux|Celestron mounts on the AUX port"
brands=""
if [ "$family" = arch ]; then
  if [ -n "$brands_choice" ]; then
    for key in ${brands_choice//,/ }; do
      grep -q "^$key|" <<<"$brand_table" || { echo "--brands: unknown maker \"$key\" (choose from: $(cut -d'|' -f1 <<<"$brand_table" | paste -sd' '))" >&2; exit 1; }
      brands="$brands $key"
    done
  else
    brands=" zwo"
    if [ "$assume_yes" = no ] && [ -r /dev/tty ]; then
      echo
      echo "Whose equipment do you have? (Drivers for ZWO AM, iOptron, Pegasus and LX200-type mounts come with INDI.)"
      n=0
      while IFS='|' read -r key packages what; do n=$((n + 1)); printf '  %2d) %s\n' "$n" "$what"; done <<<"$brand_table"
      read -r -p "Numbers separated by spaces [1] " picked </dev/tty || picked=""
      if [ -n "$picked" ]; then
        brands=""
        for number in ${picked//,/ }; do
          key="$(sed -n "${number}p" <<<"$brand_table" 2>/dev/null | cut -d'|' -f1)"
          [ -n "$key" ] && brands="$brands $key" || echo "  (ignoring \"$number\")"
        done
      fi
    fi
  fi
fi

astap_found=""
for candidate in /opt/astap/astap /opt/astap/astap_cli /usr/bin/astap /usr/local/bin/astap /usr/bin/astap_cli; do
  [ -x "$candidate" ] && { astap_found=$candidate; break; }
done
database=""
if [ -n "$database_choice" ]; then
  # Asked for by name: installed alongside any other (e.g. d50 after d20), unless it's already there.
  if compgen -G "/opt/astap/${database_choice}_*" >/dev/null; then echo "ASTAP's $database_choice star database is already installed."
  else database=$database_choice; fi
elif compgen -G '/opt/astap/*.1476' >/dev/null || compgen -G '/opt/astap/*.290' >/dev/null; then
  echo "ASTAP's star database is already installed (nova setup --database d50 adds another)."
else
  echo
  echo "Plate solving needs one of ASTAP's star databases (it's a large download):"
  echo "  1) d05   100 MB   wide fields (short lenses)"
  echo "  2) d20   400 MB   suits most telescopes (suggested)"
  echo "  3) d50   900 MB   long focal lengths and small fields"
  choice=2
  if [ "$assume_yes" = no ] && [ -r /dev/tty ]; then read -r -p "Which one? [2] " choice </dev/tty || choice=2; fi
  case "${choice:-2}" in 1|d05) database=d05 ;; 3|d50) database=d50 ;; *) database=d20 ;; esac
fi

echo
phd2=no
[ "$phd2_choice" != no ] && ask "Set up PHD2 for guiding? It runs without a screen, and you see it in NOVA's Guiding tab." y && phd2=yes

gsc=no
if [ "$family" != arch ]; then
  ask "Install the GSC star catalog? Only INDI's CCD Simulator uses it, to draw stars (a 200 MB download)." n && gsc=yes
fi

# While the repository is private, releases can only be downloaded by a signed-in GitHub CLI.
public=yes
[ "$build" = no ] && [ -z "$release_file" ] && ! curl -fsS -o /dev/null "https://api.github.com/repos/$REPO" 2>/dev/null && public=no

if [ "$build" = yes ] && [ ! -f "$HERE/publish.sh" ]; then
  echo "--build needs a copy of NOVA's source: git clone https://github.com/$REPO, then run its deploy/setup.sh." >&2
  exit 1
fi

echo
echo "About to install:"
if [ "$family" = arch ]; then
  echo "  INDI, with drivers for:"
  for key in $brands; do echo "    $(grep "^$key|" <<<"$brand_table" | cut -d'|' -f3)"; done
  echo "  Avahi (so <name>.local works), polkit, udisks2 (saving to USB drives), iw, ffmpeg and v4l-utils (the webcam view), hostapd (the Wi-Fi hotspot)"
else
  echo "  INDI with all its drivers, Avahi (so <name>.local works), polkit, udisks2 (saving to USB drives), iw, ffmpeg and v4l-utils (the webcam view), hostapd (the Wi-Fi hotspot)"
fi
[ -z "$astap_found" ] && echo "  ASTAP (plate solving)"
[ -n "$database" ] && echo "  ASTAP's $database star database"
[ "$phd2" = yes ] && echo "  PHD2 with a virtual screen (Xvfb, IceWM, x11vnc, noVNC)"
[ "$gsc" = yes ] && echo "  the GSC star catalog"
[ "$public" = no ] && echo "  the GitHub CLI, to download NOVA from its private repository"
if [ "$build" = yes ]; then echo "  build tools, then NOVA built from $(dirname "$HERE")"
elif [ -n "$release_file" ]; then echo "  NOVA from $(basename "$release_file")"
else echo "  the latest NOVA release"; fi
[ "$family" = arch ] && echo "Arch first updates the whole system (pacman -Syu), as it should before installing anything."
ask "Go ahead?" y || { echo "Nothing installed."; exit 0; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Downloads a SourceForge file of ASTAP's to $work.
astap_download() { curl -fL# --retry 3 -o "$work/$2" "https://sourceforge.net/projects/astap-program/files/$1/download"; }

# The command-line ASTAP: all NOVA uses, and it needs no desktop libraries.
install_astap_cli() {
  local zip=amd64
  [ "$rid" = linux-arm64 ] && zip=aarch64
  say "ASTAP"
  astap_download "linux_installer/astap_command-line_version_Linux_$zip.zip" astap_cli.zip
  (cd "$work" && unzip -o -q astap_cli.zip astap_cli)
  sudo install -d /opt/astap
  sudo install -m 755 "$work/astap_cli" /opt/astap/astap_cli
}

# --- Arch ---
setup_arch() {
  local aur=""
  for helper in yay paru; do has "$helper" && { aur=$helper; break; }; done
  if [ -z "$aur" ]; then
    say "AUR helper"
    echo "The ZWO drivers, ASTAP's star database and PHD2 come from the AUR, so this installs yay to fetch them."
    sudo pacman -S --needed --noconfirm base-devel git
    git clone --depth 1 https://aur.archlinux.org/yay-bin.git "$work/yay-bin"
    (cd "$work/yay-bin" && makepkg -si --noconfirm)
    aur=yay
  fi

  local packages="libindi avahi nss-mdns polkit udisks2 iw ffmpeg v4l-utils hostapd curl unzip"
  [ "$phd2" = yes ] && packages="$packages xorg-server-xvfb x11vnc icewm python-build"
  [ "$public" = no ] && packages="$packages github-cli"
  [ "$build" = yes ] && packages="$packages git dotnet-sdk nodejs npm"
  say "Packages"
  # shellcheck disable=SC2086
  sudo pacman -Syu --needed --noconfirm $packages

  # The command-line ASTAP from its SourceForge page, as on Debian: the AUR's astap builds the desktop program
  # from a source download that isn't always there (it was gone in October 2026), and NOVA needs only the CLI.
  [ -z "$astap_found" ] && install_astap_cli

  local aur_packages=""
  local key
  for key in $brands; do
    # Already there, perhaps as libindi-asi (libindi-asi-git: the plain package can be pinned to an older libindi).
    [ "$key" = zwo ] && has indi_asi_ccd && continue
    aur_packages="$aur_packages $(grep "^$key|" <<<"$brand_table" | cut -d'|' -f2)"
  done
  [ -n "$database" ] && aur_packages="$aur_packages $database-star-db-astap"
  [ "$phd2" = yes ] && aur_packages="$aur_packages phd2 novnc websockify"
  if [ -n "$aur_packages" ]; then
    say "AUR packages:$aur_packages"
    local confirm=""
    [ "$assume_yes" = yes ] && confirm="--noconfirm"
    # shellcheck disable=SC2086
    "$aur" -S --needed $confirm $aur_packages
  fi
}

# --- Debian, Raspberry Pi OS and Ubuntu ---
setup_apt() {
  say "Package lists"
  sudo apt-get update
  sudo apt-get install -y ca-certificates curl gpg unzip xz-utils

  if [ "$family" = debian ]; then
    if [ ! -f /etc/apt/sources.list.d/astroberry.sources ]; then
      say "Astroberry's repository (current INDI for Debian and Raspberry Pi OS)"
      sudo install -d -m 755 /etc/apt/keyrings
      curl -fsSL https://astroberry.io/debian/astroberry.asc | sudo gpg --dearmor --yes -o /etc/apt/keyrings/astroberry.gpg
      curl -fsSL https://astroberry.io/debian/astroberry.sources | sudo tee /etc/apt/sources.list.d/astroberry.sources >/dev/null
    fi
  else
    sudo apt-get install -y software-properties-common
    say "INDI's own repository for Ubuntu"
    sudo add-apt-repository -y universe
    sudo add-apt-repository -y ppa:mutlaqja/ppa
    if [ "$phd2" = yes ]; then
      say "PHD2's repository for Ubuntu"
      sudo add-apt-repository -y ppa:pch/phd2
    fi
  fi
  sudo apt-get update

  # The self-contained NOVA build still needs the system's ICU (text and number formats).
  local icu
  icu="$(apt-cache pkgnames libicu | grep -E '^libicu[0-9]+$' | sort -V | tail -1)"
  local packages="indi-bin libindi1 indi-3rdparty-drivers avahi-daemon libnss-mdns polkitd udisks2 iw ffmpeg v4l-utils hostapd tar $icu"
  [ "$phd2" = yes ] && packages="$packages phd2 xvfb x11vnc icewm novnc websockify"
  [ "$gsc" = yes ] && packages="$packages gsc"
  [ "$public" = no ] && packages="$packages gh"
  [ "$build" = yes ] && packages="$packages git nodejs npm"
  say "Packages"
  # shellcheck disable=SC2086
  sudo apt-get install -y $packages

  [ -z "$astap_found" ] && install_astap_cli
  if [ -n "$database" ]; then
    say "ASTAP's $database star database"
    astap_download "star_databases/${database}_star_database.deb" "$database.deb"
    sudo apt-get install -y "$work/$database.deb"
  fi
  return 0
}

if [ "$family" = arch ]; then setup_arch; else setup_apt; fi

say "Network names"
sudo systemctl enable --now avahi-daemon

# --- NOVA itself ---
if [ "$build" = yes ]; then
  if ! has dotnet || ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    echo "Building NOVA needs the .NET 10 SDK: https://learn.microsoft.com/dotnet/core/install/linux" >&2
    echo "Install it, then run: deploy/setup.sh --build (or skip --build to download a ready-made release)." >&2
    exit 1
  fi
  say "Building NOVA"
  "$HERE/publish.sh" "$rid"
  sudo "$HERE/install.sh" "$(dirname "$HERE")/out/$rid"
else
  if [ "$public" = no ] && ! gh auth status >/dev/null 2>&1; then
    say "Sign in to GitHub (NOVA's repository is private for now)"
    gh auth login
  fi
  updater="$HERE/update.sh"
  if [ ! -f "$updater" ]; then
    updater="$work/update.sh"
    curl -fsSL -o "$updater" "https://raw.githubusercontent.com/$REPO/main/deploy/update.sh"
  fi
  say "NOVA"
  bash "$updater" ${release_file:+--file "$release_file"}
fi

if [ -f "$HERE/doctor.sh" ]; then
  say "Checking everything"
  bash "$HERE/doctor.sh" || true
fi
if [ "$family" = arch ] && [ ! -d /usr/share/GSC ]; then
  echo
  echo "INDI's CCD Simulator draws no stars without the GSC catalog (real cameras don't need it). On Arch it's"
  echo "built by hand: see \"GSC by hand\" in docs/INSTALL.md."
fi
say "Done. Open http://$(hostname).local:5000 from any computer on this network."
echo "Choose your equipment's drivers in NOVA: Equipment → Drivers…"
echo "From now on: nova update (newest release), nova doctor (checks everything), nova setup (adds drivers), nova help"
[ "$phd2" = yes ] && echo "PHD2 needs one-time setup on its screen (NOVA's Guiding tab): its equipment, and Tools → Enable Server."
exit 0
