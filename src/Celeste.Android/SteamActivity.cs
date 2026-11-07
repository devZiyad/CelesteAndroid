using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Text.Method;
using Android.Util;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using CelesteAndroid.Steam;
using QRCoder;
using Color = Android.Graphics.Color;
using OperationCanceledException = System.OperationCanceledException;
using Path = System.IO.Path;
using Uri = Android.Net.Uri;

namespace CelesteAndroid
{
	/// <summary>
	/// The Steam install screen: sign-in (username/password with Steam Guard, or a QR code scanned from another device) and then the live
	/// progress of the foreground <see cref="InstallService"/>, which does the download, the checks and the patching. Never logs or
	/// stores the password; only the refresh token, encrypted in the Keystore.
	/// </summary>
	[Activity(
		Name = "org.devziyad.celeste.SteamActivity",
		Exported = false,
		Theme = "@style/Theme.Celeste",
		ScreenOrientation = ScreenOrientation.SensorLandscape,
		WindowSoftInputMode = SoftInput.AdjustResize | SoftInput.StateHidden,
		ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
			| ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation)]
	public class SteamActivity : ControllerActivity, ISteamLoginEvents
	{
		private const string LogTag = "CelesteSteam";
		private const string SteamPackage = "com.valvesoftware.android.steam.community";
		/// <summary>"Check for updates": a no-op when the installed manifest is already the latest.</summary>
		public const string ExtraUpdate = "update";
		/// <summary>Debug (adb): ignores the stored session (without deleting it) to show the login screens.</summary>
		public const string ExtraForceLogin = "forceLogin";
		/// <summary>Debug (adb): shows the error screen to check the layout.</summary>
		public const string ExtraShowError = "showError";

		private static readonly Color Night = Color.ParseColor("#120C22");
		private static readonly Color Accent = Color.ParseColor("#F2B8D8");

		private enum Screen { Message, Qr, Credentials, Progress }

		private TextView title = null!, status = null!, detail = null!;
		private LinearLayout qrGroup = null!, credentialsGroup = null!, progressGroup = null!, actions = null!;
		private ImageView qrImage = null!;
		private ProgressBar bar = null!;
		private TextView signInButton = null!, openSteamCredentials = null!, submitCode = null!, cancelButton = null!, retryButton = null!;
		private TextView backToCredentials = null!, useQr = null!, enterCode = null!;
		private EditText userField = null!, passField = null!, codeField = null!;

		private CancellationTokenSource cts = new();
		private SteamSession? session;
				private bool codeIsEmail;
		private TaskCompletionSource<bool>? switchToCredentials, switchToQr;
		private TaskCompletionSource<(string User, string Pass)>? credentialsSubmitted;
		private View? firstFocus;
		private bool finishing, installDone, updateMode;
		private Screen currentScreen = Screen.Message;
		private Action<InstallStatus>? statusHandler;

		protected override string Tag => LogTag;
		protected override View? InitialFocus() => firstFocus ?? cancelButton;
		protected override void OnBackButton()
		{
			// Leaving while the service works does not stop the download: it goes on in the background.
			if (InstallCoordinator.IsActive || installDone) CancelAndClose(stopInstall: false);
			else CancelAndClose(stopInstall: true);
		}

		private void LogLine(string line) => Log.Info("CelesteAndroid", "[CelesteAndroid] " + line);

		protected override void OnCreate(Bundle? savedInstanceState)
		{
			base.OnCreate(savedInstanceState);
			updateMode = Intent!.GetBooleanExtra(ExtraUpdate, false);
			View root = BuildLayout();
			SetContentView(root);
			WatchFocus(root);
			HideSystemBars();
			RequestNotificationPermission();
			statusHandler = status => RunOnUiThread(() => OnInstallStatus(status));
			InstallCoordinator.Changed += statusHandler;
			Begin();
		}

		protected override void OnDestroy()
		{
			InstallCoordinator.Changed -= statusHandler;
			cts.Cancel();
			session?.Dispose();
			base.OnDestroy();
		}

		#region Flow

		/// <summary>Opens the screen on a progress that is already running, or starts one (sign-in first when there is no session).</summary>
		private void Begin()
		{
			if (Intent!.GetBooleanExtra(ExtraShowError, false))
			{
				_ = ShowErrorAsync("Disconnected from Steam. (debug preview of the error screen)");
				return;
			}
			if (InstallCoordinator.IsActive)
			{
				OnInstallStatus(InstallCoordinator.Current); // reopened while the service works: show it live
				return;
			}
			if (Intent.GetBooleanExtra(ExtraForceLogin, false) || !SteamTokenStore.HasSession(this))
				_ = Task.Run(() => SignInThenInstallAsync(cts.Token));
			else
				StartInstall();
		}

