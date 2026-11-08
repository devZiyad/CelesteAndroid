using System;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Util;
using CelesteAndroid.Steam;
using SteamKit2;

namespace CelesteAndroid
{
	/// <summary>The outcome of "Check for updates": Steam's current build and whether the installed one differs.</summary>
	public sealed record UpdateCheckResult(SteamBuildInfo Current, ulong? InstalledBuild, ulong? InstalledManifest, bool UpdateAvailable);

	/// <summary>
	/// Compares the stored build / depot manifest IDs of the installed game with Steam's current ones (app info, PICS). No manifest is
	/// downloaded and no file list is compared: the manifest ID changes whenever the depot's content does.
	/// </summary>
	public static class UpdateChecker
	{
		private static void LogLine(string line) => Log.Info("CelesteAndroid", "[CelesteAndroid] " + line);

		public static async Task<UpdateCheckResult> CheckAsync(Context context, CancellationToken ct)
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
				$"Steam current build {current.BuildId} manifest {current.ManifestId} -> {(update ? "update available" : "up to date")}");
			return new UpdateCheckResult(current, build, manifest, update);
		}
	}
}
