using System;
using System.IO;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;
using CelesteAndroid.Steam;
using Color = Android.Graphics.Color;
using Uri = Android.Net.Uri;

namespace CelesteAndroid
{
	/// <summary>
	/// Home screen: the game's key art, the main button (Set up / Installing… / Play), Config, and the install progress.
	/// </summary>
	[Activity(
		Name = "org.devziyad.celeste.LauncherActivity",
		MainLauncher = true,
		Exported = true,
		Theme = "@style/Theme.Celeste",
		ScreenOrientation = ScreenOrientation.SensorLandscape,
		LaunchMode = LaunchMode.SingleTop,
		ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
			| ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation)]
	public class LauncherActivity : ControllerActivity
	{
		private const int RequestFolder = 1;
		private const int RequestConfig = 3;
		private const int RequestSteam = 4;
		private const string LogTag = "CelesteLauncher";

		private static readonly Color Night = Color.ParseColor("#120C22");
		private static readonly Color Accent = Color.ParseColor("#F2B8D8");

		private ImageView art = null!;
		private LinearLayout panel = null!;
		private TextView status = null!;
		private TextView play = null!;
		/// <summary>The big button while the game is not playable: "Set up", and "Installing…" while an install runs.</summary>
		private TextView setupButton = null!;
		private TextView configButton = null!;
		private LinearLayout progressBox = null!;
		private ProgressBar progressBar = null!;
		private TextView statsText = null!;
		private FrameLayout rootLayout = null!;
		private UiDialog? dialog;
		private bool busy;
		private bool lastInstallWasUpdate;
		private bool? lastInstalled;
		private View? lastPrimary;
		private InstallStatus? handledFailure, handledSignIn;

		protected override void OnCreate(Bundle? savedInstanceState)
		{
			base.OnCreate(savedInstanceState);
			View root = BuildLayout();
			SetContentView(root);
			WatchFocus(root);
			HideSystemBars();
			LoadArt();

			// Splash: a short loading screen on a cold start of the app (not when going back to the launcher, not when Autoplay
			// is about to open the game). The art appears as it fades away and the panel slides in right after.
			bool showSplash = !splashShown && savedInstanceState == null && !WillAutoplay();
			splashShown = true;
			panel.Alpha = 0f;
			panel.TranslationX = -Dp(32);
			if (showSplash)
				ShowSplash();
			else
				SlidePanelIn(650);

			var zoom = new ScaleAnimation(1f, 1.08f, 1f, 1.08f, Dimension.RelativeToSelf, 0.5f, Dimension.RelativeToSelf, 0.4f)
			{
				Duration = 24000,
				RepeatCount = Animation.Infinite,
				RepeatMode = RepeatMode.Reverse,
				Interpolator = new AccelerateDecelerateInterpolator(),
			};
			art.StartAnimation(zoom);

			MaybeAutoplay(savedInstanceState);

			// An install that was interrupted (process killed, reboot) resumes by itself; the main screen shows its progress.
			if (!InstallCoordinator.IsActive && InstallCoordinator.PendingMode(this) is string pending && SteamTokenStore.HasSession(this))
			{
				try { InstallService.Start(this, pending == "update"); }
				catch (Exception e) { Log.Warn(LogTag, "could not resume the install now: " + e.Message); }
			}
		}

		private static bool splashShown;
		private const int SplashMinMs = 700;

		private void ShowSplash()
		{
			FrameLayout splash = UiKit.Splash(this, "Native Android port");
			rootLayout.AddView(splash, Match());
			StartupTrace.Mark("launcher-splash-shown");
			long shownAt = SystemClock.ElapsedRealtime();
			splash.PostDelayed(() =>
			{
				Log.Info("CelesteAndroid", $"[CelesteAndroid] splash: launcher hidden after {SystemClock.ElapsedRealtime() - shownAt}ms");
				SlidePanelIn(120);
				UiKit.FadeOut(splash, done: EnsureFocus);
			}, SplashMinMs);
		}