		private void StartInstall()
		{
			SetScreen(Screen.Progress, updateMode ? "Updating Celeste" : "Installing Celeste", "Starting…");
			bar.Indeterminate = true;
			InstallService.Start(this, updateMode);
		}

		/// <summary>Interactive part only: get a Steam session (and keep its token); the foreground service does the rest.</summary>
		private async Task SignInThenInstallAsync(CancellationToken ct)
		{
			try
			{
				SetScreen(Screen.Message, "Install from Steam", "Connecting to Steam…");
				session = new SteamSession(LogLine);
				await session.ConnectAsync(ct).ConfigureAwait(false);
				SteamAuthResult auth = await SignInAsync(ct).ConfigureAwait(false);
				SteamTokenStore.Save(this, auth.AccountName, auth.RefreshToken);
				LogLine("steam: login successful, session stored encrypted (Android Keystore)");
				session.Dispose();
				session = null;
				RunOnUiThread(StartInstall);
			}
			catch (OperationCanceledException)
			{
				LogLine("steam: sign-in cancelled");
			}
			catch (Exception e)
			{
				LogLine("steam: sign-in failed: " + Describe(e));
				await ShowErrorAsync(Describe(e)).ConfigureAwait(false);
			}
		}

		/// <summary>Follows the service's state: step text, file counter and bytes while working; "Done" when finished.</summary>
		private void OnInstallStatus(InstallStatus s)
		{
			switch (s.Stage)
			{
				case InstallStage.Running or InstallStage.Waiting:
					if (currentScreen != Screen.Progress)
						SetScreen(Screen.Progress, s.Title, s.Step);
					title.Text = s.Title;
					status.Text = s.Step;
					detail.Text = s.Detail;
					bar.Indeterminate = s.Fraction < 0;
					if (s.Fraction >= 0) bar.Progress = (int)(s.Fraction * 1000);
					cancelButton.Text = "Cancel";
					break;
				case InstallStage.Done:
					installDone = true;
					if (currentScreen != Screen.Progress)
						SetScreen(Screen.Progress, s.Title, s.Step);
					title.Text = s.Title;
					status.Text = s.Step;
					detail.Text = "";
					bar.Indeterminate = false;
					bar.Progress = 1000;
					cancelButton.Text = "Done"; // Cancel turns into Done; it returns to the main screen with Play focused
					cancelButton.RequestFocus();
					break;
				case InstallStage.Failed:
					_ = ShowErrorAsync(s.Step);
					break;
				case InstallStage.NeedsSignIn:
					if (session == null)
					{
						cts.Cancel();
						cts = new CancellationTokenSource();
						CancellationToken token = cts.Token;
						_ = Task.Run(() => SignInThenInstallAsync(token));
					}
					break;
			}
		}

		private void RequestNotificationPermission()
		{
			// Android 13+: without it the service still runs, but its progress notification is hidden.
			if (OperatingSystem.IsAndroidVersionAtLeast(33) && CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted)
				RequestPermissions(new[] { "android.permission.POST_NOTIFICATIONS" }, 7);
		}

		/// <summary>Never includes details that could hold secrets: only the exception's type and message.</summary>
		private static string Describe(Exception e) => e switch
		{
			SteamKit2.Authentication.AuthenticationException a => $"Sign-in failed: {a.Result}",
			SteamException s => s.Message,
			DepotInstallException d => d.Message,
			InstallException i => i.Message,
			_ => $"{e.GetType().Name}: {e.Message}",
		};

