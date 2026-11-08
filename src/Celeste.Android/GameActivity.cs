using System;
using System.Reflection;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Window;
using Android.Util;
using Microsoft.Xna.Framework;
using Org.Libsdl.App;

namespace CelesteAndroid
{
	/// <summary>
	/// Runs the game. It lives in its own process (":game") because Celeste and FNA keep static state:
	/// every run starts in a fresh process, and the process dies when the game closes.
	/// </summary>
	[Activity(
		Name = "org.devziyad.celeste.GameActivity",
		Process = ":game",
		Exported = false,
		Theme = "@style/Theme.Celeste",
		ScreenOrientation = ScreenOrientation.SensorLandscape,
		LaunchMode = LaunchMode.SingleTask,
		HardwareAccelerated = true,
		ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
			| ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation
			| ConfigChanges.UiMode | ConfigChanges.Density | ConfigChanges.SmallestScreenSize)]
	public class GameActivity : SDLActivity
	{
		public const string LogTag = "CelesteAndroid";
		public const string ExtraDriver = "driver";

		// Java loads SDL3 and FMOD (FMOD must be loaded before FMOD.init);
		// FNA3D/FAudio are loaded by .NET via DllImport.
		protected override string[] GetLibraries() => new[] { "SDL3", "fmod", "fmodstudio" };

		protected override void OnCreate(Bundle? savedInstanceState)
		{
			base.OnCreate(savedInstanceState);
			// On Android, FMOD needs the Context for audio and for reading files.
			Org.Fmod.FMOD.Init(this);
			// System Back (button or gesture) asks to return to the launcher; with predictive back (targetSdk 36) a gesture comes as a callback
			// instead of a key event.
			if (OperatingSystem.IsAndroidVersionAtLeast(33))
				OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(0, new BackCallback(this));
		}

		[Obsolete]
		public override void OnBackPressed()
		{
			if (!forwardingGamepadBack) AskReturnToLauncher(); // Activity.onKeyUp calls this for any Back key, gamepad ones included
		}

		private bool forwardingGamepadBack;
		private long gamepadBackAt; // uptime (ms) of the last gamepad Back key event; the back callback cannot see a key's source

		// With 3-button navigation Back arrives as a key event that SDL would swallow as an unmapped AC_BACK key. The system button
		// and gesture ask to return to the launcher; a Back key from a gamepad is game input and goes to SDL like any other button.
		public override bool DispatchKeyEvent(KeyEvent? e)
		{
			if (e != null && e.KeyCode == Keycode.Back)
			{
				if (FromGamepad(e))
				{
					gamepadBackAt = SystemClock.UptimeMillis();
					forwardingGamepadBack = true;
					try { base.DispatchKeyEvent(e); } // the game's own input; reported as handled so Android does not also fire the back callback
					finally { forwardingGamepadBack = false; }
					return true;
				}
				if (e.Action == KeyEventActions.Up && !e.IsCanceled)
					AskReturnToLauncher();
				return true;
			}
			return base.DispatchKeyEvent(e!);
		}

		private static bool FromGamepad(KeyEvent e) =>
			(e.Source & InputSourceType.Gamepad) == InputSourceType.Gamepad || (e.Source & InputSourceType.Joystick) == InputSourceType.Joystick;

		private const string BackKey = "CelesteAndroid.BackRequest"; // read by the game's PortBack (Celeste.Android.Patches)
		private AlertDialog? returnDialog;

