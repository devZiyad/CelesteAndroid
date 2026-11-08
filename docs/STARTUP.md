# Startup measurements (stage 3d, part F)

`scripts/measure-startup.sh --runs 3` on the Galaxy S23 Ultra, debug package, warm cache, cold process, ms after Play was pressed
(`START ... GameActivity` in logcat). Phases: process start (`Start proc`), runtime ready (`GameActivity.OnCreate`), `sdl-main`
(SDL thread starts managed code), assembly load (`LoadFromAssemblyPath`), Celeste `Main`, Celeste's `GAME DISPLAYED` and `DONE LOADING`.

| Phase | Before (avg of 3) | After (avg of 3) |
|---|---|---|
| process start | 11 | 14 |
| runtime ready | 246 | 303 |
| sdl-main | 321 | 421 |
| assembly load | 344 → 345 | 443 → 444 |
| Celeste Main | 350 | 451 |
| GAME DISPLAYED | 5953 | 5994 |
| **DONE LOADING** | **8151** | **8189** |
| Play pressed, after the app was started | 330 | 258 |
| **App start → DONE LOADING** | **8481** | **8447** |

Before: 7987 / 8115 / 8351 (launch→done after Play). After: 8150 / 8221 / 8196.

## What the numbers say

- About 7.8 s of the 8.2 s is Celeste's own content load (`GFX LOAD` ≈ 2.9 s, `MTN LOAD` ≈ 2.0 s, audio, data), on one thread that already runs on the
  prime core at its top frequency (3.36 GHz). Our code is not in that path, so there is no copy, file check or re-patch to remove there; speeding it up would mean
  changing how the game loads its content, which this stage does not do ("no gameplay changes").
- Redundant work that was removed on our side: with Autoplay the game starts before the launcher builds its own screen (Play pressed 330 → 258 ms after the app
  start, so app start → DONE LOADING 8481 → 8447 ms); the launcher's data inventory (walks the game's ~1200 files) no longer runs on every resume, only once per
  process, shortly after the screen is up, and never when the game is starting.
- The loading screen added in this stage (the launcher's splash over the game's black window) costs about 60 ms in the first phases; the game's own timing is unchanged
  within run-to-run noise (about ±150 ms).
- Tried and dropped: `MONO_GC_PARAMS=nursery-size=64m/128m` (−1 to −2 %, inside the noise). Nothing is re-verified or re-patched at launch: `IsInstalled` is two
  existence checks, and the patch only runs at install or with the `repatch` debug extra.