		/// <summary>
		/// 1) Username and password (default), with Steam Guard: push approval in the Steam app, or a typed code.
		/// 2) QR code, to be scanned with the Steam app on ANOTHER device (the Steam app cannot approve a sign-in link on the same phone).
		/// </summary>
		private async Task<SteamAuthResult> SignInAsync(CancellationToken ct)
		{
			while (true)
			{
				ct.ThrowIfCancellationRequested();
				switchToQr = new(TaskCreationOptions.RunContinuationsAsynchronously);
				credentialsSubmitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
				SetScreen(Screen.Credentials, "Sign in to Steam", "Your password is only sent to Steam and is never saved.");
				Task<(string User, string Pass)> submitted = credentialsSubmitted.Task;
				Task which = await Task.WhenAny(submitted, switchToQr.Task).ConfigureAwait(false);
				if (which == switchToQr.Task)
				{
					SteamAuthResult? viaQr = await SignInWithQrAsync(ct).ConfigureAwait(false);
					if (viaQr != null) return viaQr;
					continue; // back to username and password
				}

				(string user, string pass) = await submitted.ConfigureAwait(false);
				try
				{
					return await LoginWithCredentialsAsync(user, pass, ct).ConfigureAwait(false);
				}
				catch (SteamKit2.Authentication.AuthenticationException e)
				{
					LogLine("steam: credentials sign-in failed: " + e.Result);
					// back to the form with the reason
					credentialsSubmitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
					SetScreen(Screen.Credentials, "Sign in to Steam", "Sign-in failed (" + e.Result + "). Check your details and try again.");
					which = await Task.WhenAny(credentialsSubmitted.Task, switchToQr.Task).ConfigureAwait(false);
					if (which == switchToQr.Task)
					{
						SteamAuthResult? viaQr = await SignInWithQrAsync(ct).ConfigureAwait(false);
						if (viaQr != null) return viaQr;
						continue;
					}
					(user, pass) = await credentialsSubmitted.Task.ConfigureAwait(false);
					return await LoginWithCredentialsAsync(user, pass, ct).ConfigureAwait(false);
				}
				finally
				{
					user = pass = "";
				}
			}
		}

		private async Task<SteamAuthResult> LoginWithCredentialsAsync(string user, string pass, CancellationToken ct)
		{
			// The form may have sat idle for minutes and Steam closes idle connections: reconnect before sending.
			if (!session!.IsConnected)
			{
				LogLine("steam: connection was closed while the form was open, reconnecting");
				session.Dispose();
				session = new SteamSession(LogLine);
				await session.ConnectAsync(ct).ConfigureAwait(false);
			}
			RunOnUiThread(() => { signInButton.Enabled = false; status.Text = "Signing in…"; });
			return await session!.LoginWithCredentialsAsync(user, pass, this, ct).ConfigureAwait(false);
		}

		/// <summary>Shows the QR code; returns the sign-in, or null when the user went back to username and password.</summary>
		private async Task<SteamAuthResult?> SignInWithQrAsync(CancellationToken ct)
		{
			while (true)
			{
				switchToCredentials = new(TaskCreationOptions.RunContinuationsAsynchronously);
				SetScreen(Screen.Qr, "Sign in with a QR code", "On another device, open the Steam app and scan this code, then approve the sign-in.");
				using var qrCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
				Task<SteamAuthResult> qr = session!.LoginWithQrAsync(this, qrCts.Token);
				Task finished = await Task.WhenAny(qr, switchToCredentials.Task).ConfigureAwait(false);
				if (finished == qr)
				{
					try
					{
						return await qr.ConfigureAwait(false);
					}
					catch (Exception e) when (e is SteamKit2.AsyncJobFailedException or SteamException or SteamKit2.Authentication.AuthenticationException && !ct.IsCancellationRequested)
					{
						// The connection dropped (or the challenge expired) while waiting for approval: reconnect and issue a new QR.
						LogLine("steam: connection lost while waiting for approval, reconnecting with a new QR code");
						session.Dispose();
						session = new SteamSession(LogLine);
						await session.ConnectAsync(ct).ConfigureAwait(false);
						continue;
					}
				}
				qrCts.Cancel();
				try { await qr.ConfigureAwait(false); } catch (OperationCanceledException) { }
				return null;
			}
		}

		private async Task ShowErrorAsync(string message)
		{
			RunOnUiThread(() =>
			{
				SetScreen(Screen.Message, "Couldn't install from Steam", message);
				retryButton.Visibility = ViewStates.Visible;
				retryButton.RequestFocus();
			});
			await Task.CompletedTask.ConfigureAwait(false);
		}

		private void CancelAndClose(bool stopInstall = true)
		{
			if (finishing) return;
			finishing = true;
			if (installDone)
			{
				SetResult(Result.Ok); // the launcher refreshes and focuses Play
			}
			else
			{
				if (stopInstall) InstallService.CancelInstall(this);
				cts.Cancel();
				SetResult(Result.Canceled);
			}
			Finish();
		}

		#endregion

		#region ISteamLoginEvents

		public void OnQrChallenge(string url)
		{
			RunOnUiThread(() => qrImage.SetImageBitmap(MakeQr(url)));
		}

