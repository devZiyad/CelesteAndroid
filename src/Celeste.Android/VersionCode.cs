using System;
using Android.Content.PM;

namespace CelesteAndroid
{
	/// <summary>The app's version code (the commit count; see _CelesteVersion in the csproj).</summary>
	public static class VersionCode
	{
		public static long Of(PackageInfo? info)
		{
			if (info == null) return 0;
			if (OperatingSystem.IsAndroidVersionAtLeast(28)) return info.LongVersionCode;
#pragma warning disable CS0618 // VersionCode is the only one before API 28
			return info.VersionCode;
#pragma warning restore CS0618
		}
	}
}
