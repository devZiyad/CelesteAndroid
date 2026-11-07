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
	/// Instala o jogo pela Steam: login no próprio aparelho (QR aprovado no app da Steam do mesmo celular, ou usuário/senha com
	/// aprovação por push e código do Steam Guard como último recurso), confere a posse do Celeste e baixa o depot Linux 504233
	/// (ramo público) direto para a pasta final, com progresso navegável por controle. Depois entrega Celeste.exe e Content/ ao
	/// passo de patch do GameInstaller. Nunca registra nem guarda a senha; só o refresh token, criptografado no Keystore.
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
		public const string ExtraRecordManifest = "recordManifest";

		private static readonly Color Night = Color.ParseColor("#120C22");
		private static readonly Color Accent = Color.ParseColor("#F2B8D8");

		private enum Screen { Message, Qr, Credentials, Progress }

		private TextView title = null!, status = null!, detail = null!;
		private LinearLayout qrGroup = null!, credentialsGroup = null!, progressGroup = null!, actions = null!;
		private ImageView qrImage = null!;
		private ProgressBar bar = null!;
		private TextView openSteamQr = null!, signInButton = null!, openSteamCredentials = null!, submitCode = null!, cancelButton = null!, retryButton = null!;
		private TextView useCredentials = null!, backToQr = null!, enterCode = null!;
		private EditText userField = null!, passField = null!, codeField = null!;

		private CancellationTokenSource cts = new();
		private SteamSession? session;
		private string? challengeUrl;
		private bool codeIsEmail;
		private TaskCompletionSource<bool>? switchToCredentials, switchToQr;
		private TaskCompletionSource<(string User, string Pass)>? credentialsSubmitted;
		private View? firstFocus;
		private bool finishing;

		protected override string Tag => LogTag;
		protected override View? InitialFocus() => firstFocus ?? cancelButton;
		protected override void OnBackButton() => CancelAndClose();

		private void LogLine(string line) => Log.Info("CelesteAndroid", "[CelesteAndroid] " + line);

		protected override void OnCreate(Bundle? savedInstanceState)
		{
			base.OnCreate(savedInstanceState);
			Window!.AddFlags(WindowManagerFlags.KeepScreenOn); // o download não pode parar com a tela apagando
			View root = BuildLayout();
			SetContentView(root);
			WatchFocus(root);
			HideSystemBars();
			_ = Task.Run(() => PipelineAsync(cts.Token));
		}

		protected override void OnDestroy()
		{
			cts.Cancel();
			session?.Dispose();
			base.OnDestroy();
		}

		#region Fluxo

		private async Task PipelineAsync(CancellationToken ct)
		{
			try
			{
				SetScreen(Screen.Message, "Install from Steam", "Connecting to Steam…");
				session = new SteamSession(LogLine);
				await session.ConnectAsync(ct).ConfigureAwait(false);

				(string Account, string RefreshToken)? stored = SteamTokenStore.Load(this);
				bool loggedOn = false;
				if (stored != null)
				{
					SetScreen(Screen.Message, "Install from Steam", "Signing in…");
					try
					{
						await session.LogOnAsync(stored.Value.Account, stored.Value.RefreshToken, ct).ConfigureAwait(false);
						loggedOn = true;
						LogLine("steam: signed in with the stored session");
					}
					catch (SteamException e)
					{
						LogLine("steam: the stored session was rejected (" + e.Message + ")");
						SteamTokenStore.Clear(this);
						session.Dispose();
						session = new SteamSession(LogLine);
						await session.ConnectAsync(ct).ConfigureAwait(false);
					}
				}
				if (!loggedOn)
				{
					SteamAuthResult auth = await SignInAsync(ct).ConfigureAwait(false);
					SteamTokenStore.Save(this, auth.AccountName, auth.RefreshToken);
					LogLine("steam: login successful, session stored encrypted (Android Keystore)");
					SetScreen(Screen.Message, "Install from Steam", "Signing in…");
					await session.LogOnAsync(auth.AccountName, auth.RefreshToken, ct).ConfigureAwait(false);
				}

				SetScreen(Screen.Message, "Install from Steam", "Checking that your account owns Celeste…");
				if (!await session.OwnsGameAsync(ct).ConfigureAwait(false))
				{
					LogLine($"steam: ownership NOT confirmed for app {SteamSession.AppId}");
					await ShowErrorAsync("This Steam account doesn't own Celeste, and no family-shared copy was found.").ConfigureAwait(false);
					return;
				}
				LogLine($"steam: ownership confirmed (app {SteamSession.AppId} is in this account's licenses, own or family-shared)");

				SetScreen(Screen.Message, "Install from Steam", "Getting the file list…");
				(ManifestSnapshot manifest, IChunkSource chunks) = await session.GetManifestAsync(ct).ConfigureAwait(false);
				FileSelection.ValidateIsFnaBuild(manifest);
				SelectionResult selection = FileSelection.Select(manifest);
				LogLine($"steam: manifest {manifest.ManifestId}: {manifest.Files.Count(f => !f.IsDirectory)} files; needed set {selection.Needed.Count} files, {selection.NeededBytes} bytes; skipping {selection.Skipped.Count} files, {selection.SkippedBytes} bytes");
				if (Intent!.GetBooleanExtra(ExtraRecordManifest, false))
				{
					string record = Path.Combine(FilesDir!.AbsolutePath, "steam", "manifest-record.json");
					Directory.CreateDirectory(Path.GetDirectoryName(record)!);
					File.WriteAllText(record, manifest.ToJson());
					LogLine("steam: manifest recorded to files/steam/manifest-record.json (debug)");
				}

				string gameDir = GameInstaller.GameDir(this);
				long free = new StatFs(FilesDir!.AbsolutePath).AvailableBytes;
				long existing = Directory.Exists(gameDir) ? Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
				if (free + existing < selection.NeededBytes + (200L << 20))
				{
					await ShowErrorAsync($"Not enough free space: Celeste needs {FormatBytes(selection.NeededBytes)}, {FormatBytes(free)} available.").ConfigureAwait(false);
					return;
				}

				SetScreen(Screen.Progress, "Downloading Celeste", "");
				var installer = new DepotInstaller(gameDir, SteamTokenStore.ResumeStatePath(this), chunks, parallelism: 6, ShowProgress);
				InstallResult result = await installer.InstallAsync(manifest, ct).ConfigureAwait(false);
				LogLine($"steam: depot {SteamSession.LinuxDepotId} download completed and verified: files={result.Files} bytes={result.Bytes} (manifest needed set: files={selection.Needed.Count} bytes={selection.NeededBytes}); removed stale files={result.Removed.FilesDeleted}");

				var gameInstaller = new GameInstaller(this, (message, fraction) => RunOnUiThread(() =>
				{
					status.Text = message;
					bar.Indeterminate = fraction < 0;
				}));
				RunOnUiThread(() => { title.Text = "Adapting the game"; detail.Text = ""; });
				gameInstaller.Patch();
				gameInstaller.PrepareBackground();
				LogLine("steam: patching completed");

				SetResult(Result.Ok);
				SetScreen(Screen.Message, "Celeste is ready", "✓  Installed from Steam. You can play offline.");
				await Task.Delay(1200).ConfigureAwait(false);
				RunOnUiThread(Finish);
			}
			catch (OperationCanceledException)
			{
				LogLine("steam: cancelled (download progress is kept and resumes next time)");
				RunOnUiThread(Finish);
			}
			catch (Exception e)
			{
				LogLine("steam: failed: " + Describe(e));
				await ShowErrorAsync(Describe(e)).ConfigureAwait(false);
			}
		}

		/// <summary>Nunca inclui detalhes que possam ter segredos: só o tipo e a mensagem da exceção.</summary>
		private static string Describe(Exception e) => e switch
		{
			SteamKit2.Authentication.AuthenticationException a => $"Sign-in failed: {a.Result}",
			SteamException s => s.Message,
			DepotInstallException d => d.Message,
			InstallException i => i.Message,
			_ => $"{e.GetType().Name}: {e.Message}",
		};

		private async Task<SteamAuthResult> SignInAsync(CancellationToken ct)
		{
			while (true)
			{
				ct.ThrowIfCancellationRequested();
				// 1) QR (padrão): "Abrir no app da Steam" no mesmo celular, ou ler o QR de outro aparelho.
				switchToCredentials = new(TaskCreationOptions.RunContinuationsAsynchronously);
				SetScreen(Screen.Qr, "Sign in to Steam", "Approve the sign-in in the Steam app on this phone.");
				using (var qrCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
				{
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
							// A conexão caiu (ou o desafio expirou) enquanto esperava a aprovação: reconecta e gera um QR novo.
							LogLine("steam: connection lost while waiting for approval, reconnecting with a new QR code");
							session.Dispose();
							session = new SteamSession(LogLine);
							await session.ConnectAsync(ct).ConfigureAwait(false);
							continue;
						}
					}
					qrCts.Cancel();
					try { await qr.ConfigureAwait(false); } catch (OperationCanceledException) { }
				}

				// 2) Usuário e senha, com aprovação por push (e código digitado como último recurso).
				switchToQr = new(TaskCreationOptions.RunContinuationsAsynchronously);
				credentialsSubmitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
				SetScreen(Screen.Credentials, "Sign in with your Steam account", "Your password is only sent to Steam and is never saved.");
				Task back = switchToQr.Task;
				Task<(string User, string Pass)> submitted = credentialsSubmitted.Task;
				Task which = await Task.WhenAny(submitted, back).ConfigureAwait(false);
				if (which == back)
					continue;

				(string user, string pass) = await submitted.ConfigureAwait(false);
				try
				{
					RunOnUiThread(() => { signInButton.Enabled = false; status.Text = "Signing in…"; });
					return await session!.LoginWithCredentialsAsync(user, pass, this, ct).ConfigureAwait(false);
				}
				catch (SteamKit2.Authentication.AuthenticationException e)
				{
					LogLine("steam: credentials sign-in failed: " + e.Result);
					RunOnUiThread(() => { signInButton.Enabled = true; status.Text = "Sign-in failed (" + e.Result + "). Check your details and try again."; });
					// volta ao formulário
					credentialsSubmitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
					SetScreen(Screen.Credentials, "Sign in with your Steam account", "Sign-in failed (" + e.Result + "). Check your details and try again.");
					which = await Task.WhenAny(credentialsSubmitted.Task, switchToQr.Task).ConfigureAwait(false);
					if (which == switchToQr.Task) continue;
					(user, pass) = await credentialsSubmitted.Task.ConfigureAwait(false);
					return await session!.LoginWithCredentialsAsync(user, pass, this, ct).ConfigureAwait(false);
				}
				finally
				{
					user = pass = "";
				}
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

		private void CancelAndClose()
		{
			if (finishing) return;
			finishing = true;
			cts.Cancel();
			SetResult(Result.Canceled);
			Finish();
		}

		#endregion

		#region ISteamLoginEvents

		public void OnQrChallenge(string url)
		{
			challengeUrl = url;
			RunOnUiThread(() => qrImage.SetImageBitmap(MakeQr(url)));
		}

		public void OnWaitingForDeviceApproval() => RunOnUiThread(() =>
		{
			status.Text = "Waiting for you to approve the sign-in in the Steam app on this phone…";
			openSteamCredentials.Visibility = ViewStates.Visible;
			enterCode.Visibility = ViewStates.Visible;
		});

		public void OnNeedGuardCode(bool isEmailCode, string? emailHint, bool previousCodeWasIncorrect) => RunOnUiThread(() =>
		{
			codeIsEmail = isEmailCode;
			ShowCodeEntry(previousCodeWasIncorrect ? "That code was wrong, try again." : isEmailCode ? "Enter the code Steam emailed you." : "Enter the code from your Steam Guard app.");
		});

		#endregion

		#region Telas

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
			// Se o conteúdo não couber (formulário + teclado, telas pequenas), rola em vez de cortar.
			var scroll = new ScrollView(this) { VerticalScrollBarEnabled = false, FillViewport = true };
			scroll.SetClipChildren(false);
			scroll.SetClipToPadding(false);
			scroll.AddView(column, new FrameLayout.LayoutParams(-1, -2));
			row.AddView(scroll, new LinearLayout.LayoutParams(0, -1, 1f));

			title = Text("", 26, Color.White, TypefaceStyle.Bold);
			column.AddView(title);
			status = Text("", 15, Color.Argb(225, 255, 255, 255), TypefaceStyle.Normal);
			column.AddView(status, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(6) });

			// QR: botão principal + alternativa
			qrGroup = UiKit.NoClip(new LinearLayout(this) { Orientation = Orientation.Vertical });
			openSteamQr = Pill("Open in Steam app", filled: true);
			openSteamQr.Click += (_, _) => { if (challengeUrl != null) OpenInSteam(challengeUrl); };
			MakeFocusable(openSteamQr, "OpenInSteamApp");
			qrGroup.AddView(openSteamQr, UiKit.ButtonParams(this, 300, marginTop: 14));
			useCredentials = Link("Sign in with username and password");
			useCredentials.Click += (_, _) => switchToCredentials?.TrySetResult(true);
			MakeFocusable(useCredentials, "UseCredentials");
			qrGroup.AddView(useCredentials, new LinearLayout.LayoutParams(-2, -2) { TopMargin = Dp(8) });
			var qrHint = Text("Signing in from another device? Scan the QR code with the Steam app.", 12, Color.Argb(170, 255, 255, 255), TypefaceStyle.Normal);
			qrGroup.AddView(qrHint, new LinearLayout.LayoutParams(-2, -2) { TopMargin = Dp(6) });
			column.AddView(qrGroup);

			// Usuário/senha
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
			backToQr = Link("Back to the QR sign-in");
			backToQr.Click += (_, _) => switchToQr?.TrySetResult(true);
			MakeFocusable(backToQr, "BackToQr");
			credentialsGroup.AddView(backToQr, new LinearLayout.LayoutParams(-2, -2) { TopMargin = Dp(6) });
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
			cancelButton.Click += (_, _) => CancelAndClose();
			MakeFocusable(cancelButton, "Cancel");
			actions.AddView(cancelButton, UiKit.ButtonParams(this, 150, marginTop: 14));
			retryButton = Pill("Try again", filled: true);
			retryButton.Visibility = ViewStates.Gone;
			retryButton.Click += (_, _) => { cts.Cancel(); cts = new CancellationTokenSource(); session?.Dispose(); retryButton.Visibility = ViewStates.Gone; _ = Task.Run(() => PipelineAsync(cts.Token)); };
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
				Screen.Qr => openSteamQr,
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
			passField.Text = ""; // a senha não fica na tela nem no campo
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

		private void ShowProgress(InstallProgress p)
		{
			RunOnUiThread(() =>
			{
				bar.Indeterminate = false;
				bar.Progress = p.BytesTotal == 0 ? 0 : (int)(p.BytesDone * 1000 / p.BytesTotal);
				status.Text = p.Phase == "Verifying" ? "Verifying files…" : "Downloading the game files…";
				detail.Text = $"{FormatBytes(p.BytesDone)} of {FormatBytes(p.BytesTotal)}  ·  {p.FilesDone}/{p.FilesTotal} files"
					+ (p.Phase == "Downloading" && p.BytesPerSecond > 0 ? $"  ·  {FormatBytes((long)p.BytesPerSecond)}/s" : "");
			});
		}

		private static string FormatBytes(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" : $"{bytes / (double)(1L << 20):0} MB";

		private void OpenInSteam(string url)
		{
			try
			{
				var intent = new Intent(Intent.ActionView, Uri.Parse(url));
				intent.SetPackage(SteamPackage);
				StartActivity(intent);
			}
			catch (ActivityNotFoundException)
			{
				try { StartActivity(new Intent(Intent.ActionView, Uri.Parse(url))); }
				catch (ActivityNotFoundException) { status.Text = "Install the Steam app first, or scan the QR code from another device."; }
			}
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
