using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using CelesteAndroid.Patcher;

namespace CelesteAndroid.Desktop
{
	public static class Program
	{
		[STAThread]
		public static int Main(string[] args)
		{
			string hostDir = AppContext.BaseDirectory;
			string gameDir = FindGameDir(args);
			string patchedDll = Path.Combine(hostDir, "patched", "Celeste.dll");
			string modDll = Path.Combine(hostDir, "Celeste.Android.mm.dll");
			string celesteExe = Path.Combine(gameDir, "Celeste.exe");

			if (IsStale(patchedDll, celesteExe, modDll))
			{
				CelestePatcher.Patch(celesteExe, modDll, patchedDll, new[] { hostDir, RuntimeEnvironment.GetRuntimeDirectory() });
			}
			// Used by scripts/deploy-android.sh: the same Celeste.dll works for Android.
			if (Array.IndexOf(args, "--patch-only") >= 0)
			{
				Console.WriteLine(patchedDll);
				return 0;
			}

			// Same contract the Android app uses (see CelesteAndroid.HostConfig).
			AppContext.SetData("CelesteAndroid.Platform", "Android");
			// On desktop, Celeste's video menu (Fullscreen/Window Scale) stays as in the original.
			AppContext.SetData("CelesteAndroid.Host", "desktop");
			// Port options (JSON / flags); the renderer becomes the FNA3D driver, as on Android.
			args = PortOptions.Apply(args, hostDir);
			if (AppContext.GetData("CelesteAndroid.Renderer") is string renderer)
				SDL3.SDL.SDL_SetHint("FNA3D_FORCE_DRIVER", renderer.StartsWith("gl", StringComparison.OrdinalIgnoreCase) || renderer.StartsWith("opengl", StringComparison.OrdinalIgnoreCase) ? "OpenGL" : "Vulkan");
			AppContext.SetData("CelesteAndroid.PrefPath", Path.Combine(hostDir, "userdata"));

			// FNA looks for Content relative to the app's base folder; we point it at the game folder.
			AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", gameDir + Path.DirectorySeparatorChar);
			Environment.CurrentDirectory = gameDir;

			AssemblyLoadContext.Default.Resolving += (ctx, name) =>
				name.Name == "Celeste" ? ctx.LoadFromAssemblyPath(patchedDll) : null;

			Assembly celeste = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("Celeste"));

			// Engine.AssemblyDirectory comes from Assembly.Location, which does not point at the game (and is empty on Android).
			celeste.GetType("Monocle.Engine", throwOnError: true)!
				.GetField("AssemblyDirectory", BindingFlags.NonPublic | BindingFlags.Static)!
				.SetValue(null, gameDir);

			// Celeste.dll lives in patched/, so the runtime does not look for the native libs (FMOD) next to the host.
			// libfmodstudio depends on the soname libfmod.so.10: we load libfmod first, by full path.
			NativeLibrary.TryLoad(Path.Combine(hostDir, "libfmod.so.10"), out _);
			NativeLibrary.SetDllImportResolver(celeste, (name, asm, path) =>
				NativeLibrary.TryLoad(Path.Combine(hostDir, "lib" + name + ".so"), out IntPtr handle) ? handle : IntPtr.Zero);

			MethodInfo main = celeste.GetType("Celeste.Celeste", throwOnError: true)!
				.GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)!;
			main.Invoke(null, new object[] { args });
			return 0;
		}

		private static string FindGameDir(string[] args)
		{
			int i = Array.IndexOf(args, "--game");
			if (i >= 0 && i + 1 < args.Length)
				return Path.GetFullPath(args[i + 1]);

			string? env = Environment.GetEnvironmentVariable("CELESTE_GAME_DIR");
			if (!string.IsNullOrEmpty(env))
				return Path.GetFullPath(env);

			// Walks up from bin/ until it finds the Celeste/ folder at the repository root.
			for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
			{
				string candidate = Path.Combine(dir.FullName, "Celeste");
				if (File.Exists(Path.Combine(candidate, "Celeste.exe")))
					return candidate;
			}
			throw new DirectoryNotFoundException("Game folder not found. Use --game <folder> or CELESTE_GAME_DIR.");
		}

		private static bool IsStale(string output, params string[] inputs)
		{
			if (!File.Exists(output))
				return true;
			DateTime built = File.GetLastWriteTimeUtc(output);
			foreach (string input in inputs)
			{
				if (File.GetLastWriteTimeUtc(input) > built)
					return true;
			}
			return false;
		}
	}
}
