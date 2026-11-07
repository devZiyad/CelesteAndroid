#pragma warning disable CS0626 // extern without an attribute: MonoMod swaps it for orig_*
using CelesteAndroid;
using MonoMod;

namespace Celeste
{
	class patch_Level : Level
	{
		[MonoModIgnore]
		public patch_Level()
		{
		}

		public extern void orig_Pause(int startIndex = 0, bool minimal = false, bool quickReset = false);

		/// <summary>Additive hook: the original builds the pause menu; the port then only adds "Exit to launcher" below "Save and Quit".</summary>
		public new void Pause(int startIndex = 0, bool minimal = false, bool quickReset = false)
		{
			orig_Pause(startIndex, minimal, quickReset);
			if (HostConfig.IsAndroid && !minimal && !quickReset)
			{
				PortExit.AddExitItem(this);
			}
		}
	}
}
