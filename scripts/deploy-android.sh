#!/usr/bin/env bash
# Device dev loop: patches Celeste, builds/installs the APK and pushes the files.
# Usage: scripts/deploy-android.sh [--content] [--no-launch] [--release]
#   --content    also pushes Celeste.exe and the Content folder (1 GB; first time, new package ID, or when it changes)
#   --play       launch straight into the game instead of stopping at the launcher
#   --no-files   install the app only: do not push the patched Celeste.dll (e.g. to test the on-device Steam install)
#   --release    install out/CelesteAndroid-<version>.apk (org.devziyad.celeste, release-signed) instead of the
#                debug build (org.devziyad.celeste.debug, "Celeste (debug)"); both can be installed together
# Uses the device in $ANDROID_SERIAL.
#
# App data safety: this script NEVER uninstalls anything and always installs with `adb install -r` (update
# in place, keeps /data/data). If the installed app cannot be updated (different signing key, newer
# versionCode installed) it stops and says so; removing the old app (which wipes its game files and saves)
# is left to you. After each install it checks that the app was updated in place (same uid and
# firstInstallTime) and, for the debuggable debug build, that its file count/size did not change.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_env.sh"

CONTENT=0; LAUNCH=1; RELEASE=0; PLAY=0; NOFILES=0
for a in "$@"; do
	case "$a" in
		--content) CONTENT=1 ;;
		--no-launch) LAUNCH=0 ;;
		--release) RELEASE=1 ;;
		--play) PLAY=1 ;;
		--no-files) NOFILES=1 ;;
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

# --- app data safety helpers -------------------------------------------------------------------------
# "<uid> <firstInstallTime>" of the installed package (empty when it is not installed).
pkg_identity() {
	"$ADB_BIN" -s "$ANDROID_SERIAL" shell dumpsys package "$PKG" 2>/dev/null \
		| awk -F= '/^ *appId=/ {u=$2} /firstInstallTime=/ {t=$2} END {if (u != "") print u, t}' | tr -d '\r'
}
# Debug build only (run-as): "<dir> files=N bytes=B" for files/Celeste (game), files/userdata (saves), patched, all.
INV_SCRIPT=/data/local/tmp/celeste-inv.sh
pkg_inventory() {
	"$ADB_BIN" -s "$ANDROID_SERIAL" shell "run-as $PKG sh $INV_SCRIPT" 2>/dev/null | tr -d '\r'
}
push_inventory_script() {
	local f; f="$(mktemp)"
	cat > "$f" <<'EOS'
cd files 2>/dev/null || { echo "files/ missing"; exit 0; }
for d in Celeste userdata patched .; do
  [ -d "$d" ] || { echo "$d files=0 bytes=0 (missing)"; continue; }
  n=$(find "$d" -type f | wc -l)
  b=$(find "$d" -type f -exec stat -c %s {} + 2>/dev/null | { s=0; while read x; do s=$((s+x)); done; echo $s; })
  echo "$d files=$n bytes=$b"
done
EOS
	"$ADB_BIN" -s "$ANDROID_SERIAL" push "$f" "$INV_SCRIPT" >/dev/null && "$ADB_BIN" -s "$ANDROID_SERIAL" shell chmod a+r "$INV_SCRIPT"
	rm -f "$f"
}
# `adb install -r` with retries only for transient (wireless adb) failures; never uninstalls.
safe_install() {
	local apk="$1" out i
	for i in 1 2 3 4 5; do
		"$ADB_BIN" -s "$ANDROID_SERIAL" wait-for-device 2>/dev/null || true
		if out="$("$ADB_BIN" -s "$ANDROID_SERIAL" install -r "$apk" 2>&1)" && grep -q '^Success' <<<"$out"; then
			echo "$out" | tail -n 2
			return 0
		fi
		echo "$out" >&2
		if grep -qE 'INSTALL_FAILED_UPDATE_INCOMPATIBLE|signatures do not match|VERSION_DOWNGRADE|INSTALL_FAILED_DUPLICATE_PERMISSION|INSTALL_FAILED_(SHARED_USER|TEST_ONLY)' <<<"$out"; then
			die "$PKG cannot be updated in place (see the error above). Fixing that needs the installed app to be removed, which ERASES its game files and saves, so this script will not do it.
  To keep your saves first: adb -s \$ANDROID_SERIAL exec-out run-as $PKG tar -cf - files/userdata > saves-backup.tar   (debug build only)
  Then, if you really want it: adb -s \$ANDROID_SERIAL uninstall $PKG   and re-run with --content."
		fi
		sleep 3
	done
	die "adb install failed 5 times"
}

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

echo "=== install ($PKG) ==="
BEFORE_ID="$(pkg_identity)"
DEBUGGABLE=0
if [ "$RELEASE" = 0 ]; then
	DEBUGGABLE=1
	push_inventory_script
	[ -n "$BEFORE_ID" ] && BEFORE_INV="$(pkg_inventory)" || BEFORE_INV=""
fi
if [ -n "$BEFORE_ID" ]; then
	echo "installed: uid/firstInstallTime = $BEFORE_ID -> updating in place"
	[ "$DEBUGGABLE" = 1 ] && { echo "data before install:"; sed 's/^/  /' <<<"$BEFORE_INV"; }
else
	echo "NOTE: $PKG is not installed: this is a FRESH install with no game files or saves (use --content to push the game)."
fi
safe_install "$APK"
AFTER_ID="$(pkg_identity)"
if [ -n "$BEFORE_ID" ] && [ "$AFTER_ID" != "$BEFORE_ID" ]; then
	die "$PKG was reinstalled instead of updated (was '$BEFORE_ID', now '$AFTER_ID'): its app data is gone."
fi
if [ -n "$BEFORE_ID" ] && [ "$DEBUGGABLE" = 1 ]; then
	AFTER_INV="$(pkg_inventory)"
	if [ "$AFTER_INV" != "$BEFORE_INV" ]; then
		echo "data after install:" >&2; sed 's/^/  /' <<<"$AFTER_INV" >&2
		die "app data changed during the install (file count/size differ from before)."
	fi
	echo "data preserved across install: identical file count/size"
fi
"${ADB[@]}" shell am force-stop "$PKG"

# /data/local/tmp is readable by the app; run-as copies into its private storage (files/).
# (run-as needs a debuggable build; with --release the launcher imports the game itself.)
if [ "$NOFILES" = 1 ]; then
	echo "=== files: skipped (--no-files) ==="
else
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
fi

if [ "$DEBUGGABLE" = 1 ]; then
	echo "data after deploy:"; pkg_inventory | sed 's/^/  /'
fi

if [ "$LAUNCH" = 1 ]; then
	"${ADB[@]}" logcat -c
	EXTRA=()
	[ "$PLAY" = 1 ] && EXTRA=(--ez autoplay true)
	"${ADB[@]}" shell am start -n "$PKG/org.devziyad.celeste.LauncherActivity" "${EXTRA[@]}"
	# The launcher logs its data inventory on start (also works for the non-debuggable release build).
	sleep 4
	"${ADB[@]}" logcat -d -s CelesteLauncher | grep "data:" | tail -n 1 | sed 's/^/launcher /' || true
fi
