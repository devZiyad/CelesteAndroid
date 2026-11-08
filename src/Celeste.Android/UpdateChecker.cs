using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Util;
using CelesteAndroid.Steam;
using SteamKit2;

namespace CelesteAndroid
{
	/// <summary>The outcome of "Check for updates": Steam's current build and whether the installed one differs.</summary>
	public sealed record UpdateCheckResult(SteamBuildInfo Current, ulong? InstalledBuild, ulong? InstalledManifest, bool UpdateAvailable, int DeltaFiles = 0, long DeltaBytes = 0);

	/// <summary>
	/// Compares the stored build / depot manifest IDs of the installed game with Steam's current ones (app info, PICS). No manifest is
	/// downloaded and no file list is compared: the manifest ID changes whenever the depot's content does.
	/// </summary>
	public static class UpdateChecker
	{
		private static void LogLine(string line) => Log.Info("CelesteAndroid", "[CelesteAndroid] " + line);

		public static async Task<UpdateCheckResult> CheckAsync(Context context, CancellationToken ct, Action<string>? status = null)
		{
			(string Account, string RefreshToken)? stored = SteamTokenStore.Load(context);
			if (stored == null)
				throw new SignInRequiredException("Sign in to Steam to check for updates (Config > Steam account).");
			using var session = new SteamSession(LogLine);
			await session.ConnectAsync(ct).ConfigureAwait(false);
			try
			{
				await session.LogOnAsync(stored.Value.Account, stored.Value.RefreshToken, ct).ConfigureAwait(false);
			}
			catch (SteamLogonRefusedException e)
			{
				LogLine("steam: the stored session was rejected (" + e.Result + "), it was deleted");
				SteamTokenStore.Clear(context);
				throw new SignInRequiredException("Your Steam session ended. Sign in again (Config > Steam account).");
			}
			SteamBuildInfo current = await session.GetLatestBuildAsync().ConfigureAwait(false);
			ulong? build = InstallCoordinator.InstalledBuild(context), manifest = InstallCoordinator.InstalledManifest(context);
			// The manifest is what decides (it changes with the depot's content); a missing one (installed before it was tracked) cannot be "current".
			bool update = manifest != current.ManifestId;
			LogLine($"steam: update check: installed build {build?.ToString() ?? "unknown"} manifest {manifest?.ToString() ?? "unknown"}; " +
				$"Steam current build {current.BuildId} manifest {current.ManifestId} -> {(update ? "manifest differs" : "same manifest")}");
			int deltaFiles = 0;
			long deltaBytes = 0;
			if (update)
			{
				// What would really have to be downloaded: the new manifest against the installed files (hashes first, local verification only where needed).
				status?.Invoke("Comparing with Steam…");
				(ManifestSnapshot newManifest, IChunkSource chunks) = await session.GetManifestAsync(ct).ConfigureAwait(false);
				SelectionResult selection = FileSelection.Select(newManifest);
				ISet<string>? trusted = await TrustedFilesAsync(session, manifest, selection).ConfigureAwait(false);
				var installer = new DepotInstaller(GameInstaller.GameDir(context), SteamTokenStore.ResumeStatePath(context), chunks);
				DeltaPlan plan = await installer.PlanAsync(newManifest, trusted, ct, (n, total) => status?.Invoke($"Verifying files ({n}/{total})")).ConfigureAwait(false);
				deltaFiles = plan.Files; deltaBytes = plan.Bytes;
				LogLine($"steam: update check delta: {plan.Files} files, {InstallCoordinator.FormatBytes(plan.Bytes)} ({plan.Bytes} bytes) to download of {plan.TotalFiles} files " +
					$"(by manifest hashes: {(trusted != null ? trusted.Count + " unchanged files" : "installed manifest unavailable, local files verified")})");
				if (plan.Files == 0)
				{
					InstallCoordinator.SaveInstalledManifest(context, current.ManifestId);
					InstallCoordinator.SaveInstalledBuild(context, current.BuildId);
					LogLine($"steam: nothing to download: stored installed build {current.BuildId} and depot manifest {current.ManifestId}");
					return new UpdateCheckResult(current, current.BuildId, current.ManifestId, false);
				}
			}
			if (!update && build == null && current.BuildId != 0)
			{
				InstallCoordinator.SaveInstalledBuild(context, current.BuildId); // installed before the build was tracked, and its manifest is current
				build = current.BuildId;
				LogLine($"steam: stored installed build {build} (manifest {manifest} is current)");
			}
			return new UpdateCheckResult(current, build, manifest, update, deltaFiles, deltaBytes);
		}

		/// <summary>The files that are identical in the installed build's manifest and the new one, or null when the installed manifest is unknown or gone.</summary>
		public static async Task<ISet<string>?> TrustedFilesAsync(SteamSession session, ulong? installedManifest, SelectionResult selection)
		{
			if (installedManifest == null) return null;
			ManifestSnapshot? old = await session.TryGetManifestAsync(installedManifest.Value).ConfigureAwait(false);
			return old == null ? null : ManifestDiff.Unchanged(old, selection.Needed);
		}
	}
}
