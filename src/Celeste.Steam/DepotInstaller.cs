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

	public sealed record InstallResult(int Files, long Bytes, int SkippedFiles, long SkippedBytes, RemovalReport Removed);

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

		public async Task<InstallResult> InstallAsync(ManifestSnapshot manifest, CancellationToken cancellationToken)
		{
			FileSelection.ValidateIsFnaBuild(manifest);
			SelectionResult selection = FileSelection.Select(manifest);
			Directory.CreateDirectory(root);

			// Before spending space: remove what is not part of the needed set (old versions, skipped files).
			RemovalReport removed = StaleFileRemover.Remove(root, selection.NeededPaths);

			ResumeState state = ResumeState.Load(stateFile, manifest.ManifestId);
			var progress = new ProgressTracker(selection.NeededBytes, selection.Needed.Count, report);
			try
			{
				for (int round = 0; round < 2; round++)
				{
					progress.Reset();
					var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
					List<(ManifestFile File, int Index)> work = PrepareFiles(selection.Needed, state, progress, remaining);
					progress.Phase("Downloading");
					await DownloadChunksAsync(work, state, progress, remaining, cancellationToken).ConfigureAwait(false);

					progress.Phase("Verifying");
					progress.SetFilesDone(state.VerifiedFiles.Count);
					List<ManifestFile> bad = await VerifyAsync(selection.Needed, state, progress, cancellationToken).ConfigureAwait(false);
					if (bad.Count == 0)
						break;
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
			progress.Phase("Done");
			return new InstallResult(files, bytes, selection.Skipped.Count, selection.SkippedBytes, removed);
		}

		private string PathOf(ManifestFile file) => System.IO.Path.Combine(root, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));

		/// <summary>Creates/adjusts the files and returns the chunks still missing.</summary>
		private List<(ManifestFile, int)> PrepareFiles(IReadOnlyList<ManifestFile> needed, ResumeState state, ProgressTracker progress, Dictionary<string, int> remaining)
		{
			var work = new List<(ManifestFile, int)>();
			foreach (ManifestFile file in needed)
			{
				string path = PathOf(file);
				var info = new FileInfo(path);
				if (state.VerifiedFiles.Contains(file.Path) && info.Exists && info.Length == file.Size)
				{
					progress.AddDone(file.Size, fileDone: true);
					continue;
				}
				state.VerifiedFiles.Remove(file.Path);

				Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
				if (!info.Exists || info.Length != file.Size)
				{
					// New file (or the wrong size): start this file over.
					state.Chunks.Remove(file.Path);
					using FileStream fs = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
					fs.SetLength(file.Size);
				}
				else if (!state.Chunks.ContainsKey(file.Path) && HashMatches(path, file))
				{
					// Already complete (earlier install): counts as verified without downloading.
					state.VerifiedFiles.Add(file.Path);
					progress.AddDone(file.Size, fileDone: true);
					continue;
				}

				HashSet<int> have = state.Chunks.TryGetValue(file.Path, out var set) ? set : (state.Chunks[file.Path] = new HashSet<int>());
				int missing = 0;
				for (int i = 0; i < file.Chunks.Count; i++)
				{
					if (have.Contains(i)) progress.AddDone(file.Chunks[i].UncompressedLength, fileDone: false);
					else { work.Add((file, i)); missing++; }
				}
				// "Files" counts files whose data is fully on disk: those already complete count from the start,
				// the others as soon as their last chunk is written.
				if (missing == 0) progress.AddFilesDone(1);
				else remaining[file.Path] = missing;
			}
			return work;
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

		private sealed class ProgressTracker
		{
			private readonly long total;
			private readonly int filesTotal;
			private readonly Action<InstallProgress>? sink;
			private readonly long begin = System.Diagnostics.Stopwatch.GetTimestamp();
			private long done, startDone = -1;
			private int filesDone;
			private string phase = "Preparing";
			private string? current;
			private long lastReport;

			public ProgressTracker(long total, int filesTotal, Action<InstallProgress>? sink)
			{
				this.total = total; this.filesTotal = filesTotal; this.sink = sink;
			}

			public void Phase(string name) { phase = name; Emit(force: true); }

			public void AddDone(long bytes, bool fileDone, string? current = null)
			{
				long now = Interlocked.Add(ref done, bytes);
				if (startDone < 0) Interlocked.CompareExchange(ref startDone, now - bytes, -1);
				if (fileDone) Interlocked.Increment(ref filesDone);
				if (current != null) this.current = current;
				Emit(force: fileDone); // a finished file always updates the counter, the rest is throttled
			}

			public void AddFilesDone(int n, string? current = null)
			{
				Interlocked.Add(ref filesDone, n);
				if (current != null) this.current = current;
				Emit(force: current == null);
			}

			public void SetFilesDone(int n) { Interlocked.Exchange(ref filesDone, n); Emit(force: true); }

			/// <summary>New round (after files failed verification): counters start again; the speed clock keeps running.</summary>
			public void Reset()
			{
				Interlocked.Exchange(ref done, 0);
				Interlocked.Exchange(ref startDone, -1);
				Interlocked.Exchange(ref filesDone, 0);
			}

			private void Emit(bool force)
			{
				if (sink == null) return;
				long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
				if (!force && ticks - Interlocked.Read(ref lastReport) < System.Diagnostics.Stopwatch.Frequency / 4) return;
				Interlocked.Exchange(ref lastReport, ticks);
				double seconds = Math.Max(0.001, System.Diagnostics.Stopwatch.GetElapsedTime(begin).TotalSeconds);
				long d = Interlocked.Read(ref done);
				long since = startDone < 0 ? 0 : d - Interlocked.Read(ref startDone);
				sink(new InstallProgress(phase, d, total, filesDone, filesTotal, since / seconds, current));
			}
		}
	}
}
