using System;
using System.Collections.Generic;
using System.Linq;

namespace CelesteAndroid.Steam
{
	public enum SelectionKind
	{
		/// <summary>O port precisa deste arquivo (Celeste.exe e Content/).</summary>
		Needed,
		Directory,
		/// <summary>Launchers do Linux (script "Celeste", Celeste.bin.x86 / x86_64).</summary>
		SkippedLauncher,
		/// <summary>Bibliotecas nativas x86/x86_64 (lib/, lib64/, *.so).</summary>
		SkippedNative,
		/// <summary>Steamworks e outras libs nativas da Steam.</summary>
		SkippedSteamworks,
		/// <summary>Runtime Mono e configs (mscorlib.dll, System*.dll, Mono*.dll, monoconfig...).</summary>
		SkippedRuntime,
		/// <summary>Qualquer outra coisa que o port não usa (FNA.dll, ícone, gamecontrollerdb.txt...).</summary>
		SkippedOther,
	}

	public sealed record FileClassification(ManifestFile File, SelectionKind Kind);

	/// <summary>O resultado da seleção: o conjunto necessário (o único que vai para o disco) e o que foi pulado.</summary>
	public sealed class SelectionResult
	{
		public IReadOnlyList<ManifestFile> Needed { get; }
		public IReadOnlyList<FileClassification> Skipped { get; }
		public long NeededBytes { get; }
		public long SkippedBytes { get; }

		public SelectionResult(IReadOnlyList<ManifestFile> needed, IReadOnlyList<FileClassification> skipped)
		{
			Needed = needed;
			Skipped = skipped;
			NeededBytes = needed.Sum(f => f.Size);
			SkippedBytes = skipped.Sum(s => s.File.Size);
		}

		public ISet<string> NeededPaths => new HashSet<string>(Needed.Select(f => f.Path), StringComparer.Ordinal);
	}

	/// <summary>
	/// Decide o que baixar do depot Linux: o port só usa Celeste.exe (que ele corrige com MonoMod) e a pasta Content/.
	/// Tudo o mais (launchers, nativos, Steamworks, runtime Mono, FNA.dll...) é pulado: o app traz o próprio FNA/SDL/FMOD.
	/// </summary>
	public static class FileSelection
	{
		public const string GameExecutable = "Celeste.exe";
		public const string ContentFolder = "Content/";

		public static SelectionKind Classify(ManifestFile file)
		{
			if (file.IsDirectory)
				return SelectionKind.Directory;
			string p = file.Path;
			if (string.Equals(p, GameExecutable, StringComparison.Ordinal) || p.StartsWith(ContentFolder, StringComparison.Ordinal))
				return SelectionKind.Needed;

			string name = p.Substring(p.LastIndexOf('/') + 1);
			if (name == "Celeste" || name.StartsWith("Celeste.bin.", StringComparison.Ordinal))
				return SelectionKind.SkippedLauncher;
			if (name.Contains("steam_api", StringComparison.OrdinalIgnoreCase) || name.Contains("Steamworks", StringComparison.OrdinalIgnoreCase)
				|| name.Contains("CSteamworks", StringComparison.OrdinalIgnoreCase))
				return SelectionKind.SkippedSteamworks;
			if (p.StartsWith("lib/", StringComparison.Ordinal) || p.StartsWith("lib64/", StringComparison.Ordinal)
				|| p.StartsWith("lib32/", StringComparison.Ordinal) || name.Contains(".so", StringComparison.Ordinal)
				|| name.EndsWith(".dylib", StringComparison.Ordinal))
				return SelectionKind.SkippedNative;
			if (name == "monoconfig" || name == "monomachineconfig" || name == "mscorlib.dll"
				|| name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Mono.", StringComparison.Ordinal)
				|| name.EndsWith(".config", StringComparison.Ordinal))
				return SelectionKind.SkippedRuntime;
			return SelectionKind.SkippedOther;
		}

		public static SelectionResult Select(ManifestSnapshot manifest)
		{
			var needed = new List<ManifestFile>();
			var skipped = new List<FileClassification>();
			foreach (ManifestFile file in manifest.Files)
			{
				SelectionKind kind = Classify(file);
				if (kind == SelectionKind.Needed)
					needed.Add(file);
				else if (kind != SelectionKind.Directory)
					skipped.Add(new FileClassification(file, kind));
			}
			return new SelectionResult(needed, skipped);
		}

		/// <summary>
		/// Garante que é mesmo o build FNA do Linux (Celeste.exe + FNA.dll no manifesto): o depot do Windows ou um beta
		/// diferente é recusado antes de baixar qualquer coisa.
		/// </summary>
		public static void ValidateIsFnaBuild(ManifestSnapshot manifest)
		{
			bool Has(string path) => manifest.Files.Any(f => !f.IsDirectory && string.Equals(f.Path, path, StringComparison.Ordinal));
			if (!Has(GameExecutable))
				throw new InvalidOperationException("The manifest has no Celeste.exe: this is not the Celeste game depot.");
			if (!Has("FNA.dll"))
				throw new InvalidOperationException("The manifest has no FNA.dll: this is not the FNA (Linux) build the port needs.");
			if (!manifest.Files.Any(f => f.Path.StartsWith(ContentFolder, StringComparison.Ordinal)))
				throw new InvalidOperationException("The manifest has no Content/ folder.");
		}
	}
}
