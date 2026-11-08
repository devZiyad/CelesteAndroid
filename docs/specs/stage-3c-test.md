# Stage 3c on-device test

Verifies items 1–7 of `stage-3c.md` on the phone. No screenshots: every check is a `scripts/check-ui.sh` report, a uiautomator dump read
by a script, or a logcat marker. Shell commands are fish. The debug build (`org.devziyad.celeste.debug`) is the target.

## Setup
```fish
set -x ANDROID_SERIAL <serial>            # wireless adb serial of the S23 Ultra
set PKG org.devziyad.celeste.debug
scripts/deploy-android.sh                  # updates in place; keeps game files and saves
# helpers used below
function key;   adb -s $ANDROID_SERIAL shell input keyevent $argv; end            # DPAD_DOWN=20 DPAD_UP=19 DPAD_LEFT=21 DPAD_RIGHT=22 ENTER=66 (A) BACK=4
function dump;  adb -s $ANDROID_SERIAL shell uiautomator dump /sdcard/ui.xml >/dev/null; adb -s $ANDROID_SERIAL exec-out cat /sdcard/ui.xml; end
function texts; dump | python3 -I -c "import sys,re; [print(m) for m in re.findall(r'text=\"([^\"]+)\"', sys.stdin.read())]"; end
function marks; adb -s $ANDROID_SERIAL logcat -d -s CelesteAndroid:I CelesteInstall:I CelesteLauncher:I CelesteConfig:I CelesteSteam:I; end
function start; adb -s $ANDROID_SERIAL shell am start -n $PKG/org.devziyad.celeste.LauncherActivity; end
adb -s $ANDROID_SERIAL logcat -c
```
Each section says which state it needs. `scripts/check-ui.sh` must exit 0 (nothing CLIPPED / OFFSCREEN / TIGHT) at every screen below; run it
as `scripts/check-ui.sh` (add `--all` to list every control). The FOCUS line it prints is the focused control.

## 1. No navigation legend (any state)
```fish
start; sleep 3
texts | grep -E "select|back|move|change value|next value"        # expect: no output
scripts/check-ui.sh --all | grep -ciE "D-pad|A  select"             # expect: 0
```
Open Config (focus Config with DPAD_DOWN, press ENTER) and repeat `texts | grep -E "D-pad|change value|next value"` → no output.

## 2. Renderer row without the note (Config > Port settings)
```fish
# on the Config screen
texts | grep -x "Renderer"                  # expect: Renderer
texts | grep -iE "applies on|next Play|next boot"   # expect: no output
```

## 3. Delete game files (installed state)
1. On Config go down to **Delete game files** (`scripts/check-ui.sh` FOCUS line shows `DeleteGameFiles`), press ENTER.
2. `scripts/check-ui.sh` → FOCUS `ConfirmCancel`; `texts` shows "Delete game files?", "Cancel", "Delete". B
   (gamepad) closes the dialog; with adb use `key 96` / `key 97` (BUTTON_A / BUTTON_B). Check B: `key 97`, then FOCUS is back on `DeleteGameFiles`, still in Config.
3. Reopen, move RIGHT (`key 22`) to **Delete** (`ConfirmYes`), press ENTER (`key 66`). Wait ~5 s.
4. ```fish
   texts | grep -E "Game files deleted — [0-9]+ MB freed"        # expect one line (the dialog title)
   scripts/check-ui.sh | grep FOCUS                                # expect: DialogOk; and `texts | grep -x OK` → one OK, no other button
   marks | grep "config dialog: Game files deleted"                 # marker
   ```
5. Press ENTER on OK. Expect: still on the Config screen (`texts | grep -x Config` and "Steam account" present), FOCUS = `CheckForUpdates` (Delete row is
   hidden now, the fallback is the next row of the same section); `texts | grep -x "Delete game files"` → no output.
