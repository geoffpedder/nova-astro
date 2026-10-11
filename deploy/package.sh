#!/usr/bin/env bash
# Packs a published build into a release download: nova-<version>-<rid>.tar.gz (the app and these deploy scripts)
# and a .sha256 checksum next to it, both in out/.
# Usage: deploy/package.sh <rid> <version>      (after deploy/publish.sh <rid>)
set -euo pipefail
RID="${1:?usage: deploy/package.sh <rid> <version>}"
VERSION="${2:?usage: deploy/package.sh <rid> <version>}"
cd "$(dirname "$0")/.."
[ -f "out/$RID/Nova.Server" ] || { echo "No build in out/$RID: run deploy/publish.sh $RID first." >&2; exit 1; }

name="nova-$VERSION-$RID"
stage="out/package/$name"
rm -rf out/package
mkdir -p "$stage"
cp -r "out/$RID" "$stage/app"
echo "$VERSION" > "$stage/app/VERSION"
cp -r deploy "$stage/deploy"
cp README.md LICENSE NOTICE CHANGELOG.md "$stage/"
cp -r docs "$stage/docs"
chmod +x "$stage"/deploy/*.sh "$stage/deploy/nova" "$stage/app/Nova.Server"

tar -C out/package -czf "out/$name.tar.gz" "$name"
(cd out && sha256sum "$name.tar.gz" > "$name.tar.gz.sha256")
rm -rf out/package
echo "Packed out/$name.tar.gz"
