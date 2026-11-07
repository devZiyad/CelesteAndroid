#!/usr/bin/env bash
# Downloads prebuilt fnalibs (SDL3, FNA3D, FAudio) for linux-x64 into natives/linux-x64.
# Source: the "fnalibs" CI artifact of FNA-XNA/fnalibs-dailies, via nightly.link (no GitHub login).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_env.sh"

DEST="$ROOT/natives/linux-x64"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
mkdir -p "$DEST" "$TMP/x"

curl -fsSL -o "$TMP/fnalibs.zip" https://nightly.link/FNA-XNA/fnalibs-dailies/workflows/ci/main/fnalibs.zip
unzip -q "$TMP/fnalibs.zip" -d "$TMP/x"
SRC="$TMP/x/lib64"
[ -d "$SRC" ] || die "lib64 folder not found in artifact"
# .NET probes lib<name>.so, so ship the soname file plus a plain .so symlink.
for n in SDL3 FNA3D FAudio; do
	cp -f "$SRC/lib$n.so.0" "$DEST/lib$n.so.0"
	ln -sf "lib$n.so.0" "$DEST/lib$n.so"
done
cp -f "$SRC/libtheorafile.so" "$DEST/"
ls -l "$DEST"
