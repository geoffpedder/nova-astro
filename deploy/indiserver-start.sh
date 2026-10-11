#!/bin/sh
# Starts indiserver with the drivers listed in $INDI_DRIVERS (from /etc/nova/indi.env),
# skipping any that aren't installed so one missing package doesn't take the rest down.
#
# Optional $INDI_DEVICE_NAMES gives drivers a device name of your choice, e.g.
#   INDI_DEVICE_NAMES="indi_lx200am5=ZWO AM3; indi_asi_wheel=Filter wheel"
# Named drivers are started through indiserver's control pipe ("start <driver> -n <name>"),
# the same mechanism KStars/Ekos uses to label devices.
set -eu

# Control pipe: lets NOVA start and stop drivers while indiserver runs, and is how named drivers start.
fifo="${INDI_CONTROL_FIFO:-/run/nova-indi/control}"
have_fifo=no
if [ -d "$(dirname "$fifo")" ]; then
  [ -p "$fifo" ] || mkfifo -m 600 "$fifo"
  have_fifo=yes
fi

# Prints the configured device name for a driver, if any.
name_for() {
  printf '%s\n' "${INDI_DEVICE_NAMES:-}" | tr ';' '\n' | while IFS='=' read -r exec name; do
    exec=$(printf '%s' "$exec" | tr -d ' \t')
    name=$(printf '%s' "$name" | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')
    if [ "$exec" = "$1" ] && [ -n "$name" ]; then
      printf '%s' "$name"
      break
    fi
  done
}

drivers=""
named=""
found=no
for driver in ${INDI_DRIVERS:-}; do
  if ! command -v "$driver" >/dev/null 2>&1; then
    echo "Skipping $driver: not installed" >&2
    continue
  fi
  found=yes
  name=$(name_for "$driver")
  case "$name" in
    *'"'*) echo "Ignoring the name for $driver: it can't contain double quotes" >&2; name="" ;;
  esac
  if [ -n "$name" ] && [ "$have_fifo" = yes ]; then
    named="${named}start $driver -n \"$name\"
"
    echo "Starting $driver as \"$name\""
  else
    [ -n "$name" ] && echo "Can't name $driver without the control pipe; starting it with its default name" >&2
    drivers="$drivers $driver"
  fi
done

if [ "$found" = no ]; then
  echo "None of the drivers in INDI_DRIVERS are installed. Edit /etc/nova/indi.env." >&2
  exit 1
fi

if [ "$have_fifo" = yes ]; then
  set -- -f "$fifo"
  # Writing to the pipe waits until indiserver opens it, so these start right after indiserver is up.
  if [ -n "$named" ]; then
    (printf '%s' "$named" > "$fifo") &
  fi
else
  set --
fi

echo "Starting indiserver with:$drivers"
# Word splitting of $drivers is intended: one argument per driver.
# shellcheck disable=SC2086
exec indiserver -v "$@" $drivers
