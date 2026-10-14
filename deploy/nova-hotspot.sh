#!/usr/bin/env bash
# The mini PC's own Wi-Fi hotspot, for when there's no Wi-Fi to join (a dark site): run as root by
# nova-hotspot.service, which NOVA starts and stops (a polkit rule lets it, and nothing else).
#   nova-hotspot.sh run       bring the hotspot up and keep it up (the service's ExecStart)
#   nova-hotspot.sh restore   back to joining Wi-Fi (ExecStopPost: also after a failure)
#   nova-hotspot.sh method    which way this machine does it: networkd, networkmanager, iwd or none
# --dry-run (first) prints what would be done instead of doing it.
# The name and password come from /etc/nova/hotspot.conf (NOVA writes it): read as data, checked again here.
# Whatever manages the Wi-Fi is used: NetworkManager, iwd, or systemd-networkd + wpa_supplicant (netplan, e.g.
# Ubuntu Server) with hostapd and networkd's own DHCP server. The hotspot is always 10.42.0.1.
set -euo pipefail

DRY=0
if [ "${1:-}" = --dry-run ]; then DRY=1; shift; fi
ACTION="${1:-}"
CONF="${NOVA_HOTSPOT_CONF:-/etc/nova/hotspot.conf}"
RUN_DIR="${NOVA_HOTSPOT_RUN:-/run/nova}"
NETWORK_FILE="${NOVA_HOTSPOT_NETWORK:-/run/systemd/network/05-nova-hotspot.network}"
SYS_NET="${NOVA_SYS_NET:-/sys/class/net}"
ADDRESS=10.42.0.1

say() { echo "nova-hotspot: $*"; }
do_() { if [ "$DRY" = 1 ]; then echo "+ $*"; else "$@"; fi; }
write() { # write <file> [mode] (contents on stdin); root's alone unless a mode is given
  if [ "$DRY" = 1 ]; then echo "+ write $1 (${2:-600}):"; sed 's/^/    /'; else install -d "$(dirname "$1")"; cat > "$1"; chmod "${2:-600}" "$1"; fi
}
active() {
  if [ -n "${NOVA_HOTSPOT_ACTIVE:-}" ]; then [[ " $NOVA_HOTSPOT_ACTIVE " == *" $1 "* ]]; return; fi
  systemctl is-active --quiet "$1" 2>/dev/null
}

# Avahi (<name>.local) can take its own announcement on the changed address for another computer's and rename itself
# "<name>-2". Restart it once the address has settled, from a timer outside this service (which may be stopping).
refresh_names() {
  if active avahi-daemon; then
    do_ systemd-run --quiet --collect --on-active=15 systemctl try-restart avahi-daemon.service || true
  fi
}

method() {
  if active NetworkManager; then echo networkmanager
  elif active iwd; then echo iwd
  elif active systemd-networkd && command -v hostapd >/dev/null 2>&1; then echo networkd
  else echo none; fi
}

