#!/usr/bin/env bash
# Checks the launcher UI on the phone without taking a screenshot: dumps the view hierarchy with uiautomator and reports every
# focusable/clickable/text control's bounds, which one has focus, and anything clipped by its parent or the screen.
# Usage: scripts/check-ui.sh [--all] [--package <id>]
#   --all      also list the controls that look fine (default: only the focused control and the problems)
#   --package  package whose windows to check (default org.devziyad.celeste.debug)
# Exit status: 0 = nothing flagged, 1 = something is clipped / off screen / has no room for the focus outline.
# Flags:
#   CLIPPED    the control's bounds stick out of its parent (a scrollable parent is only checked across its non-scroll axis)
#   OFFSCREEN  the control is (partly) outside the screen and its parent does not scroll
#   TIGHT      a focusable control touches the edge of its non-scrolling parent: its focus outline could be cut off
# Uses $ANDROID_SERIAL.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_env.sh"
[ -n "${ANDROID_SERIAL:-}" ] || die "ANDROID_SERIAL is not set"
ALL=0; PKG=org.devziyad.celeste.debug
while [ $# -gt 0 ]; do
	case "$1" in
		--all) ALL=1 ;;
		--package) PKG="$2"; shift ;;
		*) die "unknown argument: $1" ;;
	esac
	shift
done
XML="$(mktemp)"; trap 'rm -f "$XML"' EXIT
# uiautomator sometimes cannot get an idle state while a progress bar animates: retry a few times.
for attempt in 1 2 3 4 5; do
	adb -s "$ANDROID_SERIAL" shell uiautomator dump /sdcard/celeste-ui.xml 2>&1 | grep -q "dumped to" && break
	[ "$attempt" = 5 ] && die "uiautomator could not dump the UI"
	sleep 1
done
adb -s "$ANDROID_SERIAL" exec-out cat /sdcard/celeste-ui.xml > "$XML"
adb -s "$ANDROID_SERIAL" shell rm -f /sdcard/celeste-ui.xml
python3 -I - "$XML" "$ALL" "$PKG" <<'PY'
import re, sys
import xml.etree.ElementTree as ET

path, show_all, package = sys.argv[1], sys.argv[2] == "1", sys.argv[3]
root = ET.parse(path).getroot()

def bounds(node):
    m = re.match(r"\[(-?\d+),(-?\d+)\]\[(-?\d+),(-?\d+)\]", node.get("bounds", ""))
    return tuple(int(v) for v in m.groups()) if m else None

screen = bounds(root[0]) if len(root) else None
problems = 0
rows = []

def walk(node, parent_bounds, parent_scrolls):
    global problems
    b = bounds(node)
    pkg = node.get("package", "")
    interesting = (node.get("focusable") == "true" or node.get("clickable") == "true" or node.get("text")) and pkg == package
    if b and interesting:
        flags = []
        w, h = b[2] - b[0], b[3] - b[1]
        if w <= 0 or h <= 0:
            flags.append("EMPTY")
        if parent_bounds:
            p = parent_bounds
            out_x = b[0] < p[0] or b[2] > p[2]
            out_y = b[1] < p[1] or b[3] > p[3]
            if out_x or (out_y and not parent_scrolls):
                flags.append("CLIPPED")
            full_bleed = b == p  # a container that fills its parent (e.g. a dialog's dimmed overlay) has no outline to clip
            if node.get("focusable") == "true" and not parent_scrolls and not flags and not full_bleed:
                if b[0] <= p[0] or b[2] >= p[2] or b[1] <= p[1] or b[3] >= p[3]:
                    flags.append("TIGHT")
        if screen and (b[0] < screen[0] or b[2] > screen[2] or ((b[1] < screen[1] or b[3] > screen[3]) and not parent_scrolls)):
            flags.append("OFFSCREEN")
        focused = node.get("focused") == "true"
        name = node.get("content-desc") or node.get("text") or node.get("resource-id") or node.get("class")
        if flags or focused or show_all:
            rows.append((("FOCUS " if focused else "      ") + " ".join(flags), name, b))
        if flags:
            problems += 1
    scrolls = parent_scrolls or node.get("scrollable") == "true"  # rows inside a scroll list are cut by the viewport, not clipped
    for child in node:
        walk(child, b if b else parent_bounds, scrolls)

walk(root, None, False)
for flag, name, b in rows:
    print(f"{flag:<22} {str(name)[:110]:<44} [{b[0]},{b[1]}][{b[2]},{b[3]}]  {b[2]-b[0]}x{b[3]-b[1]}")
print(f"{problems} problem(s)" if problems else "OK: nothing clipped")
sys.exit(1 if problems else 0)
PY