		private void SlidePanelIn(int delayMs) =>
			panel.Animate()!.Alpha(1f).TranslationX(0f).SetStartDelay(delayMs).SetDuration(550)
				.SetInterpolator(new DecelerateInterpolator(2f))!.Start();

		/// <summary>Autoplay is decided once per app process: coming back from the game ("Exit to launcher", the title Exit) must not start it again.</summary>
		private static bool autoplayHandled;

		/// <summary>Opening the app with the game ready and Autoplay on goes straight into the game.</summary>
		private bool WillAutoplay() =>
			!autoplayHandled && !InstallCoordinator.IsActive && InstallCoordinator.PendingMode(this) == null && GameInstaller.IsInstalled(this)
			&& PortSettings.GetValue(this, PortSettingsCatalog.Autoplay) == "true";

		private void MaybeAutoplay(Bundle? savedInstanceState)
		{
			if (autoplayHandled) return;
			bool go = WillAutoplay();
			autoplayHandled = true;
			if (savedInstanceState != null || !go) return; // recreated by the system (e.g. the launcher process died while the game ran)
			Log.Info(LogTag, "autoplay: starting game");
			Play();
			OverridePendingTransition(0, 0);
		}

		private Action<InstallStatus>? statusHandler;

		protected override void OnStart()
		{
			base.OnStart();
			statusHandler = s => RunOnUiThread(() => OnInstallStatus(s));
			InstallCoordinator.Changed += statusHandler;
		}

		protected override void OnStop()
		{
			InstallCoordinator.Changed -= statusHandler;
			base.OnStop();
		}

		/// <summary>Follows the install service: the main screen redraws itself; a finished install focuses Play; problems get a dialog.</summary>
		private void OnInstallStatus(InstallStatus s)
		{
			RefreshState();
			switch (s.Stage)
			{
				case InstallStage.Done:
					LoadArt();
					if (s.Title == InstallCoordinator.UpToDateTitle)
						ShowMessage("Celeste is up to date", s.Step);
					break;
				case InstallStage.Failed when !ReferenceEquals(s, handledFailure):
					handledFailure = s;
					ShowDialog("Couldn't install from Steam", s.Step,
						new UiDialog.Button("Try again", Filled: true, () => BeginSteam(lastInstallWasUpdate), "Retry"),
						new UiDialog.Button("OK", Filled: false, Name: "DialogOk"));
					break;
				case InstallStage.NeedsSignIn when !ReferenceEquals(s, handledSignIn):
					handledSignIn = s; // the stored session was rejected: ask for a new sign-in, then continue by itself
					BeginSteam(lastInstallWasUpdate);
					break;
			}
		}

		protected override void OnResume()
		{
			base.OnResume();
			HideSystemBars();
			RefreshState();
			LogDataInventory();

			// Development: files already copied by scripts/deploy-android.sh; only patch + background.
			// adb shell am start -n org.devziyad.celeste/.LauncherActivity --ez repatch true
			if (!busy && Intent!.GetBooleanExtra("repatch", false))
			{
				Intent.RemoveExtra("repatch");
				RunInstall(_ => { });
			}
			// Development (scripts/deploy-android.sh --play): opens the game directly.
			if (!busy && Intent!.GetBooleanExtra("autoplay", false))
			{
				Intent.RemoveExtra("autoplay");
				if (GameInstaller.IsInstalled(this))
				{
					Log.Info(LogTag, "autoplay: starting game");
					Play();
				}
			}
			// Development (adb shell am start ... --ez steam true [--ez forceLogin true | --ez showError true]): the Steam flow of "Set up > Steam".
			if (!busy && Intent!.GetBooleanExtra("steam", false))
			{
				bool force = Intent.GetBooleanExtra(SteamActivity.ExtraForceLogin, false);
				bool showError = Intent.GetBooleanExtra(SteamActivity.ExtraShowError, false);
				Intent.RemoveExtra("steam");
				if (force || showError)
				{
					var steamIntent = new Intent(this, typeof(SteamActivity));
					if (force) steamIntent.PutExtra(SteamActivity.ExtraForceLogin, true);
					if (showError) steamIntent.PutExtra(SteamActivity.ExtraShowError, true);
					StartActivityForResult(steamIntent, RequestSteam);
				}
				else
					BeginSteam(false);
			}
			// Personal APK: the game already ships inside the APK; installs itself on first launch.
			else if (!busy && !GameInstaller.IsInstalled(this) && GameInstaller.HasEmbeddedGame(this))
			{
				RunInstall(installer => installer.ImportEmbedded());
			}
		}

