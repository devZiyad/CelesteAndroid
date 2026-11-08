using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.OS;
using Android.Util;
using CelesteAndroid.Steam;
using SteamKit2;

namespace CelesteAndroid
{
	/// <summary>
	/// One attempt at "install (or update) Celeste from Steam": connect, sign in with the stored refresh token, check ownership, get the
	/// manifest, download and verify, patch, generate the side art. Reports each step through <see cref="InstallCoordinator"/>. It never
	/// asks anything: with no usable session it throws <see cref="SignInRequiredException"/> and the sign-in screen takes over.
	/// The service calls it again after a network failure: the download resumes from the chunks already on disk.
	/// </summary>
	public sealed class InstallPipeline
	{
		private readonly Context context;
		private readonly bool update;
		private readonly string title;
		private long lastBytes;

		public InstallPipeline(Context context, bool update)
		{
			this.context = context;
			this.update = update;
			title = update ? "Updating Celeste" : "Installing Celeste";
		}

		/// <summary>Bytes downloaded so far in this attempt (the service uses it to tell whether retrying makes progress).</summary>
		public long BytesDone => Interlocked.Read(ref lastBytes);

		private static void LogLine(string line) => Log.Info("CelesteAndroid", "[CelesteAndroid] " + line);

		private void Step(string step, string detail = "", float fraction = -1f, int percent = -1, TransferStats? stats = null) =>
			InstallCoordinator.Publish(new InstallStatus(InstallStage.Running, title, step, detail, fraction, percent, stats));

		public async Task RunAsync(CancellationToken ct)
		{
			Step("Connecting to Steam");
			using var session = new SteamSession(LogLine);
			await session.ConnectAsync(ct).ConfigureAwait(false);

			(string Account, string RefreshToken)? stored = SteamTokenStore.Load(context);
			if (stored == null)
				throw new SignInRequiredException("Sign in to Steam to download Celeste.");
			Step($"Signing in to Steam as {stored.Value.Account}");
			try
			{
				await session.LogOnAsync(stored.Value.Account, stored.Value.RefreshToken, ct).ConfigureAwait(false);
			}
			catch (SteamLogonRefusedException e) when (IsTokenProblem(e.Result))
			{
				LogLine("steam: the stored session was rejected (" + e.Result + "), it was deleted");
				SteamTokenStore.Clear(context);
				throw new SignInRequiredException("Your Steam session ended. Sign in again.");
			}
			LogLine("steam: signed in with the stored session");

			Step("Checking that your account owns Celeste");
			if (!await session.OwnsGameAsync(ct).ConfigureAwait(false))
				throw NotOwned();
			LogLine($"steam: ownership confirmed (app {SteamSession.AppId} is in this account's licenses, own or family-shared)");

			Step("Getting the file list");
			ManifestSnapshot manifest;
			IChunkSource chunks;
			try
			{
				(manifest, chunks) = await session.GetManifestAsync(ct).ConfigureAwait(false);
			}
			catch (SteamNoAccessException)
			{
				throw NotOwned();
			}
			FileSelection.ValidateIsFnaBuild(manifest);
			SelectionResult selection = FileSelection.Select(manifest);
			LogLine($"steam: manifest {manifest.ManifestId}: {manifest.Files.Count(f => !f.IsDirectory)} files; needed set {selection.Needed.Count} files, {selection.NeededBytes} bytes; skipping {selection.Skipped.Count} files, {selection.SkippedBytes} bytes");

			if (update && GameInstaller.IsInstalled(context) && InstallCoordinator.InstalledManifest(context) == manifest.ManifestId)
			{
				LogLine($"steam: update check: manifest {manifest.ManifestId} is already installed");
				if (session.LatestBuild is { BuildId: not 0 } known) InstallCoordinator.SaveInstalledBuild(context, known.BuildId);
				InstallCoordinator.ClearPending(context);
				InstallCoordinator.Publish(new InstallStatus(InstallStage.Done, InstallCoordinator.UpToDateTitle, "You already have the latest version from Steam.", Fraction: 1f, Percent: 100));
				return;
			}

			string gameDir = GameInstaller.GameDir(context);
			long free = new StatFs(context.FilesDir!.AbsolutePath).AvailableBytes;
			long existing = Directory.Exists(gameDir) ? Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
			if (free + existing < selection.NeededBytes + (200L << 20))
				throw new InstallException($"Not enough free space: Celeste needs {InstallCoordinator.FormatBytes(selection.NeededBytes)}, {InstallCoordinator.FormatBytes(free)} available.");

			var installer = new DepotInstaller(gameDir, SteamTokenStore.ResumeStatePath(context), chunks, parallelism: 6, OnProgress);
			// An update: files that are the same in the installed build's manifest are accepted without hashing; the rest is verified.
			ISet<string>? trusted = update ? await UpdateChecker.TrustedFilesAsync(session, InstallCoordinator.InstalledManifest(context), selection).ConfigureAwait(false) : null;
			InstallResult result = await installer.InstallAsync(manifest, ct, trusted).ConfigureAwait(false);
			LogLine($"steam: downloaded {result.DownloadedFiles} files, {InstallCoordinator.FormatBytes(result.DownloadedBytes)} ({result.DownloadedBytes} bytes)");
			LogLine($"steam: depot {SteamSession.LinuxDepotId} download completed and verified: files={result.Files} bytes={result.Bytes} (manifest needed set: files={selection.Needed.Count} bytes={selection.NeededBytes}); removed stale files={result.Removed.FilesDeleted}");

			if (update && result.DownloadedFiles == 0 && File.Exists(GameInstaller.PatchedDll(context)))
			{
				LogLine("steam: nothing was downloaded: the patched game is still current");
			}
			else
			{
				var gameInstaller = new GameInstaller(context, (message, fraction) => Step(message, fraction: fraction));
				gameInstaller.Patch();
				gameInstaller.PrepareBackground();
				LogLine("steam: patching completed");
			}

			InstallCoordinator.SaveInstalledManifest(context, manifest.ManifestId);
			if (session.LatestBuild is { BuildId: not 0 } build) InstallCoordinator.SaveInstalledBuild(context, build.BuildId);
			LogLine($"steam: stored installed build {session.LatestBuild?.BuildId} and depot manifest {manifest.ManifestId}");
			InstallCoordinator.ClearPending(context);
			InstallCoordinator.Publish(new InstallStatus(InstallStage.Done, "Celeste is ready", "Installed from Steam. You can play offline.", Fraction: 1f, Percent: 100));
		}