6. Back to the main screen (`key 97`): `texts` must not contain "freed", "deleted", "Ready" or any message; the big button reads **Set up**.
7. Cancel path: with the game installed, open Delete and cancel; main screen afterwards shows Play and an empty line above it
   (`texts` has "Native Android port", "PLAY", "Config" and nothing else).

## 4. Not installed: Set up (needs the not-installed state, e.g. right after section 3)
```fish
start; sleep 3
scripts/check-ui.sh | grep FOCUS          # expect: Setup
texts | grep -E "^(Set up|Install from Steam|Import folder|Import \.zip)$"   # expect exactly: Set up
key 66; sleep 1
texts                                      # expect "Set up Celeste", "Steam", "Import from folder"
scripts/check-ui.sh | grep FOCUS          # expect: SetupSteam ; run again after `key 22` → SetupImportFolder
marks | grep "launcher dialog: Set up Celeste"
```
- **Import from folder**: focus `SetupImportFolder`, ENTER → the system folder picker opens (`adb shell dumpsys window | grep -i DocumentsUI`). Back out with `key 4`.
- **B closes the chooser**: `key 97` → chooser gone, FOCUS back on `Setup`.
- **Steam, signed in** (needs a stored session: `adb shell run-as $PKG ls files/steam` shows the token file):
  ```fish
  adb -s $ANDROID_SERIAL logcat -c
  # Set up → Steam (ENTER on SetupSteam)
  marks | grep "launcher: signed in to Steam, starting the download"      # marker, no sign-in screen
  texts | grep -E "Signed in as|Signing in to Steam as"                   # shows the account on the status line within ~1 s
  ```
- **Steam, signed out** (Config > Steam account > Sign out first, then delete the game as in section 3, or use `--ez steam true --ez forceLogin true`):
  ```fish
  adb -s $ANDROID_SERIAL logcat -c
  # Set up → Steam
  marks | grep "launcher: not signed in to Steam, opening the sign-in"
  texts | grep "Sign in to Steam"           # SteamActivity sign-in screen; check-ui.sh clean
  # sign in (username/password or QR on another device); do NOT tap anything afterwards
  sleep 20
  marks | grep -E "steam: login successful|steam: ownership confirmed|progress:"    # in this order, no tap in between
  texts | grep -E "Installing…|Downloading "                               # back on the main screen, installing
  ```
  Expect: after the login the app returns to the main screen by itself, the download starts, and `CelesteInstall: progress:` lines appear
  every 5 s. A second `input` event is never needed. If the account does not own Celeste: dialog "Couldn't install from Steam" with
  "Try again"/"OK" (`texts`), and nothing is installed.

## 5. No game .zip import
```fish
texts | grep -iE "\.zip"                                  # main screen, chooser, Config: only the two Saves rows "Import saves from a .zip" / "Export saves to a .zip" may appear
scripts/check-ui.sh --all | grep -ciE "ImportZip|ReimportZip"   # expect 0
```
On Config: Saves has "Import saves from a folder", "Import saves from a .zip", "Export saves to a folder", "Export saves to a .zip"; Game files has "Check for updates", "Re-import from folder", "Delete game files" (installed state).

## 6. Config dialogs return to Config
For each opener, record FOCUS before (`scripts/check-ui.sh | grep FOCUS`), open the dialog, close it, and compare:
| Opener (focus name) | Open | Close | Expect after close |
|---|---|---|---|
| `DeleteGameFiles` | ENTER | `key 97` (B) or Cancel | Config, FOCUS `DeleteGameFiles` |
| `DeleteGameFiles` → Delete | ENTER, RIGHT, ENTER | OK | Config, FOCUS `CheckForUpdates` (row removed; see 3) |
| `ExportSavesZip` (needs a save) | ENTER, pick a place in the picker, confirm | OK on "Saves exported" | Config, FOCUS `ExportSavesZip` |
| `ExportSavesFolder` | same with a folder | OK | Config, FOCUS `ExportSavesFolder` |
| `ImportSavesFolder` | pick a folder with `*.celeste` | OK on "Saves imported" | Config, FOCUS `ImportSavesFolder` |
| `ImportSavesFolder` with an empty folder | pick it | OK on "Couldn't finish" | Config, FOCUS `ImportSavesFolder` |
Throughout: `texts | grep -x "Native Android port"` → no output (never the main screen) and `marks | grep "config dialog:"` shows each dialog.
Also scroll check: with the opener far down the list (Steam account), the dialog closing keeps the list scrolled there (FOCUS bounds inside the screen, no OFFSCREEN).

