#!/usr/bin/env bash
# Device dev loop: patches Celeste, builds/installs the APK and pushes the files.
# Usage: scripts/deploy-android.sh [--content] [--no-launch] [--release]
#   --content    also pushes Celeste.exe and the Content folder (1 GB; first time, new package ID, or when it changes)
#   --play       launch straight into the game instead of stopping at the launcher
#   --release    install out/CelesteAndroid-<version>.apk (org.devziyad.celeste, release-signed) instead of the
#                debug build (org.devziyad.celeste.debug, "Celeste (debug)"); both can be installed together
# Uses the device in $ANDROID_SERIAL.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_env.sh"

CONTENT=0; LAUNCH=1; RELEASE=0; PLAY=0
for a in "$@"; do
	case "$a" in
		--content) CONTENT=1 ;;
		--no-launch) LAUNCH=0 ;;
		--release) RELEASE=1 ;;
		--play) PLAY=1 ;;
		*) die "unknown argument: $a" ;;
	esac
done
[ -n "${ANDROID_SERIAL:-}" ] || die "ANDROID_SERIAL is not set"
if [ "$RELEASE" = 1 ]; then PKG=org.devziyad.celeste; else PKG=org.devziyad.celeste.debug; fi
ADB_BIN="$ANDROID_HOME/platform-tools/adb"
# Wireless adb can drop and re-register between steps: wait for the device and retry before each call.
adb_s() {
	local i
	for i in 1 2 3 4 5; do
		"$ADB_BIN" -s "$ANDROID_SERIAL" wait-for-device 2>/dev/null || true
		"$ADB_BIN" -s "$ANDROID_SERIAL" "$@" && return 0
		sleep 3
	done
	return 1
}
ADB=(adb_s)

echo "=== patch ==="
dotnet build "$ROOT/src/Celeste.Desktop" -nologo -v q >&2
PATCHED="$("$ROOT/src/Celeste.Desktop/bin/Debug/net10.0/linux-x64/CelesteDesktop" --patch-only | tail -n1)"

echo "=== apk ==="
if [ "$RELEASE" = 1 ]; then
	"$ROOT/scripts/build-release.sh"
	APK="$(ls -t "$ROOT"/out/CelesteAndroid-*.apk | grep -v personal | head -n1)"
else
	dotnet build "$ROOT/src/Celeste.Android" -c Debug -nologo -v q >&2
	# Newest one: older target-framework folders may hold stale APKs.
	APK="$(find "$ROOT/src/Celeste.Android/bin/Debug" -name '*-Signed.apk' -printf '%T@ %p\n' | sort -nr | head -n1 | cut -d' ' -f2-)"
fi
"${ADB[@]}" install -r "$APK"
"${ADB[@]}" shell am force-stop "$PKG"

# /data/local/tmp is readable by the app; run-as copies into its private storage (files/).
# (run-as needs a debuggable build; with --release the launcher imports the game itself.)
echo "=== files ==="
TMP=/data/local/tmp/celeste
"${ADB[@]}" shell "rm -rf $TMP && mkdir -p $TMP/patched"
"${ADB[@]}" push "$PATCHED" "$TMP/patched/"
if [ "$CONTENT" = 1 ]; then
	"${ADB[@]}" shell "mkdir -p $TMP/Celeste"
	"${ADB[@]}" push "$ROOT/Celeste/Content" "$TMP/Celeste/"
	"${ADB[@]}" push "$ROOT/Celeste/Celeste.exe" "$TMP/Celeste/"
fi
if "${ADB[@]}" shell "run-as $PKG true" >/dev/null 2>&1; then
	"${ADB[@]}" shell "chmod -R a+rX $TMP && run-as $PKG sh -c 'mkdir -p files && cp -r $TMP/* files/' && rm -rf $TMP"
else
	echo "note: $PKG is not debuggable; files left in $TMP for the launcher to import."
fi

if [ "$LAUNCH" = 1 ]; then
	"${ADB[@]}" logcat -c
	EXTRA=()
	[ "$PLAY" = 1 ] && EXTRA=(--ez autoplay true)
	"${ADB[@]}" shell am start -n "$PKG/org.devziyad.celeste.LauncherActivity" "${EXTRA[@]}"
fi
