# Everest compatibility (input for stage 5)

Stage 5 merges Everest on top of this port, so every MonoMod patch in `src/Celeste.Android.Patches` was audited against
Everest's own patches. **Rule: port patches are additive hooks/wrappers** (`orig_Member()` + extra logic, or new members),
never `[MonoModReplace]` of a whole method (see `docs/ARCHITECTURE.md`).

## How the audit was done

- Everest source: `refs/Everest` (gitignored, read-only), a shallow checkout of
  <https://github.com/EverestAPI/Everest> at commit `322fb9e19d890564b997657fe362d8d44b1f338d`
  (2026-10-08, "Merge pull request #1173"). That is exactly the build installed at
  `/mnt/games/SteamLibrary/steamapps/common/Celeste`: `Celeste.Mod.mm.dll` reports the informational version
  `1.6662.0-azure-322fb-stable` / `+322fb9e19d890564b997657fe362d8d44b1f338d`.
  Recreate with: `mkdir -p refs/Everest && cd refs/Everest && git init -q && git remote add origin https://github.com/EverestAPI/Everest.git && git fetch --depth 1 origin 322fb9e19d890564b997657fe362d8d44b1f338d && git checkout FETCH_HEAD`.
- Everest paths below are relative to `refs/Everest/Celeste.Mod.mm/`. Everything was found in source; the decompiled
  `Celeste.Mod.mm.dll` was only used to cross-check.

## Patch audit table

"Mode" is what our patch does today. "Was" notes conversions made in this stage (behavior unchanged).

