#!/usr/bin/env bash
# Measures game startup on the phone: N cold launches (autoplay), each reported per phase from logcat, in ms after the launch
# (the system's "START ... GameActivity" line, i.e. the moment Play was pressed). Prints one line per launch and the averages.
# Usage: scripts/measure-startup.sh [--runs N] [--package <id>]     Uses $ANDROID_SERIAL.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_env.sh"
[ -n "${ANDROID_SERIAL:-}" ] || die "ANDROID_SERIAL is not set"
RUNS=3; PKG=org.devziyad.celeste.debug
while [ $# -gt 0 ]; do
	case "$1" in
		--runs) RUNS="$2"; shift ;;
		--package) PKG="$2"; shift ;;
		*) die "unknown argument: $1" ;;
	esac
	shift
done
a() { adb -s "$ANDROID_SERIAL" "$@"; }
LOGS="$(mktemp -d)"; trap 'rm -rf "$LOGS"' EXIT
for i in $(seq 1 "$RUNS"); do
	a shell am force-stop "$PKG"; sleep 2
	a logcat -c
	a shell am start -n "$PKG/org.devziyad.celeste.LauncherActivity" --ez autoplay true >/dev/null
	for _ in $(seq 1 90); do
		sleep 1
		a logcat -d | grep -q "DONE LOADING" && break
	done
	a logcat -d -v threadtime > "$LOGS/run$i.log"
	sleep 1
done
python3 -I - "$LOGS" "$RUNS" <<'PY'
import re, sys
from datetime import datetime
d, runs = sys.argv[1], int(sys.argv[2])
def ts(line):
    return datetime.strptime("2000-" + line[:18], "%Y-%m-%d %H:%M:%S.%f")
PHASES = [("process-start", r"Start proc \d+:[^ ]*:game"), ("runtime-ready", r"startup: runtime-ready"), ("sdl-main", r"startup: sdl-main"),
          ("assembly-load-begin", r"startup: assembly-load-begin"), ("assembly-load-end", r"startup: assembly-load-end"),
          ("celeste-main", r"startup: celeste-main"), ("game-displayed", r"GAME DISPLAYED"), ("done-loading", r"DONE LOADING")]
rows = []
for i in range(1, runs + 1):
    lines = open(f"{d}/run{i}.log", errors="replace").read().splitlines()
    t0 = next((ts(l) for l in lines if "ActivityTaskManager: START" in l and "GameActivity" in l), None)
    if t0 is None:
        print(f"run {i}: no GameActivity START line"); continue
    row = {}
    for name, rx in PHASES:
        l = next((l for l in lines if re.search(rx, l)), None)
        if l: row[name] = (ts(l) - t0).total_seconds() * 1000
    rows.append(row)
    print(f"run {i}: " + "  ".join(f"{n}={row[n]:.0f}" for n, _ in PHASES if n in row))
if rows:
    print("avg  : " + "  ".join(f"{n}={sum(r[n] for r in rows if n in r)/len([r for r in rows if n in r]):.0f}" for n, _ in PHASES if any(n in r for r in rows)) + "   (ms after Play)")
PY