		public void OnWaitingForDeviceApproval() => RunOnUiThread(() =>
		{
			status.Text = "Approve the sign-in in the Steam app (a notification was sent), or enter a Steam Guard code.";
			openSteamCredentials.Visibility = ViewStates.Visible;
			enterCode.Visibility = ViewStates.Visible;
		});

		public void OnNeedGuardCode(bool isEmailCode, string? emailHint, bool previousCodeWasIncorrect) => RunOnUiThread(() =>
		{
			codeIsEmail = isEmailCode;
			ShowCodeEntry(previousCodeWasIncorrect ? "That code was wrong, try again." : isEmailCode ? "Enter the code Steam emailed you." : "Enter the code from your Steam Guard app.");
		});

		#endregion

		#region Screens

		private View BuildLayout()
		{
			var root = new FrameLayout(this);
			root.SetBackgroundColor(Night);
			root.AddView(new View(this)
			{
				Background = new GradientDrawable(GradientDrawable.Orientation.TopBottom!, new int[] { Color.Argb(255, 38, 24, 70), Color.Argb(255, 18, 12, 34) }),
			}, new FrameLayout.LayoutParams(-1, -1));

			var row = UiKit.NoClip(new LinearLayout(this) { Orientation = Orientation.Horizontal });
			row.SetGravity(GravityFlags.CenterVertical);
			row.SetPadding(Dp(64), Dp(12), Dp(64), Dp(12));
			root.AddView(row, new FrameLayout.LayoutParams(-1, -1));

			var column = UiKit.NoClip(new LinearLayout(this) { Orientation = Orientation.Vertical });
			column.SetGravity(GravityFlags.CenterVertical);
			column.SetPadding(Dp(6), Dp(10), Dp(6), Dp(10));
			// If the content does not fit (form + keyboard, small screens), scroll instead of clipping.
			var scroll = new ScrollView(this) { VerticalScrollBarEnabled = false, FillViewport = true };
			scroll.SetClipChildren(false);
			scroll.SetClipToPadding(false);
			scroll.AddView(column, new FrameLayout.LayoutParams(-1, -2));
			row.AddView(scroll, new LinearLayout.LayoutParams(0, -1, 1f));

			title = Text("", 26, Color.White, TypefaceStyle.Bold);
			column.AddView(title);
			status = Text("", 15, Color.Argb(225, 255, 255, 255), TypefaceStyle.Normal);
			column.AddView(status, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(6) });

			// QR code (second option): scanned with the Steam app on another device; the code itself is drawn at the right
			qrGroup = UiKit.NoClip(new LinearLayout(this) { Orientation = Orientation.Vertical });
			backToCredentials = Link("Sign in with username and password instead");
			backToCredentials.Click += (_, _) => switchToCredentials?.TrySetResult(true);
			MakeFocusable(backToCredentials, "BackToCredentials");
			qrGroup.AddView(backToCredentials, new LinearLayout.LayoutParams(-2, -2) { TopMargin = Dp(14) });
			column.AddView(qrGroup);

			// Username/password
			credentialsGroup = UiKit.NoClip(new LinearLayout(this) { Orientation = Orientation.Vertical });
			userField = Field("Steam username", password: false);
			passField = Field("Password", password: true);
			codeField = Field("Steam Guard code", password: false);
			codeField.Visibility = ViewStates.Gone;
			credentialsGroup.AddView(userField, FieldParams(10));
			credentialsGroup.AddView(passField, FieldParams(6));
			signInButton = Pill("Sign in", filled: true);
			signInButton.Click += (_, _) => SubmitCredentials();
			MakeFocusable(signInButton, "SignIn");
			credentialsGroup.AddView(signInButton, UiKit.ButtonParams(this, 300, marginTop: 10));
			openSteamCredentials = Pill("Open the Steam app to approve", filled: false);
			openSteamCredentials.Visibility = ViewStates.Gone;
			openSteamCredentials.Click += (_, _) => OpenSteamApp();
			MakeFocusable(openSteamCredentials, "OpenSteamApp");
			credentialsGroup.AddView(openSteamCredentials, UiKit.ButtonParams(this, 300, marginTop: 6));
			enterCode = Link("Enter a Steam Guard code instead");
			enterCode.Visibility = ViewStates.Gone;
			enterCode.Click += (_, _) => ShowCodeEntry("Enter the code from your Steam Guard app.");
			MakeFocusable(enterCode, "EnterCode");
			credentialsGroup.AddView(enterCode, new LinearLayout.LayoutParams(-2, -2) { TopMargin = Dp(4) });
			credentialsGroup.AddView(codeField, FieldParams(6));
			submitCode = Pill("Submit code", filled: false);
			submitCode.Visibility = ViewStates.Gone;
			submitCode.Click += (_, _) => SubmitCode();
			MakeFocusable(submitCode, "SubmitCode");
			credentialsGroup.AddView(submitCode, UiKit.ButtonParams(this, 300, marginTop: 6));
			useQr = Link("Sign in with a QR code instead");
			useQr.Click += (_, _) => switchToQr?.TrySetResult(true);
			MakeFocusable(useQr, "UseQr");
			credentialsGroup.AddView(useQr, new LinearLayout.LayoutParams(-2, -2) { TopMargin = Dp(6) });
			column.AddView(credentialsGroup);

