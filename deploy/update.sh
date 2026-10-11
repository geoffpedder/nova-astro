#!/usr/bin/env bash
# Installs the newest NOVA release (or a chosen one) from GitHub: downloads the build for this machine, checks its
# checksum and runs its installer. Nothing is compiled, so no .NET or Node is needed.
#   nova update             the latest release          (deploy/update.sh does the same from a copy of the source)
#   nova update v0.2.0      a particular release (also to go back to an older one)
#   nova update --force     reinstall even if that version is already installed
#   nova update --file nova-0.2.0-linux-x64.tar.gz   a release downloaded already, e.g. brought on a USB stick
#                           (its .sha256 file, if it's alongside, is checked too)
# Run it as yourself (not with sudo): it asks for sudo only to install. While the repository is private it downloads
# through the GitHub CLI, so run "gh auth login" once first.
set -euo pipefail
REPO="${NOVA_REPO:-geoffpedder/nova-astro}"

tag=""
force=no
file=""
while [ $# -gt 0 ]; do
  arg="$1"; shift
  case "$arg" in
    --force) force=yes ;;
    --file) file="${1:?--file needs a nova-….tar.gz file}"; shift ;;
    -h|--help) sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) tag="$arg" ;;
  esac
done

case "$(uname -m)" in
  x86_64) rid=linux-x64 ;;
  aarch64|arm64) rid=linux-arm64 ;;
  *) echo "There's no NOVA build for $(uname -m). It needs a 64-bit PC, or a Raspberry Pi 4 or 5 running 64-bit Raspberry Pi OS." >&2; exit 1 ;;
esac

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

if [ -n "$file" ]; then
  [ -f "$file" ] || { echo "No such file: $file" >&2; exit 1; }
  case "$(basename "$file")" in
    nova-*-"$rid".tar.gz) ;;
    *) echo "$(basename "$file") isn't a NOVA release for this computer: it needs nova-<version>-$rid.tar.gz." >&2; exit 1 ;;
  esac
  cp "$file" "$work/"
  if [ -f "$file.sha256" ]; then cp "$file.sha256" "$work/"
  else echo "No $(basename "$file").sha256 alongside it, so its checksum can't be checked."; fi
else
  echo "Fetching ${tag:-the latest} NOVA release for $rid…"
  if command -v gh >/dev/null 2>&1 && gh auth status >/dev/null 2>&1; then
    # Works for private repositories too.
    gh release download ${tag:+"$tag"} -R "$REPO" -p "nova-*-$rid.tar.gz" -p "nova-*-$rid.tar.gz.sha256" -D "$work"
  else
    if [ -n "$tag" ]; then api="https://api.github.com/repos/$REPO/releases/tags/$tag"; else api="https://api.github.com/repos/$REPO/releases/latest"; fi
    urls="$(curl -fsSL "$api" | grep -o "\"browser_download_url\": *\"[^\"]*nova-[^\"]*-$rid\.tar\.gz[^\"]*\"" | sed -E 's/.*"(https[^"]+)"/\1/')" || true
    if [ -z "$urls" ]; then
      echo "Couldn't find a release for $rid. If the repository is private: install the GitHub CLI and run 'gh auth login'." >&2
      exit 1
    fi
    for url in $urls; do curl -fsSL -o "$work/$(basename "$url")" "$url"; done
  fi
fi

archive="$(ls "$work"/nova-*-"$rid".tar.gz)"
[ ! -f "$archive.sha256" ] || (cd "$work" && sha256sum -c --quiet "$(basename "$archive").sha256")
version="$(basename "$archive" | sed -E "s/^nova-(.+)-$rid\.tar\.gz$/\1/")"
installed="$(cat /opt/nova/VERSION 2>/dev/null || echo none)"
if [ "$version" = "$installed" ] && [ "$force" = no ]; then
  echo "NOVA $version is already installed (--force reinstalls it)."
  exit 0
fi

echo "Installing NOVA $version (installed now: $installed)…"
tar -C "$work" -xzf "$archive"
sudo "$work/nova-$version-$rid/deploy/install.sh" "$work/nova-$version-$rid/app"
