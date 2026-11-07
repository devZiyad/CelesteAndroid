using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CelesteAndroid.Steam;
using Xunit;

namespace CelesteAndroid.Steam.Tests
{
	public sealed class TempDir : IDisposable
	{
		public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "celeste-steam-tests-" + Guid.NewGuid().ToString("N"));
		public TempDir() => Directory.CreateDirectory(Path);
		public string Combine(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
		public void Write(string relative, int bytes = 10)
		{
			string full = Combine(relative);
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
			File.WriteAllBytes(full, new byte[bytes]);
		}
		public IEnumerable<string> RelativeFiles() =>
			Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)
				.Select(f => System.IO.Path.GetRelativePath(Path, f).Replace(System.IO.Path.DirectorySeparatorChar, '/')).OrderBy(x => x, StringComparer.Ordinal);
		public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
	}

	public class StaleFileRemoverTests
	{
		[Fact]
		public void Removes_every_file_that_is_not_in_the_needed_set_and_keeps_the_rest()
		{
			using var dir = new TempDir();
			dir.Write("Celeste.exe", 100);
			dir.Write("Content/Graphics/a.data", 50);
			dir.Write("Content/Dialog/english.txt", 5);
			dir.Write("Celeste.bin.x86_64", 70);            // launcher de uma instalação anterior
			dir.Write("lib64/libfmod.so.10", 30);           // nativo antigo
			dir.Write("Content/Graphics/old-version.data", 40); // arquivo de uma versão antiga do jogo
			dir.Write("notes.tmp", 1);

			var needed = new HashSet<string> { "Celeste.exe", "Content/Graphics/a.data", "Content/Dialog/english.txt" };
			RemovalReport report = StaleFileRemover.Remove(dir.Path, needed);

			Assert.Equal(new[] { "Celeste.exe", "Content/Dialog/english.txt", "Content/Graphics/a.data" }, dir.RelativeFiles());
			Assert.Equal(4, report.FilesDeleted);
			Assert.Equal(70 + 30 + 40 + 1, report.BytesFreed);
		}

		[Fact]
		public void Prunes_directories_that_become_empty_but_keeps_needed_ones()
		{
			using var dir = new TempDir();
			dir.Write("Celeste.exe");
			dir.Write("lib64/deep/nested/libx.so");
			dir.Write("Content/keep.bin");

			RemovalReport report = StaleFileRemover.Remove(dir.Path, new HashSet<string> { "Celeste.exe", "Content/keep.bin" });

			Assert.False(Directory.Exists(dir.Combine("lib64")));
			Assert.True(Directory.Exists(dir.Combine("Content")));
			Assert.Equal(3, report.DirectoriesRemoved); // lib64, lib64/deep, lib64/deep/nested
		}

		[Fact]
		public void Is_case_sensitive_like_the_linux_filesystem()
		{
			using var dir = new TempDir();
			dir.Write("Content/Art.data");
			dir.Write("Content/art.data");
			StaleFileRemover.Remove(dir.Path, new HashSet<string> { "Content/Art.data" });
			Assert.Equal(new[] { "Content/Art.data" }, dir.RelativeFiles());
		}

		[Fact]
		public void Missing_root_is_a_no_op()
		{
			RemovalReport report = StaleFileRemover.Remove(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")), new HashSet<string>());
			Assert.Equal(new RemovalReport(0, 0, 0), report);
		}

		[Fact]
		public void Nothing_to_remove_leaves_the_tree_untouched()
		{
			using var dir = new TempDir();
			dir.Write("Celeste.exe");
			dir.Write("Content/a");
			RemovalReport report = StaleFileRemover.Remove(dir.Path, new HashSet<string> { "Celeste.exe", "Content/a" });
			Assert.Equal(new RemovalReport(0, 0, 0), report);
			Assert.Equal(2, dir.RelativeFiles().Count());
		}
	}
}
