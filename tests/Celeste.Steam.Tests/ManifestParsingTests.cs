using System.Linq;
using CelesteAndroid.Steam;
using Xunit;

namespace CelesteAndroid.Steam.Tests
{
	public class ManifestParsingTests
	{
		[Fact]
		public void Fixture_identifies_the_linux_depot_on_the_public_branch()
		{
			ManifestSnapshot m = Fixture.Load();
			Assert.Equal(504230u, m.AppId);
			Assert.Equal(504233u, m.DepotId);
			Assert.Equal("public", m.Branch);
			Assert.NotEmpty(m.Files);
		}

		[Fact]
		public void Paths_are_normalized_to_forward_slashes()
		{
			ManifestSnapshot m = ManifestSnapshot.FromJson("""
				{"appId":1,"depotId":2,"manifestId":3,"branch":"public","files":[
				 {"path":"Content\\Graphics\\Atlases\\Gameplay.data","size":0,"flags":0,"sha1":"","chunks":[]},
				 {"path":"\\Celeste.exe","size":0,"flags":0,"sha1":"","chunks":[]}]}
				""");
			Assert.Equal("Content/Graphics/Atlases/Gameplay.data", m.Files[0].Path);
			Assert.Equal("Celeste.exe", m.Files[1].Path);
		}

		[Fact]
		public void Directory_entries_are_flagged_and_have_no_chunks()
		{
			ManifestSnapshot m = Fixture.Load();
			var dirs = m.Files.Where(f => f.IsDirectory).ToList();
			Assert.NotEmpty(dirs);
			Assert.All(dirs, d => Assert.Empty(d.Chunks));
			Assert.Contains(dirs, d => d.Path == "Content");
		}

		[Fact]
		public void Chunks_cover_each_file_contiguously()
		{
			ManifestSnapshot m = Fixture.Load();
			foreach (ManifestFile f in m.Files.Where(f => !f.IsDirectory))
			{
				long offset = 0;
				foreach (ManifestChunk c in f.Chunks)
				{
					Assert.Equal(offset, c.Offset);
					offset += c.UncompressedLength;
				}
				Assert.Equal(f.Size, offset);
			}
		}

		[Fact]
		public void Json_round_trip_keeps_everything()
		{
			ManifestSnapshot m = Fixture.Load();
			ManifestSnapshot again = ManifestSnapshot.FromJson(m.ToJson());
			Assert.Equal(m.Files.Count, again.Files.Count);
			Assert.Equal(m.Files.Sum(f => f.Size), again.Files.Sum(f => f.Size));
			Assert.Equal(m.Files[10].Chunks.Count, again.Files[10].Chunks.Count);
			Assert.Equal(m.Files[10].Sha1, again.Files[10].Sha1);
		}
	}
}
