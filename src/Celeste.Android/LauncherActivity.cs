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
using Color = Android.Graphics.Color;
using Uri = Android.Net.Uri;

namespace CelesteAndroid
{
	/// <summary>
	/// Home screen: the game's key art, game file import and the play button.
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
		private const int RequestZip = 2;
		private const int RequestSaves = 3;
		private const int RequestSteam = 4;
		private const string LogTag = "CelesteLauncher";

		private static readonly Color Night = Color.ParseColor("#120C22");
		private static readonly Color Accent = Color.ParseColor("#F2B8D8");

		private ImageView art = null!;
		private LinearLayout panel = null!;
		private TextView status = null!;
		private TextView play = null!;
		private TextView steamButton = null!;
		private TextView importFolder = null!;
		private TextView deleteGame = null!;
		private LinearLayout links2 = null!;
		private TextView deleteSeparator = null!;
		private TextView openZip = null!;
		private TextView importSaves = null!;
		private TextView settingsLink = null!;
		private LinearLayout links = null!;
		private LinearLayout progressBox = null!;
		private ProgressBar progressBar = null!;
		private TextView progressText = null!;
		private bool busy;


		protected override void OnCreate(Bundle? savedInstanceState)
		{
			base.OnCreate(savedInstanceState);
			View root = BuildLayout();
			SetContentView(root);
			WatchFocus(root);
			HideSystemBars();
			LoadArt();

			// "Splash": the art appears alone and the panel slides in right after.
			panel.Alpha = 0f;
			panel.TranslationX = -Dp(32);
			panel.Animate()!.Alpha(1f).TranslationX(0f).SetStartDelay(650).SetDuration(550)
				.SetInterpolator(new DecelerateInterpolator(2f))!.Start();

			var zoom = new ScaleAnimation(1f, 1.08f, 1f, 1.08f, Dimension.RelativeToSelf, 0.5f, Dimension.RelativeToSelf, 0.4f)
			{
				Duration = 24000,
				RepeatCount = Animation.Infinite,
				RepeatMode = RepeatMode.Reverse,
				Interpolator = new AccelerateDecelerateInterpolator(),
			};
			art.StartAnimation(zoom);

			MaybeAutoplay(savedInstanceState);

			// An install that was interrupted (process killed, reboot) resumes by itself; one that is running opens its screen.
			if (InstallCoordinator.IsActive)
				StartSteam();
			else if (InstallCoordinator.PendingMode(this) is string pending && SteamTokenStore.HasSession(this))
			{
				try { InstallService.Start(this, pending == "update"); StartSteam(); }
				catch (Exception e) { Log.Warn(LogTag, "could not resume the install now: " + e.Message); }
			}
		}

		/// <summary>Autoplay is decided once per app process: coming back from the game ("Exit to launcher", the title Exit) must not start it again.</summary>
		private static bool autoplayHandled;

		/// <summary>Opening the app with the game ready and Autoplay on goes straight into the game.</summary>
		private void MaybeAutoplay(Bundle? savedInstanceState)
		{
			if (autoplayHandled) return;
			autoplayHandled = true;
			if (savedInstanceState != null) return; // recreated by the system (e.g. the launcher process died while the game ran)
			if (InstallCoordinator.IsActive || InstallCoordinator.PendingMode(this) != null || !GameInstaller.IsInstalled(this)) return;
			if (PortSettings.GetValue(this, PortSettingsCatalog.Autoplay) != "true") return;
			Log.Info(LogTag, "autoplay: starting game");
			Play();
			OverridePendingTransition(0, 0);
		}

		private Action<InstallStatus>? statusHandler;

		protected override void OnStart()
		{
			base.OnStart();
			statusHandler = s => RunOnUiThread(() =>
			{
				RefreshState();
				if (s.Stage == InstallStage.Done) { LoadArt(); if (play.Enabled) play.RequestFocus(); }
			});
			InstallCoordinator.Changed += statusHandler;
		}

