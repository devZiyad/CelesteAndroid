using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.OS;
using Android.Util;
using CelesteAndroid.Steam;
using OperationCanceledException = System.OperationCanceledException;

namespace CelesteAndroid
{
	/// <summary>
	/// Runs the Steam download and install as a foreground service (type dataSync) so it goes on with the app in the background, the
	/// screen off or the launcher swiped from recents. It holds a Wi-Fi lock and a partial wake lock while it works, shows a progress
	/// notification (percent + Cancel), retries by itself when the network drops, and resumes after the process was killed (the
	/// "install.pending" marker). <see cref="InstallCoordinator"/> carries the state to the screens.
	/// </summary>
	[Service(Name = "org.devziyad.celeste.InstallService", Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
	public class InstallService : Service
	{
		public const string ActionStart = "org.devziyad.celeste.action.INSTALL_START";
		public const string ActionCancel = "org.devziyad.celeste.action.INSTALL_CANCEL";
		public const string ExtraUpdate = "update";

		private const string ProgressChannel = "install_progress";
		private const string ResultChannel = "install_result";
		private const int ProgressId = 41;
		private const int ResultId = 42;
		private const string LogTag = "CelesteInstall";
		/// <summary>Consecutive failed attempts, with no byte of progress between them, before giving up.</summary>
		private const int MaxFailures = 30, MaxOtherFailures = 3;

		private static CancellationTokenSource? cts;
		private PowerManager.WakeLock? wakeLock;
		private WifiManager.WifiLock? wifiLock;
		private Task? worker;
		private long lastNotify, lastLog;
		private InstallStage lastStage;

		public static void Start(Context context, bool update) =>
			context.StartForegroundService(new Intent(context, typeof(InstallService)).SetAction(ActionStart).PutExtra(ExtraUpdate, update));

		/// <summary>Stops the install on purpose (the download progress is kept; it will not resume by itself).</summary>
		public static void CancelInstall(Context context)
		{
			if (!InstallCoordinator.IsActive) return;
			context.StartService(new Intent(context, typeof(InstallService)).SetAction(ActionCancel));
		}

		public override IBinder? OnBind(Intent? intent) => null;

		public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
		{
			if (intent?.Action == ActionCancel)
			{
				Log.Info(LogTag, "cancel requested");
				cts?.Cancel();
				return StartCommandResult.NotSticky;
			}

			bool update;
			if (intent == null)
			{
				// The system restarted the service after the process was killed: resume only if an install was in progress.
				string? pending = InstallCoordinator.PendingMode(this);
				if (pending == null)
				{
					StopSelf();
					return StartCommandResult.NotSticky;
				}
				update = pending == "update";
				Log.Info(LogTag, "restarted after the process was killed: resuming the " + pending);
			}
			else
			{
				update = intent.GetBooleanExtra(ExtraUpdate, false);
			}

			CreateChannels();
			StartForeground(ProgressId, BuildProgress(InstallStatus.None with { Step = "Starting", Title = update ? "Updating Celeste" : "Installing Celeste" }, null),
				ForegroundService.TypeDataSync);

			if (worker is { IsCompleted: false })
				return StartCommandResult.Sticky; // already running: the screen just attaches to it

			InstallCoordinator.MarkPending(this, update);
			AcquireLocks();
			cts = new CancellationTokenSource();
			InstallCoordinator.Changed += OnStatus;
			CancellationToken token = cts.Token;
			worker = Task.Run(() => RunAsync(update, token));
			return StartCommandResult.Sticky;
		}

		private async Task RunAsync(bool update, CancellationToken ct)
		{
			int failures = 0;
			try
			{
				while (true)
				{
					var pipeline = new InstallPipeline(this, update);
					try
					{
						await pipeline.RunAsync(ct).ConfigureAwait(false);
						return;
					}
					catch (OperationCanceledException) when (ct.IsCancellationRequested)
					{
						Log.Info(LogTag, "cancelled (the download progress is kept and resumes next time)");
						InstallCoordinator.ClearPending(this);
						InstallCoordinator.Publish(InstallStatus.None);
						return;
					}
					catch (SignInRequiredException e)
					{
						Log.Info(LogTag, "sign-in required: " + e.Message);
						InstallCoordinator.ClearPending(this);
						InstallCoordinator.Publish(new InstallStatus(InstallStage.NeedsSignIn, "Sign in to Steam", e.Message));
						return;
					}
					catch (Exception e) when (IsPermanent(e))
					{
						Log.Error(LogTag, "failed: " + Describe(e));
						InstallCoordinator.ClearPending(this);
						InstallCoordinator.Publish(new InstallStatus(InstallStage.Failed, "Couldn't install from Steam", Describe(e)));
						return;
					}
					catch (Exception e)
					{
						if (pipeline.BytesDone > 0) failures = 0;
						failures++;
						bool network = IsNetwork(e);
						int limit = network ? MaxFailures : MaxOtherFailures; // a bug (e.g. the patch step) is not fixed by waiting
						Log.Warn(LogTag, $"attempt failed ({failures}/{limit}, {(network ? "network" : "other")}): {Describe(e)}\n{e}");
						if (failures >= limit)
						{
							InstallCoordinator.ClearPending(this);
							InstallCoordinator.Publish(new InstallStatus(InstallStage.Failed, "Couldn't install from Steam", Describe(e)));
							return;
						}
						int wait = Math.Min(60, 5 * failures);
						for (int left = wait; left > 0; left--)
						{
							InstallCoordinator.Publish(new InstallStatus(InstallStage.Waiting, update ? "Updating Celeste" : "Installing Celeste",
								network ? $"Connection lost. Retrying in {left} s…" : $"Something went wrong. Retrying in {left} s…", Describe(e)));
							await Task.Delay(1000, ct).ConfigureAwait(false);
						}
					}
				}
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				InstallCoordinator.ClearPending(this);
				InstallCoordinator.Publish(InstallStatus.None);
			}
			catch (Exception e)
			{
				Log.Error(LogTag, "unexpected: " + e);
				InstallCoordinator.Publish(new InstallStatus(InstallStage.Failed, "Couldn't install from Steam", Describe(e)));
			}
			finally
			{
				Finish();
			}
		}

		private void Finish()
		{
			InstallCoordinator.Changed -= OnStatus;
			ReleaseLocks();
			InstallStatus end = InstallCoordinator.Current;
			StopForeground(StopForegroundFlags.Remove);
			var manager = (NotificationManager)GetSystemService(NotificationService)!;
			manager.Cancel(ProgressId);
			if (end.Stage is InstallStage.Done or InstallStage.Failed)
				manager.Notify(ResultId, BuildResult(end));
			StopSelf();
		}

		#region Errors

		/// <summary>What retrying cannot fix: not owned, disk full, a game that fails verification, a wrong depot.</summary>
		private static bool IsPermanent(Exception e) =>
			e is NotOwnedException or InstallException or DepotInstallException or InvalidOperationException
				|| (e is IOException io && io is not EndOfStreamException && io.Message.Contains("space", StringComparison.OrdinalIgnoreCase));

		/// <summary>Errors a retry can fix: the connection dropped, a server timed out, Steam disconnected.</summary>
		private static bool IsNetwork(Exception e) => e switch
		{
			FileNotFoundException or DirectoryNotFoundException => false, // not a connection problem (e.g. Cecil's AssemblyResolutionException)
			SocketException or IOException or System.Net.Http.HttpRequestException or TimeoutException or SteamException => true,
			OperationCanceledException => true, // a timeout inside Steam/HTTP (our own cancel is handled before this is asked)
			AggregateException a => a.InnerExceptions.Any(IsNetwork),
			_ => e.InnerException != null && IsNetwork(e.InnerException),
		};

		private static string Describe(Exception e) => e switch
		{
			NotOwnedException n => n.Message,
			SteamKit2.Authentication.AuthenticationException a => $"Sign-in failed: {a.Result}",
			SteamException s => s.Message,
			DepotInstallException d => d.Message,
			InstallException i => i.Message,
			SocketException => "The network is not reachable.",
			_ => $"{e.GetType().Name}: {e.Message}",
		};

		#endregion

		#region Locks

		private void AcquireLocks()
		{
			var power = (PowerManager)GetSystemService(PowerService)!;
			wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "Celeste:install");
			wakeLock.SetReferenceCounted(false);
			wakeLock.Acquire(6 * 3600 * 1000L);

			var wifi = (WifiManager?)ApplicationContext!.GetSystemService(WifiService);
			if (wifi != null)
			{
				wifiLock = wifi.CreateWifiLock(OperatingSystem.IsAndroidVersionAtLeast(29) ? Android.Net.WifiMode.FullLowLatency : Android.Net.WifiMode.FullHighPerf, "Celeste:install");
				wifiLock.SetReferenceCounted(false);
				wifiLock.Acquire();
			}
		}

		private void ReleaseLocks()
		{
			if (wakeLock is { IsHeld: true }) wakeLock.Release();
			if (wifiLock is { IsHeld: true }) wifiLock.Release();
			wakeLock = null;
			wifiLock = null;
		}

		#endregion

		#region Notifications

		private void CreateChannels()
		{
			var manager = (NotificationManager)GetSystemService(NotificationService)!;
			manager.CreateNotificationChannel(new NotificationChannel(ProgressChannel, "Game download", NotificationImportance.Low)
			{
				Description = "Progress of the Celeste download from Steam",
			});
			manager.CreateNotificationChannel(new NotificationChannel(ResultChannel, "Download finished", NotificationImportance.Default)
			{
				Description = "Tells you when the game is ready or the download stopped",
			});
		}

		private PendingIntent OpenApp() =>
			PendingIntent.GetActivity(this, 0, new Intent(this, typeof(LauncherActivity)).AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop),
				PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;

		private Notification BuildProgress(InstallStatus status, string? text)
		{
			PendingIntent cancel = PendingIntent.GetService(this, 1, new Intent(this, typeof(InstallService)).SetAction(ActionCancel),
				PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
			var builder = new Notification.Builder(this, ProgressChannel)
				.SetSmallIcon(Resource.Drawable.ic_stat_install)!
				.SetContentTitle(status.Percent >= 0 ? $"{status.Title} · {status.Percent}%" : status.Title)!
				.SetContentText(text ?? status.Step)!
				.SetStyle(new Notification.BigTextStyle().BigText(text ?? (status.Stats == null ? status.Step : status.Step + "\n" + InstallText.FormatStats(status.Stats))))!
				.SetOngoing(true)!
				.SetOnlyAlertOnce(true)!
				.SetContentIntent(OpenApp())!
				.AddAction(new Notification.Action.Builder(null, "Cancel", cancel).Build())!;
			if (status.Percent >= 0) builder.SetProgress(100, status.Percent, false);
			else builder.SetProgress(100, 0, true);
			return builder.Build()!;
		}

		private Notification BuildResult(InstallStatus status)
		{
			bool done = status.Stage == InstallStage.Done;
			return new Notification.Builder(this, ResultChannel)
				.SetSmallIcon(Resource.Drawable.ic_stat_install)!
				.SetContentTitle(done ? status.Title : "Download stopped")!
				.SetContentText(done ? (status.Title == InstallCoordinator.UpToDateTitle ? status.Step : "The game is ready to play.") : status.Step)!
				.SetStyle(new Notification.BigTextStyle().BigText(done ? "The game is ready to play." : status.Step))!
				.SetAutoCancel(true)!
				.SetContentIntent(OpenApp())!
				.Build()!;
		}

		/// <summary>Keeps the notification in step with the status (at most once a second; stage changes always go through).</summary>
		private void OnStatus(InstallStatus status)
		{
			if (!status.IsActive) return;
			long now = SystemClock.ElapsedRealtime();
			// A progress marker every 5 s in logcat (scripted checks use it to see the download advance with the screen off).
			if (now - lastLog >= 5000 || status.Stage != lastStage)
			{
				lastLog = now;
				Log.Info(LogTag, $"progress: {status.Stage} {status.Percent}% | {status.Step} | {status.Detail}");
			}
			if (status.Stage == lastStage && now - lastNotify < 1000) return;
			lastNotify = now;
			lastStage = status.Stage;
			((NotificationManager)GetSystemService(NotificationService)!).Notify(ProgressId, BuildProgress(status, null));
		}

		#endregion
	}
}
