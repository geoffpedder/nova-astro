#!/usr/bin/env bash
# Shares virtual display :99 in the browser: x11vnc serves it to localhost only (port 5999, which NOVA's PHD2 tab
# relays), and websockify bridges that to noVNC's own page on port 6080 (http://<host>:6080/vnc.html).
# Both run as children of this script, and if either stops the script exits with an error, so systemd restarts the
# pair: the service is only "running" while the screen really is shared.
set -eu

web=""
for dir in /usr/share/webapps/novnc /usr/share/novnc /usr/share/noVNC; do
  if [ -f "$dir/vnc.html" ]; then web="$dir"; break; fi
done
if [ -z "$web" ]; then
  echo "noVNC not found. Install it with: nova setup (Arch: yay -S novnc websockify)" >&2
  exit 1
fi

# The virtual display can still be starting (at boot, or just after nova-display restarted): wait for its socket,
# or x11vnc would give up straight away.
for _ in $(seq 1 30); do
  [ -S /tmp/.X11-unix/X99 ] && break
  sleep 1
done
if [ ! -S /tmp/.X11-unix/X99 ]; then
  echo "Display :99 isn't up (nova-display.service): not sharing PHD2's screen yet" >&2
  exit 1
fi

# With NOVA's password set (auth.json exists), noVNC's own page only answers on the mini PC itself: from other
# computers PHD2's screen is then only reachable through NOVA's PHD2 tab, which asks for the password.
listen=6080
[ -f "${Nova__ConfigDirectory:-$HOME/.config/nova}/auth.json" ] && listen=127.0.0.1:6080

# -clear_all: no key left held down from before (a browser can lose a key's release, Alt after Alt+Tab).
x11vnc -display :99 -forever -shared -nopw -localhost -rfbport 5999 -quiet -clear_all &
vnc=$!
websockify --web "$web" "$listen" localhost:5999 &
bridge=$!
trap 'kill $vnc $bridge 2>/dev/null' EXIT

# Whichever stops first (x11vnc losing the display, say): say which, and exit so systemd starts both again.
set +e
wait -n $vnc $bridge
status=$?
if ! kill -0 $vnc 2>/dev/null; then echo "x11vnc stopped (exit $status): restarting PHD2's screen sharing" >&2
else echo "websockify stopped (exit $status): restarting PHD2's screen sharing" >&2; fi
exit 1