wifi_interface() {
  local dev
  for dev in "$SYS_NET"/*; do
    [ -d "$dev/wireless" ] && { basename "$dev"; return; }
  done
  return 1
}

# The name and password: KEY=value lines, never run as a script.
read_conf() {
  SSID="" PASSPHRASE=""
  [ -f "$CONF" ] || { say "no settings in $CONF: set the hotspot's name and password in NOVA"; exit 2; }
  local line
  while IFS= read -r line || [ -n "$line" ]; do
    case "$line" in
      SSID=*) SSID="${line#SSID=}" ;;
      PASSPHRASE=*) PASSPHRASE="${line#PASSPHRASE=}" ;;
    esac
  done < "$CONF"
  if [ -z "$SSID" ] || [ "${#SSID}" -gt 32 ]; then say "the hotspot's name must be 1 to 32 characters"; exit 2; fi
  if [ "${#PASSPHRASE}" -lt 8 ] || [ "${#PASSPHRASE}" -gt 63 ] || [[ ! "$PASSPHRASE" =~ ^[[:print:]]+$ ]]; then
    say "the hotspot's password must be 8 to 63 ordinary characters"; exit 2
  fi
  [[ "$SSID" =~ ^[[:print:]]+$ ]] || { say "the hotspot's name has characters it can't use"; exit 2; }
}

# iwd names a profile after its network: plain if simple, otherwise "=" and the name in hex.
iwd_profile_name() {
  if [[ "$1" =~ ^[A-Za-z0-9_\ -]+$ ]]; then echo "$1"; else echo "=$(printf '%s' "$1" | od -An -tx1 | tr -d ' \n')"; fi
}

run() {
  read_conf
  local dev; dev=$(wifi_interface) || { say "no Wi-Fi adapter found"; exit 3; }
  local how; how=$(method)
  say "starting hotspot '$SSID' on $dev ($how), address $ADDRESS"
  do_ install -d "$RUN_DIR"
  case "$how" in
    networkd)
      # Stop joining Wi-Fi: netplan's wpa_supplicant unit, or the per-interface one. Remembered for restore.
      local client=""
      for unit in "netplan-wpa-$dev.service" "wpa_supplicant@$dev.service"; do
        if active "$unit"; then client=$unit; do_ systemctl stop "$unit"; break; fi
      done
      echo "$client" | write "$RUN_DIR/hotspot-client"
      # Sorts before netplan's own file for this interface, so networkd uses this one: a fixed address, and its own
      # DHCP server for phones and laptops. No router or DNS handed out: a phone keeps its mobile data for the internet.
      # Readable by all: networkd runs as its own user (systemd-network). No secrets in it.
      write "$NETWORK_FILE" 644 <<EOF
# NOVA's hotspot (nova-hotspot.service); removed when it stops.
[Match]
Name=$dev

[Network]
Address=$ADDRESS/24
DHCPServer=yes
ConfigureWithoutCarrier=yes
LinkLocalAddressing=no
IPv6AcceptRA=no

[DHCPServer]
PoolOffset=10
PoolSize=100
EmitRouter=no
EmitDNS=no
EOF
      do_ networkctl reload
      write "$RUN_DIR/hostapd.conf" <<EOF
interface=$dev
driver=nl80211
ssid=$SSID
hw_mode=g
channel=6
ieee80211n=1
wmm_enabled=1
auth_algs=1
wpa=2
wpa_key_mgmt=WPA-PSK
rsn_pairwise=CCMP
wpa_passphrase=$PASSPHRASE
EOF
      do_ networkctl reconfigure "$dev" || true
      refresh_names
      if [ "$DRY" = 1 ]; then echo "+ exec hostapd $RUN_DIR/hostapd.conf"; return; fi
      exec hostapd "$RUN_DIR/hostapd.conf"
      ;;
    networkmanager)
      do_ nmcli connection delete nova-hotspot >/dev/null 2>&1 || true
      do_ nmcli device wifi hotspot ifname "$dev" con-name nova-hotspot ssid "$SSID" password "$PASSPHRASE"
      ;;
    iwd)
      local profile; profile=$(iwd_profile_name "$SSID")
      echo "$profile" | write "$RUN_DIR/hotspot-iwd-profile"
      write "/var/lib/iwd/ap/$profile.ap" <<EOF
[Security]
Passphrase=$PASSPHRASE

[IPv4]
Address=$ADDRESS
Netmask=255.255.255.0
EOF
      do_ iwctl device "$dev" set-property Mode ap
      do_ iwctl ap "$dev" start-profile "$SSID"
      ;;
    *)
      say "can't run a hotspot here: it needs NetworkManager, iwd, or systemd-networkd with hostapd installed (nova setup installs it)"
      exit 3
      ;;
  esac
  refresh_names
  # NetworkManager and iwd keep the hotspot up themselves; stay running so the service stays up until it's stopped.
  if [ "$DRY" = 1 ]; then echo "+ wait until stopped"; else exec sleep infinity; fi
}

restore() {
  local dev; dev=$(wifi_interface) || exit 0
  local how; how=$(method)
  say "back to joining Wi-Fi on $dev ($how)"
  case "$how" in
    networkmanager)
      do_ nmcli connection down nova-hotspot >/dev/null 2>&1 || true
      do_ nmcli connection delete nova-hotspot >/dev/null 2>&1 || true
      ;;
    iwd)
      do_ iwctl ap "$dev" stop || true
      do_ iwctl device "$dev" set-property Mode station || true
      if [ -f "$RUN_DIR/hotspot-iwd-profile" ]; then
        do_ rm -f "/var/lib/iwd/ap/$(cat "$RUN_DIR/hotspot-iwd-profile").ap" "$RUN_DIR/hotspot-iwd-profile"
      fi
      ;;
    *)
      # networkd (also when hostapd went missing since): undo whatever was done, whatever state it's in.
      do_ rm -f "$NETWORK_FILE" "$RUN_DIR/hostapd.conf"
      if command -v networkctl >/dev/null 2>&1; then
        do_ networkctl reload || true
        do_ networkctl reconfigure "$dev" || true
      fi
      local client=""
      [ -f "$RUN_DIR/hotspot-client" ] && client=$(cat "$RUN_DIR/hotspot-client")
      if [ -n "$client" ]; then do_ systemctl start "$client" || true
      else
        for unit in "netplan-wpa-$dev.service" "wpa_supplicant@$dev.service"; do
          if systemctl cat "$unit" >/dev/null 2>&1; then do_ systemctl start "$unit" || true; break; fi
        done
      fi
      do_ rm -f "$RUN_DIR/hotspot-client"
      ;;
  esac
  refresh_names
}

case "$ACTION" in
  run) run ;;
  restore) restore ;;
  method) method ;;
  *) echo "usage: $0 [--dry-run] run|restore|method" >&2; exit 64 ;;
esac
