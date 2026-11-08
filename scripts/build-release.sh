#!/usr/bin/env bash
# Builds the release APK into out/.
#   out/CelesteAndroid-<version>.apk           public build: no official art, safe to share
#   out/CelesteAndroid-<version>-personal.apk  (--personal) official icon/logo from your own game files
#                                              (+ --embed-game: your game inside the APK) - NEVER share it
# Signing: ~/.celeste-android/release.jks, alias "celeste", password in $CELESTE_KEYSTORE_PASS.
# Override with CELESTE_KEYSTORE / CELESTE_KEYSTORE_ALIAS. Without a password: debug key.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_env.sh"

PERSONAL=0; EMBED=0
for a in "$@"; do
	case "$a" in
		--personal) PERSONAL=1 ;;
		--embed-game) EMBED=1 ;;
		*) die "unknown argument: $a" ;;
	esac
done

PROJECT="$ROOT/src/Celeste.Android/Celeste.Android.csproj"
OUT="$ROOT/out"; mkdir -p "$OUT"
VERSION="$(git -C "$ROOT" describe --tags --always --dirty | sed 's/^v//')"

KEYSTORE="${CELESTE_KEYSTORE:-$HOME/.celeste-android/release.jks}"
ALIAS="${CELESTE_KEYSTORE_ALIAS:-celeste}"
SIGN=()
if [ -f "$KEYSTORE" ] && [ -n "${CELESTE_KEYSTORE_PASS:-}" ]; then
	# Passed through env:, so the password never appears on the command line or in a file.
	SIGN=("-p:AndroidSigningKeyStore=$KEYSTORE" "-p:AndroidSigningKeyAlias=$ALIAS"
		"-p:AndroidSigningStorePass=env:CELESTE_KEYSTORE_PASS" "-p:AndroidSigningKeyPass=env:CELESTE_KEYSTORE_PASS")
else
	echo "warning: no release keystore/password: signing with the debug key." >&2
fi

# The icon (art/icon.*) goes into every build; key art and logo only into personal ones.
"$ROOT/scripts/generate-game-art.sh" >/dev/null

if [ "$PERSONAL" = 1 ]; then
	NAME="CelesteAndroid-$VERSION-personal.apk"; FLAGS=(-p:UseGameArt=true)
	[ "$EMBED" = 1 ] && FLAGS+=(-p:EmbedGame=true)
else
	NAME="CelesteAndroid-$VERSION.apk"; FLAGS=(-p:UseGameArt=false)
fi

PUBLISH="$ROOT/build/publish"
rm -rf "$PUBLISH" "$ROOT/src/Celeste.Android/obj/Release" "$ROOT/src/Celeste.Android/bin/Release"
dotnet publish "$PROJECT" -c Release -o "$PUBLISH" "${FLAGS[@]}" "${SIGN[@]}" -nologo -v q
APK="$(find "$PUBLISH" -maxdepth 1 -name '*-Signed.apk' | head -n1)"
[ -n "$APK" ] || die "signed APK not found"

if [ "$PERSONAL" = 0 ] && unzip -l "$APK" | grep -Eq 'assets/game/|celeste_art|celeste_logo'; then
	die "public APK contains game files/key art/logo; aborting."
fi
cp -f "$APK" "$OUT/$NAME"
ls -l "$OUT/$NAME"
