# Building CelesteAndroid

This guide builds the APK from source on **Linux** (developed on CachyOS). The scripts are bash; commands below are shown for fish.

## 1. Toolchain

| Tool | Version used | Notes |
|---|---|---|
| .NET SDK | 10.0.x | `dotnet-install.sh` into `~/.dotnet` |
| .NET Android workload | 36.1 | `dotnet workload install android` |
| JDK | 17 (Temurin) | `sdk install java 17.0.20-tem` |
| Android SDK | platform 36, build-tools 36.0.0, platform-tools | via `cmdline-tools` |
| Android NDK | **r27d** (27.3.13750724) | LTS; used for SDL3/FNA3D/FAudio |
| CMake + Ninja | CMake 3.24+ (4.x works) / Ninja | `scripts/build-natives-android.sh` passes `-DCMAKE_POLICY_VERSION_MINIMUM=3.5` |
| ImageMagick | any | only for `scripts/generate-game-art.sh` |

Environment (fish):

```fish
set -x JAVA_HOME ~/.sdkman/candidates/java/17.0.20-tem
set -x ANDROID_HOME ~/Android/Sdk
set -x ANDROID_NDK_HOME $ANDROID_HOME/ndk/27.3.13750724
fish_add_path ~/.dotnet
```

The scripts default to these values, so setting them is optional if your layout matches.

## 2. Clone

```fish
git clone --recursive https://github.com/devZiyad/CelesteAndroid
```

`external/FNA` (with FNA3D, FAudio, SDL3-CS...) and `external/SDL` (release-3.4.20) are submodules.

## 3. Files you have to supply

