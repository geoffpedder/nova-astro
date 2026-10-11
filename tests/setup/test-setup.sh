#!/usr/bin/env bash
# Tests deploy/setup.sh end to end, as a new user would run it: in a clean container running systemd, a fresh user
# runs setup.sh --yes, installing the given release file, then this checks that the services run, that NOVA
# answers with that version, and that nova doctor finds no problems.
#   tests/setup/test-setup.sh debian:trixie out/nova-0.2.0-linux-x64.tar.gz
# Images: debian:trixie (also stands in for Raspberry Pi OS), ubuntu:26.04, archlinux. Needs Docker able to run
# privileged containers (systemd runs inside). Downloads the system's packages, so it takes 10-30 minutes.
set -euo pipefail
image="${1:?usage: tests/setup/test-setup.sh <image> <nova-….tar.gz>}"
release="${2:?usage: tests/setup/test-setup.sh <image> <nova-….tar.gz>}"
repo="$(cd "$(dirname "$0")/../.." && pwd)"
pkg="$(cd "$(dirname "$release")" && pwd)"
file="$(basename "$release")"
version="$(echo "$file" | sed -E 's/^nova-(.+)-linux-(x64|arm64)\.tar\.gz$/\1/')"
name="nova-setup-test-$$"

step() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
in_box() { docker exec "$name" bash -c "$1"; }
as_user() { docker exec -u tester -w /home/tester "$name" bash -c "$1"; }

cleanup() {
  if [ "${passed:-no}" != yes ] && docker ps -q -f "name=$name" | grep -q .; then
    step "Logs"
    in_box 'for unit in nova indiserver phd2 nova-screen; do echo "--- $unit"; journalctl -u $unit -n 25 --no-pager 2>/dev/null; done' || true
  fi
  docker rm -f "$name" >/dev/null 2>&1 || true
}
trap cleanup EXIT

case "$image" in
  archlinux*) prep='pacman -Syu --noconfirm --needed sudo >/dev/null'; init=/usr/lib/systemd/systemd ;;
  *) prep='apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq systemd systemd-sysv dbus sudo ca-certificates >/dev/null'
     init=/lib/systemd/systemd ;;
esac

step "Starting $image with systemd"
docker run -d --name "$name" --privileged --cgroupns=host -v /sys/fs/cgroup:/sys/fs/cgroup:rw \
  -v "$repo/deploy:/src/deploy:ro" -v "$pkg:/pkg:ro" "$image" bash -c "$prep && exec $init" >/dev/null
for _ in $(seq 1 120); do
  state="$(docker exec "$name" systemctl is-system-running 2>/dev/null || true)"
  case "$state" in running|degraded) break ;; esac
  sleep 5
done
echo "systemd: ${state:-not running}"
case "$state" in running|degraded) ;; *) echo "systemd didn't start" >&2; exit 1 ;; esac

step "A new user with sudo, and a copy of deploy/"
in_box 'useradd -m -s /bin/bash tester && echo "tester ALL=(ALL) NOPASSWD: ALL" > /etc/sudoers.d/tester &&
        cp -r /src/deploy /home/tester/deploy && chown -R tester: /home/tester/deploy'

step "deploy/setup.sh --yes --database d05 --release-file $file"
as_user "deploy/setup.sh --yes --database d05 --release-file /pkg/$file </dev/null"

step "Checks"
sleep 10
as_user 'nova status'
# NOVA's own version is the status's last field (PHD2's, under guider, comes earlier).
reported="$(in_box 'curl -fsS localhost:5000/api/status' | grep -o '"version":"[^"]*"' | tail -1 | cut -d'"' -f4)"
echo "NOVA reports version: ${reported:-nothing}"
[ "$reported" = "$version" ] || { echo "Expected NOVA $version to be running" >&2; exit 1; }
echo "The setup guide sees:"
in_box 'curl -fsS localhost:5000/api/setup'; echo
problems=0
as_user 'nova doctor' || problems=$?
if [ "$problems" -ne 0 ]; then echo "nova doctor found $problems problem(s)" >&2; exit 1; fi

passed=yes
step "Passed: $image"