		/// <summary>
		/// Logs the size of the app's data (files/ and shared_prefs/, with each file's name): proof, in logcat,
		/// that a deploy did not erase it. "android.app.ActivityThread.IDS.xml" belongs to Android itself (a framework
		/// counter, rewritten on every update) and is left out.
		/// </summary>
		private void LogDataInventory()
		{
			string? filesDir = FilesDir?.AbsolutePath;
			string? prefsDir = filesDir == null ? null : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(filesDir)!, "shared_prefs");
			System.Threading.Tasks.Task.Run(() =>
			{
				static (int Files, long Bytes, string Names) Walk(string? dir)
				{
					if (dir == null || !Directory.Exists(dir)) return (0, 0, "");
					int n = 0; long b = 0; var names = new System.Collections.Generic.List<string>();
					foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
					{
						string name = System.IO.Path.GetFileName(f);
						if (name == "android.app.ActivityThread.IDS.xml") continue;
						long len = 0;
						try { len = new FileInfo(f).Length; } catch (IOException) { }
						n++; b += len; names.Add($"{name}:{len}");
					}
					names.Sort(StringComparer.Ordinal);
					return (n, b, string.Join(",", names));
				}
				var files = Walk(filesDir); var prefs = Walk(prefsDir);
				Log.Info(LogTag, $"data: files/ files={files.Files} bytes={files.Bytes}; shared_prefs/ files={prefs.Files} bytes={prefs.Bytes} [{prefs.Names}]");
			});
		}

		protected override string Tag => LogTag;
		protected override View? InitialFocus() =>
			play.Visibility == ViewStates.Visible && play.Enabled ? play
			: setupButton.Visibility == ViewStates.Visible && setupButton.Enabled ? setupButton
			: configButton;
		protected override bool InputBlocked => busy;
		protected override void OnBackButton()
		{
			if (dialog is { IsOpen: true })
			{
				Log.Info(LogTag, "back: close dialog");
				dialog.Dismiss();
				return;
			}
			Log.Info(LogTag, "back: exit launcher");
			Finish();
		}

		#region Layout

