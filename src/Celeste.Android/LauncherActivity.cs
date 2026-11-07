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
	/// Tela inicial: key art do jogo, importação dos arquivos do jogo e botão de jogar.
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
	public class LauncherActivity : Activity
	{
		private const int RequestFolder = 1;
		private const int RequestZip = 2;
		private const int RequestSaves = 3;
		private const string PrefDriver = "driver";
		private const string LogTag = "CelesteLauncher";

		private static readonly Color Night = Color.ParseColor("#120C22");
		private static readonly Color Accent = Color.ParseColor("#F2B8D8");

		private ImageView art = null!;
		private LinearLayout panel = null!;
		private TextView status = null!;
		private Button play = null!;
		private Button openFolder = null!;
		private TextView openZip = null!;
		private TextView importSaves = null!;
		private TextView driverToggle = null!;
		private LinearLayout links = null!;
		private LinearLayout progressBox = null!;
		private ProgressBar progressBar = null!;
		private TextView progressText = null!;
		private bool busy;
		private long nextStickNavMs;
		private FocusSearchDirection? stickDir;

		private ISharedPreferences Prefs => GetSharedPreferences("launcher", FileCreationMode.Private)!;

		protected override void OnCreate(Bundle? savedInstanceState)
		{
			base.OnCreate(savedInstanceState);
			View root = BuildLayout();
			SetContentView(root);
			root.ViewTreeObserver!.GlobalFocusChange += (_, e) =>
				Log.Info(LogTag, $"focus: {Describe(e.OldFocus)} -> {Describe(e.NewFocus)}");
			HideSystemBars();
			LoadArt();

			// "Splash": a arte aparece sozinha e o painel entra logo depois.
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
		}

		protected override void OnResume()
		{
			base.OnResume();
			HideSystemBars();
			RefreshState();

			// Desenvolvimento: arquivos já copiados por scripts/deploy-android.sh; só patch + fundo.
			// adb shell am start -n org.devziyad.celeste/.LauncherActivity --ez repatch true
			if (!busy && Intent!.GetBooleanExtra("repatch", false))
			{
				Intent.RemoveExtra("repatch");
				RunInstall(_ => { });
			}
			// Desenvolvimento (scripts/deploy-android.sh --play): abre o jogo direto.
			if (!busy && Intent!.GetBooleanExtra("autoplay", false))
			{
				Intent.RemoveExtra("autoplay");
				if (GameInstaller.IsInstalled(this))
				{
					Log.Info(LogTag, "autoplay: starting game");
					Play();
				}
			}
			// APK pessoal: o jogo já vem dentro do APK; instala sozinho na primeira abertura.
			else if (!busy && !GameInstaller.IsInstalled(this) && GameInstaller.HasEmbeddedGame(this))
			{
				RunInstall(installer => installer.ImportEmbedded());
			}
		}

		#region Controle

		private static string Describe(View? view) => view?.ContentDescription ?? view?.GetType().Name ?? "none";

		/// <summary>Torna a view alcançável por D-pad/analógico e dá um leve zoom enquanto focada.</summary>
		private void Focusable(View view, string name)
		{
			view.ContentDescription = name;
			view.Focusable = true;
			view.FocusableInTouchMode = true;
			view.FocusChange += (_, e) =>
				view.Animate()!.ScaleX(e.HasFocus ? 1.05f : 1f).ScaleY(e.HasFocus ? 1.05f : 1f).SetDuration(120).Start();
		}

		private void EnsureFocus()
		{
			if (CurrentFocus != null && CurrentFocus.IsShown && CurrentFocus.Enabled)
				return;
			if (play.Enabled)
				play.RequestFocus();
			else
				openFolder.RequestFocus();
		}

		private bool MoveFocus(FocusSearchDirection dir)
		{
			if (busy)
				return true;
			View? from = CurrentFocus;
			if (from == null)
			{
				EnsureFocus();
				return true;
			}
			View? next = from.FocusSearch(dir);
			if (next != null)
				next.RequestFocus(dir);
			return true;
		}

		public override bool DispatchKeyEvent(KeyEvent? e)
		{
			if (e == null)
				return base.DispatchKeyEvent(e);
			Keycode code = e.KeyCode;
			Log.Info(LogTag, $"key: {code} {e.Action}");
			switch (code)
			{
				case Keycode.ButtonA:
					// A = confirmar (o Android só mapeia ENTER/DPAD_CENTER por padrão).
					if (e.Action == KeyEventActions.Up && !busy)
					{
						View? target = CurrentFocus;
						Log.Info(LogTag, $"confirm: {Describe(target)}");
						target?.PerformClick();
					}
					return true;
				case Keycode.ButtonB:
					if (e.Action == KeyEventActions.Up && !busy)
					{
						Log.Info(LogTag, "back: exit launcher");
						Finish();
					}
					return true;
			}
			return base.DispatchKeyEvent(e);
		}

		/// <summary>Analógico esquerdo e D-pad "hat" (que muitos controles reportam como eixo) movem o foco, com repetição.</summary>
		public override bool OnGenericMotionEvent(MotionEvent? e)
		{
			if (e != null && e.Action == MotionEventActions.Move
				&& (e.Source & InputSourceType.Joystick) == InputSourceType.Joystick)
			{
				float x = e.GetAxisValue(Axis.HatX), y = e.GetAxisValue(Axis.HatY);
				if (x == 0 && y == 0) { x = e.GetAxisValue(Axis.X); y = e.GetAxisValue(Axis.Y); }
				FocusSearchDirection? dir = null;
				if (Math.Abs(x) > 0.6f || Math.Abs(y) > 0.6f)
				{
					dir = Math.Abs(x) > Math.Abs(y)
						? (x > 0 ? FocusSearchDirection.Right : FocusSearchDirection.Left)
						: (y > 0 ? FocusSearchDirection.Down : FocusSearchDirection.Up);
				}
				long now = SystemClock.UptimeMillis();
				if (dir == null)
				{
					stickDir = null;
				}
				else if (dir != stickDir || now >= nextStickNavMs)
				{
					nextStickNavMs = now + (dir != stickDir ? 380 : 140);
					stickDir = dir;
					Log.Info(LogTag, $"stick: {dir}");
					MoveFocus(dir.Value);
				}
				return true;
			}
			return base.OnGenericMotionEvent(e);
		}

		#endregion

		#region Layout

		private View BuildLayout()
		{
			var root = new FrameLayout(this);
			root.SetBackgroundColor(Night);

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

			panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
			panel.SetGravity(GravityFlags.CenterVertical | GravityFlags.Start);
			panel.SetPadding(Dp(56), Dp(12), Dp(24), Dp(12));
			root.AddView(panel, new FrameLayout.LayoutParams(Dp(460), ViewGroup.LayoutParams.MatchParent));

			// Logo do jogo (art/logo.png → GameArt) ou, sem ele, o nome em texto.
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
			Focusable(play, "Play");
			panel.AddView(play, new LinearLayout.LayoutParams(Dp(260), Dp(50)) { TopMargin = Dp(14) });

			openFolder = PillButton("Open game files", filled: false);
			openFolder.Click += (_, _) => PickFolder();
			Focusable(openFolder, "OpenGameFiles");
			panel.AddView(openFolder, new LinearLayout.LayoutParams(Dp(260), Dp(44)) { TopMargin = Dp(10) });

			links = new LinearLayout(this) { Orientation = Orientation.Horizontal };
			openZip = LinkText("Import .zip");
			openZip.Click += (_, _) => PickZip();
			Focusable(openZip, "ImportZip");
			importSaves = LinkText("Import saves");
			importSaves.Click += (_, _) => StartActivityForResult(new Intent(Intent.ActionOpenDocumentTree), RequestSaves);
			Focusable(importSaves, "ImportSaves");
			driverToggle = LinkText("");
			driverToggle.Click += (_, _) => ToggleDriver();
			Focusable(driverToggle, "GraphicsDriver");
			links.AddView(openZip);
			links.AddView(Text("·", 14, Color.Argb(120, 255, 255, 255), TypefaceStyle.Normal), Margins(left: 10, right: 10));
			links.AddView(importSaves);
			links.AddView(Text("·", 14, Color.Argb(120, 255, 255, 255), TypefaceStyle.Normal), Margins(left: 10, right: 10));
			links.AddView(driverToggle);
			panel.AddView(links, Margins(top: 6, left: 6));

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
			// Arte embutida no build (GameArt/) ou, na versão compartilhável, a key art do jogo importado.
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

		private Button PillButton(string label, bool filled)
		{
			var button = new Button(this) { Text = label, StateListAnimator = null };
			button.SetAllCaps(false);
			button.SetTextSize(ComplexUnitType.Sp, filled ? 18 : 15);
			button.SetTypeface(Typeface.Create("sans-serif-medium", filled ? TypefaceStyle.Bold : TypefaceStyle.Normal), filled ? TypefaceStyle.Bold : TypefaceStyle.Normal);
			button.LetterSpacing = filled ? 0.12f : 0.02f;
			var shape = new GradientDrawable();
			shape.SetCornerRadius(Dp(25));
			if (filled)
			{
				shape.SetColor(Color.White);
				button.SetTextColor(Night);
			}
			else
			{
				shape.SetColor(Color.Argb(40, 255, 255, 255));
				shape.SetStroke(Dp(1.5f), Color.Argb(200, 255, 255, 255));
				button.SetTextColor(Color.White);
			}
			var ripple = new RippleDrawable(Android.Content.Res.ColorStateList.ValueOf(Color.Argb(60, 242, 184, 216)), shape, null);
			// Foco (controle/teclado): contorno rosa grosso, bem visível à distância.
			var focused = new GradientDrawable();
			focused.SetCornerRadius(Dp(25));
			focused.SetColor(filled ? Color.Argb(255, 255, 236, 246) : Color.Argb(110, 242, 184, 216));
			focused.SetStroke(Dp(3.5f), Accent);
			var states = new StateListDrawable();
			states.AddState(new[] { Android.Resource.Attribute.StateFocused }, focused);
			states.AddState(new int[0], ripple);
			button.Background = states;
			return button;
		}

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

		private void HideSystemBars()
		{
			if (!OperatingSystem.IsAndroidVersionAtLeast(30))
				return;
			// A partir do Android 15 o app já é edge-to-edge por padrão.
			if (!OperatingSystem.IsAndroidVersionAtLeast(35))
				Window!.SetDecorFitsSystemWindows(false);
			IWindowInsetsController? insets = Window!.InsetsController;
			if (insets != null)
			{
				insets.Hide(WindowInsets.Type.SystemBars());
				insets.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
			}
		}

		#endregion

		#region Estado e ações

		private void RefreshState()
		{
			bool installed = GameInstaller.IsInstalled(this);
			play.Enabled = installed && !busy;
			play.Alpha = play.Enabled ? 1f : 0.4f;
			openFolder.Enabled = !busy;
			openZip.Enabled = !busy;
			importSaves.Enabled = !busy;
			// A tela é baixa (~360dp): durante a instalação a barra de progresso ocupa o lugar dos links.
			links.Visibility = busy ? ViewStates.Gone : ViewStates.Visible;
			openFolder.Text = installed ? "Change game files" : "Open game files";
			if (!busy)
			{
				status.Text = installed
					? "✓  Ready to play"
					: "Pick the folder of your Celeste PC copy (FNA / \"opengl\" build) or the itch.io .zip.";
			}
			driverToggle.Text = "Graphics: " + (Prefs.GetString(PrefDriver, "") == "OpenGL" ? "OpenGL ES" : "Vulkan");
			EnsureFocus();
		}

		private void Play()
		{
			var intent = new Intent(this, typeof(GameActivity));
			string? driver = Prefs.GetString(PrefDriver, "");
			if (!string.IsNullOrEmpty(driver))
				intent.PutExtra(GameActivity.ExtraDriver, driver);
			StartActivity(intent);
		}

		private void ToggleDriver()
		{
			string next = Prefs.GetString(PrefDriver, "") == "OpenGL" ? "" : "OpenGL";
			Prefs.Edit()!.PutString(PrefDriver, next)!.Apply();
			RefreshState();
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

		/// <summary>Importação do jogo: cópia → patch → fundo.</summary>
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

		/// <summary>Roda uma tarefa do instalador fora da thread de UI, com progresso; devolve a mensagem de sucesso.</summary>
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
