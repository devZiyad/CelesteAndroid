#!/usr/bin/env bash
# Device dev loop: patches Celeste, builds/installs the APK and pushes the files.
# Usage: scripts/deploy-android.sh [--content] [--no-launch] [--release]
#   --content    also pushes the game's Content folder (1 GB; first time or when it changes)
#   --release    install out/CelesteAndroid-<version>.apk (release-signed) instead of a debug build
# Uses the device in $ANDROID_SERIAL.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_env.sh"

CONTENT=0; LAUNCH=1; RELEASE=0
for a in "$@"; do
	case "$a" in
		--content) CONTENT=1 ;;
		--no-launch) LAUNCH=0 ;;
		--release) RELEASE=1 ;;
		*) die "unknown argument: $a" ;;
	esac
done
[ -n "${ANDROID_SERIAL:-}" ] || die "ANDROID_SERIAL is not set"
PKG=org.devziyad.celeste
ADB=("$ANDROID_HOME/platform-tools/adb" -s "$ANDROID_SERIAL")

echo "=== patch ==="
dotnet build "$ROOT/src/Celeste.Desktop" -nologo -v q >&2
PATCHED="$("$ROOT/src/Celeste.Desktop/bin/Debug/net10.0/linux-x64/CelesteDesktop" --patch-only | tail -n1)"

echo "=== apk ==="
if [ "$RELEASE" = 1 ]; then
	"$ROOT/scripts/build-release.sh"
	APK="$(ls -t "$ROOT"/out/CelesteAndroid-*.apk | grep -v personal | head -n1)"
else
	dotnet build "$ROOT/src/Celeste.Android" -c Debug -nologo -v q >&2
	APK="$(find "$ROOT/src/Celeste.Android/bin/Debug" -name '*-Signed.apk' | head -n1)"
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
fi
if "${ADB[@]}" shell "run-as $PKG true" >/dev/null 2>&1; then
	"${ADB[@]}" shell "chmod -R a+rX $TMP && run-as $PKG sh -c 'mkdir -p files && cp -r $TMP/* files/' && rm -rf $TMP"
else
	echo "note: $PKG is not debuggable; files left in $TMP for the launcher to import."
fi

if [ "$LAUNCH" = 1 ]; then
	"${ADB[@]}" logcat -c
	"${ADB[@]}" shell am start -n "$PKG/.LauncherActivity"
fi