| Patched member (vanilla) | Our patch | Mode | Was | Everest also patches it? (source) | Status |
|---|---|---|---|---|---|
| `Monocle.Engine.Update(GameTime)` | `Monocle/patch_Engine.cs` | **hook** (`orig_Update`, then frame-cap `SuppressDraw`) | hook | **Yes**: `[MonoModIgnore] [PatchEngineUpdate]` IL patch, `Patches/Monocle/Engine.cs:105-107` (processor `MonoModRules.PatchEngineUpdate`, `:203`) | Compatible in principle (we call `orig_Update`); verify the composed order at stage 5 |
| `Monocle.Engine.SetWindowed(int,int)` | `Monocle/patch_Engine.cs` | **hook** (Android: `SetFullscreen()`, else `orig_SetWindowed`) | `[MonoModReplace]` | No (no `SetWindowed` anywhere in Everest source) | OK |
| `Monocle.Engine.SetFullscreen()` | `Monocle/patch_Engine.cs` | **hook** (`orig_SetFullscreen`, then apply Resolution scale) | `[MonoModReplace]` | No | OK |
| `Monocle.Engine.UpdateView()` (private) | `Monocle/patch_Engine.cs` | **hook** (`orig_UpdateView`, then `PortView.Adjust`) | hook | No. Everest only adds public setters for `ViewWidth`/`ViewHeight`/`Viewport` (`Patches/Monocle/Engine.cs:19-26`, `:158-181`) | OK; `PortView` sets those via reflection (`GetSetMethod(true)`), still valid |
| `Monocle.Engine.RenderCore()` (protected virtual) | `Monocle/patch_Engine.cs` | **hook** (`orig_RenderCore`, then paint the bars and draw the overlay) | `[MonoModReplace]` | No (no `RenderCore` in Everest source) | OK |
| `Monocle.Engine` constructor | `Monocle/patch_Engine.cs` | `[MonoModIgnore]` stub, **body not changed** (needed only so the patch class compiles) | same | Yes: `[PatchEngineCctor]` (`Patches/Monocle/Engine.cs:48-49`) | No conflict: we do not modify the constructor |
| `Monocle.Engine` new members (`artTexture`, `barBatch`, `DrawBars`, `DrawArt`, `EnsureResources`, ...) | `Monocle/patch_Engine.cs` | **additive** (new members) | same | n/a | OK (names do not collide with Everest's) |
| `Celeste.Celeste.Initialize()` | `Celeste/patch_Celeste.cs` | **hook** (`orig_Initialize`, then vsync + log applied settings) | hook | **Yes**: wrapper `Patches/Celeste.cs:323-327` (`orig_Initialize()` then `Everest.Initialize()`) | Compatible (both wrap) |
| `Celeste.Celeste` constructor | `Celeste/patch_Celeste.cs` | `[MonoModIgnore]` stub, body not changed | same | Yes: `orig_ctor_Celeste` wrapper `Patches/Celeste.cs:303-307` | No conflict |
| `Celeste.Settings.ApplyScreen()` | `Celeste/patch_Settings.cs` | **hook** (Android: `Fullscreen = true`, then `orig_ApplyScreen`) | `[MonoModReplace]` | No. Everest patches `Settings.Initialize` (`Patches/Settings.cs:167-170`) and `[MonoModLinkFrom]`s the key/button fields (`:13-...`), not `ApplyScreen` | OK |
| `Celeste.MenuOptions.Create(bool, EventInstance)` | `Celeste/patch_MenuOptions.cs` | **hook** (`orig_Create`, then hide Fullscreen/Window Scale on Android and **append** the PORT section) | hook | **Yes**: wrapper `Patches/MenuOptions.cs` (calls `orig_Create`, inserts photosensitivity items after the Photosensitive option) | Compatible (both wrap; we append at the end, Everest inserts mid-menu). Never rewrite the menu-building method |
| `Monocle.ErrorLog.Open()` | `Monocle/patch_ErrorLog.cs` | **hook** (Android: no-op; else `orig_Open` guarded by try/catch) | `[MonoModReplace]` | **Yes**: wrapper + IL patch `[PatchErrorLogOpen]`, `Patches/Monocle/ErrorLog.cs:55-60` (processor `:109`) | Was a real conflict (two definitions of `Open`); now compatible |
| `SDL2.SDL::SDL_GetPlatform()`, `SDL2.SDL::SDL_GetPrefPath(string,string)` (call sites) | `Shims.cs` `SDLShim` (`[MonoModLinkFrom]`) | **call redirect** (returns `Android` / `files/userdata/...`) | same | **Yes**: `RelinkMap` SDL2 -> SDL3 for FNA SDL3 builds, `MonoModRules.Game.cs:223-226` | **Open conflict** (see below) |
| `System.Reflection.Assembly::GetEntryAssembly()` (call sites) | `Shims.cs` `ReflectionShim` (`[MonoModLinkFrom]`) | **call redirect** (returns the Celeste assembly) | same | **Yes**: same `[MonoModLinkFrom]`, `Mod/Helpers/FakeAssembly.cs:24` (returns a `FakeAssembly` wrapping `typeof(Celeste).Assembly`) | **Open conflict**, benign result (see below) |
| `System.Runtime.GCSettings::set_LatencyMode` (call sites) | `Shims.cs` `GCShim` (`[MonoModLinkFrom]`) | **call redirect** (ignores `PlatformNotSupportedException` on Android) | same | No (no `LatencyMode`/`GCSettings` in Everest source) | OK |
| New types merged into `Celeste.dll`: `HostConfig`, `PortConfig`, `PortSettingsCatalog`/`PortSettingsFile`, `PortView`, `PortOverlay`, `PortLive` | `src/Celeste.Android.Patches/*.cs` | **additive** (new types, no existing member touched) | same | n/a | OK |

`[MonoModLinkFrom]` shims redirect references to external (BCL / SDL2) methods rather than patching a Celeste member, so
they cannot be turned into `orig_` wrappers.

## Remaining conflicts and plan for stage 5

1. **`Assembly.GetEntryAssembly()`**: both mods `LinkFrom` the same signature; whichever is applied last wins. Everest's
   `FakeAssembly` wraps `typeof(Celeste).Assembly`, which is what our shim returns, so the outcome is equivalent. Plan: drop
   `ReflectionShim` from the Everest build (guard it with a MonoMod flag) and keep it only for the vanilla build.
2. **`SDL_GetPlatform` / `SDL_GetPrefPath`**: Everest relinks the SDL2 calls to the real SDL3 functions
   (`SDL3.SDL.SDL_GetPlatform` already returns `"Android"` on device); ours returns fixed values. Plan: let Everest's relink
   handle `SDL_GetPlatform`; for `SDL_GetPrefPath` either keep our redirect applied *after* Everest's or point SDL3's pref path at
   `files/userdata` so saves stay in `files/userdata/Celeste` (the shared `port-settings.json` also lives there).
3. **`Engine.Update`**: Everest IL-patches it in place, we wrap it. Check at stage 5 that the composed method still ends with our
   frame-cap `SuppressDraw` and that `orig_Update` resolves to Everest's patched body.
4. **View properties**: `PortView.Adjust` sets `Engine.ViewWidth/ViewHeight/Viewport` by reflection; once Everest is present it
   may be cleaner to call Everest's `patch_Engine.SetViewWidth/SetViewHeight/SetViewport` helpers.
5. **Menu**: `HideFullscreenOptions` reads `TextMenu.items` by reflection. Everest's `patch_TextMenu` exposes `GetItems()`; either works,
   but re-test the Video menu (no Fullscreen/Window Scale) and the PORT section with Everest's menu entries present. Everest's
   `TextMenuExt.EaseInSubHeaderExt` could be used for "applies on next launch" descriptions instead of the label suffix.