		private View BuildLayout()
		{
			var root = new FrameLayout(this);
			root.SetBackgroundColor(Night);
			rootLayout = root;

			art = new ImageView(this);
			art.SetScaleType(ImageView.ScaleType.CenterCrop);
			root.AddView(art, Match());

			var shade = new View(this)
			{
				Background = new GradientDrawable(GradientDrawable.Orientation.LeftRight!, new int[]
				{
					Color.Argb(245, 18, 12, 34), Color.Argb(190, 18, 12, 34), Color.Argb(40, 18, 12, 34), Color.Argb(0, 0, 0, 0),
				}),
			};
			root.AddView(shade, Match());

			panel = UiKit.NoClip(new LinearLayout(this) { Orientation = Orientation.Vertical });
			panel.SetGravity(GravityFlags.CenterVertical | GravityFlags.Start);
			panel.SetPadding(Dp(56), Dp(12), Dp(24), Dp(12));
			root.AddView(panel, new FrameLayout.LayoutParams(Dp(460), ViewGroup.LayoutParams.MatchParent));

			// The game's logo (art/logo.png → GameArt) or, without it, the name as text.
			int logoId = Resources!.GetIdentifier("celeste_logo", "drawable", PackageName);
			if (logoId != 0)
			{
				var logo = new ImageView(this);
				logo.SetImageResource(logoId);
				logo.SetAdjustViewBounds(true);
				logo.SetScaleType(ImageView.ScaleType.FitStart);
				logo.Elevation = Dp(6);
				panel.AddView(logo, new LinearLayout.LayoutParams(Dp(150), Dp(118)) { LeftMargin = -Dp(4) });
			}
			else
			{
				var title = Text("CELESTE", 54, Color.White, TypefaceStyle.Bold);
				title.LetterSpacing = 0.18f;
				title.SetShadowLayer(Dp(12), 0, Dp(2), Color.Argb(160, 0, 0, 0));
				panel.AddView(title);
			}

			var subtitle = Text("Native Android port", 15, Accent, TypefaceStyle.Normal);
			subtitle.LetterSpacing = 0.06f;
			panel.AddView(subtitle, Margins(top: -4));

			// The status line: always exactly one line (so the layout never moves while the text changes); text that is too long is
			// shortened in the middle by SetStatus (InstallText.FitMiddle), and the TextView itself also ellipsizes in the middle as a safety net.
			status = Text("", 14, Color.Argb(220, 255, 255, 255), TypefaceStyle.Normal);
			status.SetSingleLine(true);
			status.SetLines(1);
			status.SetMaxLines(1);
			status.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
			panel.AddView(status, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(10) });

			play = PillButton("PLAY", filled: true);
			play.Click += (_, _) => Play();
			MakeFocusable(play, "Play");
			panel.AddView(play, UiKit.ButtonParams(this, 260, marginTop: 14));

			// Not playable: one large "Set up" button (focused); it reads "Installing…" while an install runs.
			setupButton = PillButton("Set up", filled: true);
			setupButton.Click += (_, _) => OnSetupPressed();
			MakeFocusable(setupButton, "Setup");
			panel.AddView(setupButton, UiKit.ButtonParams(this, 260, marginTop: 14));

			configButton = PillButton("Config", filled: false, small: true);
			configButton.Click += (_, _) => StartActivityForResult(new Intent(this, typeof(ConfigActivity)), RequestConfig);
			MakeFocusable(configButton, "Config");
			panel.AddView(configButton, UiKit.ButtonParams(this, 126, marginTop: 6));