## 7. Install progress on the main screen
Start a Steam install (section 4, signed in) and, while it runs:
```fish
function snap; dump > /tmp/ui-$argv[1].xml; end
for i in (seq 1 6); snap $i; sleep 5; end
python3 -I - <<'PY'
import re, glob
heights = {"status": set(), "stats": set(), "bar": set()}
for f in sorted(glob.glob("/tmp/ui-*.xml")):
    x = open(f).read()
    for m in re.finditer(r'<node[^>]*text="([^"]*)"[^>]*bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"', x):
        text, l, t, r, b = m.group(1), *map(int, m.groups()[1:])
        if text.startswith(("Downloading ", "Verifying ", "Connecting", "Signing in", "Checking", "Getting")): heights["status"].add((t, b)); print("status:", text, (l, r))
        if " files · " in text: heights["stats"].add((t, b)); print("stats: ", text)
print(heights)           # expect: exactly one (top,bottom) pair for status and for stats across all samples
PY
```
Expectations:
- **Button**: `scripts/check-ui.sh | grep FOCUS` → `Setup`, and `texts | grep -x "Installing…"` matches; no "view progress" anywhere (`texts | grep -i "view progress"` → nothing).
- **Status line** (under "Native Android port"): "Downloading Content/..." with the path shortened in the middle when long: any status text containing `…` has
  letters on both sides of it, none ends with `…`, and the status node's right edge never exceeds the panel (`check-ui.sh` reports no CLIPPED).
  Single line: the status node's height is identical in every sample.
- **Stats line** at the bottom, matching `^\d+ / \d+ files · \d+ (MB|GB) / [\d.]+ (MB|GB)( · [\d.]+ (MB|KB)/s)?$`; its (top,bottom) is identical in every sample, and so is the progress bar's
  (`scripts/check-ui.sh --all | grep -i progress`). Nothing wraps: no node text contains a newline.
- **Logcat**: `marks | grep "CelesteInstall: progress"` shows a line every ~5 s while the numbers in the stats line rise.
- **Cancel**: ENTER on **Installing…** → `texts` shows "Cancel the installation?", "Keep installing", "Cancel install"; FOCUS `KeepInstalling`.
  B closes it and the install keeps going. Reopen, RIGHT, ENTER on `CancelInstall` → within a few seconds the button reads **Set up** (`texts | grep -x "Set up"`), the bar and stats line are gone (`texts | grep " files · "` → nothing), `marks | grep "cancel requested"`.
- **Screen off / back to app**: lock the phone for 30 s, unlock, `start`: the main screen shows the live status again (service kept running).
- **Completion** (let the install finish, or run `--ez repatch true` on an already-downloaded tree):
  ```fish
  texts | grep -x "PLAY"                                  # the button became Play
  scripts/check-ui.sh | grep FOCUS                        # FOCUS Play
  texts | grep -E "Downloading|Verifying| files · |Installing"   # expect: no output (status line, bar and stats are gone)
  scripts/check-ui.sh --all | grep -ci "progress"         # expect 0
  ```
- **No dedicated progress screen**: during the whole install `adb shell dumpsys activity activities | grep -c SteamActivity` stays 0 once signed in
  (only the sign-in screen exists, and only while signing in).
- **Check for updates** (Config > Game files, game installed): Config closes, the main screen shows "Installing…" briefly; with nothing new the dialog
  "Celeste is up to date" (OK, focus `DialogOk`) appears, then OK → Play focused.
