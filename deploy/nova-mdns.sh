#!/usr/bin/env bash
# Keeps the mini PC answering to <hostname>.local. Avahi renames itself "<hostname>-2" when it thinks another computer
# has its name, which it can wrongly think at boot or after a network change (its own announcement coming back to it,
# from a new address, or repeated by a router or mesh access points). It never changes back by itself, so this sets
# it back. Run by nova-mdns.timer (as root) every 30 seconds.
set -u
want="$(hostname -s)"
reply="$(busctl --system call org.freedesktop.Avahi / org.freedesktop.Avahi.Server GetHostName 2>/dev/null)" || exit 0
have="$(printf '%s' "$reply" | sed -E 's/^s "(.*)"$/\1/')"
[ -z "$have" ] || [ "$have" = "$want" ] && exit 0

if busctl --system call org.freedesktop.Avahi / org.freedesktop.Avahi.Server SetHostName s "$want" >/dev/null 2>&1; then
  echo "nova-mdns: Avahi had renamed itself $have.local; set back to $want.local"
else
  echo "nova-mdns: Avahi had renamed itself $have.local and wouldn't take $want back: restarting it"
  systemctl restart avahi-daemon
fi