			// Install progress at the bottom of the screen: the bar, and under it one stats line. Both have fixed heights (the line is
			// exactly one line, never wraps and is never cut at the end), so nothing moves while the numbers change.
			progressBox = UiKit.NoClip(new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone });
			progressBar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 1000 };
			progressBar.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(Accent);
			progressBar.IndeterminateTintList = Android.Content.Res.ColorStateList.ValueOf(Accent);
			statsText = Text("", 13, Color.Argb(210, 255, 255, 255), TypefaceStyle.Normal);
			statsText.SetSingleLine(true);
			statsText.SetLines(1);
			statsText.SetMaxLines(1);
			statsText.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
			progressBox.AddView(progressBar, new LinearLayout.LayoutParams(-1, Dp(8)));
			progressBox.AddView(statsText, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(6) });
			root.AddView(progressBox, new FrameLayout.LayoutParams(Dp(420), ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom | GravityFlags.Start)
				{ LeftMargin = Dp(56), BottomMargin = Dp(24) });

			return root;
		}

		private void LoadArt()
		{
			// Art embedded in the build (GameArt/) or, in the shareable build, the imported game's key art.
			if (Resources!.GetIdentifier("celeste_art", "drawable", PackageName) != 0)
			{
				art.SetImageResource(Resource.Drawable.launcher_art);
				return;
			}
			string imported = System.IO.Path.Combine(GameInstaller.GameDir(this), "Content", "Graphics", "SplashScreen.png");
			if (File.Exists(imported))
				art.SetImageBitmap(BitmapFactory.DecodeFile(imported, new BitmapFactory.Options { InSampleSize = 2 }));
			else
				art.SetImageResource(Resource.Drawable.launcher_art);
		}

		private TextView PillButton(string label, bool filled, bool small = false) => UiKit.Pill(this, label, filled, minHeightDp: small ? 38 : filled ? 54 : 44);

		private TextView Text(string text, float sp, Color color, TypefaceStyle style)
		{
			var view = new TextView(this) { Text = text };
			view.SetTextSize(ComplexUnitType.Sp, sp);
			view.SetTextColor(color);
			view.SetTypeface(Typeface.Create("sans-serif", style), style);
			return view;
		}

		private TextView LinkText(string text)
		{
			var view = Text(text, 14, Accent, TypefaceStyle.Normal);
			view.SetPadding(Dp(8), Dp(6), Dp(8), Dp(6));
			var focused = new GradientDrawable();
			focused.SetCornerRadius(Dp(14));
			focused.SetColor(Color.Argb(70, 242, 184, 216));
			focused.SetStroke(Dp(2.5f), Accent);
			var states = new StateListDrawable();
			states.AddState(new[] { Android.Resource.Attribute.StateFocused }, focused);
			states.AddState(new int[0], new ColorDrawable(Color.Transparent));
			view.Background = states;
			return view;
		}

		private static FrameLayout.LayoutParams Match() =>
			new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);

		private LinearLayout.LayoutParams Margins(int top = 0, int left = 0, int right = 0) =>
			new(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
			{
				TopMargin = Dp(top), LeftMargin = Dp(left), RightMargin = Dp(right),
			};

		private int Dp(float dp) => (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, dp, Resources!.DisplayMetrics);

		#endregion

		#region State and actions

		private void RefreshState()
		{
			bool installed = GameInstaller.IsInstalled(this);
			bool installing = InstallCoordinator.IsActive || busy;
			InstallStatus live = InstallCoordinator.Current;
			if (lastInstalled != installed) { lastInstalled = installed; LoadArt(); }

			// Playable: Play (large, focused) and Config. Otherwise: Set up (large, focused) - "Installing…" while an install runs - and Config.
			bool showPlay = installed && !installing;
			play.Visibility = showPlay ? ViewStates.Visible : ViewStates.Gone;
			play.Enabled = showPlay;
			setupButton.Visibility = showPlay ? ViewStates.Gone : ViewStates.Visible;
			setupButton.Text = installing ? "Installing…" : "Set up";
			setupButton.Enabled = !busy;
			configButton.Enabled = !busy;

			// Status line under the subtitle, progress bar and stats line at the bottom: only while installing.
			progressBox.Visibility = installing ? ViewStates.Visible : ViewStates.Gone;
			if (InstallCoordinator.IsActive)
			{
				SetStatus(live.Step);
				progressBar.Indeterminate = live.Fraction < 0;
				if (live.Fraction >= 0) progressBar.Progress = (int)(live.Fraction * 1000);
				statsText.Text = live.Stats is { } stats ? InstallText.FormatStats(stats) : "";
			}
			else if (!busy)
			{
				bool paused = File.Exists(SteamTokenStore.ResumeStatePath(this));
				// Nothing above Play (its label says it all); a hint only while the game is not installed.
				SetStatus(installed ? "" : paused ? "Download paused — choose Set up to resume." : "Set up the game to start playing.");
				statsText.Text = "";
			}

			// When the main button changes (Set up → Installing… → Play), focus follows it, as long as no dialog is open.
			View primary = showPlay ? play : setupButton;
			if (dialog is not { IsOpen: true } && !ReferenceEquals(primary, lastPrimary) && primary.Enabled)
				primary.RequestFocus();
			lastPrimary = primary;
			if (dialog is not { IsOpen: true }) EnsureFocus();
		}

		/// <summary>
		/// Sets the status line: one line only. A long path is shortened in the middle with an ellipsis to fit the line's width
		/// ("Downloading Content/Ma…ity.bin"), never wrapped and never cut at the end.
		/// </summary>
		private void SetStatus(string text)
		{
			int available = status.Width > 0 ? status.Width - status.PaddingLeft - status.PaddingRight : Dp(380);
			int keep = text.StartsWith("Downloading ", StringComparison.Ordinal) ? "Downloading ".Length
				: text.StartsWith("Verifying ", StringComparison.Ordinal) ? "Verifying ".Length : 0;
			string fitted = InstallText.FitMiddle(text, status.Paint!.MeasureText, available, keep);
			if (status.Text != fitted) status.Text = fitted;
		}

		/// <summary>The big button: "Set up" opens the chooser; "Installing…" offers to cancel.</summary>
		private void OnSetupPressed()
		{
			if (InstallCoordinator.IsActive)
			{
				ShowDialog("Cancel the installation?", "The download stops. What was already downloaded is kept and continues next time.",
					new UiDialog.Button("Keep installing", Filled: false, Name: "KeepInstalling"),
					new UiDialog.Button("Cancel install", Filled: true, () => InstallService.CancelInstall(this), "CancelInstall"));
				return;
			}
			ShowDialog("Set up Celeste", "Where should the game come from?",
				new UiDialog.Button("Steam", Filled: true, () => BeginSteam(false), "SetupSteam"),
				new UiDialog.Button("Import from folder", Filled: false, PickFolder, "SetupImportFolder"));
		}

		private void ShowDialog(string title, string message, params UiDialog.Button[] buttons)
		{
			if (dialog is { IsOpen: true }) return;
			Log.Info("CelesteAndroid", "[CelesteAndroid] launcher dialog: " + title);
			dialog = UiDialog.Show(this, rootLayout, panel, title, message, buttons, 0, returnFocus: null, fallbackFocus: InitialFocus());
		}

		private void ShowMessage(string title, string message) =>
			ShowDialog(title, message, new UiDialog.Button("OK", Filled: true, Name: "DialogOk"));

		/// <summary>
		/// Steam setup. Signed in: the account is shown and the download starts at once. Signed out: the sign-in screen opens and, after a
		/// successful login, the install starts by itself (its first steps check that the account owns Celeste) with no further taps.
		/// </summary>
		private void BeginSteam(bool update)
		{
			lastInstallWasUpdate = update;
			if (SteamTokenStore.HasSession(this))
			{
				string account = SteamAccount.Name(this) ?? "Steam";
				Log.Info("CelesteAndroid", "[CelesteAndroid] launcher: signed in to Steam, starting the " + (update ? "update check" : "download"));
				StartInstallService(update, "Signed in as " + account);
			}
			else
			{
				Log.Info("CelesteAndroid", "[CelesteAndroid] launcher: not signed in to Steam, opening the sign-in");
				StartActivityForResult(new Intent(this, typeof(SteamActivity)).PutExtra(SteamActivity.ExtraUpdate, update), RequestSteam);
			}
		}

		private void StartInstallService(bool update, string firstStep)
		{
			lastInstallWasUpdate = update;
			// Android 13+: without the permission the service still runs, but its progress notification is hidden.
			if (OperatingSystem.IsAndroidVersionAtLeast(33) && CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted)
				RequestPermissions(new[] { "android.permission.POST_NOTIFICATIONS" }, 7);
			try
			{
				// Shown at once; the service replaces it with its own first step.
				InstallCoordinator.Publish(new InstallStatus(InstallStage.Running, update ? "Updating Celeste" : "Installing Celeste", firstStep));
				InstallService.Start(this, update);
			}
			catch (Exception e)
			{
				Log.Error(LogTag, "could not start the install service: " + e.Message);
				InstallCoordinator.Publish(InstallStatus.None);
				ShowMessage("Couldn't start the download", e.Message);
			}
		}

		private void Play()
		{
			var intent = new Intent(this, typeof(GameActivity));
			string driver = PortSettings.GetValue(this, PortSettings.Renderer);
			if (driver == "OpenGL")
				intent.PutExtra(GameActivity.ExtraDriver, driver);
			StartActivity(intent);
		}

		/// <summary>Installs (or updates) the game from Steam; the download resumes where it stopped.</summary>
		private void StartSteam() => StartActivityForResult(new Intent(this, typeof(SteamActivity)), RequestSteam);

		private void PickFolder()
		{
			StartActivityForResult(new Intent(Intent.ActionOpenDocumentTree), RequestFolder);
		}

		protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
		{
			base.OnActivityResult(requestCode, resultCode, data);
			if (requestCode == RequestConfig)
			{
				HandleConfigResult(resultCode, data);
				return;
			}
			if (requestCode == RequestSteam)
			{
				// Signed in: start the install now (no extra taps). Cancelled: back to the main screen as it was.
				if (resultCode == Result.Ok)
					StartInstallService(data?.GetBooleanExtra(SteamActivity.ExtraUpdate, false) ?? false, "Signed in to Steam");
				RefreshState();
				return;
			}
			Uri? uri = data?.Data;
			if (resultCode != Result.Ok || uri == null)
				return;
			if (requestCode == RequestFolder)
				RunInstall(installer => installer.ImportFolder(uri));
		}

		/// <summary>Config hands back what needs the main screen's progress (re-import, update check).</summary>
		private void HandleConfigResult(Result resultCode, Intent? data)
		{
			LoadArt(); // the game files may have been deleted in Config
			RefreshState();
			string? action = data?.GetStringExtra(ConfigActivity.ExtraAction);
			Uri? uri = data?.Data;
			if (resultCode != Result.Ok || action == null) return;
			switch (action)
			{
				case ConfigActivity.ActionReimportFolder when uri != null:
					RunInstall(installer => installer.ImportFolder(uri));
					break;
				case ConfigActivity.ActionCheckUpdates:
					BeginSteam(update: true);
					break;
			}
		}

		/// <summary>Game import: copy → patch → background.</summary>
		private void RunInstall(Action<GameInstaller> import)
		{
			RunJob(installer =>
			{
				import(installer);
				installer.Patch();
				installer.PrepareBackground();
				RunOnUiThread(LoadArt);
				return "Game imported; ready to play";
			});
		}

		/// <summary>Runs an installer task off the UI thread, with progress on the main screen. A failure gets a dialog; success needs no text.</summary>
		private void RunJob(Func<GameInstaller, string> job)
		{
			busy = true;
			RefreshState();
			progressBar.Indeterminate = true;
			statsText.Text = "";

			var installer = new GameInstaller(this, (message, fraction) => RunOnUiThread(() =>
			{
				SetStatus(message);
				progressBar.Indeterminate = fraction < 0;
				if (fraction >= 0)
					progressBar.Progress = (int)(fraction * 1000);
			}));

			new Thread(() =>
			{
				string? error = null;
				string? success = null;
				try
				{
					success = job(installer);
				}
				catch (InstallException e)
				{
					error = e.Message;
				}
				catch (Exception e)
				{
					Log.Error(GameActivity.LogTag, e.ToString());
					error = "Something went wrong: " + e.Message;
				}

				RunOnUiThread(() =>
				{
					busy = false;
					if (success != null) Log.Info("CelesteAndroid", "[CelesteAndroid] launcher job done: " + success);
					RefreshState();
					if (error != null) ShowMessage("Couldn't finish", error);
				});
			}) { Name = "CelesteInstall", IsBackground = true }.Start();
		}

		#endregion
	}
}
