using Android.Util;

namespace CelesteAndroid
{
	/// <summary>
	/// Startup phase markers in logcat (<c>[CelesteAndroid] startup: phase +Nms</c>, N = milliseconds since the game process started),
	/// read by scripts/measure-startup.sh. Cheap enough to leave on.
	/// </summary>
	public static class StartupTrace
	{
		public static void Mark(string phase)
		{
			long sinceStart = Android.OS.SystemClock.ElapsedRealtime() - Android.OS.Process.StartElapsedRealtime;
			Log.Info(GameActivity.LogTag, $"[CelesteAndroid] startup: {phase} +{sinceStart}ms");
		}
	}
}