			// Progresso
			progressGroup = new LinearLayout(this) { Orientation = Orientation.Vertical };
			bar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 1000 };
			bar.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(Accent);
			bar.IndeterminateTintList = Android.Content.Res.ColorStateList.ValueOf(Accent);
			progressGroup.AddView(bar, new LinearLayout.LayoutParams(Dp(420), Dp(10)) { TopMargin = Dp(14) });
			detail = Text("", 14, Color.Argb(210, 255, 255, 255), TypefaceStyle.Normal);
			progressGroup.AddView(detail, new LinearLayout.LayoutParams(-2, -2) { TopMargin = Dp(8) });
			column.AddView(progressGroup);

			actions = UiKit.NoClip(new LinearLayout(this) { Orientation = Orientation.Horizontal });
			cancelButton = Pill("Cancel", filled: false);
			cancelButton.Click += (_, _) => CancelAndClose(stopInstall: true);
			MakeFocusable(cancelButton, "Cancel");
			actions.AddView(cancelButton, UiKit.ButtonParams(this, 150, marginTop: 14));
			retryButton = Pill("Try again", filled: true);
			retryButton.Visibility = ViewStates.Gone;
			retryButton.Click += (_, _) => { cts.Cancel(); cts = new CancellationTokenSource(); session?.Dispose(); session = null; retryButton.Visibility = ViewStates.Gone; Begin(); };
			MakeFocusable(retryButton, "Retry");
			actions.AddView(retryButton, UiKit.ButtonParams(this, 170, marginTop: 14));
			column.AddView(actions);

