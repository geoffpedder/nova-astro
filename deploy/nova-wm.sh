#!/bin/sh
# The window manager on PHD2's virtual screen (nova-wm.service): IceWM, with a taskbar listing PHD2's windows and
# title bars to move them by (config in /opt/nova/icewm). A mini PC set up before IceWM keeps Openbox until
# "nova setup" (or the icewm package) adds it.
if command -v icewm >/dev/null; then
  export ICEWM_PRIVCFG=/opt/nova/icewm
  icewm &
  wm=$!
  # PHD2's main window fills the screen each time it appears (after a restart too). Matched by its title: its
  # dialogs share its window class, and keep their own size.
  last=""
  while kill -0 "$wm" 2>/dev/null; do
    id=$(icesh -n '^PHD2 Guiding' list 2>/dev/null | awk 'NR == 1 { print $1 }')
    if [ -n "$id" ] && [ "$id" != "$last" ] && icesh -w "$id" maximize 2>/dev/null; then last=$id; fi
    sleep 3
  done
  wait "$wm"
  exit $?
fi
exec openbox --config-file /opt/nova/openbox-rc.xml
