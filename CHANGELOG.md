# Changelog

Changes since this repository was forked from [BelmanteGu/CelesteAndroid](https://github.com/BelmanteGu/CelesteAndroid) at `v1.0.1`.
The app's version is `git describe` (tag, commits since it, commit); its version code is the commit count.
Config > About shows both.

## Stage 3d — polish
- Config is now five tabs (Port settings, Saves, Game files, Steam account, About); L1/R1 switch tabs.
- Up/Down/Left/Right wrap around on every launcher screen, in dialogs and across tabs.
- Release label "Celeste" (debug: "Celeste (debug)"); splash screen; consistent launcher look.
- Share logs (Config > About): one plain `.txt` with recent logcat, Celeste's error log, version, device and port settings.
- Version from `git describe`, version code from the commit count, shown in About.
- Startup phase markers and `scripts/measure-startup.sh` (per-phase launch timings from logcat).
- Cleanup of dead code, README/docs brought up to date, review of the original repository's newer commits.

## Stage 3c — install from Steam, polished
- Set up chooser (Steam / import from folder); install progress and stats on the main screen; Config dialogs.
- Install resumes after a dropped connection or a killed process; timeouts are retried, not treated as a cancel.
- Saves import from a folder works without a `Saves/` subfolder; the saves `.zip` import/export was removed.
- System Back shows "Return to launcher?" (Yes = save and quit); gamepad buttons stay game input.
- Game state markers in logcat (title, main menu, file select, level, pause menu) for scripted checks.
- `scripts/check-ui.sh`: launcher UI check without screenshots.

## Stage 3b — services, Config, lifecycle
- Steam download in a foreground service with a progress notification, Wi-Fi and wake locks; resumes when it can.
- Username/password sign-in with Steam Guard by default, QR code from another device as the second option.
- Config screen (Port settings, Saves, Game files, Steam account); Check for updates; Sign out / Switch account.
- Autoplay setting and an "Exit to launcher" item in the pause menu.
- The game pauses and suspends FMOD in the background; landscape-only game and launcher.
- In-game Options > PORT lists only what applies live.

## Stage 3a — Steam installer
- Install the game from Steam (SteamKit2): depot downloader core with unit tests, sign-in session, delete-game-files.
- Debug launch options to preview the Steam sign-in and error screens.

## Stage 2 — port settings and controller-first launcher
- Port settings (renderer, frame cap, VSync, resolution scale, FPS overlay, screen fit, integer scaling) shared by the launcher and the
  game through one JSON file; in-game Options > PORT section.
- Launcher fully navigable with a gamepad; focus outlines never clipped.
- Patches turned into additive hooks (Everest-compatible); Everest patch audit in `docs/EVEREST.md`.
- SDL 3.4.20, FNA 26.11; frame cap limited to 30 or 60.
- Deploy script never clears app data and proves it.

## Stage 1 — Linux fork
- Builds and runs on Linux only (bash scripts replace PowerShell); package renamed to `org.devziyad.celeste`.
- Debug build gets its own application ID and label so it installs next to the release build.
- Desktop host retargeted to linux-x64.