			qrImage = new ImageView(this);
			qrImage.SetScaleType(ImageView.ScaleType.FitCenter);
			qrImage.SetBackgroundColor(Color.White);
			qrImage.SetPadding(Dp(8), Dp(8), Dp(8), Dp(8));
			qrImage.Visibility = ViewStates.Gone;
			row.AddView(qrImage, new LinearLayout.LayoutParams(Dp(230), Dp(230)) { LeftMargin = Dp(24) });
			return root;
		}

		private void SetScreen(Screen screen, string titleText, string statusText) => RunOnUiThread(() =>
		{
			currentScreen = screen;
			title.Text = titleText;
			status.Text = statusText;
			qrGroup.Visibility = screen == Screen.Qr ? ViewStates.Visible : ViewStates.Gone;
			qrImage.Visibility = screen == Screen.Qr ? ViewStates.Visible : ViewStates.Gone;
			credentialsGroup.Visibility = screen == Screen.Credentials ? ViewStates.Visible : ViewStates.Gone;
			progressGroup.Visibility = screen == Screen.Progress ? ViewStates.Visible : ViewStates.Gone;
			retryButton.Visibility = ViewStates.Gone;
			signInButton.Enabled = true;
			firstFocus = screen switch
			{
				Screen.Qr => backToCredentials,
				Screen.Credentials => userField,
				_ => cancelButton,
			};
			CurrentFocus?.ClearFocus();
			firstFocus.RequestFocus();
		});

		private void ShowCodeEntry(string message)
		{
			status.Text = message;
			codeField.Visibility = ViewStates.Visible;
			submitCode.Visibility = ViewStates.Visible;
			enterCode.Visibility = ViewStates.Gone;
			codeField.RequestFocus();
		}

		private void SubmitCredentials()
		{
			string user = userField.Text ?? "", pass = passField.Text ?? "";
			if (user.Length == 0 || pass.Length == 0) { status.Text = "Enter your username and password."; return; }
			passField.Text = ""; // the password stays neither on screen nor in the field
			((InputMethodManager?)GetSystemService(InputMethodService))?.HideSoftInputFromWindow(passField.WindowToken, HideSoftInputFlags.None);
			credentialsSubmitted?.TrySetResult((user, pass));
			signInButton.Enabled = false;
		}

		private async void SubmitCode()
		{
			string code = codeField.Text ?? "";
			codeField.Text = "";
			if (code.Length == 0 || session == null) return;
			try { await session.SubmitGuardCodeAsync(code, codeIsEmail); }
			catch (Exception e) { status.Text = Describe(e); }
		}

		private void OpenSteamApp()
		{
			Intent? launch = PackageManager!.GetLaunchIntentForPackage(SteamPackage);
			if (launch != null) StartActivity(launch);
			else status.Text = "The Steam app isn't installed on this phone.";
		}

		private Bitmap MakeQr(string text)
		{
			using var generator = new QRCodeGenerator();
			using QRCodeData data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
			var matrix = data.ModuleMatrix;
			const int quiet = 2, scale = 10;
			int modules = matrix.Count, size = (modules + quiet * 2) * scale;
			var pixels = new int[size * size];
			Array.Fill(pixels, unchecked((int)0xFFFFFFFF));
			for (int y = 0; y < modules; y++)
				for (int x = 0; x < modules; x++)
					if (matrix[y][x])
						for (int dy = 0; dy < scale; dy++)
							for (int dx = 0; dx < scale; dx++)
								pixels[((y + quiet) * scale + dy) * size + (x + quiet) * scale + dx] = unchecked((int)0xFF000000);
			return Bitmap.CreateBitmap(pixels, size, size, Bitmap.Config.Argb8888!)!;
		}

		#endregion

		#region Visual

		private LinearLayout.LayoutParams FieldParams(float marginTop) =>
			new(Dp(310), ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(marginTop), BottomMargin = Dp(4), LeftMargin = Dp(4), RightMargin = Dp(4) };

		private EditText Field(string hint, bool password)
		{
			var field = new EditText(this) { Hint = hint };
			field.SetTextColor(Color.White);
			field.SetHintTextColor(Color.Argb(120, 255, 255, 255));
			field.SetSingleLine(true);
			field.SetTextSize(ComplexUnitType.Sp, 16);
			field.SetPadding(Dp(16), Dp(10), Dp(16), Dp(10));
			field.SetMinHeight(Dp(46));
			field.SetMinimumHeight(Dp(46));
			field.InputType = password ? InputTypes.ClassText | InputTypes.TextVariationPassword : InputTypes.ClassText | InputTypes.TextFlagNoSuggestions;
			if (password) field.TransformationMethod = PasswordTransformationMethod.Instance;
			field.ImeOptions = ImeAction.Next | (ImeAction)ImeFlags.NoExtractUi;
			field.Background = FocusBackground(Dp(23), Color.Argb(40, 255, 255, 255), 1.5f);
			field.Focusable = true;
			field.FocusableInTouchMode = true;
			field.ContentDescription = hint;
			return field;
		}

		private TextView Pill(string label, bool filled) => UiKit.Pill(this, label, filled, minHeightDp: filled ? 50 : 44);

		private TextView Link(string text)
		{
			var view = Text(text, 14, Accent, TypefaceStyle.Normal);
			view.SetPadding(Dp(8), Dp(6), Dp(8), Dp(6));
			view.Background = FocusBackground(Dp(14), Color.Argb(70, 242, 184, 216), 2.5f, transparentWhenIdle: true);
			return view;
		}

		private Drawable FocusBackground(int radius, Color fill, float strokeDp, bool transparentWhenIdle = false)
		{
			var focused = new GradientDrawable();
			focused.SetCornerRadius(radius);
			focused.SetColor(fill);
			focused.SetStroke(Dp(strokeDp), Accent);
			var idle = new GradientDrawable();
			idle.SetCornerRadius(radius);
			idle.SetColor(transparentWhenIdle ? Color.Transparent : fill);
			if (!transparentWhenIdle) idle.SetStroke(Dp(1.5f), Color.Argb(160, 255, 255, 255));
			var states = new StateListDrawable();
			states.AddState(new[] { Android.Resource.Attribute.StateFocused }, focused);
			states.AddState(new int[0], idle);
			return states;
		}

		private TextView Text(string text, float sp, Color color, TypefaceStyle style)
		{
			var view = new TextView(this) { Text = text };
			view.SetTextSize(ComplexUnitType.Sp, sp);
			view.SetTextColor(color);
			view.SetTypeface(Typeface.Create("sans-serif", style), style);
			return view;
		}

		private int Dp(float dp) => (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, dp, Resources!.DisplayMetrics);

		#endregion
	}
}
