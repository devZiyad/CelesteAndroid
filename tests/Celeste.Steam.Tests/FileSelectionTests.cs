using System;
using System.Collections.Generic;
using System.Linq;
using CelesteAndroid.Steam;
using Xunit;

namespace CelesteAndroid.Steam.Tests
{
	public class FileSelectionTests
	{
		private static ManifestFile F(string path, long size = 1, int flags = 0) => new(path, size, flags, "", new List<ManifestChunk>());

		[Theory]
		[InlineData("Celeste.exe", SelectionKind.Needed)]
		[InlineData("Content/Graphics/Atlases/Gameplay.data", SelectionKind.Needed)]
		[InlineData("Content/Dialog/english.txt", SelectionKind.Needed)]
		// Linux launchers
		[InlineData("Celeste", SelectionKind.SkippedLauncher)]
		[InlineData("Celeste.bin.x86", SelectionKind.SkippedLauncher)]
		[InlineData("Celeste.bin.x86_64", SelectionKind.SkippedLauncher)]
		// x86 / x86_64 natives
		[InlineData("lib/libSDL2-2.0.so.0", SelectionKind.SkippedNative)]
		[InlineData("lib64/libfmod.so.10", SelectionKind.SkippedNative)]
		[InlineData("lib64/libFNA3D.so.0", SelectionKind.SkippedNative)]
		// Steamworks and Steam libs
		[InlineData("Steamworks.NET.dll", SelectionKind.SkippedSteamworks)]
		[InlineData("lib64/libsteam_api.so", SelectionKind.SkippedSteamworks)]
		[InlineData("lib/libCSteamworks.so", SelectionKind.SkippedSteamworks)]
		// Mono runtime and configs
		[InlineData("mscorlib.dll", SelectionKind.SkippedRuntime)]
		[InlineData("System.Core.dll", SelectionKind.SkippedRuntime)]
		[InlineData("Mono.Posix.dll", SelectionKind.SkippedRuntime)]
		[InlineData("monoconfig", SelectionKind.SkippedRuntime)]
		[InlineData("Celeste.exe.config", SelectionKind.SkippedRuntime)]
		// The rest, which the port does not use
		[InlineData("FNA.dll", SelectionKind.SkippedOther)]
		[InlineData("Celeste.png", SelectionKind.SkippedOther)]
		[InlineData("gamecontrollerdb.txt", SelectionKind.SkippedOther)]
		[InlineData("Celeste.Content.dll", SelectionKind.SkippedOther)]
		public void Classify_applies_the_skip_list(string path, SelectionKind expected) =>
			Assert.Equal(expected, FileSelection.Classify(F(path)));

		[Fact]
		public void Directories_are_never_selected()
		{
			Assert.Equal(SelectionKind.Directory, FileSelection.Classify(F("Content", 0, ManifestFile.DirectoryFlag)));
		}

		[Fact]
		public void Case_matters_and_lookalikes_are_not_selected()
		{
			Assert.NotEqual(SelectionKind.Needed, FileSelection.Classify(F("celeste.exe")));
			Assert.NotEqual(SelectionKind.Needed, FileSelection.Classify(F("ContentExtra/file.bin")));
			Assert.NotEqual(SelectionKind.Needed, FileSelection.Classify(F("lib64/Content/x")));
		}

		[Fact]
		public void Recorded_manifest_selects_exactly_celeste_exe_and_content()
		{
			ManifestSnapshot m = Fixture.Load();
			SelectionResult r = FileSelection.Select(m);

			Assert.All(r.Needed, f => Assert.True(f.Path == "Celeste.exe" || f.Path.StartsWith("Content/", StringComparison.Ordinal)));
			Assert.Contains(r.Needed, f => f.Path == "Celeste.exe");
			// Everything that is not needed is listed as skipped, with a reason.
			int fileCount = m.Files.Count(f => !f.IsDirectory);
			Assert.Equal(fileCount, r.Needed.Count + r.Skipped.Count);
			Assert.All(r.Skipped, s => Assert.NotEqual(SelectionKind.Needed, s.Kind));
			Assert.Equal(m.Files.Where(f => !f.IsDirectory).Sum(f => f.Size), r.NeededBytes + r.SkippedBytes);
		}

