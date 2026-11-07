using System;
using System.IO;
using Android.Content;
using CelesteAndroid.Steam;

namespace CelesteAndroid
{
	public enum InstallStage
	{
		Idle,
		/// <summary>Working: connecting, checking, downloading, verifying or patching.</summary>
		Running,
		/// <summary>The network dropped or Steam closed the connection: waiting to retry (progress is kept).</summary>
		Waiting,
		/// <summary>There is no usable Steam session: the sign-in screen has to run.</summary>
		NeedsSignIn,
		Failed,
		Done,
	}

	/// <summary>What the launcher's main screen and the notification show. <see cref="Fraction"/> is -1 when unknown.</summary>
	public sealed record InstallStatus(InstallStage Stage, string Title, string Step, string Detail = "", float Fraction = -1f, int Percent = -1,
		TransferStats? Stats = null)
	{
		public static readonly InstallStatus None = new(InstallStage.Idle, "", "");
		public bool IsActive => Stage is InstallStage.Running or InstallStage.Waiting;
	}

	/// <summary>
	/// The install's current state, shared by the foreground service (which does the work) and the launcher.
	/// They all run in the app's main process, so a static is enough: a screen opened while the service is running just reads
	/// <see cref="Current"/> and subscribes to <see cref="Changed"/> (called on a worker thread).
	/// </summary>
	public static class InstallCoordinator
	{
		private static InstallStatus current = InstallStatus.None;

		public static InstallStatus Current => current;
		public static bool IsActive => current.IsActive;
		public static event Action<InstallStatus>? Changed;

		public static void Publish(InstallStatus status)
		{
			current = status;
			Changed?.Invoke(status);
		}

		#region Pending marker (so an install interrupted by a kill or reboot resumes by itself)

		private static string PendingFile(Context context) => Path.Combine(context.FilesDir!.AbsolutePath, "steam", "install.pending");

		/// <summary>"install" or "update" when an install was started and has not finished or been cancelled; otherwise null.</summary>
		public static string? PendingMode(Context context)
		{
			string path = PendingFile(context);
			return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
		}

		public static void MarkPending(Context context, bool update)
		{
			string path = PendingFile(context);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, update ? "update" : "install");
		}

		public static void ClearPending(Context context)
		{
			string path = PendingFile(context);
			if (File.Exists(path)) File.Delete(path);
		}

		#endregion

		#region Installed manifest (to tell whether "Check for updates" has anything to do)

		private static string ManifestFile(Context context) => Path.Combine(context.FilesDir!.AbsolutePath, "steam", "installed-manifest.txt");

		public static ulong? InstalledManifest(Context context) =>
			File.Exists(ManifestFile(context)) && ulong.TryParse(File.ReadAllText(ManifestFile(context)).Trim(), out ulong id) ? id : null;

		public static void SaveInstalledManifest(Context context, ulong manifestId)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(ManifestFile(context))!);
			File.WriteAllText(ManifestFile(context), manifestId.ToString());
		}

		public static void ClearInstalledManifest(Context context)
		{
			if (File.Exists(ManifestFile(context))) File.Delete(ManifestFile(context));
		}

		#endregion

		public static string FormatBytes(long bytes) => InstallText.FormatBytes(bytes);

		/// <summary>Title of the Done status that means an update check found nothing to do.</summary>
		public const string UpToDateTitle = "Celeste is up to date";
	}

	/// <summary>The stored session is missing or Steam rejected it: the user has to sign in again.</summary>
	public sealed class SignInRequiredException(string message) : Exception(message);

	/// <summary>The signed-in account has no license (own or family-shared) for Celeste. Terminal: never retried.</summary>
	public sealed class NotOwnedException(string message) : Exception(message);
}