		private static NotOwnedException NotOwned()
		{
			LogLine($"steam: ownership NOT confirmed for app {SteamSession.AppId}");
			return new NotOwnedException("This Steam account doesn't own Celeste and has no family-shared copy of it. Your existing install was not changed.");
		}

		/// <summary>Results that mean "this token will never work again" (as opposed to Steam being busy or unreachable).</summary>
		private static bool IsTokenProblem(EResult result) =>
			result is EResult.InvalidPassword or EResult.AccessDenied or EResult.Expired or EResult.Revoked
				or EResult.AccountLogonDenied or EResult.InvalidLoginAuthCode or EResult.AccountDisabled or EResult.LoggedInElsewhere;

		/// <summary>
		/// Two labeled phases: "Verifying files (n/N)" (the bar follows the file counter) and "Downloading (a MB / Y MB)" (the bar and the byte
		/// counters cover only what is downloaded).
		/// </summary>
		private string? loggedPhase;

		private void OnProgress(InstallProgress p)
		{
			Interlocked.Exchange(ref lastBytes, p.BytesDone);
			if (p.Phase != loggedPhase)
			{
				loggedPhase = p.Phase;
				LogLine($"steam: phase {p.Phase}: {p.FilesTotal} files" + (p.Phase == "Downloading" ? $", {InstallText.FormatBytes(p.BytesTotal)} to download" : ""));
			}
			if (p.Phase == "Downloading")
			{
				float fraction = p.BytesTotal == 0 ? 0f : Math.Min(1f, p.BytesDone / (float)p.BytesTotal);
				string step = $"Downloading ({InstallText.FormatBytes(p.BytesDone)} / {InstallText.FormatBytes(p.BytesTotal)})";
				Step(step, fraction: fraction, percent: (int)(fraction * 100), stats: new TransferStats(p.FilesDone, p.FilesTotal, p.BytesDone, p.BytesTotal, p.BytesPerSecond));
			}
			else
			{
				float fraction = p.FilesTotal == 0 ? 0f : Math.Min(1f, p.FilesDone / (float)p.FilesTotal);
				Step($"Verifying files ({p.FilesDone}/{p.FilesTotal})", fraction: fraction, percent: (int)(fraction * 100));
			}
		}
	}
}
