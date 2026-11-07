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
git clone --recursive https://github.com/BelmanteGu/CelesteAndroid
```

`external/FNA` (with FNA3D, FAudio, SDL3-CS...) and `external/SDL` (release-3.4.16) are submodules.

## 3. Files you have to supply

These are **not** in the repository and must never be committed (they're in `.gitignore`):

| Path | What |
|---|---|
| `Celeste/` | Your Celeste PC copy, **FNA build** (`Celeste.exe`, `FNA.dll`, `Content/`). Steam: *opengl* beta. itch.io: Linux zip. Used for compiling the patch module and, optionally, for personal builds. |
| `fmod/libs/android-arm64/` | `libfmod.so`, `libfmodstudio.so`, `fmod.jar` from the FMOD Engine **1.10.14** Android package (`api/lowlevel/lib/arm64-v8a`, `api/studio/lib/arm64-v8a`, `api/lowlevel/lib/fmod.jar`) |
| `Celeste/lib64/` | *(desktop host)* `libfmod.so.10` and `libfmodstudio.so.10` (FMOD 1.10, shipped with the Linux game); the desktop build copies them as `libfmod.so` / `libfmodstudio.so` |
| `art/icon.png` (or `.jpg`) | *(optional)* square image for the app icon, used by every build. Without it, personal builds crop the key art and public builds use the original mountain icon. |
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

The release package is `org.devziyad.celeste` ("Celeste (dev)") and the debug build is `org.devziyad.celeste.debug` ("Celeste (debug)"); `deploy-android.sh` uses the debug one unless `--release`. They install alongside the original app. The debug build is `run-as`-able, so the script copies files straight into the app's private storage. To regenerate the patch and background on the device without re-importing:

```
adb -s $ANDROID_SERIAL shell am start -n org.devziyad.celeste.debug/org.devziyad.celeste.LauncherActivity --ez repatch true
```

`scripts/deploy-android.sh --play` starts the game right away (the launcher gets an `autoplay` extra); `--content` also pushes `Celeste.exe` and `Content/`, which a freshly installed package ID needs once. The launcher is controller-first: D-pad / left stick move the focus, A confirms, B exits; focus and key events are logged under the `CelesteLauncher` logcat tag.


To force OpenGL ES: `--es driver OpenGL` on `GameActivity`, or the *Graphics* toggle in the launcher.
