using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteAndroid.Steam
{
	/// <summary>De onde vêm os bytes de um chunk (a Steam em produção, um gerador falso nos testes).</summary>
	public interface IChunkSource
	{
		/// <summary>Baixa, descriptografa e descomprime o chunk em <paramref name="buffer"/>; devolve quantos bytes escreveu.</summary>
		Task<int> DownloadChunkAsync(ManifestChunk chunk, byte[] buffer, CancellationToken cancellationToken);
	}

	public sealed record InstallProgress(string Phase, long BytesDone, long BytesTotal, int FilesDone, int FilesTotal, double BytesPerSecond, string? CurrentFile);

	public sealed record InstallResult(int Files, long Bytes, int SkippedFiles, long SkippedBytes, RemovalReport Removed);

	public sealed class DepotInstallException : Exception
	{
		public DepotInstallException(string message) : base(message) { }
	}

	/// <summary>
	/// Baixa o conjunto necessário do manifesto direto para a pasta final do jogo (sem cópia de staging, então o jogo nunca fica
	/// guardado duas vezes), em chunks, com retomada: o progresso fica num arquivo de estado fora da pasta do jogo, que é
	/// apagado quando tudo foi verificado (SHA-1 de cada arquivo). Sobras fora do conjunto necessário são removidas.
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

			// Antes de gastar espaço: tira o que não faz parte do conjunto necessário (versões antigas, arquivos pulados).
			RemovalReport removed = StaleFileRemover.Remove(root, selection.NeededPaths);

			ResumeState state = ResumeState.Load(stateFile, manifest.ManifestId);
			var progress = new ProgressTracker(selection.NeededBytes, selection.Needed.Count, report);
			try
			{
				for (int round = 0; round < 2; round++)
				{
					List<(ManifestFile File, int Index)> work = PrepareFiles(selection.Needed, state, progress);
					progress.Phase("Downloading");
					await DownloadChunksAsync(work, state, progress, cancellationToken).ConfigureAwait(false);

					progress.Phase("Verifying");
					List<ManifestFile> bad = await VerifyAsync(selection.Needed, state, progress, cancellationToken).ConfigureAwait(false);
					if (bad.Count == 0)
						break;
					if (round == 1)
						throw new DepotInstallException($"{bad.Count} file(s) failed verification, e.g. {bad[0].Path}.");
				}
			}
			finally
			{
				state.Save(stateFile); // retomável mesmo se cancelado/falhou
			}

			removed = Merge(removed, StaleFileRemover.Remove(root, selection.NeededPaths));
			(int files, long bytes) = Measure();
			if (files != selection.Needed.Count || bytes != selection.NeededBytes)
				throw new DepotInstallException($"Installed {files} files / {bytes} bytes, expected {selection.Needed.Count} / {selection.NeededBytes}.");

			ResumeState.Delete(stateFile); // verificado: nada de estado de retomada, cache ou temporário sobra
			progress.Phase("Done");
			return new InstallResult(files, bytes, selection.Skipped.Count, selection.SkippedBytes, removed);
		}

		private string PathOf(ManifestFile file) => System.IO.Path.Combine(root, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));

		/// <summary>Cria/ajusta os arquivos e devolve os chunks que ainda faltam.</summary>
		private List<(ManifestFile, int)> PrepareFiles(IReadOnlyList<ManifestFile> needed, ResumeState state, ProgressTracker progress)
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
					// Arquivo novo (ou de tamanho errado): recomeça este arquivo.
					state.Chunks.Remove(file.Path);
					using FileStream fs = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
					fs.SetLength(file.Size);
				}
				else if (!state.Chunks.ContainsKey(file.Path) && HashMatches(path, file))
				{
					// Já existe completo (instalação anterior): conta como verificado sem baixar.
					state.VerifiedFiles.Add(file.Path);
					progress.AddDone(file.Size, fileDone: true);
					continue;
				}

				HashSet<int> have = state.Chunks.TryGetValue(file.Path, out var set) ? set : (state.Chunks[file.Path] = new HashSet<int>());
				for (int i = 0; i < file.Chunks.Count; i++)
				{
					if (have.Contains(i)) progress.AddDone(file.Chunks[i].UncompressedLength, fileDone: false);
					else work.Add((file, i));
				}
			}
			return work;
		}

		private async Task DownloadChunksAsync(List<(ManifestFile File, int Index)> work, ResumeState state, ProgressTracker progress, CancellationToken ct)
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

				lock (stateLock)
				{
					state.Chunks[file.Path].Add(index);
					if (lastSave.ElapsedSeconds() >= 2)
					{
						state.Save(stateFile);
						lastSave.Restart();
					}
				}
				progress.AddDone(chunk.UncompressedLength, fileDone: false, current: file.Path);
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

		/// <summary>Confere o SHA-1 de cada arquivo; os que falham são apagados (e seus chunks esquecidos) para baixar de novo.</summary>
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
						progress.AddFilesDone(1);
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
				return new FileInfo(path).Length == file.Size; // sem hash no manifesto: só o tamanho
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
				Emit(force: false);
			}

			public void AddFilesDone(int n) { Interlocked.Add(ref filesDone, n); Emit(force: true); }

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
