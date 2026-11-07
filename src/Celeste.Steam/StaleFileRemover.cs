using System;
using System.Collections.Generic;
using System.IO;

namespace CelesteAndroid.Steam
{
	public sealed record RemovalReport(int FilesDeleted, long BytesFreed, int DirectoriesRemoved);

	/// <summary>
	/// Ao baixar de novo ou atualizar, remove da pasta do jogo todo arquivo que não está no conjunto necessário do manifesto
	/// atual (versões antigas, arquivos pulados de instalações anteriores, sobras) e as pastas que ficarem vazias.
	/// </summary>
	public static class StaleFileRemover
	{
		/// <param name="root">Pasta do jogo (a raiz do depot).</param>
		/// <param name="neededPaths">Caminhos relativos (com '/') que devem existir.</param>
		public static RemovalReport Remove(string root, ISet<string> neededPaths)
		{
			if (!Directory.Exists(root))
				return new RemovalReport(0, 0, 0);

			string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
			int files = 0, dirs = 0;
			long bytes = 0;
			foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
			{
				string full = Path.GetFullPath(file);
				if (!full.StartsWith(fullRoot, StringComparison.Ordinal))
					continue; // nunca mexe fora da raiz
				string relative = full.Substring(fullRoot.Length).Replace(Path.DirectorySeparatorChar, '/');
				if (neededPaths.Contains(relative))
					continue;
				bytes += new FileInfo(full).Length;
				File.Delete(full);
				files++;
			}
			// Pastas vazias, das mais fundas para as mais rasas.
			var all = new List<string>(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories));
			all.Sort((a, b) => b.Length.CompareTo(a.Length));
			foreach (string dir in all)
			{
				if (!Directory.EnumerateFileSystemEntries(dir).GetEnumerator().MoveNext())
				{
					Directory.Delete(dir);
					dirs++;
				}
			}
			return new RemovalReport(files, bytes, dirs);
		}
	}
}