		[Fact]
		public void Recorded_manifest_skips_launchers_natives_steamworks_and_runtime()
		{
			SelectionResult r = FileSelection.Select(Fixture.Load());
			SelectionKind KindOf(string path) => r.Skipped.Single(s => s.File.Path == path).Kind;

			Assert.Equal(SelectionKind.SkippedLauncher, KindOf("Celeste"));
			Assert.Equal(SelectionKind.SkippedLauncher, KindOf("Celeste.bin.x86_64"));
			Assert.Equal(SelectionKind.SkippedSteamworks, KindOf("Steamworks.NET.dll"));
			Assert.Equal(SelectionKind.SkippedSteamworks, KindOf("lib64/libsteam_api.so"));
			Assert.Equal(SelectionKind.SkippedNative, KindOf("lib64/libfmod.so.10"));
			Assert.Equal(SelectionKind.SkippedRuntime, KindOf("mscorlib.dll"));
			Assert.DoesNotContain(r.Skipped, s => s.File.Path.StartsWith("Content/", StringComparison.Ordinal));
			Assert.True(r.SkippedBytes > 0);
		}

		[Fact]
		public void Needed_set_is_far_smaller_than_the_whole_depot_nothing_native_is_downloaded()
		{
			SelectionResult r = FileSelection.Select(Fixture.Load());
			Assert.DoesNotContain(r.Needed, f => f.Path.Contains(".so", StringComparison.Ordinal));
			Assert.DoesNotContain(r.Needed, f => f.Path.Contains("steam", StringComparison.OrdinalIgnoreCase));
			Assert.DoesNotContain(r.Needed, f => f.Path.StartsWith("lib", StringComparison.Ordinal));
		}

		[Fact]
		public void Real_manifest_matches_what_the_phone_reported()
		{
			// Real manifest of depot 504233 (public branch, recorded on the device): the same numbers as the install's logcat.
			ManifestSnapshot m = Fixture.Load();
			Assert.Equal(5880027853585448535UL, m.ManifestId);
			SelectionResult r = FileSelection.Select(m);
			Assert.Equal(1217, r.Needed.Count);
			Assert.Equal(1161239007L, r.NeededBytes);
			Assert.Equal(42, r.Skipped.Count);
			Assert.Equal(40601319L, r.SkippedBytes);
		}

		[Fact]
		public void Fna_build_validation_accepts_the_linux_depot()
		{
			FileSelection.ValidateIsFnaBuild(Fixture.Load());
		}

		[Fact]
		public void Fna_build_validation_rejects_a_depot_without_fna_dll()
		{
			// A depot that is not the Linux FNA build (e.g. the Windows one, without FNA.dll) is refused before downloading.
			ManifestSnapshot m = Fixture.Load();
			var withoutFna = m with { Files = m.Files.Where(f => f.Path != "FNA.dll").ToList() };
			Assert.Throws<InvalidOperationException>(() => FileSelection.ValidateIsFnaBuild(withoutFna));
		}

		[Fact]
		public void Fna_build_validation_rejects_a_depot_without_the_game()
		{
			ManifestSnapshot m = Fixture.Load();
			Assert.Throws<InvalidOperationException>(() =>
				FileSelection.ValidateIsFnaBuild(m with { Files = m.Files.Where(f => f.Path != "Celeste.exe").ToList() }));
			Assert.Throws<InvalidOperationException>(() =>
				FileSelection.ValidateIsFnaBuild(m with { Files = m.Files.Where(f => !f.Path.StartsWith("Content", StringComparison.Ordinal)).ToList() }));
		}
	}
}
