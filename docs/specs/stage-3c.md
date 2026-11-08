# Stage 3c spec: launcher UI cleanup

Goal (verbatim summary): (1) no navigation legend; (2) no "applies on next boot" note on Renderer; (3) Delete game files: nothing above
Play, a "Game files deleted — X MB freed" dialog with one OK; (4) not installed: one large focused "Set up" button → chooser "Steam" /
"Import from folder" (Steam: signed in → show the account and start at once; signed out → sign-in, then ownership check and download with
no extra taps); (5) no .zip anywhere (game or saves; saves import/export use plain files in a folder); (6) dialogs opened from Config return to the same Config section, focus on the opener;
(7) install progress on the main screen (button "Installing…" → Cancel confirmation; status line under the subtitle; bar and stats line
at the bottom; nothing wraps, truncates at the end or changes height; on completion Play is focused and the extras disappear).

The phone was not available while this was written: nothing here was run on a device. `stage-3c-test.md` has the on-device steps.

## Decisions and assumptions
- **No .zip**: the game import from a .zip, the saves import from a .zip and the saves export to a .zip are all removed (UI and code:
  `ImportZip`, `ImportSavesFromZip`, `ExportSavesToZip`, the pickers, the debug `import_zip`/`export_zip` extras). Saves export copies every
  save file (`0.celeste`, `1.celeste`, `2.celeste` if present, `settings.celeste`) as plain files into a folder picked with Android's folder
  picker; import reads the plain `.celeste` files from a picked folder, validates them, backs up the current saves, then copies them in.
  Config > Game files keeps "Re-import from folder".
- **Android Back** (change of design, after the first 3c draft): the system Back button/gesture no longer sends Escape. It pauses the game and shows
  "Return to launcher?" (Yes / No, controller-navigable, No focused). Yes uses the same save-and-exit path as the pause-menu item *Exit to launcher* and does
  not trigger Autoplay again (it runs once per process); No resumes the paused game. Gamepad B, Start and Select (and a gamepad's Back key) keep their normal
  in-game behavior and never show it. Game-state markers: `[CelesteAndroid] state: title|main menu|file select|level|pause menu|return confirmation|...`.
- **Legend**: the launcher's bottom-right legend and Config's bottom legend are both removed.
- **Renderer note**: the actual string was "(applies on the next Play)" (Config row label); the suffix is gone. `PortSetting.NextLaunch` stays
  because it still decides which settings the in-game PORT menu hides.
- **Where dialogs live**: `UiDialog` draws a dialog over the activity's own root (no new activity), blocks focus in the content behind it,
  and on close refocuses the control that was focused when it opened (or a fallback if that control is gone). Config runs Delete and the
  saves import/export itself, so the dialog and the result dialog return to Config. After a delete the "Delete game files" row is hidden, so
  focus falls back to "Check for updates" (same section).
- **Tasks that need the main screen's progress** (Re-import from folder, Check for updates) still close Config and run on the main screen.
- **Switch account** now only signs in again (back in Config, with "Signed in as …"); it no longer starts an update check by itself.
- **Set up chooser** has the two buttons only; B (or a tap outside) closes it. Focus starts on "Steam".
- **Signed in → Steam**: the launcher publishes "Signed in as <account>" as the first status, the install service starts at once, and its
  first steps are "Signing in to Steam as <account>" then "Checking that your account owns Celeste", so ownership is checked automatically.
- **Signed out → Steam**: `SteamActivity` is sign-in only; on success it stores the session and returns `Result.Ok` (echoing the update flag);
  the launcher then starts the service. A token rejected during an install (`NeedsSignIn`) reopens the sign-in the same way.
- **Install failures** (`Failed`) show a dialog "Couldn't install from Steam" with "Try again" / "OK" instead of the old error screen. An update
  check that finds nothing shows a dialog "Celeste is up to date" (nothing is written above Play).
- **"Installing…"** is shown whenever an install, an update or an import runs, also when the game is already installed (update / re-import);
  Play is hidden meanwhile. During an import the button is disabled (imports cannot be cancelled); for a Steam install, pressing it opens
  "Cancel the installation?" with "Keep installing" (focused) / "Cancel install".
- **Status line**: `SetSingleLine`, `SetLines(1)`, `Ellipsize=Middle`, plus `InstallText.FitMiddle` (the real fit, using the TextView's own paint and
  width, protecting the "Downloading " / "Verifying " prefix). **Stats line**: one line, same rules, text from `InstallText.FormatStats`.
  Both have a fixed one-line height, even when empty. The bar is a fixed 8 dp.
- **Stats**: speed is left out while it is unknown (zero, and always during "Verifying"). Bytes: MB below 1 GB, otherwise one decimal in GB.
- **Not-installed hint** on the status line: "Set up the game to start playing." (or "Download paused — choose Set up to resume.").
  When installed the status line is empty.
- **Warnings**: the build has 7 pre-existing warnings (GameActivity, InstallService, OverridePendingTransition); none were added.

## Logcat markers added
- `[CelesteAndroid] launcher dialog: <title>` and `config dialog: <title>`
- `[CelesteAndroid] launcher: signed in to Steam, starting the download|update check` / `launcher: not signed in to Steam, opening the sign-in`
- `[CelesteAndroid] launcher job done: <text>` (imports; replaces the old inline success text)
- existing: `CelesteInstall: progress: ...` every 5 s, `CelesteLauncher`/`CelesteConfig` focus lines
