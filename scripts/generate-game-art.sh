#!/usr/bin/env bash
# Generates the app icon and the launcher art from the user's own files (needs ImageMagick).
# Output (src/Celeste.Android/GameArt) is git-ignored: it's official game art, not part of the repository.
#   GameArt/icon/res   app icon (all builds): art/icon.png|jpg, or a crop of the game's key art
#   GameArt/art/res    launcher background + logo (personal builds only): SplashScreen.png, art/logo.png
# Usage: scripts/generate-game-art.sh [--game <dir>] [--icon <image>] [--logo <png>]
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_env.sh"

GAME="$ROOT/Celeste"; ICON=""; LOGO="$ROOT/art/logo.png"
while [ $# -gt 0 ]; do
	case "$1" in
		--game) GAME="$2"; shift 2 ;;
		--icon) ICON="$2"; shift 2 ;;
		--logo) LOGO="$2"; shift 2 ;;
		*) die "unknown argument: $1" ;;
	esac
done
for c in "$ROOT/art/icon.png" "$ROOT/art/icon.jpg"; do [ -z "$ICON" ] && [ -f "$c" ] && ICON="$c"; done
SPLASH="$GAME/Content/Graphics/SplashScreen.png"
OUT="$ROOT/src/Celeste.Android/GameArt"
rm -rf "$OUT"
ICONRES="$OUT/icon/res"; ARTRES="$OUT/art/res"

if ! command -v magick >/dev/null; then
	echo "ImageMagick not found: skipping game art (fallback icon will be used)." >&2
	exit 0
fi

SRC=""; CROP=""
if [ -n "$ICON" ]; then SRC="$ICON"
elif [ -f "$SPLASH" ]; then SRC="$SPLASH"; CROP="480x480+700+0"   # winged strawberry + Madeline's hand
fi
if [ -n "$SRC" ]; then
	declare -A DENS=([mdpi]=108 [hdpi]=162 [xhdpi]=216 [xxhdpi]=324 [xxxhdpi]=432)
	for d in "${!DENS[@]}"; do
		px=${DENS[$d]}; inset=$((px / 6)); inner=$((px - 2 * inset)); legacy=$((px * 48 / 108))
		dir="$ICONRES/mipmap-$d"; mkdir -p "$dir"
		base=(magick "$SRC"); [ -n "$CROP" ] && base+=(-crop "$CROP" +repage)
		"${base[@]}" -resize "${px}x${px}!" "$dir/ic_celeste_bg.png"
		"${base[@]}" -resize "${inner}x${inner}!" -background none -gravity center -extent "${px}x${px}" "$dir/ic_celeste_fg.png"
		"${base[@]}" -resize "${legacy}x${legacy}!" "$dir/ic_celeste.png"
	done
	mkdir -p "$ICONRES/mipmap-anydpi-v26"
	cat > "$ICONRES/mipmap-anydpi-v26/ic_celeste.xml" <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
	<background android:drawable="@mipmap/ic_celeste_bg" />
	<foreground android:drawable="@mipmap/ic_celeste_fg" />
</adaptive-icon>
XML
fi

if [ -f "$SPLASH" ]; then
	mkdir -p "$ARTRES/drawable-nodpi"
	magick "$SPLASH" -resize 1920x1080! -quality 88 "$ARTRES/drawable-nodpi/celeste_art.jpg"
fi
if [ -f "$LOGO" ]; then
	mkdir -p "$ARTRES/drawable-nodpi"
	magick "$LOGO" -resize '720x>' "$ARTRES/drawable-nodpi/celeste_logo.png"
fi
[ -d "$OUT" ] && (cd "$OUT" && find . -type f | sort) || true
