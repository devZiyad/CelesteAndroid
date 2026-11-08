using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteAndroid.Steam
{
	/// <summary>Where a chunk's bytes come from (Steam in production, a fake generator in tests).</summary>
	public interface IChunkSource
	{
		/// <summary>Downloads, decrypts and decompresses the chunk into <paramref name="buffer"/>; returns how many bytes it wrote.</summary>
		Task<int> DownloadChunkAsync(ManifestChunk chunk, byte[] buffer, CancellationToken cancellationToken);
	}

	public sealed record InstallProgress(string Phase, long BytesDone, long BytesTotal, int FilesDone, int FilesTotal, double BytesPerSecond, string? CurrentFile);

	public sealed record InstallResult(int Files, long Bytes, int SkippedFiles, long SkippedBytes, RemovalReport Removed, int DownloadedFiles = 0, long DownloadedBytes = 0);

	/// <summary>What an update has to fetch: the files that are not already on disk as the manifest says, and their (uncompressed) bytes.</summary>
	public sealed record DeltaPlan(int Files, long Bytes, int TotalFiles);

	public sealed class DepotInstallException : Exception
	{
		public DepotInstallException(string message) : base(message) { }
	}

	/// <summary>
	/// Downloads the manifest's needed set straight into the game's final folder (no staging copy, so the game is never
	/// stored twice), in chunks, with resume: progress is kept in a state file outside the game folder, which is
	/// deleted once everything is verified (SHA-1 of every file). Leftovers outside the needed set are removed.
	/// </summary>
	public sealed class DepotInstaller
	{
		private const int MaxChunkAttempts = 4;

		private readonly string root;
		private readonly string stateFile;
		private readonly IChunkSource source;
		private readonly int parallelism;
		private readonly Action<InstallProgress>? report;

		public DepotInstaller(string gameDirectory, string resumeStateFile, IChunkSource source, int parallelism = 6, Action<InstallProgress>? progress = null)
		{
			root = gameDirectory;
			stateFile = resumeStateFile;
			this.source = source;
			this.parallelism = Math.Max(1, parallelism);
			report = progress;
		}

		/// <summary>
		/// Which files an install would have to fetch, without writing anything. <paramref name="trusted"/> (paths whose content is the same in
		/// the installed manifest and this one) are accepted by size alone; every other file on disk is hashed against this manifest.
		/// </summary>
		public async Task<DeltaPlan> PlanAsync(ManifestSnapshot manifest, ISet<string>? trusted, CancellationToken cancellationToken, Action<int, int>? verifyProgress = null)
		{
			FileSelection.ValidateIsFnaBuild(manifest);
			SelectionResult selection = FileSelection.Select(manifest);
			ResumeState state = ResumeState.Load(stateFile, manifest.ManifestId);
			List<ManifestFile> incomplete = await FindIncompleteAsync(selection.Needed, state, trusted, verifyProgress, cancellationToken).ConfigureAwait(false);
			long bytes = 0;
			foreach (ManifestFile file in incomplete)
			{
				state.Chunks.TryGetValue(file.Path, out HashSet<int>? have);
				for (int i = 0; i < file.Chunks.Count; i++)
					if (have == null || !have.Contains(i)) bytes += file.Chunks[i].UncompressedLength;
			}
			return new DeltaPlan(incomplete.Count, bytes, selection.Needed.Count);
		}

		public async Task<InstallResult> InstallAsync(ManifestSnapshot manifest, CancellationToken cancellationToken, ISet<string>? trusted = null)
		{
			FileSelection.ValidateIsFnaBuild(manifest);
			SelectionResult selection = FileSelection.Select(manifest);
			Directory.CreateDirectory(root);

			// Before spending space: remove what is not part of the needed set (old versions, skipped files).
			RemovalReport removed = StaleFileRemover.Remove(root, selection.NeededPaths);

			ResumeState state = ResumeState.Load(stateFile, manifest.ManifestId);
			var progress = new ProgressTracker(report);
			int fetchedFiles = 0;
			long fetchedBytes = 0;
			try
			{
				for (int round = 0; round < 2; round++)
				{
					// Phase 1: which files are already right (counter n / total; nothing is downloaded or counted in bytes).
					progress.Begin("Verifying", 0, selection.Needed.Count);
					List<ManifestFile> incomplete = await FindIncompleteAsync(selection.Needed, state, trusted, (n, _) => progress.SetFilesDone(n), cancellationToken).ConfigureAwait(false);

					// Phase 2: download only the missing chunks; bytes and the bar count only these.
					var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
					(List<(ManifestFile File, int Index)> work, int alreadyComplete) = PrepareFiles(incomplete, state, remaining);
					long workBytes = work.Sum(w => (long)w.File.Chunks[w.Index].UncompressedLength);
					fetchedFiles = incomplete.Count;
					fetchedBytes = workBytes;
					progress.Begin("Downloading", workBytes, incomplete.Count);
					progress.AddFilesDone(alreadyComplete);
					await DownloadChunksAsync(work, state, progress, remaining, cancellationToken).ConfigureAwait(false);

					// Phase 3: check what was downloaded.
					progress.Begin("Verifying", workBytes, incomplete.Count);
					List<ManifestFile> bad = await VerifyAsync(incomplete, state, progress, cancellationToken).ConfigureAwait(false);
					if (bad.Count == 0)
					{
						progress.Begin("Done", workBytes, incomplete.Count);
						progress.Finish();
						break;
					}
					if (round == 1)
						throw new DepotInstallException($"{bad.Count} file(s) failed verification, e.g. {bad[0].Path}.");
				}
			}
			finally
			{
				state.Save(stateFile); // resumable even if cancelled/failed
			}

			removed = Merge(removed, StaleFileRemover.Remove(root, selection.NeededPaths));
			(int files, long bytes) = Measure();
			if (files != selection.Needed.Count || bytes != selection.NeededBytes)
				throw new DepotInstallException($"Installed {files} files / {bytes} bytes, expected {selection.Needed.Count} / {selection.NeededBytes}.");

			ResumeState.Delete(stateFile); // verified: no resume state, cache or temp files are left
			return new InstallResult(files, bytes, selection.Skipped.Count, selection.SkippedBytes, removed, fetchedFiles, fetchedBytes);
		}

		/// <summary>
		/// The needed files that are not complete on disk. A file is complete when it has the manifest's size and was verified earlier,
		/// is <paramref name="trusted"/>, or hashes to the manifest's SHA-1 (hashed with two threads).
		/// </summary>
		private async Task<List<ManifestFile>> FindIncompleteAsync(IReadOnlyList<ManifestFile> needed, ResumeState state, ISet<string>? trusted, Action<int, int>? onProgress, CancellationToken ct)
		{
			var incomplete = new bool[needed.Count];
			int done = 0;
			var gate = new object();
			await Parallel.ForEachAsync(Enumerable.Range(0, needed.Count), new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, (i, token) =>
			{
				ManifestFile file = needed[i];
				string path = PathOf(file);
				var info = new FileInfo(path);
				bool ok = info.Exists && info.Length == file.Size
					&& (state.VerifiedFiles.Contains(file.Path) || trusted?.Contains(file.Path) == true
						|| (!state.Chunks.ContainsKey(file.Path) && HashMatches(path, file)));
				lock (gate)
				{
					if (ok) state.VerifiedFiles.Add(file.Path);
					else { state.VerifiedFiles.Remove(file.Path); incomplete[i] = true; }
					onProgress?.Invoke(++done, needed.Count);
				}
				return ValueTask.CompletedTask;
			}).ConfigureAwait(false);
			return needed.Where((_, i) => incomplete[i]).ToList();
		}

		private string PathOf(ManifestFile file) => System.IO.Path.Combine(root, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));

		/// <summary>Creates/adjusts the incomplete files; returns the chunks still missing and how many of those files already have all their chunks.</summary>
		private (List<(ManifestFile, int)> Work, int AlreadyComplete) PrepareFiles(IReadOnlyList<ManifestFile> incomplete, ResumeState state, Dictionary<string, int> remaining)
		{
			var work = new List<(ManifestFile, int)>();
			int complete = 0;
			foreach (ManifestFile file in incomplete)
			{
				string path = PathOf(file);
				var info = new FileInfo(path);
				Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
				if (!info.Exists || info.Length != file.Size)
				{
					// New file (or the wrong size): start this file over.
					state.Chunks.Remove(file.Path);
					using FileStream fs = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
					fs.SetLength(file.Size);
				}

				HashSet<int> have = state.Chunks.TryGetValue(file.Path, out var set) ? set : (state.Chunks[file.Path] = new HashSet<int>());
				int missing = 0;
				for (int i = 0; i < file.Chunks.Count; i++)
				{
					if (!have.Contains(i)) { work.Add((file, i)); missing++; }
				}
				if (missing == 0) complete++;
				else remaining[file.Path] = missing;
			}
			return (work, complete);
		}

		private async Task DownloadChunksAsync(List<(ManifestFile File, int Index)> work, ResumeState state, ProgressTracker progress, Dictionary<string, int> remaining, CancellationToken ct)
		{
			var lastSave = new Stopwatch2();
			var stateLock = new object();
			var options = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct };
			await Parallel.ForEachAsync(work, options, async (item, token) =>
			{
				(ManifestFile file, int index) = item;
				ManifestChunk chunk = file.Chunks[index];
				byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(chunk.UncompressedLength, 1));
				try
				{
					int written = await DownloadWithRetryAsync(chunk, buffer, token).ConfigureAwait(false);
					if (written != chunk.UncompressedLength)
						throw new DepotInstallException($"Chunk of {file.Path} came back with {written} bytes, expected {chunk.UncompressedLength}.");
					using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(PathOf(file), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
					RandomAccess.Write(handle, new ReadOnlySpan<byte>(buffer, 0, written), chunk.Offset);
				}
				finally
				{
					System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
				}

				bool fileComplete;
				lock (stateLock)
				{
					state.Chunks[file.Path].Add(index);
					fileComplete = --remaining[file.Path] == 0;
					if (lastSave.ElapsedSeconds() >= 2)
					{
						state.Save(stateFile);
						lastSave.Restart();
					}
				}
				progress.AddDone(chunk.UncompressedLength, fileDone: fileComplete, current: file.Path);
			}).ConfigureAwait(false);
		}

		private async Task<int> DownloadWithRetryAsync(ManifestChunk chunk, byte[] buffer, CancellationToken ct)
		{
			for (int attempt = 1; ; attempt++)
			{
				ct.ThrowIfCancellationRequested();
				try
				{
					return await source.DownloadChunkAsync(chunk, buffer, ct).ConfigureAwait(false);
				}
				catch (Exception e) when (attempt < MaxChunkAttempts && e is not OperationCanceledException)
				{
					await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct).ConfigureAwait(false);
				}
			}
		}

		/// <summary>Checks each file's SHA-1; the ones that fail are deleted (and their chunks forgotten) to download again.</summary>
		private async Task<List<ManifestFile>> VerifyAsync(IReadOnlyList<ManifestFile> needed, ResumeState state, ProgressTracker progress, CancellationToken ct)
		{
			var bad = new List<ManifestFile>();
			var pending = needed.Where(f => !state.VerifiedFiles.Contains(f.Path)).ToList();
			var gate = new object();
			await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, (file, token) =>
			{
				string path = PathOf(file);
				bool ok = new FileInfo(path).Length == file.Size && HashMatches(path, file);
				lock (gate)
				{
					if (ok)
					{
						state.VerifiedFiles.Add(file.Path);
						progress.AddFilesDone(1, file.Path);
					}
					else
					{
						bad.Add(file);
						File.Delete(path);
						state.Chunks.Remove(file.Path);
					}
				}
				return ValueTask.CompletedTask;
			}).ConfigureAwait(false);
			return bad;
		}

		private static bool HashMatches(string path, ManifestFile file)
		{
			if (string.IsNullOrEmpty(file.Sha1) || file.Sha1.All(c => c == '0'))
				return new FileInfo(path).Length == file.Size; // no hash in the manifest: size only
			using FileStream fs = File.OpenRead(path);
			return string.Equals(Convert.ToHexString(SHA1.HashData(fs)), file.Sha1, StringComparison.OrdinalIgnoreCase);
		}

		private (int Files, long Bytes) Measure()
		{
			int n = 0; long b = 0;
			foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
			{
				n++; b += new FileInfo(f).Length;
			}
			return (n, b);
		}

		private static RemovalReport Merge(RemovalReport a, RemovalReport b) =>
			new(a.FilesDeleted + b.FilesDeleted, a.BytesFreed + b.BytesFreed, a.DirectoriesRemoved + b.DirectoriesRemoved);

		private sealed class Stopwatch2
		{
			private long start = System.Diagnostics.Stopwatch.GetTimestamp();
			public double ElapsedSeconds() => System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds;
			public void Restart() => start = System.Diagnostics.Stopwatch.GetTimestamp();
		}

		/// <summary>Counters of the current phase (Verifying / Downloading / Done); <see cref="Begin"/> starts a phase with its own totals.</summary>
		private sealed class ProgressTracker
		{
			private readonly Action<InstallProgress>? sink;
			private long begin = System.Diagnostics.Stopwatch.GetTimestamp();
			private long total, done, lastReport;
			private int filesTotal, filesDone;
			private string phase = "Preparing";
			private string? current;

			public ProgressTracker(Action<InstallProgress>? sink) => this.sink = sink;

			public void Begin(string name, long totalBytes, int totalFiles)
			{
				phase = name; total = totalBytes; filesTotal = totalFiles;
				Interlocked.Exchange(ref done, 0);
				Interlocked.Exchange(ref filesDone, 0);
				current = null;
				begin = System.Diagnostics.Stopwatch.GetTimestamp(); // the speed is per phase
				Emit(force: true);
			}

			/// <summary>Everything of the phase is done (the last "Done" report: all bytes and files).</summary>
			public void Finish()
			{
				Interlocked.Exchange(ref done, total);
				Interlocked.Exchange(ref filesDone, filesTotal);
				Emit(force: true);
			}

			public void AddDone(long bytes, bool fileDone, string? current = null)
			{
				Interlocked.Add(ref done, bytes);
				if (fileDone) Interlocked.Increment(ref filesDone);
				if (current != null) this.current = current;
				Emit(force: fileDone); // a finished file always updates the counter, the rest is throttled
			}

			public void AddFilesDone(int n, string? current = null)
			{
				Interlocked.Add(ref filesDone, n);
				if (current != null) this.current = current;
				Emit(force: false);
			}

			public void SetFilesDone(int n) { Interlocked.Exchange(ref filesDone, n); Emit(force: false); }

			private void Emit(bool force)
			{
				if (sink == null) return;
				long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
				if (!force && ticks - Interlocked.Read(ref lastReport) < System.Diagnostics.Stopwatch.Frequency / 4) return;
				Interlocked.Exchange(ref lastReport, ticks);
				double seconds = Math.Max(0.001, System.Diagnostics.Stopwatch.GetElapsedTime(begin).TotalSeconds);
				long d = Interlocked.Read(ref done);
				sink(new InstallProgress(phase, d, total, filesDone, filesTotal, d / seconds, current));
			}
		}
	}
}
