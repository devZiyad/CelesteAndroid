using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CelesteAndroid.Steam;
using Xunit;

namespace CelesteAndroid.Steam.Tests
{
	/// <summary>Deterministic content per (file, offset): serves as a fake "Steam" and as the source of the manifest hashes.</summary>
	internal sealed class FakeDepot : IChunkSource
	{
		public const int ChunkSize = 1000;
		private readonly Dictionary<string, (string Path, int Version)> byChunkId = new();
		public ConcurrentBag<string> Requested { get; } = new();
		public int FailAfter { get; set; } = int.MaxValue;
		private int served;

		public static byte[] Content(string path, int version, long offset, int length)
		{
			var bytes = new byte[length];
			for (int i = 0; i < length; i++)
				bytes[i] = (byte)((path.Length * 31 + version * 7 + offset + i) % 251);
			return bytes;
		}

		public ManifestFile File(string path, int size, int version = 1)
		{
			var chunks = new List<ManifestChunk>();
			for (long off = 0; off < size; off += ChunkSize)
			{
				int len = (int)Math.Min(ChunkSize, size - off);
				string id = $"{path}@{version}@{off}";
				chunks.Add(new ManifestChunk(id, off, len, len, 0));
				byChunkId[id] = (path, version);
			}
			byte[] all = Content(path, version, 0, size);
			return new ManifestFile(path, size, 0, size == 0 ? new string('0', 40) : Convert.ToHexString(SHA1.HashData(all)), chunks);
		}

		public Task<int> DownloadChunkAsync(ManifestChunk chunk, byte[] buffer, CancellationToken ct)
		{
			Requested.Add(chunk.Id);
			if (Interlocked.Increment(ref served) > FailAfter)
				throw new InvalidOperationException("simulated network failure");
			(string path, int version) = byChunkId[chunk.Id];
			byte[] data = Content(path, version, chunk.Offset, chunk.UncompressedLength);
			Array.Copy(data, buffer, data.Length);
			return Task.FromResult(data.Length);
		}
	}

	public class DepotInstallerTests
	{
		private static ManifestSnapshot Manifest(FakeDepot depot, ulong id, params ManifestFile[] files) =>
			new(504230, 504233, id, "public", files.Concat(new[]
			{
				depot.File("FNA.dll", 500),               // only to validate the build; not needed and must not be downloaded
				depot.File("lib64/libfmod.so.10", 2500),  // native: skipped
				depot.File("Celeste.bin.x86_64", 1200),   // launcher: skipped
			}).ToList());

		private static (ManifestSnapshot Manifest, FakeDepot Depot) Game(int version = 1, ulong id = 1)
		{
			var depot = new FakeDepot();
			ManifestSnapshot m = Manifest(depot, id,
				depot.File("Celeste.exe", 3500, version),
				depot.File("Content/Graphics/a.data", 2500, version),
				depot.File("Content/Graphics/b.data", 999, version),
				depot.File("Content/Dialog/english.txt", 10, version),
				depot.File("Content/empty.bin", 0, version));
			return (m, depot);
		}

		private static DepotInstaller Installer(TempDir dir, FakeDepot depot, Action<InstallProgress>? progress = null) =>
			new(dir.Combine("game"), dir.Combine("steam/resume.json"), depot, parallelism: 4, progress);