These are **not** in the repository and must never be committed (they're in `.gitignore`):

| Path | What |
|---|---|
| `Celeste/` | Your Celeste PC copy, **FNA build** (`Celeste.exe`, `FNA.dll`, `Content/`). Steam: *opengl* beta. itch.io: the unzipped Linux build. Used for compiling the patch module and, optionally, for personal builds. |
| `fmod/libs/android-arm64/` | `libfmod.so`, `libfmodstudio.so`, `fmod.jar` from the FMOD Engine **1.10.14** Android package (`api/lowlevel/lib/arm64-v8a`, `api/studio/lib/arm64-v8a`, `api/lowlevel/lib/fmod.jar`) |
| `Celeste/lib64/` | *(desktop host)* `libfmod.so.10` and `libfmodstudio.so.10` (FMOD 1.10, shipped with the Linux game); the desktop build copies them as `libfmod.so` / `libfmodstudio.so` |
| `art/icon.png` (or `.jpg`) | *(optional)* square image for the app icon of **personal** builds. Without it they crop the key art. Public builds always use the original mountain icon. |
| `art/logo.png` | *(optional, personal builds)* logo shown in the launcher instead of the text title |

Why 1.10.14: Celeste's `.bank` files were built with FMOD Studio 1.10, and the game's C# FMOD wrapper matches that API.

## 4. Native libraries

```fish
scripts/build-natives-android.sh           # -> natives/android-arm64/libSDL3.so, libFNA3D.so, libFAudio.so
```

API 26, arm64-v8a, `ANDROID_SUPPORT_FLEXIBLE_PAGE_SIZES=ON` (16 KB page alignment for Android 15+).

## 5. APK

```fish
scripts/build-release.sh                   # public APK (no official art) -> out/
scripts/build-release.sh --personal        # official icon/logo from YOUR game files (don't share)
scripts/build-release.sh --personal --embed-game   # also bundles your game (~860 MB; don't share)
```

The version name is `git describe --tags --always --dirty` (the fork point is tagged `v1.0.1`) and the version code is the commit count, both set by the project on every build (`_CelesteVersion` in `Celeste.Android.csproj`); Config > About shows them and the APK is named after the version. Release label: "Celeste"; debug: "Celeste (debug)".

Signing: uses `~/.celeste-android/release.jks`, alias `celeste` and the password in `$CELESTE_KEYSTORE_PASS` (override path/alias with `CELESTE_KEYSTORE` / `CELESTE_KEYSTORE_ALIAS`); without a password the debug key is used. Verify with `$ANDROID_HOME/build-tools/36.0.0/apksigner verify --print-certs out/*.apk`. Keep the same key across builds, or Android won't let you update without uninstalling (which deletes saves).

## 6. Desktop test host (optional)

`src/Celeste.Desktop` runs the exact same patched `Celeste.dll` on Linux x64. It's the fastest way to test patches:

```fish
scripts/fetch-fnalibs.sh                   # SDL3/FNA3D/FAudio linux-x64 -> natives/linux-x64
dotnet run --project src/Celeste.Desktop   # finds ./Celeste automatically, or use --game <dir>
```

It behaves like the Android build (`Platform = "Android"`, saves go to `bin/.../userdata`), so your real PC saves are never touched.

## 7. Development loop on a device

```fish
scripts/deploy-android.sh --content        # first time: also pushes Content/ (1 GB)
scripts/deploy-android.sh                  # afterwards: patch + debug APK + patched dll
adb -s $ANDROID_SERIAL logcat -s CelesteAndroid DOTNET SDL SDL/GPU fmod
```

The release package is `org.devziyad.celeste` ("Celeste") and the debug build is `org.devziyad.celeste.debug` ("Celeste (debug)"); `deploy-android.sh` uses the debug one unless `--release`. They install alongside the original app. The debug build is `run-as`-able, so the script copies files straight into the app's private storage. To regenerate the patch and background on the device without re-importing:

```
adb -s $ANDROID_SERIAL shell am start -n org.devziyad.celeste.debug/org.devziyad.celeste.LauncherActivity --ez repatch true
```

`scripts/deploy-android.sh --play` starts the game right away (the launcher gets an `autoplay` extra); `--content` also pushes `Celeste.exe` and `Content/`, which a freshly installed package ID needs once. The launcher is controller-first: D-pad / left stick move the focus (wrapping around at the ends), A confirms, B goes back, L1/R1 switch the Config tabs; focus and key events are logged under the `CelesteLauncher` / `CelesteConfig` logcat tags.

### Checks on the phone

```fish
scripts/check-ui.sh                        # focused control + anything clipped in the current launcher screen (uiautomator, no screenshot)
scripts/measure-startup.sh --runs 3        # cold launches (autoplay): ms after Play for each phase, ending at Celeste's "DONE LOADING"
adb -s $ANDROID_SERIAL shell input keyevent KEYCODE_BUTTON_R1    # L1 / R1: Config tabs
```

`measure-startup.sh` reads `[CelesteAndroid] startup: <phase> +Nms` markers (`StartupTrace`) and Celeste's own `GAME DISPLAYED` / `DONE LOADING` lines from logcat; the phases are the process start, the runtime ready, the SDL main thread, the assembly load and Celeste's `Main`. The script passes the `autoplay` extra itself, so the Autoplay port setting does not matter.


To force OpenGL ES: `--es driver OpenGL` on `GameActivity`, or the *Graphics* toggle in the launcher.

## Port settings on the desktop host

The desktop host reads the same options as the launcher's *Port settings* screen from `port-settings.json` next to the host binary (or `--settings <file>`), overridden by flags:

```fish
dotnet run --project src/Celeste.Desktop -- --overlay graph --corner topright --screen-fit blackbars --integer-scaling on --frame-cap 60 --vsync on --res-scale 1 --renderer vulkan
```

JSON keys: `renderer`, `frameCap`, `vsync`, `resolutionScale`, `overlay`, `overlayCorner`, `screenFit`, `integerScaling`.

On the phone, `adb shell input keyevent` presses are released within a millisecond and the game polls input once per frame, so send held presses to drive the game (`adb shell input keyevent --duration 90 KEYCODE_C`; Celeste confirm = `C`, back = `X`).

## Why the debug app's data was once wiped (root cause)

On 2026-11-07 the debug package's game files and saves disappeared. Root cause: the apps were uninstalled by hand from the phone's home screen (around 20:19), so the next `deploy-android.sh` run (20:25) was a *fresh* install, not an update. The deploy script itself was not at fault: it uses `adb install -r`, has no uninstall path, the debug keystore and `versionCode` were unchanged, and no Fast Deployment is used. The device log agrees: the system uninstaller was started from the Samsung home screen at 20:19:04 and the 20:25 install has no "update" markers and a new uid.

The script still guards against this class of problem: it prints a `NOTE: ... FRESH install` when the package is not installed, verifies that an install updated the app in place and left its data unchanged, and stops (never uninstalls) if an update is impossible.
