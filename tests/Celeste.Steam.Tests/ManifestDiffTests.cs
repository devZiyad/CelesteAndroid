using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace CelesteAndroid.Steam.Tests
{
	public class ManifestDiffTests
	{
		private static ManifestFile F(string path, long size, string sha) => new(path, size, 0, sha, new List<ManifestChunk>());
		private static ManifestSnapshot M(ulong id, params ManifestFile[] files) => new(504230, 504233, id, "public", files);

		[Fact]
		public void Only_files_with_the_same_size_and_hash_are_unchanged()
		{
			var old = M(1, F("Celeste.exe", 10, "aa"), F("Content/a", 5, "bb"), F("Content/b", 5, "cc"), F("Content/gone", 1, "dd"));
			var next = new[] { F("Celeste.exe", 11, "aa2"), F("Content/a", 5, "BB"), F("Content/b", 6, "cc"), F("Content/new", 1, "ee") };
			Assert.Equal(new[] { "Content/a" }, ManifestDiff.Unchanged(old, next).ToArray());
		}

		[Fact]
		public void Files_without_a_real_hash_are_never_trusted()
		{
			var zero = new string('0', 40);
			var old = M(1, F("Content/a", 5, ""), F("Content/b", 5, zero));
			Assert.Empty(ManifestDiff.Unchanged(old, new[] { F("Content/a", 5, ""), F("Content/b", 5, zero) }));
		}
	}
}
