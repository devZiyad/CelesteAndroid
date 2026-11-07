using System.IO;
using CelesteAndroid.Steam;

namespace CelesteAndroid.Steam.Tests
{
	internal static class Fixture
	{
		public const string Name = "celeste-504233-public.manifest.json";

		public static ManifestSnapshot Load() =>
			ManifestSnapshot.Load(Path.Combine(System.AppContext.BaseDirectory, "fixtures", Name));
	}
}
