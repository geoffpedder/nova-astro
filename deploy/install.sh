#!/usr/bin/env bash
# Installs (or updates) a published build as systemd services on the mini PC.
# Usage: sudo deploy/install.sh out/linux-x64 [user]
# The user (default: whoever ran sudo) owns the services and gets USB/serial access.
set -euo pipefail
SRC="${1:?usage: sudo deploy/install.sh <publish dir> [user]}"
NOVA_USER="${2:-${SUDO_USER:-$USER}}"
HERE="$(cd "$(dirname "$0")" && pwd)"

if [ ! -x "$SRC/Nova.Server" ] && [ ! -f "$SRC/Nova.Server" ]; then
  echo "No Nova.Server in $SRC. Run deploy/publish.sh first." >&2
  exit 1
fi
if ! command -v indiserver >/dev/null; then
  echo "indiserver not found. Install INDI first: setup.sh does it (or on Arch: sudo pacman -S libindi)." >&2
  exit 1
fi

# A running executable can't be overwritten ("Text file busy"), so stop it for updates.
if systemctl is-active --quiet nova.service; then
  systemctl stop nova.service
fi

install -d /opt/nova
# A release carries a VERSION file; a build made here doesn't, so don't leave an older release's behind.
rm -f /opt/nova/VERSION
cp -r "$SRC"/. /opt/nova/
chmod +x /opt/nova/Nova.Server
install -m 755 "$HERE/indiserver-start.sh" /opt/nova/indiserver-start.sh
install -m 755 "$HERE/nova-screen.sh" /opt/nova/nova-screen.sh
install -m 644 "$HERE/openbox-rc.xml" /opt/nova/openbox-rc.xml
install -m 755 "$HERE/nova-wm.sh" /opt/nova/nova-wm.sh
# The Wi-Fi hotspot's root helper (run by nova-hotspot.service only), root-owned so NOVA can't change it.
install -m 755 "$HERE/nova-hotspot.sh" /opt/nova/nova-hotspot.sh
install -d /opt/nova/icewm
install -m 644 "$HERE"/icewm/* /opt/nova/icewm/
# The maintenance scripts and the "nova" command (nova update, nova doctor, ...), so they work without a copy of the
# source. install(1) replaces files rather than writing into them, so a running "nova update" isn't disturbed.
install -d /opt/nova/scripts
for script in update.sh doctor.sh setup.sh; do
  install -m 755 "$HERE/$script" "/opt/nova/scripts/$script"
done
install -D -m 755 "$HERE/nova" /usr/local/bin/nova

# Config lives outside /opt/nova so updates never overwrite it.
install -d /etc/nova
[ -f /etc/nova/indi.env ] || install -m 644 "$HERE/indi.env" /etc/nova/indi.env
# NOVA saves the driver choice here (Equipment → Drivers…), so it owns this one file.
chown "$NOVA_USER" /etc/nova/indi.env
[ -f /etc/nova/nova.env ] || install -m 644 "$HERE/nova.env" /etc/nova/nova.env
# The hotspot's name and password, which NOVA writes (set on its page) and the hotspot's helper reads: NOVA's alone.
[ -f /etc/nova/hotspot.conf ] || install -m 600 /dev/null /etc/nova/hotspot.conf
chown "$NOVA_USER" /etc/nova/hotspot.conf
chmod 600 /etc/nova/hotspot.conf

for unit in indiserver nova nova-display nova-wm phd2 nova-screen nova-hotspot; do
  sed "s/NOVA_USER/$NOVA_USER/" "$HERE/$unit.service" > "/etc/systemd/system/$unit.service"
done

# Serial mounts and focusers need the serial group (uucp on Arch, dialout on Debian/Ubuntu); a webcam (the Webcam
# view) needs video.
for group in uucp dialout video; do
  if getent group "$group" >/dev/null; then
    usermod -aG "$group" "$NOVA_USER"
  fi
done

# Let NOVA restart and shut down the mini PC from its web page, and mount and eject USB drives to save frames to
# (and nothing more), through polkit.
if [ -d /etc/polkit-1/rules.d ]; then
  sed "s/NOVA_USER/$NOVA_USER/" "$HERE/nova-power.rules" > /etc/polkit-1/rules.d/49-nova-power.rules
  chmod 644 /etc/polkit-1/rules.d/49-nova-power.rules
  sed "s/NOVA_USER/$NOVA_USER/" "$HERE/nova-storage.rules" > /etc/polkit-1/rules.d/49-nova-storage.rules
  chmod 644 /etc/polkit-1/rules.d/49-nova-storage.rules
  # And start and stop its Wi-Fi hotspot (nova-hotspot.service), no other service.
  sed "s/NOVA_USER/$NOVA_USER/" "$HERE/nova-hotspot.rules" > /etc/polkit-1/rules.d/49-nova-hotspot.rules
  chmod 644 /etc/polkit-1/rules.d/49-nova-hotspot.rules
else
  echo "polkit isn't installed, so NOVA can't restart or shut down the mini PC. To allow it, install polkit (Arch: polkit, Debian/Ubuntu: polkitd), then run this again."
fi

systemctl daemon-reload
systemctl enable indiserver.service nova.service
systemctl restart indiserver.service nova.service

# Headless PHD2 (optional): runs on a virtual display, viewable in the browser on port 6080.
if command -v phd2 >/dev/null && command -v Xvfb >/dev/null; then
  systemctl enable nova-display.service
  systemctl restart nova-display.service
  if command -v icewm >/dev/null || command -v openbox >/dev/null; then
    systemctl enable nova-wm.service
    systemctl restart nova-wm.service
    command -v icewm >/dev/null || echo "PHD2's screen uses Openbox: install icewm (nova setup does) for a taskbar of PHD2's windows."
  else
    echo "Without a window manager PHD2 won't fill its screen and its windows can't be moved. Fix: install icewm, then run this installer again."
  fi
  systemctl enable phd2.service
  systemctl restart phd2.service
  if command -v x11vnc >/dev/null && command -v websockify >/dev/null; then
    systemctl enable nova-screen.service
    systemctl restart nova-screen.service
    echo "PHD2 screen: http://$(hostname).local:6080/vnc.html"
  else
    echo "For PHD2's screen in the browser: nova setup (or on Arch: sudo pacman -S x11vnc && yay -S novnc websockify)"
  fi
else
  echo "Headless PHD2 not set up. nova setup sets it up (on Arch, by hand:"
  echo "sudo pacman -S xorg-server-xvfb x11vnc icewm && yay -S phd2 novnc websockify, then run this installer again)."
fi

# A firewall that blocks incoming connections (Omarchy turns ufw on) keeps browsers out: let NOVA's ports in.
# 5000 the web page, 10001 Stellarium's telescope link, 5353/udp mDNS so <name>.local resolves.
if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q '^Status: active'; then
  ufw allow 5000/tcp comment 'NOVA' >/dev/null
  ufw allow 10001/tcp comment 'NOVA: Stellarium telescope link' >/dev/null
  ufw allow 5353/udp comment 'mDNS: .local names' >/dev/null
  ufw allow 67/udp comment 'NOVA hotspot: addresses for phones (DHCP)' >/dev/null
  echo "Firewall (ufw): ports 5000 and 10001 (TCP), 5353 (UDP, for .local names) and 67 (UDP, the hotspot's addresses) are open"
elif command -v firewall-cmd >/dev/null && firewall-cmd --state >/dev/null 2>&1; then
  firewall-cmd --quiet --permanent --add-port=5000/tcp --add-port=10001/tcp --add-service=mdns --add-service=dhcp
  firewall-cmd --quiet --reload
  echo "Firewall (firewalld): ports 5000 and 10001 (TCP) and mDNS (for .local names) are open"
fi

echo "NOVA $(cat /opt/nova/VERSION 2>/dev/null || echo '(built here)') running: http://$(hostname).local:5000"
echo "Choose drivers in NOVA (Equipment → Drivers…), or edit /etc/nova/indi.env and: nova restart"
echo "Later: nova update (newest release), nova doctor (checks everything), nova help"