		protected override void OnStop()
		{
			InstallCoordinator.Changed -= statusHandler;
			base.OnStop();
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
			// Development (adb shell am start ... --ez steam true [--ez forceLogin true | --ez showError true]): opens the Steam install.
			if (!busy && Intent!.GetBooleanExtra("steam", false))
			{
				bool force = Intent.GetBooleanExtra(SteamActivity.ExtraForceLogin, false);
				bool showError = Intent.GetBooleanExtra(SteamActivity.ExtraShowError, false);
				Intent.RemoveExtra("steam");
				var steamIntent = new Intent(this, typeof(SteamActivity));
				if (force) steamIntent.PutExtra(SteamActivity.ExtraForceLogin, true);
				if (showError) steamIntent.PutExtra(SteamActivity.ExtraShowError, true);
				StartActivityForResult(steamIntent, RequestSteam);
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
		protected override View? InitialFocus() => play.Enabled ? play : steamButton;
		protected override bool InputBlocked => busy;
		protected override void OnBackButton()
		{
			if (confirmOverlay != null)
			{
				Log.Info(LogTag, "back: close confirmation");
				DismissConfirm();
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

			status = Text("", 14, Color.Argb(220, 255, 255, 255), TypefaceStyle.Normal);
			panel.AddView(status, Margins(top: 10));

			play = PillButton("PLAY", filled: true);
			play.Click += (_, _) => Play();
			MakeFocusable(play, "Play");
			panel.AddView(play, UiKit.ButtonParams(this, 260, marginTop: 14));

			steamButton = PillButton("Install from Steam", filled: false);
			steamButton.Click += (_, _) => StartSteam();
			MakeFocusable(steamButton, "InstallFromSteam");
			panel.AddView(steamButton, UiKit.ButtonParams(this, 260, marginTop: 8));

			links = new LinearLayout(this) { Orientation = Orientation.Horizontal };
			importFolder = LinkText("Import folder");
			importFolder.Click += (_, _) => PickFolder();
			MakeFocusable(importFolder, "ImportFolder");
			openZip = LinkText("Import .zip");
			openZip.Click += (_, _) => PickZip();
			MakeFocusable(openZip, "ImportZip");
			importSaves = LinkText("Import saves");
			importSaves.Click += (_, _) => StartActivityForResult(new Intent(Intent.ActionOpenDocumentTree), RequestSaves);
			MakeFocusable(importSaves, "ImportSaves");
			settingsLink = LinkText("Port settings");
			settingsLink.Click += (_, _) => StartActivity(new Intent(this, typeof(PortSettingsActivity)));
			MakeFocusable(settingsLink, "PortSettings");
			deleteGame = LinkText("Delete game files");
			deleteGame.Click += (_, _) => ConfirmDeleteGame();
			MakeFocusable(deleteGame, "DeleteGameFiles");
			links.AddView(importFolder);
			links.AddView(Text("·", 14, Color.Argb(120, 255, 255, 255), TypefaceStyle.Normal), Margins(left: 8, right: 8));
			links.AddView(openZip);
			links.AddView(Text("·", 14, Color.Argb(120, 255, 255, 255), TypefaceStyle.Normal), Margins(left: 8, right: 8));
			links.AddView(importSaves);
			panel.AddView(links, Margins(top: 4, left: 6));
			links2 = new LinearLayout(this) { Orientation = Orientation.Horizontal };
			links2.AddView(settingsLink);
			deleteSeparator = Text("·", 14, Color.Argb(120, 255, 255, 255), TypefaceStyle.Normal);
			links2.AddView(deleteSeparator, Margins(left: 8, right: 8));
			links2.AddView(deleteGame);
			panel.AddView(links2, Margins(top: 0, left: 6));

			progressBox = new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
			progressBar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 1000 };
			progressBar.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(Accent);
			progressBar.IndeterminateTintList = Android.Content.Res.ColorStateList.ValueOf(Accent);
			progressText = Text("", 13, Color.Argb(210, 255, 255, 255), TypefaceStyle.Normal);
			progressBox.AddView(progressBar, new LinearLayout.LayoutParams(Dp(260), Dp(8)));
			progressBox.AddView(progressText, Margins(top: 6));
			panel.AddView(progressBox, Margins(top: 12));

			var hint = Text("A  select     B  back     D-pad / stick  move", 12, Color.Argb(150, 255, 255, 255), TypefaceStyle.Normal);
			var hintParams = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent,
				GravityFlags.Bottom | GravityFlags.End) { RightMargin = Dp(28), BottomMargin = Dp(20) };
			root.AddView(hint, hintParams);

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

		private TextView PillButton(string label, bool filled) => UiKit.Pill(this, label, filled, minHeightDp: filled ? 50 : 44);

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
			play.Enabled = installed && !busy;
			play.Alpha = play.Enabled ? 1f : 0.4f;
			// "Update from Steam" is gone from here: updates live in Port settings > Check for updates.
			bool installing = InstallCoordinator.IsActive;
			steamButton.Visibility = installed && !installing ? ViewStates.Gone : ViewStates.Visible;
			steamButton.Enabled = !busy;
			importFolder.Enabled = !busy;
			openZip.Enabled = !busy;
			importSaves.Enabled = !busy;
			deleteGame.Enabled = !busy;
			deleteGame.Visibility = installed ? ViewStates.Visible : ViewStates.Gone;
			deleteSeparator.Visibility = deleteGame.Visibility;
			// The screen is short (~360dp): during installation the progress bar takes the links' place.
			links.Visibility = busy ? ViewStates.Gone : ViewStates.Visible;
			links2.Visibility = busy ? ViewStates.Gone : ViewStates.Visible;
			steamButton.Text = installing ? "Installing… (view progress)" : "Install from Steam";
			if (installing)
			{
				InstallStatus live = InstallCoordinator.Current;
				status.Text = live.Step;
				progressBox.Visibility = ViewStates.Visible;
				progressText.Text = live.Detail;
				progressBar.Indeterminate = live.Fraction < 0;
				if (live.Fraction >= 0) progressBar.Progress = (int)(live.Fraction * 1000);
			}
			else if (!busy)
			{
				bool paused = File.Exists(SteamTokenStore.ResumeStatePath(this));
				status.Text = installed
					? "✓  Ready to play"
					: paused
						? "Download paused: choose Install from Steam to resume."
						: "Install Celeste from your Steam account, or import the folder of your PC copy / the Linux .zip.";
			}
			EnsureFocus();
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

		private FrameLayout rootLayout = null!;
		private View? confirmOverlay;
		private View? focusBeforeConfirm;

		private void ConfirmDeleteGame() =>
			ShowConfirm("Delete game files?",
				"This frees the space used by the game (about 1.2 GB). Your saves and settings are kept; you can install again from Steam or import the game.",
				"Delete", () => RunJob(installer =>
				{
					long freed = installer.DeleteGame();
					RunOnUiThread(LoadArt);
					return $"✓  Game files deleted ({freed / (1024 * 1024)} MB freed)";
				}));

		/// <summary>Confirmation in the launcher's own look: visible focus, Cancel focused first, A confirms and B cancels.</summary>
		private void ShowConfirm(string title, string message, string confirmText, Action onConfirm)
		{
			if (confirmOverlay != null) return;
			focusBeforeConfirm = CurrentFocus;
			panel.DescendantFocusability = DescendantFocusability.BlockDescendants;

			var overlay = new FrameLayout(this) { Clickable = true };
			overlay.SetBackgroundColor(Color.Argb(205, 8, 5, 18));
			var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
			var cardShape = new GradientDrawable();
			cardShape.SetCornerRadius(Dp(22));
			cardShape.SetColor(Color.ParseColor("#1F1636"));
			cardShape.SetStroke(Dp(1.5f), Color.Argb(120, 242, 184, 216));
			card.Background = cardShape;
			card.SetPadding(Dp(26), Dp(22), Dp(26), Dp(20));
			card.AddView(Text(title, 22, Color.White, TypefaceStyle.Bold));
			var body = Text(message, 15, Color.Argb(225, 255, 255, 255), TypefaceStyle.Normal);
			var bodyScroll = new ScrollView(this) { VerticalScrollBarEnabled = false };
			bodyScroll.AddView(body);
			card.AddView(bodyScroll, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(8) });

			var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
			buttons.SetGravity(GravityFlags.End);
			TextView cancel = PillButton("Cancel", filled: false);
			MakeFocusable(cancel, "ConfirmCancel");
			cancel.Click += (_, _) => DismissConfirm();
			TextView confirm = PillButton(confirmText, filled: true);
			MakeFocusable(confirm, "ConfirmYes");
			confirm.Click += (_, _) => { DismissConfirm(); onConfirm(); };
			buttons.AddView(cancel, UiKit.ButtonParams(this, 140, marginTop: 4, marginSide: 6));
			buttons.AddView(confirm, UiKit.ButtonParams(this, 140, marginTop: 4, marginSide: 6));
			card.AddView(buttons, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(14) });

			UiKit.NoClip(card); UiKit.NoClip(buttons);
			card.SetPadding(Dp(26), Dp(22), Dp(26), Dp(22));
			overlay.AddView(card, new FrameLayout.LayoutParams(Dp(520), ViewGroup.LayoutParams.WrapContent, GravityFlags.Center) { TopMargin = Dp(16), BottomMargin = Dp(16) });
			rootLayout.AddView(overlay, Match());
			confirmOverlay = overlay;
			cancel.RequestFocus();
		}

