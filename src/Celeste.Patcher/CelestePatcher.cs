using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using MonoMod;

namespace CelesteAndroid.Patcher
{
	/// <summary>
	/// Generates a Celeste.dll that runs on modern .NET from the user's original Celeste.exe.
	/// </summary>
	public static class CelestePatcher
	{
		/// <param name="celesteExe">Celeste.exe original (build FNA).</param>
		/// <param name="modAssembly">Celeste.Android.mm.dll.</param>
		/// <param name="outputDll">Destino do Celeste.dll patcheado.</param>
		/// <param name="dependencyDirs">Where to find FNA.dll, Steamworks.NET.dll etc. (ours, not the game's).</param>
		public static void Patch(string celesteExe, string modAssembly, string outputDll, IEnumerable<string> dependencyDirs, Action<string>? log = null)
		{
			log ??= Console.WriteLine;
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputDll))!);

			using LoggingModder modder = new(log)
			{
				InputPath = celesteExe,
				OutputPath = outputDll,
				// Deferred: Cecil only resolves what it uses. On Android there is no mscorlib/System.Runtime as
				// files, and Immediate mode tries to resolve the types of every attribute.
				ReadingMode = ReadingMode.Deferred,
				MissingDependencyThrow = false,
			};
			modder.DependencyDirs.AddRange(dependencyDirs);

			modder.Read();
			modder.ReadMod(modAssembly);
			modder.MapDependencies();
			modder.AutoPatch();

			// Celeste.exe is flagged as x86 (32BITREQUIRED); on ARM64/x64 that prevents loading.
			ModuleDefinition module = modder.Module;
			module.Attributes &= ~(ModuleAttributes.Required32Bit | ModuleAttributes.Preferred32Bit);
			module.Attributes |= ModuleAttributes.ILOnly;
			module.Architecture = TargetArchitecture.I386;

			modder.Write();
			log($"Celeste patcheado: {outputDll}");
		}

		private sealed class LoggingModder(Action<string> log) : MonoModder
		{
			public override void Log(string text) => log("[MonoMod] " + text);
		}
	}
}
