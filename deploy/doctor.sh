#!/usr/bin/env bash
# Checks that everything NOVA uses on the mini PC is installed and running, and says how to fix what isn't.
#   nova doctor   (or deploy/doctor.sh from a copy of the source)
# Safe to run any time (it only looks); handy after an Arch/AUR update. Exit code = number of problems.

set -u
problems=0
warnings=0
if [ -t 1 ]; then ok=$'\e[32m✓\e[0m'; bad=$'\e[31m✗\e[0m'; warn=$'\e[33m!\e[0m'; else ok='✓'; bad='✗'; warn='!'; fi

has() { command -v "$1" >/dev/null 2>&1; }
# How to put things right: through the installed "nova" command, or the scripts in a copy of the source.
here="$(cd "$(dirname "$0")" && pwd)"
if [ "$here" = /opt/nova/scripts ] || [ ! -f "$here/install.sh" ]; then setup="nova setup"; else setup="deploy/setup.sh"; fi
if [ -f /opt/nova/VERSION ] || [ "$setup" = "nova setup" ]; then reinstall="nova update --force"
else reinstall="sudo deploy/install.sh out/$( [ "$(uname -m)" = x86_64 ] && echo linux-x64 || echo linux-arm64 )"; fi
# The fixes below are written for Arch; elsewhere setup installs whatever is missing.
hint() {
  if ! has pacman && [[ "$1" == *pacman* || "$1" == *yay* || "$1" == *AUR* ]]; then echo "$setup   (installs what's missing)"
  else echo "$1"; fi
}
pass() { echo "  $ok $1"; }
fail() { echo "  $bad $1"; [ -n "${2:-}" ] && echo "      fix: $(hint "$2")"; problems=$((problems + 1)); }
note() { echo "  $warn $1"; [ -n "${2:-}" ] && echo "      $(hint "$2")"; warnings=$((warnings + 1)); }
section() { echo; echo "$1"; }

# A release (deploy/update.sh) comes ready-made; only a build made here needs the build tools.
if [ -f /opt/nova/VERSION ]; then
  section "NOVA"
  pass "release $(cat /opt/nova/VERSION) (update: nova update)"
else
  section "Building NOVA (deploy/publish.sh)"
  for tool in git dotnet node npm; do
    if has "$tool"; then pass "$tool $("$tool" --version 2>/dev/null | head -1)"
    else fail "$tool missing" "sudo pacman -S --needed git dotnet-sdk nodejs npm"; fi
  done
  if has dotnet && ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    fail ".NET 10 SDK not found ($(dotnet --list-sdks 2>/dev/null | tr '\n' ' '))" "sudo pacman -S dotnet-sdk"
  fi
fi

section "INDI"
if has indiserver; then pass "indiserver ($(pacman -Q libindi 2>/dev/null || dpkg-query -W -f '${Package} ${Version}' indi-bin 2>/dev/null || echo 'libindi'))"
else fail "indiserver missing" "sudo pacman -S libindi"; fi
drivers="indi_lx200am5 indi_asi_ccd indi_asi_wheel indi_asi_focuser indi_asi_rotator"
if [ -r /etc/nova/indi.env ]; then
  configured=$(. /etc/nova/indi.env 2>/dev/null; echo "${INDI_DRIVERS:-}")
  [ -n "$configured" ] && drivers=$configured