		/// <summary>"Return to launcher?" Yes / No. The game pauses behind it; Yes leaves through the pause menu's "Exit to launcher" path.</summary>
		private void AskReturnToLauncher()
		{
			if (returnDialog != null) return;
			AppContext.SetData(BackKey, "show");
			var dialog = new AlertDialog.Builder(this, Android.Resource.Style.ThemeDeviceDefaultDialogAlert)!
				.SetTitle("Return to launcher?")!
				.SetMessage("Your progress is saved first.")!
				.SetPositiveButton("Yes", (_, _) => Answer("yes"))!
				.SetNegativeButton("No", (_, _) => Answer("no"))!
				.SetOnCancelListener(new CancelListener(() => Answer("no")))!
				.Create()!;
			returnDialog = dialog;
			// Gamepad: B answers No (the buttons take D-pad focus and A natively).
			dialog.SetOnKeyListener(new KeyListener(() => dialog.Dismiss(), Keycode.ButtonB));
			dialog.Show();
			foreach (DialogButtonType type in new[] { DialogButtonType.Positive, DialogButtonType.Negative })
				if (dialog.GetButton((int)type) is { } button)
					button.FocusableInTouchMode = true; // D-pad / gamepad focus works even right after a touch
			dialog.GetButton((int)DialogButtonType.Negative)?.RequestFocus();
			Log.Info("CelesteAndroid", "[CelesteAndroid] return dialog: shown");
		}

		private void Answer(string answer)
		{
			if (returnDialog == null) return;
			returnDialog = null;
			AppContext.SetData(BackKey, answer);
			Log.Info("CelesteAndroid", "[CelesteAndroid] return dialog: " + answer);
		}

		private sealed class CancelListener(Action action) : Java.Lang.Object, IDialogInterfaceOnCancelListener
		{
			public void OnCancel(IDialogInterface? dialog) => action();
		}

		private sealed class KeyListener(Action action, Keycode code) : Java.Lang.Object, IDialogInterfaceOnKeyListener
		{
			public bool OnKey(IDialogInterface? dialog, Keycode keyCode, KeyEvent? e)
			{
				if (keyCode != code || e == null || e.Action != KeyEventActions.Up) return false;
				action();
				return true;
			}
		}

		private sealed class BackCallback(GameActivity owner) : Java.Lang.Object, IOnBackInvokedCallback
		{
			public void OnBackInvoked()
			{
				// Android fires this on the key-up of any Back key, a gamepad's included (that one is game input): ignore it right after one.
				if (SystemClock.UptimeMillis() - owner.gamepadBackAt < 400) return;
				owner.AskReturnToLauncher();
			}
		}

		protected override void OnDestroy()
		{
			Org.Fmod.FMOD.Close();
			base.OnDestroy();
			if (IsFinishing)
				Process.KillProcess(Process.MyPid());
		}

		// SDL calls this when it creates the window and would switch the activity to FULL_USER (portrait allowed),
		// overriding the manifest: the port is landscape only.
		public override void SetOrientationBis(int w, int h, bool resizable, string hint)
		{
			RequestedOrientation = ScreenOrientation.SensorLandscape;
		}

		// Called by SDL on the "SDLThread" thread once the surface exists; replaces the native SDL_main.
		protected override void Main()
		{
			// FNA/FNA3D log to stderr, which does not reach logcat on Android.
			FNALoggerEXT.LogInfo = msg => Log.Info(LogTag, msg);
			FNALoggerEXT.LogWarn = msg => Log.Warn(LogTag, msg);
			FNALoggerEXT.LogError = msg => Log.Error(LogTag, msg);

			// "OpenGL" forces the GLES driver; the default is Vulkan (SDL_GPU).
			string? driver = Intent?.GetStringExtra(ExtraDriver);
			if (!string.IsNullOrEmpty(driver))
				SDL3.SDL.SDL_SetHint("FNA3D_FORCE_DRIVER", driver);

			try
			{
				if (GameInstaller.IsInstalled(this))
				{
					CelesteLauncher.Run(this);
				}
				else
				{
					// The launcher only enables "PLAY" when the game is installed.
					Log.Warn(LogTag, "Game not installed; closing.");
					RunOnUiThread(Finish);
				}
			}
			catch (Exception e)
			{
				Log.Error(LogTag, (e is TargetInvocationException { InnerException: not null } tie ? tie.InnerException! : e).ToString());
				throw;
			}
		}
	}
}
