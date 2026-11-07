#pragma warning disable CS0626 // extern without an attribute: MonoMod swaps it for orig_*
using CelesteAndroid;

namespace Celeste
{
	class patch_Settings : Settings
	{
		public extern void orig_ApplyScreen();

		/// <summary>
		/// On Android the game always runs fullscreen at the native resolution (windowed mode renders 320x180×scale and the
		/// system stretches the image): forces Fullscreen before the original ApplyScreen, which then takes the fullscreen branch.
		/// No desktop continua como no original.
		/// </summary>
		public new void ApplyScreen()
		{
			if (HostConfig.IsAndroid)
			{
				Fullscreen = true;
			}
			orig_ApplyScreen();
		}
	}
}
