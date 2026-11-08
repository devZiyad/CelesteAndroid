using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Android.Content;
using Android.Util;

namespace CelesteAndroid
{
	/// <summary>
	/// Loads the patched Celeste.dll and calls the original Main, with the same contract as the desktop host.
	/// </summary>
	public static class CelesteLauncher
	{
		public static void Run(Context context)
		{
			string gameDir = GameInstaller.GameDir(context);
			Log.Info(GameActivity.LogTag, $"Starting Celeste from {gameDir}");

			// See CelesteAndroid.HostConfig (Celeste.Android.Patches).
			AppContext.SetData("CelesteAndroid.Platform", "Android");
			AppContext.SetData("CelesteAndroid.PrefPath", GameInstaller.UserDir(context));
			AppContext.SetData("CelesteAndroid.BackgroundPath", GameInstaller.BackgroundPng(context));
			AppContext.SetData("CelesteAndroid.Host", "android");
			// Options from the "Port settings" screen (renderer, frame cap, overlay, screen fit...).
			PortSettings.ApplyToAppContext(context);

			// On Android, FNA resolves Content relative to the working directory.
			AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", gameDir + Path.DirectorySeparatorChar);
			Environment.CurrentDirectory = gameDir;

			StartupTrace.Mark("assembly-load-begin");
			Assembly celeste = AssemblyLoadContext.Default.LoadFromAssemblyPath(GameInstaller.PatchedDll(context));
			StartupTrace.Mark("assembly-load-end");

			// Engine.AssemblyDirectory comes from Assembly.Location, which does not point at the game.
			celeste.GetType("Monocle.Engine", throwOnError: true)!
				.GetField("AssemblyDirectory", BindingFlags.NonPublic | BindingFlags.Static)!
				.SetValue(null, gameDir);

			MethodInfo main = celeste.GetType("Celeste.Celeste", throwOnError: true)!
				.GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)!;
			StartupTrace.Mark("celeste-main");
			main.Invoke(null, new object[] { Array.Empty<string>() });
		}
	}
}
