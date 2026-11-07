using System;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Util;
using CelesteAndroid.Steam;

namespace CelesteAndroid
{
	/// <summary>The signed-in Steam account: its name, and signing out. Game files and saves are never touched.</summary>
	public static class SteamAccount
	{
		private static void LogLine(string line) => Log.Info("CelesteAndroid", "[CelesteAndroid] " + line);

		public static string? Name(Context context) => SteamTokenStore.Load(context)?.Account;

		/// <summary>
		/// Ends the session: stops a running download, asks Steam to revoke the refresh token (when the network allows it), and deletes
		/// the token from storage and its encryption key from the Keystore. Safe to call when nobody is signed in.
		/// </summary>
		public static async Task SignOutAsync(Context context)
		{
			InstallService.CancelInstall(context);
			InstallCoordinator.ClearPending(context);
			(string Account, string RefreshToken)? stored = SteamTokenStore.Load(context);
			if (stored != null)
			{
				try
				{
					using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
					using var session = new SteamSession(LogLine);
					await session.ConnectAsync(timeout.Token).ConfigureAwait(false);
					await session.LogOnAsync(stored.Value.Account, stored.Value.RefreshToken, timeout.Token).ConfigureAwait(false);
					bool revoked = await session.TryRevokeOwnRefreshTokenAsync(timeout.Token).ConfigureAwait(false);
					LogLine(revoked ? "steam: refresh token revoked" : "steam: refresh token not revoked (no connection or no permission)");
				}
				catch (Exception e) when (e is not OutOfMemoryException)
				{
					LogLine("steam: could not revoke the refresh token (" + e.GetType().Name + "); deleting it locally");
				}
			}
			SteamTokenStore.Clear(context);
			LogLine("steam: session ended, refresh token deleted from storage and Keystore");
		}
	}
}