		[Fact]
		public async Task Fresh_install_downloads_only_the_needed_set_into_the_final_folder()
		{
			using var dir = new TempDir();
			(ManifestSnapshot m, FakeDepot depot) = Game();
			InstallResult r = await Installer(dir, depot).InstallAsync(m, CancellationToken.None);

			SelectionResult sel = FileSelection.Select(m);
			Assert.Equal(sel.Needed.Count, r.Files);
			Assert.Equal(sel.NeededBytes, r.Bytes);
			Assert.Equal(3, r.SkippedFiles);
			// Only the needed files are on disk, with the right content.
			string game = dir.Combine("game");
			Assert.Equal(sel.Needed.Select(f => f.Path).OrderBy(x => x, StringComparer.Ordinal),
				Directory.EnumerateFiles(game, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(game, f).Replace('\\', '/')).OrderBy(x => x, StringComparer.Ordinal));
			foreach (ManifestFile f in sel.Needed)
				Assert.Equal(f.Sha1, f.Size == 0 ? new string('0', 40) : Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(Path.Combine(game, f.Path)))));
			// No chunks of skipped files.
			Assert.DoesNotContain(depot.Requested, id => id.StartsWith("lib64/") || id.StartsWith("Celeste.bin") || id.StartsWith("FNA.dll"));
		}

		[Fact]
		public async Task After_a_verified_install_no_temp_chunk_or_resume_files_are_left()
		{
			using var dir = new TempDir();
			(ManifestSnapshot m, FakeDepot depot) = Game();
			await Installer(dir, depot).InstallAsync(m, CancellationToken.None);

			Assert.False(File.Exists(dir.Combine("steam/resume.json")));
			Assert.False(File.Exists(dir.Combine("steam/resume.json.tmp")));
			Assert.Empty(Directory.EnumerateFiles(dir.Path, "*.tmp", SearchOption.AllDirectories));
			Assert.Empty(Directory.EnumerateFiles(dir.Path, "*.part", SearchOption.AllDirectories));
			// The only copy of the game is the one in the final folder: the total on disk is exactly the needed set.
			long onDisk = Directory.EnumerateFiles(dir.Path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
			Assert.Equal(FileSelection.Select(m).NeededBytes, onDisk);
		}

		[Fact]
		public async Task An_interrupted_download_resumes_and_only_fetches_the_missing_chunks()
		{
			using var dir = new TempDir();
			(ManifestSnapshot m, FakeDepot depot) = Game();
			int totalChunks = FileSelection.Select(m).Needed.Sum(f => f.Chunks.Count);

			depot.FailAfter = 4;
			await Assert.ThrowsAnyAsync<Exception>(() => Installer(dir, depot).InstallAsync(m, CancellationToken.None));
			Assert.True(File.Exists(dir.Combine("steam/resume.json")), "resume state is kept after a failure");

			var second = new FakeDepot();
			ManifestSnapshot m2 = Game().Manifest; // same manifest (same chunk ids)
			// The second round's source needs to know the same chunks:
			ManifestSnapshot rebuilt = Manifest(second, 1,
				second.File("Celeste.exe", 3500), second.File("Content/Graphics/a.data", 2500),
				second.File("Content/Graphics/b.data", 999), second.File("Content/Dialog/english.txt", 10), second.File("Content/empty.bin", 0));
			InstallResult r = await Installer(dir, second).InstallAsync(rebuilt, CancellationToken.None);

			Assert.Equal(FileSelection.Select(rebuilt).Needed.Count, r.Files);
			Assert.True(second.Requested.Count < totalChunks, $"resumed run fetched {second.Requested.Count} of {totalChunks} chunks");
			Assert.False(File.Exists(dir.Combine("steam/resume.json")));
		}

		[Fact]
		public async Task A_corrupted_existing_file_is_detected_and_downloaded_again()
		{
			using var dir = new TempDir();
			(ManifestSnapshot m, FakeDepot depot) = Game();
			// A file with the right size and wrong content, with no resume state.
			string path = dir.Combine("game/Content/Graphics/a.data");
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllBytes(path, new byte[2500]);

			await Installer(dir, depot).InstallAsync(m, CancellationToken.None);

			ManifestFile a = m.Files.Single(f => f.Path == "Content/Graphics/a.data");
			Assert.Equal(a.Sha1, Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(path))));
		}

		[Fact]
		public async Task An_intact_existing_install_is_not_downloaded_again()
		{
			using var dir = new TempDir();
			(ManifestSnapshot m, FakeDepot depot) = Game();
			await Installer(dir, depot).InstallAsync(m, CancellationToken.None);

			var again = new FakeDepot();
			ManifestSnapshot m2 = Manifest(again, 1,
				again.File("Celeste.exe", 3500), again.File("Content/Graphics/a.data", 2500),
				again.File("Content/Graphics/b.data", 999), again.File("Content/Dialog/english.txt", 10), again.File("Content/empty.bin", 0));
			await Installer(dir, again).InstallAsync(m2, CancellationToken.None);
			Assert.Empty(again.Requested);
		}

		[Fact]
		public async Task Updating_removes_files_no_longer_in_the_needed_set_and_replaces_changed_ones()
		{
			using var dir = new TempDir();
			(ManifestSnapshot v1, FakeDepot d1) = Game(version: 1, id: 1);
			await Installer(dir, d1).InstallAsync(v1, CancellationToken.None);

			// Leftovers from old installs (skipped files and a file that left the game).
			dir.Write("game/Celeste.bin.x86_64", 77);
			dir.Write("game/lib64/libfmod.so.10", 33);
			dir.Write("game/Content/Graphics/removed-in-v2.data", 12);

			var d2 = new FakeDepot();
			ManifestSnapshot v2 = Manifest(d2, 2,
				d2.File("Celeste.exe", 3600, 2),                 // mudou
				d2.File("Content/Graphics/a.data", 2500, 1),     // same as v1
				d2.File("Content/Dialog/english.txt", 10, 1));   // same as v1 (b.data and empty.bin are gone)
			InstallResult r = await Installer(dir, d2).InstallAsync(v2, CancellationToken.None);

			string game = dir.Combine("game");
			string[] files = Directory.EnumerateFiles(game, "*", SearchOption.AllDirectories)
				.Select(f => Path.GetRelativePath(game, f).Replace('\\', '/')).OrderBy(x => x, StringComparer.Ordinal).ToArray();
			Assert.Equal(new[] { "Celeste.exe", "Content/Dialog/english.txt", "Content/Graphics/a.data" }, files);
			Assert.True(r.Removed.FilesDeleted >= 5, "launcher, native, removed file, b.data, empty.bin");
			Assert.False(Directory.Exists(Path.Combine(game, "lib64")));
			ManifestFile exe = v2.Files.Single(f => f.Path == "Celeste.exe");
			Assert.Equal(exe.Sha1, Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(Path.Combine(game, "Celeste.exe")))));
			// Only what changed was downloaded again.
			Assert.All(d2.Requested, id => Assert.StartsWith("Celeste.exe@2@", id));
		}

		[Fact]
		public async Task A_wrong_depot_is_rejected_before_anything_is_written()
		{
			using var dir = new TempDir();
			var depot = new FakeDepot();
			var windowsLike = new ManifestSnapshot(504230, 504231, 1, "public", new[] { depot.File("Celeste.exe", 100), depot.File("Content/a", 10) });
			await Assert.ThrowsAsync<InvalidOperationException>(() => Installer(dir, depot).InstallAsync(windowsLike, CancellationToken.None));
			Assert.False(Directory.Exists(dir.Combine("game")) && Directory.EnumerateFileSystemEntries(dir.Combine("game")).Any());
			Assert.Empty(depot.Requested);
		}

		[Fact]
		public async Task Progress_reaches_the_total_and_reports_files()
		{
			using var dir = new TempDir();
			(ManifestSnapshot m, FakeDepot depot) = Game();
			InstallProgress? last = null;
			await Installer(dir, depot, p => last = p).InstallAsync(m, CancellationToken.None);

			SelectionResult sel = FileSelection.Select(m);
			Assert.NotNull(last);
			Assert.Equal("Done", last!.Phase);
			Assert.Equal(sel.NeededBytes, last.BytesTotal);
			Assert.Equal(sel.NeededBytes, last.BytesDone);
		}

		[Fact]
		public async Task The_file_counter_advances_while_downloading_and_never_exceeds_the_total()
		{
			using var dir = new TempDir();
			(ManifestSnapshot m, FakeDepot depot) = Game();
			var events = new System.Collections.Concurrent.ConcurrentQueue<InstallProgress>();
			await Installer(dir, depot, events.Enqueue).InstallAsync(m, CancellationToken.None);

			List<InstallProgress> downloading = events.Where(p => p.Phase == "Downloading").ToList();
			int needed = FileSelection.Select(m).Needed.Count;
			// Files are counted as soon as their last chunk is written, not only in the verify phase.
			Assert.Contains(downloading, p => p.FilesDone > 0);
			Assert.All(events, p => Assert.InRange(p.FilesDone, 0, needed));
			Assert.Contains(events, p => p.Phase == "Verifying");
			Assert.Equal(needed, events.Last().FilesDone);
		}

		[Fact]
		public async Task The_delta_of_an_installed_game_is_empty_and_an_update_fetches_nothing()
		{
			using var dir = new TempDir();
			(ManifestSnapshot m, FakeDepot depot) = Game();
			await Installer(dir, depot).InstallAsync(m, CancellationToken.None);
			depot.Requested.Clear();

			// A new manifest ID with identical content: nothing to fetch, nothing requested, nothing counted.
			ManifestSnapshot same = m with { ManifestId = 2 };
			DeltaPlan plan = await Installer(dir, depot).PlanAsync(same, null, CancellationToken.None);
			Assert.Equal(0, plan.Files);
			Assert.Equal(0, plan.Bytes);
			InstallResult r = await Installer(dir, depot).InstallAsync(same, CancellationToken.None);
			Assert.Equal(0, r.DownloadedFiles);
			Assert.Empty(depot.Requested);
		}

		[Fact]
		public async Task The_delta_of_a_changed_game_counts_only_the_changed_files()
		{
			using var dir = new TempDir();
			(ManifestSnapshot old, FakeDepot depot) = Game(version: 1, id: 1);
			await Installer(dir, depot).InstallAsync(old, CancellationToken.None);
			(ManifestSnapshot updated, FakeDepot depot2) = Game(version: 1, id: 2);
			// Same files except the executable.
			var changed = updated.Files.Select(f => f.Path == "Celeste.exe" ? depot2.File("Celeste.exe", 3600, 2) : f).ToList();
			updated = updated with { Files = changed };

			DeltaPlan plan = await Installer(dir, depot2).PlanAsync(updated, null, CancellationToken.None);
			Assert.Equal(1, plan.Files);
			Assert.Equal(3600, plan.Bytes);

			var seen = new System.Collections.Concurrent.ConcurrentQueue<InstallProgress>(); // progress arrives from several threads
			InstallResult r = await Installer(dir, depot2, seen.Enqueue).InstallAsync(updated, CancellationToken.None);
			Assert.Equal(1, r.DownloadedFiles);
			Assert.Equal(3600, r.DownloadedBytes);
			Assert.All(seen.Where(p => p.Phase == "Downloading"), p => Assert.Equal(3600, p.BytesTotal));
		}

		[Fact]
		public async Task Cancellation_keeps_the_resume_state()
		{
			using var dir = new TempDir();
			(ManifestSnapshot m, FakeDepot depot) = Game();
			using var cts = new CancellationTokenSource();
			depot.FailAfter = 3;
			cts.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Installer(dir, depot).InstallAsync(m, cts.Token));
			Assert.True(File.Exists(dir.Combine("steam/resume.json")));
		}
	}
}
