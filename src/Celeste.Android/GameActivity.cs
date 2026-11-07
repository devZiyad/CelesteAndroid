using System;
using System.Reflection;
using Android.App;
using Android.Content.PM;
using Android.OS;
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
		}

		protected override void OnDestroy()
		{
			Org.Fmod.FMOD.Close();
			base.OnDestroy();
			if (IsFinishing)
				Process.KillProcess(Process.MyPid());
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
