#!/usr/bin/env bash
# Removes NOVA from this computer: its services, its program (/opt/nova), the "nova" command, and the permissions,
# firewall openings and time-server fallback its install added.
#   nova uninstall            asks first; keeps NOVA's settings, so installing again picks up where it left off
#   --purge                   also removes the settings (/etc/nova and ~/.config/nova): equipment, sites, sequences, password
#   --yes                     doesn't ask
# Never touched: your pictures, and the programs setup installed alongside (INDI, ASTAP, PHD2, ...), which other
# programs may use; it lists them at the end.
set -euo pipefail

# The whole script is one function, read before any of it runs: it removes its own file part way through.
main() {
  local purge=no assume_yes=no arg
  for arg in "$@"; do
    case "$arg" in
      --purge) purge=yes ;;
      --yes|-y) assume_yes=yes ;;
      -h|--help) sed -n '2,8p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
      *) echo "Unknown option: $arg (try --help)" >&2; exit 1 ;;
    esac
  done

  # For tests: everything is removed under this folder instead of the real system.
  local root="${NOVA_UNINSTALL_ROOT:-}"

  if [ ! -d "$root/opt/nova" ] && [ ! -f "$root/etc/systemd/system/nova.service" ]; then
    echo "NOVA isn't installed here."
    [ "$purge" = yes ] || exit 0
  fi

  # NOVA's user (the services' owner), whose settings folder it is.
  local user home config
  user="$(systemctl show -p User --value nova 2>/dev/null || true)"
  user="${user:-${SUDO_USER:-$(id -un)}}"
  home="$(getent passwd "$user" 2>/dev/null | cut -d: -f6 || true)"
  config="$(sed -n 's/^Nova__ConfigDirectory=//p' "$root/etc/nova/nova.env" 2>/dev/null | tail -1)"
  [ -n "$config" ] || config="${home:-/home/$user}/.config/nova"
  config="$root$config"

  if [ "$assume_yes" = no ]; then
    echo "This removes NOVA: its services, /opt/nova and the nova command."
    if [ "$purge" = yes ]; then echo "Its settings go too (--purge): /etc/nova and $config"
    else echo "Its settings are kept (/etc/nova and $config); --purge removes them too."; fi
    echo "Your pictures stay, and so do INDI, ASTAP, PHD2 and the other programs setup installed."
    local reply=""
    [ -r /dev/tty ] || { echo "Nobody to ask: run it again with --yes." >&2; exit 1; }
    read -r -p "Remove NOVA? [y/N] " reply </dev/tty || reply=""
    [[ "$reply" =~ ^[Yy] ]] || { echo "Nothing removed."; exit 0; }
  fi

  if [ -z "$root" ] && [ "$(id -u)" -ne 0 ]; then
    local again=(--yes)
    [ "$purge" = yes ] && again+=(--purge)
    exec sudo bash "${BASH_SOURCE[0]}" "${again[@]}"
  fi

  # Run from NOVA's own terminal (Activity tab), this is part of nova.service, so stopping NOVA would stop it half
  # way. Then a copy carries on as a job of its own.
  if [ -z "$root" ] && [ -z "${NOVA_UNINSTALL_DETACHED:-}" ] && grep -qs 'nova\.service' /proc/self/cgroup; then
    local copy unit
    copy="$(mktemp /var/tmp/nova-uninstall.XXXXXX)"
    cp "${BASH_SOURCE[0]}" "$copy"
    unit="nova-uninstall-$(date +%s)"
    local flags="--yes"
    [ "$purge" = yes ] && flags="--yes --purge"
    systemd-run --unit "$unit" --collect --quiet --setenv=NOVA_UNINSTALL_DETACHED=1 bash -c "bash '$copy' $flags; rm -f '$copy'"
    echo "Removing NOVA in the background (job $unit): this terminal closes with it."
    exit 0
  fi

  echo "Stopping NOVA's services"
  # The hotspot first, while its helper in /opt/nova is still there to put the Wi-Fi back.
  local units="nova-hotspot nova-mdns.timer nova-mdns phd2 nova-screen nova-wm nova-display nova indiserver" unit file
  for unit in $units; do
    systemctl stop "$unit" >/dev/null 2>&1 || true
    systemctl disable "$unit" >/dev/null 2>&1 || true
  done
  for unit in $units; do
    case "$unit" in *.timer) file="$root/etc/systemd/system/$unit" ;; *) file="$root/etc/systemd/system/$unit.service" ;; esac
    [ -f "$file" ] || continue
    # indiserver.service and phd2.service are everyday names: only NOVA's own are removed.
    case "$unit" in
      nova*) rm -f "$file" ;;
      *) if grep -qi 'nova' "$file"; then rm -f "$file"; else echo "  left $file: it isn't NOVA's"; fi ;;
    esac
  done
  systemctl daemon-reload >/dev/null 2>&1 || true
  systemctl reset-failed >/dev/null 2>&1 || true

  echo "Removing /opt/nova and the nova command"
  rm -rf "$root/opt/nova" "$root/run/nova" "$root/run/nova-indi"
  rm -f "$root/usr/local/bin/nova"

  echo "Removing NOVA's permissions (polkit)"
  rm -f "$root"/etc/polkit-1/rules.d/49-nova-power.rules "$root"/etc/polkit-1/rules.d/49-nova-clock.rules \
        "$root"/etc/polkit-1/rules.d/49-nova-storage.rules "$root"/etc/polkit-1/rules.d/49-nova-hotspot.rules

  # The time-server fallback setup added on Ubuntu.
  if [ -f "$root/etc/chrony/sources.d/nova.sources" ] || [ -f "$root/etc/chrony/conf.d/nova.conf" ]; then
    echo "Removing the time-server fallback"
    rm -f "$root/etc/chrony/sources.d/nova.sources" "$root/etc/chrony/conf.d/nova.conf"
    systemctl try-restart chrony >/dev/null 2>&1 || true
  fi

  # The firewall openings the install made. mDNS (.local names) stays open: other programs use it.
  if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q '^Status: active'; then
    local rule
    for rule in 5000/tcp 10001/tcp 67/udp; do
      # Only the ones marked as NOVA's.
      if ufw status 2>/dev/null | grep "^$rule " | grep -q '# NOVA'; then ufw delete allow "$rule" >/dev/null 2>&1 || true; fi
    done
    echo "Firewall (ufw): closed NOVA's ports (5000, 10001 and the hotspot's 67/udp)"
  elif command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
    firewall-cmd --quiet --permanent --remove-port=5000/tcp --remove-port=10001/tcp || true
    firewall-cmd --quiet --reload || true
    echo "Firewall (firewalld): closed ports 5000 and 10001 (mDNS and DHCP are left as they are)"
  fi

  if [ "$purge" = yes ]; then
    echo "Removing NOVA's settings"
    rm -rf "$root/etc/nova"
    case "$config" in
      */nova|*/nova/) rm -rf "$config" ;;
      *) echo "  left $config: remove it by hand if it holds only NOVA's settings" ;;
    esac
  fi

  echo
  echo "NOVA is removed."
  [ "$purge" = yes ] || echo "Its settings are kept in /etc/nova and $config (nova uninstall --purge would have removed them)."
  echo "Left alone, because other programs may use them:"
  echo "  your pictures (~/Pictures/Nova unless you chose another folder)"
  if command -v pacman >/dev/null 2>&1; then
    echo "  the packages setup installed: libindi and the makers' driver packages, hostapd, ttyd, and with PHD2:"
    echo "    phd2, xorg-server-xvfb, x11vnc, icewm, novnc, websockify   (sudo pacman -Rs <package>)"
  else
    echo "  the packages setup installed: indi-bin, libindi1, indi-3rdparty-drivers, hostapd, ttyd, gsc, and with PHD2:"
    echo "    phd2, xvfb, x11vnc, icewm, novnc, websockify   (sudo apt remove <package>)"
    [ -f /etc/apt/sources.list.d/astroberry.sources ] && echo "  Astroberry's package repository: /etc/apt/sources.list.d/astroberry.sources"
    if compgen -G '/etc/apt/sources.list.d/mutlaqja-*' >/dev/null; then echo "  INDI's package repository: sudo add-apt-repository --remove ppa:mutlaqja/ppa"; fi
    if compgen -G '/etc/apt/sources.list.d/pch-*' >/dev/null; then echo "  PHD2's package repository: sudo add-apt-repository --remove ppa:pch/phd2"; fi
    [ -x /usr/local/bin/ttyd ] && echo "  ttyd: /usr/local/bin/ttyd"
  fi
  [ -d /opt/astap ] && echo "  ASTAP and its star database: /opt/astap"
  echo "  Avahi (.local names) and Bluetooth, which setup turned on"
  echo "  $user's membership of the serial, video, bluetooth and log-reading groups"
  return 0
}

main "$@"
exit 0