fi
# The AUR package with a third-party driver (the same list as PackageFor in DriverSetupService.cs).
aur_package() {
  case "$1" in
    indi_qhy*) echo libindi-qhy ;;
    indi_playerone*|indi_pomodoro*) echo libindi-playerone ;;
    indi_svbony*|indi_sv305*) echo libindi-svbony ;;
    indi_eqmod*|indi_azgti*|indi_staradventurer*|indi_skywatcherAltAz*) echo libindi-eqmod ;;
    indi_gphoto*|indi_canon*|indi_nikon*|indi_sony*|indi_pentax_ccd*) echo libindi-gphoto ;;
    indi_toupcam*) echo libindi-toupcam ;;
    indi_altair*) echo libindi-altaircam ;;
    indi_ogmacam*) echo libindi-ogmacam ;;
    indi_omegonpro*) echo libindi-omegonprocam ;;
    indi_starshootg*) echo libindi-starshootg ;;
    indi_tscam*) echo libindi-tscam ;;
    indi_mallincam*) echo libindi-mallincam ;;
    indi_nncam*) echo libindi-nncam ;;
    indi_atik*) echo libindi-atik ;;
    indi_mi_*) echo libindi-mi ;;
    indi_sx*) echo libindi-sx ;;
    indi_fli*) echo libindi-fli ;;
    indi_sbig*) echo libindi-sbig ;;
    indi_celestron_aux*) echo libindi-celestronaux ;;
  esac
}
for driver in $drivers; do
  if has "$driver"; then pass "$driver"
  elif [ "$driver" = indi_lx200am5 ]; then fail "$driver missing (ZWO AM3/AM5 mount)" "sudo pacman -S libindi"
  elif [[ "$driver" == indi_asi_* ]]; then
    if [ -z "${asi_hint:-}" ]; then
      asi_hint=1
      fail "$driver missing (ZWO camera/EFW/EAF/CAA)" \
        "yay -S libindi-asi-git   (the plain libindi-asi package can be pinned to an older libindi and refuse to install)"
    else fail "$driver missing (same package)"; fi
  elif [[ "$driver" == indi_simulator_* ]]; then fail "$driver missing" "sudo pacman -S libindi"
  elif package=$(aur_package "$driver") && [ -n "$package" ]; then
    # Debian, Raspberry Pi OS and Ubuntu have every third-party driver in one package.
    if has pacman; then fix="sudo pacman -Syu && yay -S $package"; else fix="sudo apt install indi-3rdparty-drivers"; fi
    fail "$driver missing (chosen in Equipment → Drivers…)" "$fix   (or untick it there)"
  else fail "$driver missing (chosen in Equipment → Drivers…)" "install its driver, or untick it there"; fi
done
nova_user=$(systemctl show -p User --value nova 2>/dev/null)
if [ -f /etc/nova/indi.env ] && [ -n "$nova_user" ]; then
  if [ "$(stat -c %U /etc/nova/indi.env)" = "$nova_user" ]; then pass "NOVA can save the driver choice (Equipment → Drivers…)"
  else note "NOVA can't save /etc/nova/indi.env (Equipment → Drivers… will only show the list)" "sudo chown $nova_user /etc/nova/indi.env   (or: $reinstall)"; fi
fi
if pacman -Q libindi-asi-git >/dev/null 2>&1 || pacman -Q libindi-asi >/dev/null 2>&1; then
  pass "ZWO drivers package: $(pacman -Q libindi-asi-git 2>/dev/null || pacman -Q libindi-asi)"
fi

section "GSC star catalog (stars for INDI's CCD Simulator; optional with real cameras)"
# Arch's hand-built package puts the program in /usr/share/GSC/bin, Debian's and Ubuntu's in /usr/bin.
if { [ -x /usr/share/GSC/bin/gsc ] || has gsc; } && ls -d /usr/share/GSC/N* >/dev/null 2>&1; then pass "GSC in /usr/share/GSC"
elif ! has pacman; then
  note "GSC not installed: the INDI CCD Simulator shows no stars (real cameras don't need it)" "sudo apt install gsc"
else
  note "GSC not installed: the INDI CCD Simulator shows no stars (real cameras don't need it)" \
    "The AUR gsc package needs two tweaks: download GSC_1.2.tar.gz yourself and point the PKGBUILD's source at it,
      and build with:  make CC=\"gcc -std=gnu89 -fpermissive -I.\"   then:  makepkg -Cfsi"
fi

section "Plate solving (ASTAP)"
astap=""
for candidate in /opt/astap/astap /usr/bin/astap /usr/local/bin/astap /opt/astap/astap_cli /usr/bin/astap_cli; do
  [ -x "$candidate" ] && { astap=$candidate; break; }
