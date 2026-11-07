using System;
using System.Collections.Generic;
using System.IO;

namespace CelesteAndroid.Steam
{
	public sealed record RemovalReport(int FilesDeleted, long BytesFreed, int DirectoriesRemoved);

	/// <summary>
	/// When re-downloading or updating, removes from the game folder every file that is not in the current manifest's needed
	/// set (old versions, skipped files from earlier installs, leftovers) and the folders that end up empty.
	/// </summary>
	public static class StaleFileRemover
	{
		/// <param name="root">The game folder (the depot root).</param>
		/// <param name="neededPaths">Relative paths (with '/') that must exist.</param>
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
					continue; // never touch anything outside the root
				string relative = full.Substring(fullRoot.Length).Replace(Path.DirectorySeparatorChar, '/');
				if (neededPaths.Contains(relative))
					continue;
				bytes += new FileInfo(full).Length;
				File.Delete(full);
				files++;
			}
			// Empty folders, deepest first.
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
