using System;
using System.Collections.Generic;
using System.Linq;

namespace CelesteAndroid.Steam
{
	public enum SelectionKind
	{
		/// <summary>The port needs this file (Celeste.exe and Content/).</summary>
		Needed,
		Directory,
		/// <summary>Launchers do Linux (script "Celeste", Celeste.bin.x86 / x86_64).</summary>
		SkippedLauncher,
		/// <summary>Bibliotecas nativas x86/x86_64 (lib/, lib64/, *.so).</summary>
		SkippedNative,
		/// <summary>Steamworks and other native Steam libs.</summary>
		SkippedSteamworks,
		/// <summary>The Mono runtime and configs (mscorlib.dll, System*.dll, Mono*.dll, monoconfig...).</summary>
		SkippedRuntime,
		/// <summary>Anything else the port does not use (FNA.dll, icon, gamecontrollerdb.txt...).</summary>
		SkippedOther,
	}

	public sealed record FileClassification(ManifestFile File, SelectionKind Kind);

	/// <summary>The selection result: the needed set (the only one that goes to disk) and what was skipped.</summary>
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
	/// Decides what to download from the Linux depot: the port only uses Celeste.exe (which it patches with MonoMod) and the Content/ folder.
	/// Everything else (launchers, natives, Steamworks, the Mono runtime, FNA.dll...) is skipped: the app ships its own FNA/SDL/FMOD.
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
		/// Makes sure this really is the Linux FNA build (Celeste.exe + FNA.dll in the manifest): the Windows depot or a different
		/// beta is refused before downloading anything.
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
