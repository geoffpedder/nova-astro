#!/usr/bin/env bash
# Builds the web UI and a self-contained Linux build (no .NET runtime needed on the target).
# Usage: deploy/publish.sh [linux-x64|linux-arm64]    (arm64 for a Raspberry Pi)
# NOVA_VERSION=0.2.0 stamps the build with that version (the release workflow sets it from the tag).
# Works from Linux or from Git Bash on Windows.
set -euo pipefail
RID="${1:-linux-x64}"
cd "$(dirname "$0")/.."

(cd web && npm ci && npm run build)
dotnet publish src/Nova.Server -c Release -r "$RID" --self-contained -p:PublishSingleFile=true -o "out/$RID" \
  ${NOVA_VERSION:+-p:Version=$NOVA_VERSION}
echo "Published to out/$RID"