		private void DismissConfirm()
		{
			if (confirmOverlay == null) return;
			rootLayout.RemoveView(confirmOverlay);
			confirmOverlay = null;
			panel.DescendantFocusability = DescendantFocusability.AfterDescendants;
			(focusBeforeConfirm ?? play).RequestFocus();
		}

		private void PickFolder()
		{
			StartActivityForResult(new Intent(Intent.ActionOpenDocumentTree), RequestFolder);
		}

		private void PickZip()
		{
			var intent = new Intent(Intent.ActionOpenDocument);
			intent.AddCategory(Intent.CategoryOpenable);
			intent.SetType("*/*");
			intent.PutExtra(Intent.ExtraMimeTypes, new[] { "application/zip", "application/x-zip-compressed" });
			StartActivityForResult(intent, RequestZip);
		}

		protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
		{
			base.OnActivityResult(requestCode, resultCode, data);
			if (requestCode == RequestSteam)
			{
				LoadArt();
				RefreshState();
				if (resultCode == Result.Ok && play.Enabled)
					play.RequestFocus(); // "Done" returns to the main screen with Play focused
				return;
			}
			Uri? uri = data?.Data;
			if (resultCode != Result.Ok || uri == null)
				return;
			if (requestCode == RequestFolder)
				RunInstall(installer => installer.ImportFolder(uri));
			else if (requestCode == RequestZip)
				RunInstall(installer => installer.ImportZip(uri));
			else if (requestCode == RequestSaves)
				RunJob(installer => $"✓  Imported {installer.ImportSaves(uri)} save file(s)");
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
				return "✓  Game imported! Ready to play";
			});
		}

		/// <summary>Runs an installer task off the UI thread, with progress; returns the success message.</summary>
		private void RunJob(Func<GameInstaller, string> job)
		{
			busy = true;
			progressBox.Visibility = ViewStates.Visible;
			RefreshState();

			var installer = new GameInstaller(this, (message, fraction) => RunOnUiThread(() =>
			{
				progressText.Text = message;
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
					progressBox.Visibility = ViewStates.Gone;
					RefreshState();
					status.Text = error ?? success;
				});
			}) { Name = "CelesteInstall", IsBackground = true }.Start();
		}

		#endregion
	}
}
