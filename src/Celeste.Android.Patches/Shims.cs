using System;
using System.Reflection;
using MonoMod;

namespace CelesteAndroid
{
	/// <summary>
	/// Configuration passed by the host (desktop or the Android Activity) before the game starts.
	/// Uses AppContext so it does not need a reference to the patched Celeste.dll.
	/// </summary>
	public static class HostConfig
	{
		public const string PlatformKey = "CelesteAndroid.Platform";
		public const string PrefPathKey = "CelesteAndroid.PrefPath";
		public const string BackgroundPathKey = "CelesteAndroid.BackgroundPath";
		public const string HostKey = "CelesteAndroid.Host";

		/// <summary>"android" (the app) or "desktop" (the Linux test host); the default is Android.</summary>
		public static string Host => AppContext.GetData(HostKey) as string ?? "android";

		/// <summary>On desktop, Celeste's video menu (Fullscreen/Window Scale) stays as in the original.</summary>
		public static bool IsAndroid => !string.Equals(Host, "desktop", StringComparison.OrdinalIgnoreCase);

		public static string Platform => AppContext.GetData(PlatformKey) as string ?? "Android";

		/// <summary>Image for the side bars on screens wider than 16:9 (optional).</summary>
		public static string? BackgroundPath => AppContext.GetData(BackgroundPathKey) as string;

		public static string PrefPath => AppContext.GetData(PrefPathKey) as string
			?? throw new InvalidOperationException($"{PrefPathKey} was not set by the host.");
	}

	/// <summary>
	/// Celeste calls SDL2 directly, but the current FNA runs on SDL3.
	/// The calls are relinked to here during patching.
	/// </summary>
	public static class SDLShim
	{
		[MonoModLinkFrom("System.String SDL2.SDL::SDL_GetPlatform()")]
		public static string SDL_GetPlatform() => HostConfig.Platform;

		[MonoModLinkFrom("System.String SDL2.SDL::SDL_GetPrefPath(System.String,System.String)")]
		public static string SDL_GetPrefPath(string org, string app)
		{
			string path = System.IO.Path.Combine(HostConfig.PrefPath, app);
			System.IO.Directory.CreateDirectory(path);
			return path;
		}
	}

	public static class GCShim
	{
		/// <summary>Android's Mono runtime throws PlatformNotSupportedException when changing the GC latency mode.</summary>
		[MonoModLinkFrom("System.Void System.Runtime.GCSettings::set_LatencyMode(System.Runtime.GCLatencyMode)")]
		public static void SetLatencyMode(System.Runtime.GCLatencyMode mode)
		{
			// Through reflection: a direct call would be relinked to here too (infinite recursion).
			try
			{
				typeof(System.Runtime.GCSettings).GetProperty(nameof(System.Runtime.GCSettings.LatencyMode))!.SetValue(null, mode);
			}
			catch (TargetInvocationException e) when (e.InnerException is PlatformNotSupportedException)
			{
			}
		}
	}

	/// <summary>
	/// Celeste uses GetEntryAssembly() to find its own types; with a loader, the "entry" assembly is not Celeste.
	/// </summary>
	public static class ReflectionShim
	{
		[MonoModLinkFrom("System.Reflection.Assembly System.Reflection.Assembly::GetEntryAssembly()")]
		public static Assembly GetEntryAssembly() => typeof(ReflectionShim).Assembly;
	}
}