done
if [ -n "$astap" ]; then pass "ASTAP: $astap"; else fail "ASTAP missing" "$setup   (installs the command-line ASTAP in /opt/astap)"; fi
databases=$(ls /opt/astap/*.1476 /opt/astap/*.290 2>/dev/null | wc -l)
if [ "$databases" -gt 0 ]; then
  pass "star database: $(ls /opt/astap/*.1476 /opt/astap/*.290 2>/dev/null | head -1 | xargs -r basename | cut -c1-3) ($databases files)"
else fail "no ASTAP star database in /opt/astap" "yay -S d20-star-db-astap   (or d05 / d50 for other fields of view)"; fi

section "Headless PHD2 (guiding, its screen at :6080)"
# Optional: without PHD2 itself there's nothing to check, and its services and ports are left out below.
phd2=yes
has phd2 || phd2=no
if [ "$phd2" = no ]; then
  note "PHD2 isn't installed (optional: NOVA guides through PHD2)" "$setup   (answer yes to PHD2)"
fi
for tool in phd2 Xvfb x11vnc icewm websockify; do
  [ "$phd2" = no ] && break
  if has "$tool"; then pass "$tool"
  else
    case $tool in
      phd2) fail "phd2 missing" "yay -S phd2" ;;
      Xvfb) fail "Xvfb missing" "sudo pacman -S xorg-server-xvfb" ;;
      icewm) if has openbox; then note "PHD2's screen uses Openbox: IceWM adds a taskbar of PHD2's windows" "sudo pacman -S icewm   (Debian/Ubuntu: sudo apt install icewm), then $reinstall"
             else fail "no window manager for PHD2's screen" "sudo pacman -S icewm   (Debian/Ubuntu: sudo apt install icewm), then $reinstall"; fi ;;
      websockify) fail "websockify missing" "sudo pacman -S python-build && yay -S websockify   (it needs python-build to build)" ;;
      *) fail "$tool missing" "sudo pacman -S $tool" ;;
    esac
  fi
done
novnc=""
for dir in /usr/share/webapps/novnc /usr/share/novnc /usr/share/noVNC; do [ -f "$dir/vnc.html" ] && { novnc=$dir; break; }; done
if [ -n "$novnc" ]; then pass "noVNC: $novnc"; elif [ "$phd2" = yes ]; then fail "noVNC missing" "yay -S novnc"; fi

section "Wi-Fi details in the header (optional)"
if has nmcli || has iwctl || has iw; then pass "Wi-Fi tools: $(for t in nmcli iwctl iw; do has $t && printf '%s ' $t; done)"
else note "no Wi-Fi tool found: the header can show addresses but not the network name" "sudo pacman -S --needed iw"; fi
has iw || note "iw isn't installed: NOVA can't tell when the mini PC runs a hotspot" "sudo pacman -S --needed iw"

section "Wi-Fi hotspot (optional: when there's no Wi-Fi to join)"
wifi=""
for dev in /sys/class/net/*; do [ -d "$dev/wireless" ] && { wifi=$(basename "$dev"); break; }; done
if [ -z "$wifi" ]; then note "no Wi-Fi adapter: no hotspot (a cable still works)"
else
  if systemctl is-active --quiet NetworkManager 2>/dev/null; then how="NetworkManager"
  elif systemctl is-active --quiet iwd 2>/dev/null; then how="iwd"
  elif systemctl is-active --quiet systemd-networkd 2>/dev/null; then how="systemd-networkd + hostapd"
  else how=""; fi
  if [ -z "$how" ]; then note "the Wi-Fi isn't run by NetworkManager, iwd or systemd-networkd: NOVA can't start a hotspot"
  else
    pass "$wifi, run by $how"
    if [ "$how" = "systemd-networkd + hostapd" ] && ! has hostapd && [ ! -x /usr/sbin/hostapd ]; then
      note "hostapd isn't installed: the hotspot can't start" "$setup   (or: sudo apt install hostapd, sudo pacman -S hostapd)"
    fi
    if has iw && ! iw list 2>/dev/null | sed -n '/Supported interface modes/,/:$/p' | grep -q '\* AP$'; then
      note "this Wi-Fi adapter doesn't say it can be a hotspot (AP mode): it may not work"
    fi
  fi
  if [ ! -f /etc/systemd/system/nova-hotspot.service ] || [ ! -x /opt/nova/nova-hotspot.sh ]; then
    note "the hotspot isn't installed yet" "$reinstall"
  # (Whether polkit lets NOVA start it can't be checked from here: polkit takes a check naming the unit only from root.
  # NOVA says so if starting it is refused.)
  elif [ -r /etc/nova/hotspot.conf ] && ! grep -q '^PASSPHRASE=.\{8,\}' /etc/nova/hotspot.conf; then
    note "no hotspot password set: set one in NOVA (click the network name in the header)"
  else
    systemctl is-active --quiet nova-hotspot.service && pass "hotspot on now (10.42.0.1)" || pass "ready (starts when no Wi-Fi is found)"
  fi
fi

section "Bluetooth (optional: ZWO mounts without their USB cable)"
if ! has bluetoothctl && [ ! -x /usr/lib/bluetooth/bluetoothd ] && [ ! -x /usr/libexec/bluetooth/bluetoothd ]; then
  note "BlueZ isn't installed: the mount can only use its USB cable" "$setup   (or: sudo apt install bluez python3-bleak, sudo pacman -S bluez bluez-utils python-bleak)"
else
  if systemctl is-active --quiet bluetooth 2>/dev/null; then pass "bluetooth service running"
  else note "the bluetooth service isn't running" "sudo systemctl enable --now bluetooth"; fi
  if ls /sys/class/bluetooth/hci* >/dev/null 2>&1; then pass "Bluetooth adapter: $(basename "$(ls -d /sys/class/bluetooth/hci* | head -1)")"
  else note "no Bluetooth adapter found (a USB Bluetooth dongle works)"; fi
  if python3 -c 'import bleak' 2>/dev/null; then pass "Python's Bleak library"
  else note "Python's Bleak library isn't installed: NOVA can't talk to the mount over Bluetooth" "sudo apt install python3-bleak   (or: sudo pacman -S python-bleak)"; fi
  [ -f /opt/nova/nova-mount-ble.py ] || note "NOVA's Bluetooth helper isn't installed" "$reinstall"
fi

section "Terminal (optional: the Activity tab's Mini PC box)"
if has ttyd; then pass "ttyd $(ttyd --version 2>/dev/null | awk '{print $NF}')"
else note "ttyd isn't installed: no terminal in the Activity tab" "$setup   (or: sudo apt install ttyd, sudo pacman -S ttyd)"; fi
# The password file is NOVA's; a password is what lets the terminal open.
if [ -f "${XDG_CONFIG_HOME:-$HOME/.config}/nova/auth.json" ]; then pass "a NOVA password is set (the terminal needs one)"
else note "no NOVA password: the terminal stays closed until one is set (⏻ → Set a password…, or: nova password)"; fi

section "Network names (optional: http://<minipc>.local:5000)"
if systemctl is-active --quiet avahi-daemon 2>/dev/null; then
  pass "avahi-daemon running"
  name="$(busctl --system call org.freedesktop.Avahi / org.freedesktop.Avahi.Server GetHostName 2>/dev/null | sed -E 's/^s "(.*)"$/\1/')"
  if [ -n "$name" ] && [ "$name" != "$(hostname -s)" ]; then
    note "Avahi is answering to $name.local, not $(hostname -s).local (it thought another computer had the name)" "sudo systemctl restart avahi-daemon"
  elif [ -n "$name" ]; then pass "answers to $name.local"; fi
  systemctl is-enabled --quiet nova-mdns.timer 2>/dev/null || note "the check that keeps $(hostname -s).local isn't installed" "$reinstall"
else note "avahi-daemon not running: use the IP address, or" "sudo pacman -S avahi nss-mdns && sudo systemctl enable --now avahi-daemon"; fi

section "Firewall"
# Its rules are root-only, so this can only say that one is on; NOVA's installer opens its ports in it.
if systemctl is-active --quiet ufw 2>/dev/null; then pass "ufw is on: the install opens ports 5000, 10001 and 5353/udp (check: sudo ufw status)"
elif systemctl is-active --quiet firewalld 2>/dev/null; then pass "firewalld is on: the install opens ports 5000, 10001 and mDNS (check: sudo firewall-cmd --list-all)"
else pass "no firewall running"; fi

section "Restart / shut down from the web page"
# The rules folder is root-only, so ask polkit itself (as this user, normally the one NOVA runs as).
if ! has pkcheck; then note "polkit isn't installed: NOVA can't restart or shut down the mini PC" "sudo pacman -S polkit && $reinstall"
elif pkcheck --action-id org.freedesktop.login1.power-off --process $$ >/dev/null 2>&1; then pass "allowed (polkit)"
elif ! systemctl is-active --quiet polkit 2>/dev/null; then note "polkit isn't running, so it can't say whether NOVA may restart the mini PC" "sudo systemctl start polkit   (journalctl -u polkit if it won't start)"
else note "NOVA isn't allowed to restart or shut down the mini PC yet" "$reinstall   (check: sudo ls -l /etc/polkit-1/rules.d/)"; fi

section "USB drives (saving frames to one)"
if ! has udisksctl; then note "udisks2 isn't installed: frames can only be saved on the mini PC" "$setup   (or: sudo pacman -S udisks2, sudo apt install udisks2)"
elif pkcheck --action-id org.freedesktop.udisks2.filesystem-mount-other-seat --process $$ >/dev/null 2>&1; then pass "NOVA may mount and eject USB drives (udisks2)"
else note "NOVA isn't allowed to mount USB drives yet" "$reinstall"; fi
if has lsblk; then
  usb=$(lsblk -dn -o TRAN,MODEL 2>/dev/null | awk '$1=="usb"{ $1=""; print substr($0,2) }' | paste -sd, -)
  [ -n "$usb" ] && pass "plugged in: $usb"
fi

section "Webcam view (optional)"
if ! has ffmpeg; then note "ffmpeg isn't installed: the Webcam tab can't show a USB webcam" "$setup   (or: sudo pacman -S ffmpeg, sudo apt install ffmpeg)"
else pass "ffmpeg $(ffmpeg -version 2>/dev/null | head -1 | awk '{print $3}')"; fi
if ! has v4l2-ctl; then note "v4l-utils isn't installed: the webcam's controls (brightness, exposure…) can't be shown" "$setup   (or: sudo pacman -S v4l-utils, sudo apt install v4l-utils)"
else pass "v4l2-ctl (the webcam's controls)"; fi
cams=$(for d in /sys/class/video4linux/video*; do
  [ -f "$d/index" ] && [ "$(cat "$d/index")" = 0 ] && cat "$d/name"
done 2>/dev/null | paste -sd, -)
if [ -n "$cams" ]; then
  pass "webcam plugged in: $cams"
  if id -nG | grep -qw video; then pass "allowed to use it (video group)"
  else note "this user isn't in the video group, so NOVA can't open the webcam" "$reinstall"; fi
fi

section "Clock"
# Altitudes, the Sun and meridian flips are worked out in UTC, so a wrong time zone only makes NOVA's
# times read oddly; a wrong clock puts everything in the wrong place.
if has timedatectl; then
  zone=$(timedatectl show -p Timezone --value 2>/dev/null)
  if [ -z "$zone" ] || [[ "$zone" == UTC || "$zone" == Etc/UTC || "$zone" == Etc/Universal ]]; then
    note "time zone is UTC: NOVA's times (logs, sequences, \"start after\") show UTC, not your local time" \
      "sudo timedatectl set-timezone Europe/London   (yours: timedatectl list-timezones), then: sudo systemctl restart nova"
  else pass "time zone $zone ($(date +%H:%M) now)"; fi
  if [ "$(timedatectl show -p NTPSynchronized --value 2>/dev/null)" = yes ]; then pass "clock synced from the internet ($(date -u '+%H:%M UTC'))"
  elif [ "$(timedatectl show -p NTP --value 2>/dev/null)" != yes ]; then
    note "clock isn't synced from the internet (it says $(date -u '+%Y-%m-%d %H:%M UTC'))" "sudo timedatectl set-ntp true"
  elif has chronyc; then
    note "clock not synced yet (it says $(date -u '+%Y-%m-%d %H:%M UTC')): fine for a few minutes after a boot, or on the hotspot" \
      "chronyc -N sources   (no server marked *: the network may block Ubuntu's secure time servers; $setup adds a fallback)"
  else
    note "clock not synced yet (it says $(date -u '+%Y-%m-%d %H:%M UTC')): fine for a few minutes after a boot, or on the hotspot" \
      "timedatectl timesync-status"
  fi
fi

section "Password"
nova_home=$(getent passwd "${nova_user:-$(id -un)}" | cut -d: -f6)
if [ -f "${Nova__ConfigDirectory:-${nova_home:-$HOME}/.config/nova}/auth.json" ]; then pass "set: browsers sign in to use NOVA"
else note "no password: anyone on your network can use NOVA and move the telescope" "nova password   (or ⏻ → Set a password… in NOVA)"; fi

section "Logs (spotting failing USB devices, the diagnostics zip)"
if [ -n "${nova_user:-}" ] && id -nG "$nova_user" 2>/dev/null | grep -qwE "systemd-journal|adm|wheel"; then
  pass "NOVA can read the kernel and service logs"
else note "NOVA can't read the kernel log: it won't spot USB devices being reset, and the diagnostics zip has no logs" "$reinstall"; fi

section "Services"
units="indiserver nova"
[ "$phd2" = yes ] && units="$units nova-display nova-wm phd2 nova-screen"
for unit in $units; do
  if ! systemctl cat "$unit" >/dev/null 2>&1; then fail "$unit.service not installed" "$reinstall"; continue; fi
  state=$(systemctl is-active "$unit" 2>/dev/null)
  enabled=$(systemctl is-enabled "$unit" 2>/dev/null)
  if [ "$state" = active ]; then pass "$unit ($enabled)"
  else fail "$unit is $state ($enabled)" "journalctl -u $unit -n 30   then: sudo systemctl restart $unit"; fi
done

section "Listening ports"
# Something answers on the port (bash's /dev/tcp, so no ss or netstat needed).
listens() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }
ports="5000:NOVA web|7624:INDI|10001:Stellarium telescope link"
[ "$phd2" = yes ] && ports="$ports|4400:PHD2 event server|6080:PHD2 screen (noVNC)"
IFS='|' read -ra entries <<<"$ports"
for entry in "${entries[@]}"; do
  port=${entry%%:*}; what=${entry#*:}
  if listens "$port"; then pass "$port $what"
  elif [ "$port" = 4400 ]; then note "$port $what not listening" "In PHD2 (http://<minipc>:6080): Tools > Enable Server"
  elif [ "$port" = 10001 ]; then note "$port $what not listening" "off if Nova__StellariumPort=0 in /etc/nova/nova.env"
  else fail "$port $what not listening" "see the service checks above"; fi
done

echo
if [ "$problems" -eq 0 ] && [ "$warnings" -eq 0 ]; then echo "$ok All good."
elif [ "$problems" -eq 0 ]; then echo "$ok All good ($warnings note(s) above)."
else echo "$bad $problems problem(s), $warnings note(s). Fix the ✗ lines above, then run this again."; fi
exit "$problems"
