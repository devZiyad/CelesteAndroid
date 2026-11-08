# Architecture and porting notes

This document covers how CelesteAndroid works and every non-obvious problem found while porting, so the next person (or the next port of an FNA game) doesn't have to rediscover them.

## Why this is possible at all

Celeste is a C# game built on **FNA** (an XNA 4 reimplementation). `Celeste.exe` isn't x86 machine code: it's .NET IL, which is architecture independent. So the port doesn't decompile or rewrite the game. It provides:

1. a .NET runtime on ARM64 (.NET 10 for Android, Mono runtime),
2. FNA (C#) plus its native dependencies compiled for Android (SDL3, FNA3D, FAudio),
3. FMOD, the audio middleware the game calls directly,
4. a small set of patches, applied with MonoMod to a copy of the user's `Celeste.exe`.

## Components

```
src/
  Celeste.Android/           .NET for Android app (launcher + game activity)
    LauncherActivity.cs      main screen: key art, Play (or Set up / Installing… when not playable) and Config; install progress (status line, bar, stats line); autoplay
    ConfigActivity.cs        Config, five tabs (L1/R1): Port settings, Saves (import/export), Game files (check for updates, re-import, delete), Steam account, About (version, Share logs)
    ControllerActivity.cs    base of the launcher screens: D-pad/stick focus movement with wrap-around, A/B/L1/R1, focus logging
    UiKit.cs, UiDialog.cs    shared look: type scale, focus style, key-art backdrop, splash/loading screen, pill buttons, modal dialog
    LogShare.cs              Share logs: scrubbed plain-text report + read-only content provider for the share sheet
    StartupTrace.cs          startup phase markers in logcat (read by scripts/measure-startup.sh)
    SteamActivity.cs         sign-in only (username/password + Steam Guard, or QR for another device); returns to the launcher, which starts the install
    InstallService.cs        foreground service (dataSync) that downloads and installs from Steam; InstallPipeline.cs is one attempt, InstallCoordinator.cs the shared state
    GameInstaller.cs         import (SAF folder, embedded assets) → MonoMod patch → blurred background; saves import/export
    GameActivity.cs          SDLActivity subclass, process ":game"; runs the game on the SDL thread
    CelesteLauncher.cs       loads the patched Celeste.dll and invokes Celeste.Main via reflection
  Celeste.Android.Patches/   MonoMod patch module (Celeste.Android.mm.dll), merged into Celeste.dll
  Celeste.Patcher/           wraps MonoModder: Celeste.exe + mm.dll → Celeste.dll (desktop and device)
  Celeste.Desktop/           Linux x64 host running the same patched dll (optional dev tool)
  Celeste.Steam/             Steam install logic without Android dependencies (SteamKit2 session, depot download, log scrubber); tests in tests/
  Steamworks.NET/            stub with the exact signatures Celeste references
external/FNA, external/SDL   submodules
scripts/                     natives build, release build, device deploy, art extraction
```

## Boot sequence on Android

1. **LauncherActivity** (main process). If the game isn't installed: the user picks a folder (Set up > Import from folder) → `GameInstaller` copies `Celeste.exe` + `Content/` into `files/Celeste` (internal storage) → runs **MonoMod on the device** → `files/patched/Celeste.dll` → generates `files/background.png`.
2. **PLAY** starts **GameActivity** in a separate process (`:game`). Celeste and FNA keep a lot of static state, so every session gets a fresh process, and the process is killed when the activity finishes.
3. `GameActivity` extends SDL's `SDLActivity` (SDL's Java sources are compiled into the APK and bound to C# with `AndroidJavaSource Bind="true"`). It overrides:
   - `getLibraries()` → `SDL3`, `fmod`, `fmodstudio` (FMOD must be loaded before `FMOD.init`);
   - `main()` → runs managed code **directly on the SDLThread**, instead of SDL's `nativeRunMain`/`SDL_main`. FNA calls `SDL_SetMainReady` itself, so nothing is lost.
4. `CelesteLauncher.Run` passes configuration to the patched game through `AppContext` data (`CelesteAndroid.Platform`, `.PrefPath`, `.BackgroundPath`), overrides `APP_CONTEXT_BASE_DIRECTORY`, sets the working directory to the game folder, sets `Monocle.Engine.AssemblyDirectory` by reflection and invokes the private static `Celeste.Main(string[])`.

## The patches (`Celeste.Android.Patches`)

| Problem | Fix |
|---|---|
| `Main` calls `SteamAPI.Init()` / `RestartAppIfNecessary`; achievements and stats call Steamworks | `Steamworks.NET` stub assembly: same names and signatures (they must match the IL), all no-ops, `Init()` returns true |
| Celeste calls SDL**2** directly (`SDL_GetPlatform`, `SDL_GetPrefPath`), but current FNA runs on SDL**3** | `[MonoModLinkFrom]` relinks both to `SDLShim`: the platform comes from the host (`"Android"`), and the pref path points to the app's private storage. This also decides where saves go (`UserIO.GetSavePath`). |
| `Assembly.GetEntryAssembly()` is used to scan the game's own types (Tracker, Pooler, Overworld, Commands). Under a host the entry assembly is the host (or null on Android). | Relinked to return the Celeste assembly |
| `Engine.AssemblyDirectory` comes from `Assembly.Location` (empty when loaded on Android) | Set by reflection from the host |
| `GCSettings.LatencyMode = SustainedLowLatency` throws `PlatformNotSupportedException` on the Android Mono runtime | Relinked to a shim that sets it via reflection and ignores that exception. It *must* go through reflection: a direct call inside the shim would itself be relinked → infinite recursion. |
| `ErrorLog.Open()` launches the log with `Process.Start` | Hook: a no-op on Android (the log is still written; Config > About > Share logs includes it) |
| 19.5:9 screens show black pillarbox bars | `Engine.RenderCore` replaced: draws `background.png` (blurred, darkened key art) full-screen, then fills the 16:9 game viewport with the clear color and renders the scene as usual |
| `Celeste.exe` is flagged 32-bit (`32BITREQUIRED`) | The patcher clears the flag (ILOnly, AnyCPU) |

`BinaryFormatter` (removed from modern .NET) appears in `Monocle.SaveLoad`, but nothing calls it.

## Native side

- **SDL 3.4.20**, **FNA3D** and **FAudio** are built with NDK r27d for arm64-v8a, API 26, with 16 KB page alignment. They depend only on system libraries (no `libc++_shared`).
- FNA3D includes both drivers. The default is **SDL_GPU → Vulkan** (Adreno 740: *Vulkan Conformance 1.3*). OpenGL ES can be forced with `FNA3D_FORCE_DRIVER=OpenGL`. It has to be set with `SDL_SetHint`: on Unix, .NET's `Environment.SetEnvironmentVariable` doesn't reach native `getenv`.
- SPIR-V goes straight to Vulkan. `SDL_shadercross` is optional, loaded at runtime only if present, and not needed.
- `FNADllMap` is inert on Android (no `.dll.config`), so `DllImport("SDL3")` resolves to `libSDL3.so` naturally.

## FMOD

- Celeste's banks need **FMOD Studio 1.10.x**. Version 1.10.14 is still downloadable from fmod.com ("Older" → "Unsupported"). The arm64 libs only depend on system libraries and are 64 KB aligned.
- On Android FMOD needs its Java part: `fmod.jar` is bound (`AndroidLibrary Bind="true"`), and `org.fmod.FMOD.init(context)` is called in `GameActivity.OnCreate`, after SDL's Java code has loaded `libfmod.so`.
- On Windows (desktop host), `fmodstudio64.dll` imports **`fmod64.dll`** by name, so that file must exist next to `fmod.dll` (which is the name the game's `DllImport("fmod")` looks for).

## On-device patching

MonoMod (and Mono.Cecil) run inside the launcher. The things that were needed:

- MonoMod needs **files**, not the .NET assembly store. So the APK carries `assets/patcher/`: the patch module, `FNA.dll`, `Steamworks.NET.dll` and **`MonoMod.Patcher.dll`** (it defines the `[MonoModPatch]` attributes the relinker must resolve). An MSBuild target adds them as assets after reference resolution.
- `ReadingMode.Deferred`: in `Immediate` mode Cecil resolves every custom attribute's types up front (for example `DebuggableAttribute`'s enum in `mscorlib`), and the .NET Framework reference assemblies don't exist as files on Android.

## Release build

- **No trimming, no AOT.** The game is loaded at runtime and uses reflection and `XmlSerializer` over the whole framework, including the .NET Framework facades (`mscorlib`, `System.Xml`...) that `Celeste.exe` references. AOT requires trimming in .NET for Android, so the release runs on JIT, like the debug build.
- Official art is generated by `scripts/generate-game-art.sh` into `GameArt/` (git-ignored), in two independent parts:
  - `GameArt/icon`: the app icon (`art/icon.png|jpg`, or a crop of the key art). The whole image sits in the central 72dp of the adaptive icon, so launcher masks never crop the face. Used by personal builds and by the debug build when present; the public APK is built with `-p:UseGameIcon=false` and keeps the fallback mountain icon.
  - `GameArt/art`: the launcher background (the user's `Content/Graphics/SplashScreen.png`) and logo (`art/logo.png`), **personal builds only**. The public APK is built with `-p:UseGameArt=false` and shows the key art from the *imported* game instead.
  Resource *aliases* (`@mipmap/app_icon`, `@drawable/launcher_art`) switch between the generated art and the original fallbacks. `build-release.sh` always builds clean and aborts if a public APK contains game files, the key art, the logo or the generated icon.

## Gotchas found along the way

- **adb-pushed files in `Android/data/<pkg>`** are visible to `run-as`, but the running app didn't see them. The fix was to use internal storage (`files/`), which also avoids FUSE overhead for 1 GB of content.
- **Debug APKs installed by hand** need `EmbedAssembliesIntoApk=true`. Otherwise Fast Deployment aborts with *"No assemblies found"*.
- **SDL Java bindings**: a few protected fields use package-private types. They're removed in `Transforms/Metadata.xml`.
- **FNA viewport after `ApplyChanges`**: on Android the internal viewport stays at the first backbuffer size (800×480). Celeste sets its own viewport every frame, so it isn't affected. `HelloGame` resets it explicitly.
- **Touch**: SDL reports **3 touch devices** on the S23, but FNA's `UpdateTouchPanelState` only polls device index 0, so touches never reach `TouchPanel`. This has to be fixed before on-screen controls.
- **fnalibs**: the old archive URL is gone. The dailies are GitHub Actions artifacts, fetched via nightly.link (`scripts/fetch-fnalibs.sh`).

## Roadmap

- On-screen touch controls (fix FNA's touch device polling, then draw them in the pillarbox bars)
- Everest (mod loader) support
- More devices tested (Mali, Xclipse, Tensor)

## Port settings, display modes and overlay

- **One settings file, two editors.** The options live in `files/userdata/port-settings.json` (a flat JSON object, see
  `PortSettingsCatalog.cs`, compiled into the patches, the app and the desktop host). The launcher's *Config > Port settings* section
  (`ConfigActivity`, for pre-boot use) and the **PORT section of Celeste's own Options menu** both read and write it, so a change
  in one shows up in the other. `CelesteLauncher.Run` publishes the values as `AppContext` keys `CelesteAndroid.<Key>` plus the file path
  (`CelesteAndroid.SettingsFile`); in game, `PortConfig.Set` updates the `AppContext` value (read every frame) and rewrites the file.
  Overlay, overlay corner, screen fit, integer scaling, frame cap, vsync and resolution scale apply immediately (`PortLive`); the
  renderer is changed only in the launcher's Config and takes effect on the next Play.
- **PORT menu** (`Celeste/patch_MenuOptions.cs`): an additive hook. The original menu is built by `orig_Create`; the hook then only appends a
  `PORT` sub-header and `TextMenu.Slider`/`OnOff` items (same style, font, sounds and controller navigation as VIDEO/AUDIO).
- **Additive-hook rule.** Port patches must never replace or rewrite a vanilla method (`[MonoModReplace]`, or re-implementing a
  menu-building method). Use `orig_X()` wrappers/hooks and append items/members, so the patches coexist with Everest's. The audit table
  and the remaining conflicts are in `docs/EVEREST.md`.

- **Display modes.** On Android (`HostConfig.IsAndroid`) the game always runs fullscreen at the device's native resolution times *Resolution scale* (FNA3D upscales a smaller backbuffer on present; the scale also applies live from the PORT menu). `Engine.SetWindowed` redirects to `SetFullscreen`, `Settings.ApplyScreen` forces `Fullscreen = true` before the original runs, and the *Fullscreen* / *Window Scale* items are hidden in the Video menu (all as hooks around the originals, see `docs/EVEREST.md`). The desktop host keeps them.
- **Screen fit.** `Engine.UpdateView` is post-processed by `PortView.Adjust` (stretch, integer scaling on the 320x180 pixel grid); after `orig_RenderCore` the bars outside the game viewport are painted with the side art (scissored) or black, then the overlay is drawn.
- **Frame cap.** Only 30 or 60. The game logic stays at a fixed 60 Hz (the physics depends on it), so a higher cap could never produce more frames: 30 skips every other draw, 60 draws once per update. A value above 60 saved by an older version (90 / 120 / unlimited) is migrated to 60 by the launcher (`PortSettings.MigrateFrameCap`), and the game clamps any other source (desktop JSON/flags) to 60; the startup log notes it (`[CelesteAndroid] setting frame_cap=60 (requested 120, limited to 60 ...)`).
- **Overlay.** `PortOverlay.Draw` runs at the end of `Engine.RenderCore` and logs `[CelesteAndroid] fps ...` once per second. Applied settings are logged at startup as `[CelesteAndroid] setting name=value`.

## App lifecycle, orientation and back (stage 3b)

- **Background / foreground.** `PortLifecycle` installs an SDL event watch (`SDL_AddEventWatch`). SDL raises `WILL_ENTER_BACKGROUND` from inside its event
  pump, i.e. on the game thread, and then blocks that pump until the app resumes. The handler opens Celeste's pause menu (`Level.Pause`, retried every update
  if the level cannot pause yet) and calls FMOD's `mixerSuspend`; `DID_ENTER_FOREGROUND` calls `mixerResume` and the game stays paused. SDL recreates the
  surface and the swapchain/GL context itself (Vulkan and OpenGL ES alike), so there is no black screen or device-lost crash. Logcat markers:
  `[CelesteAndroid] lifecycle: ...`.
- **Landscape only.** SDL's `setOrientationBis` overwrote the manifest's `sensorLandscape` with `FULL_USER` when the window was created (portrait allowed);
  `GameActivity` overrides it.
- **Android Back** (system button or gesture) leaves the game, it is not the game's Escape. `GameActivity` shows a "Return to launcher?" dialog (Yes / No,
  D-pad and A work, B answers No) and sets `AppContext` data `CelesteAndroid.BackRequest` (`show` / `yes` / `no`); `PortBack` (patches) reads it each update:
  `show` pauses the level, `no` resumes it through the pause menu's own cancel, `yes` goes through the same path as the pause-menu item *Exit to launcher*
  (`PortExit.RequestExit`: Save and Quit, then close; outside a level there is nothing to save and the game closes at once). Gamepad buttons (B, Start,
  Select and a gamepad's Back key) are game input: `DispatchKeyEvent` forwards them and consumes them, and the back callback ignores the key-up that follows
  one. Back while the dialog is open closes it as No; every way of closing it (buttons, Back, B, `Dismiss`) goes through the dialog's dismiss listener, which resets its state, so it always opens again; losing the window sends `reset` (the game stays paused as it is). Logcat: `[CelesteAndroid] return dialog: shown|yes|no|reset`, `state: return confirmation`.
- **Autoplay.** `LauncherActivity.MaybeAutoplay` runs once per process in `OnCreate` (never in `OnResume`, never for a recreated activity): with the game
  installed and the Autoplay setting on, the app opens straight into the game. Returning from the game (the title's Exit, or *Exit to launcher*) shows the
  launcher and does not start the game again. *Exit to launcher* (`PortExit`, an additive `Level.Pause` hook) runs the game's own *Save and Quit* and closes
  the game when the main menu is back.

## Install from Steam (stage 3b)

- `InstallService` is a foreground service (`foregroundServiceType=dataSync`, notification channel `install_progress`) holding a Wi-Fi lock and a partial wake
  lock. `InstallPipeline` does one attempt (connect, sign in with the stored refresh token, ownership, manifest, download and verify, patch, side art); the
  service repeats it with a backoff when the network drops. `files/steam/install.pending` marks an unfinished install, so the system restarting the service
  after a kill (or the launcher opening) resumes it. The launcher's main screen only follows `InstallCoordinator`: the step text, the file counter (a file counts when its last
  chunk is written), bytes and speed, and `Done`.
- `SteamActivity` only does the interactive part (sign-in). The launcher's main screen shows the live state (status line with the current file, shortened in the middle by `InstallText.FitMiddle`; bar; stats line from `InstallText.FormatStats`). *Check for updates* runs the same pipeline in update mode and stops after
  the manifest when the installed manifest id is already the latest. Signing out (`SteamAccount`) ends the session, asks Steam to revoke the refresh token
  when it can, and deletes the token and its Keystore key; game files and saves are kept.
- The in-game PORT section lists only settings that apply live. Renderer (needs a restart) is only in Config.
- Logcat markers for scripted checks: `CelesteInstall: progress: ...` every 5 s, `[CelesteAndroid] menu PORT: ...`, `menu pause: ...`, `viewport x,y,w,h ...`.

## Polish (stage 3d)

- **Navigation.** `ControllerActivity` moves focus itself (D-pad keys, stick, hat), never the system: Up/Down/Left/Right wrap at the ends of every screen and dialog
  (`WrapTarget`). A screen may supply an explicit Up/Down order (`VerticalRing`; Config uses *selected tab, then its rows*). L1/R1 call `OnShoulder`
  (Config switches tabs). Logcat: `move: <dir> -> <control>`, `[CelesteAndroid] config tab: <name>`.
- **Look.** `UiKit` holds the type scale, the one focus style (accent outline + soft fill), the dimmed key-art backdrop (Config, Steam), the splash/loading screen and the shared pill
  button. Screens fade into each other (`Animation.Celeste`), tabs and dialogs fade in. The launcher shows the splash on a cold start (not under Autoplay);
  `GameActivity` shows the same screen over the game window until Celeste's loader appears (`PortState` sets `CelesteAndroid.State`; a 25 s cap covers an older patched dll).
  Logcat: `[CelesteAndroid] splash: ... hidden after Nms`.
- **Version.** `_CelesteVersion` (csproj) sets versionName from `git describe --tags --always --dirty` and versionCode from the commit count before the manifest is generated.
- **Share logs.** `LogShare.CreateReport` writes `cache/logs/celeste-logs-<time>.txt` (one file, the previous one is deleted): header (app, device, Android, port settings), Celeste's
  `error_log.txt` if any, the app's own `logcat -d -t 4000`. `LogScrubber` (Celeste.Steam, unit tested) drops lines that mention tokens, passwords and similar, and masks the Steam
  account name, JWT-like strings, Steam IDs, e-mails and long opaque strings. `LogShareProvider` serves the file read-only (authority `<applicationId>.logs`) to the share sheet.
- **Startup.** `StartupTrace` logs phase markers; `scripts/measure-startup.sh` turns them into per-phase numbers. Numbers: `docs/STARTUP.md`.
