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
			// Usado pelo scripts/deploy-android.sh: o mesmo Celeste.dll serve para o Android.
			if (Array.IndexOf(args, "--patch-only") >= 0)
			{
				Console.WriteLine(patchedDll);
				return 0;
			}

			// Mesmo contrato que o app Android vai usar (ver CelesteAndroid.HostConfig).
			AppContext.SetData("CelesteAndroid.Platform", "Android");
			// No desktop o menu de vídeo do Celeste (Fullscreen/Window Scale) fica como no original.
			AppContext.SetData("CelesteAndroid.Host", "desktop");
			// Opções do port (JSON / flags); o renderer vira o driver do FNA3D, como no Android.
			args = PortOptions.Apply(args, hostDir);
			if (AppContext.GetData("CelesteAndroid.Renderer") is string renderer)
				SDL3.SDL.SDL_SetHint("FNA3D_FORCE_DRIVER", renderer.StartsWith("gl", StringComparison.OrdinalIgnoreCase) || renderer.StartsWith("opengl", StringComparison.OrdinalIgnoreCase) ? "OpenGL" : "Vulkan");
			AppContext.SetData("CelesteAndroid.PrefPath", Path.Combine(hostDir, "userdata"));

			// O FNA procura o Content relativo à pasta-base do app; apontamos para a pasta do jogo.
			AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", gameDir + Path.DirectorySeparatorChar);
			Environment.CurrentDirectory = gameDir;

			AssemblyLoadContext.Default.Resolving += (ctx, name) =>
				name.Name == "Celeste" ? ctx.LoadFromAssemblyPath(patchedDll) : null;

			Assembly celeste = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("Celeste"));

			// Engine.AssemblyDirectory vem de Assembly.Location, que não aponta para o jogo (e no Android é vazio).
			celeste.GetType("Monocle.Engine", throwOnError: true)!
				.GetField("AssemblyDirectory", BindingFlags.NonPublic | BindingFlags.Static)!
				.SetValue(null, gameDir);

			// O Celeste.dll fica em patched/, então o runtime não procura as libs nativas (FMOD) ao lado do host.
			// libfmodstudio depende do soname libfmod.so.10: carregamos o libfmod antes, por caminho completo.
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

			// Sobe a partir de bin/ até achar a pasta Celeste/ na raiz do repositório.
			for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
			{
				string candidate = Path.Combine(dir.FullName, "Celeste");
				if (File.Exists(Path.Combine(candidate, "Celeste.exe")))
					return candidate;
			}
			throw new DirectoryNotFoundException("Pasta do jogo não encontrada. Use --game <pasta> ou CELESTE_GAME_DIR.");
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
