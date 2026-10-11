#!/bin/sh
# Shares virtual display :99 in the browser: x11vnc serves it to localhost only, and websockify
# bridges that to noVNC's web client on port 6080 (http://<host>:6080/vnc.html).
set -eu

web=""
for dir in /usr/share/webapps/novnc /usr/share/novnc /usr/share/noVNC; do
  if [ -f "$dir/vnc.html" ]; then web="$dir"; break; fi
done
if [ -z "$web" ]; then
  echo "noVNC not found. Install it with: nova setup (Arch: yay -S novnc websockify)" >&2
  exit 1
fi

# With NOVA's password set (auth.json exists), noVNC's own page only answers on the mini PC itself: from other
# computers PHD2's screen is then only reachable through NOVA's PHD2 tab, which asks for the password.
listen=6080
[ -f "${Nova__ConfigDirectory:-$HOME/.config/nova}/auth.json" ] && listen=127.0.0.1:6080

# -clear_all: no key left held down from before (a browser can lose a key's release, Alt after Alt+Tab).
x11vnc -display :99 -forever -shared -nopw -localhost -rfbport 5999 -quiet -clear_all &
trap 'kill $! 2>/dev/null' EXIT
exec websockify --web "$web" "$listen" localhost:5999
