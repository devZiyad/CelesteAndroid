#pragma warning disable CS0626 // extern without an attribute: MonoMod swaps it for orig_*
using System;
using CelesteAndroid;
using MonoMod;

namespace Monocle
{
	[MonoModPatch("Monocle.ErrorLog")]
	public static class patch_ErrorLog
	{
		public static extern void orig_Open();

		/// <summary>
		/// The original opens errorLog.txt with the system default program (Process.Start), which does not exist on Android
		/// (the log is still written by ErrorLog.Write). On desktop it calls the original without letting a
		/// do Process.Start derrubar o tratamento do crash.
		/// </summary>
		public static void Open()
		{
			if (HostConfig.IsAndroid)
			{
				return;
			}
			try
			{
				orig_Open();
			}
			catch (Exception)
			{
			}
		}
	}
}
