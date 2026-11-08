using System;
using System.Collections.Generic;
using System.Linq;

namespace CelesteAndroid.Steam
{
	/// <summary>Compares two manifests by file hashes (no file on disk is read).</summary>
	public static class ManifestDiff
	{
		/// <summary>
		/// Paths of <paramref name="needed"/> whose size and SHA-1 are the same in <paramref name="installed"/> (the manifest of the build on
		/// disk): those files are already what the new build has. Files without a real hash are never listed (they get verified).
		/// </summary>
		public static ISet<string> Unchanged(ManifestSnapshot installed, IEnumerable<ManifestFile> needed)
		{
			var old = installed.Files.Where(f => !f.IsDirectory).ToDictionary(f => f.Path, StringComparer.Ordinal);
			var same = new HashSet<string>(StringComparer.Ordinal);
			foreach (ManifestFile f in needed)
			{
				if (string.IsNullOrEmpty(f.Sha1) || f.Sha1.All(c => c == '0')) continue;
				if (old.TryGetValue(f.Path, out ManifestFile? before) && before.Size == f.Size
					&& string.Equals(before.Sha1, f.Sha1, StringComparison.OrdinalIgnoreCase))
					same.Add(f.Path);
			}
			return same;
		}
	}
}
