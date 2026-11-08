# Stage 3d spec — polish

(A) Config tabs: replace the single Config list with tabs: Port settings | Saves | Game files | Steam account | About. Switch tabs with the controller shoulder buttons (L1/R1) and by moving onto the tab bar with the D-pad. Dialogs keep returning to the same tab and control.

(B) Wrap-around navigation everywhere in the launcher: pressing Up on the first item jumps to the last, Down on the last jumps to the first; Left/Right wrap across tabs and horizontal rows.

(C) Launcher look: a consistent visual pass on every launcher screen: background art, spacing, font sizes, focus style, short transitions between screens. Keep it clean and controller-first; nothing clipped.

(D) App name and icon: release label "Celeste", debug label "Celeste (debug)". Personal builds use the icon generated from the user's game art (scripts/generate-game-art.sh); public builds keep the fallback icon. Add a short splash/loading screen consistent with the launcher.

(E) Controller polish: controllers connected or disconnected mid-game work without restarting (Celeste picks up the new controller; disconnect pauses the game); rumble works through the game's own rumble setting on controllers that support it; button prompts match the connected controller type (Xbox/PlayStation/Nintendo) using Celeste's built-in prompt sets where available.

(F) Faster startup: measure launch → DONE LOADING with per-phase timestamps (process start, runtime init, assembly load, content load), remove redundant work (re-verifying files or re-patching when nothing changed, unnecessary copies), and report before/after numbers. No gameplay changes.

(G) Version and changelog: versionName from `git describe`, versionCode increasing from the commit count; show the version in Config > About; add CHANGELOG.md summarizing changes since the fork, grouped by stage.

(H) Share logs: Config > About > "Share logs" creates one plain .txt file (no zip) with the app's own recent logcat, Celeste's error log if present, app version, device model, Android version and port settings, never tokens or account details, and opens Android's share sheet.

(I) Cleanup: remove dead code and unused resources, update README and docs to match the current app, and review commits on the original repo (BelmanteGu/CelesteAndroid) since the fork; merge anything useful that doesn't conflict with this fork's design and list what was skipped and why.
