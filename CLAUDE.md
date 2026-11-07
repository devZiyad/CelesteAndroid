# CelesteAndroid — project facts

Builds and runs entirely on Linux (CachyOS, fish shell). No Windows, no PowerShell. Give the user shell commands in **fish** syntax (`set -x VAR value`, not `export`).

## Game files (never modify, never commit)
- Vanilla game files live in `Celeste/` at the repo root: `Celeste.exe`, `FNA.dll`, `Content/`, `lib64/`. Gitignored.
- FMOD 1.10 Linux libs for the desktop host are in `Celeste/lib64` and are versioned (`libfmod.so.10`, `libfmodstudio.so.10`). The desktop build copies them next to the binary as `libfmod.so` / `libfmodstudio.so`.
- FMOD Android libs (`libfmod.so`, `libfmodstudio.so`, `fmod.jar`) are in `fmod/libs/android-arm64` (gitignored).
- The user's Everest-modded Steam install at `/mnt/games/SteamLibrary/steamapps/common/Celeste` is a **read-only reference**.
- Game files, FMOD libs and keystores must never be committed.

## Toolchain
- Before Android builds: `JAVA_HOME=~/.sdkman/candidates/java/17.0.20-tem`, `ANDROID_HOME=~/Android/Sdk`, `ANDROID_NDK_HOME=$ANDROID_HOME/ndk/27.3.13750724` (scripts default to these via `scripts/_env.sh`).
- .NET 10 SDK is in `~/.dotnet` (with the `android` workload); `scripts/_env.sh` puts it on PATH.
- CMake is 4.x: pass `-DCMAKE_POLICY_VERSION_MINIMUM=3.5` if a submodule declares an older minimum (the native build script already does).

## Device
- Galaxy S23 Ultra (Adreno 740) over wireless adb. Serial is in `$ANDROID_SERIAL`; always use it (`adb -s $ANDROID_SERIAL ...`).
- Packages: release `org.devziyad.celeste` ("Celeste (dev)"), Debug `org.devziyad.celeste.debug` ("Celeste (debug)"); both install side by side and alongside the original app. `scripts/deploy-android.sh` targets the debug ID by default, `--release` targets the release ID. Game process: `<package>:game`. Activity class names stay `org.devziyad.celeste.*` in both (use `pkg/org.devziyad.celeste.LauncherActivity`, not `.LauncherActivity`).
- `scripts/deploy-android.sh` never uninstalls and installs with `adb install -r` (updates in place, keeps game files and saves). It checks the app kept its uid/firstInstallTime and, for the debug build, its file count/size. If an install needs the old app removed (signature mismatch, downgrade) it stops and says so; never run `adb uninstall` on these packages without backing up `files/userdata` first (`adb exec-out run-as <pkg> tar -cf - files/userdata`, debug build only).

## Signing
- Release signing: `~/.celeste-android/release.jks`, alias `celeste`, password in `$CELESTE_KEYSTORE_PASS`. Never print or write the password.

## Scripts (`scripts/`)
- `build-natives-android.sh` — SDL3/FNA3D/FAudio for arm64-v8a → `natives/android-arm64`
- `fetch-fnalibs.sh` — prebuilt linux-x64 fnalibs → `natives/linux-x64` (desktop host)
- `build-release.sh` — signed release APK → `out/`
- `deploy-android.sh` — patch, install, push files, launch on the phone
- `generate-game-art.sh` — icon/launcher art from the user's files (ImageMagick)

See `docs/BUILDING.md` and `docs/ARCHITECTURE.md`.

## Git workflow
- Work on a branch named `stage-N` created from `main`. `main` stays at `origin/main`.
- Small, focused commits, one per working change, each only after it builds and its check passes. Never commit a broken build.
- Conventional, imperative commit messages (`feat:`, `fix:`, `docs:`, `build:`...) with a short "why" body when not obvious.
- Never rewrite pushed history or force-push.
- Push the branch to origin when the goal is met. Never merge into `main` — the user merges after testing.

## MonoMod patches (`src/Celeste.Android.Patches`)
- **Additive hooks only.** Wrap with `orig_X()` plus extra logic, or add new members/types; never `[MonoModReplace]` a whole method and never
  rewrite a menu-building method (append items after `orig_Create` instead). This keeps the patches compatible with Everest (stage 5).
  `docs/EVEREST.md` holds the audit table against the Everest source in `refs/Everest` (gitignored shallow checkout of the commit matching the
  installed Everest) and the known conflicts.

## Verification
- The phone is the only source of truth: verify on `$ANDROID_SERIAL` (screenshots, logcat). The desktop host (`src/Celeste.Desktop`) is an optional
  dev tool, not a verification target: use it only when it genuinely speeds up iterating on a game patch or debugging a crash, and just make sure
  it still compiles.

## Launcher UI
- Every new or changed launcher screen or dialog must be screenshot-checked on the phone with each control focused (gamepad keys), and nothing may be clipped (buttons, focus outlines, text). No fixed heights: use `UiKit` (`Pill`, `ButtonParams`, `NoClip`), wrap_content with margin for the focus outline, and a ScrollView where content may not fit.

## Image reads (save usage)
- Minimize image reads. Launcher UI checks: `scripts/check-ui.sh` (uses `adb shell uiautomator dump`) reports each control's bounds and focus and flags anything clipped by its parent or the screen; no screenshots for clipping or focus checks.
- Game checks: logcat markers (`[CelesteAndroid] ...` lines for menu contents, overlay state, screen-fit viewport rect, pause state) plus scripted pixel measurements on the PC (ImageMagick/PIL on the screenshot file; never read the image into context).
- Read an image only when a scripted check fails or a visual cannot be verified otherwise, and then downscale to <= 800 px wide JPEG and crop to the relevant region first.
- Never read the same screen twice to "confirm": trust the scripted check.

## Phone connectivity
- Never disable the phone's Wi-Fi or mobile data (`svc wifi/data disable`, airplane mode, ...): wireless adb is the only link and it may not come back. For offline checks, block only the app's network (per-app rule) or have the user do the offline step.

## Language
- All code comments, docs, log messages and commit messages must be in English. When you edit a file that still has Portuguese comments, translate the comments in the parts you touch.
