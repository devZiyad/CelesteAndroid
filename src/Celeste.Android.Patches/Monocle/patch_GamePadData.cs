using CelesteAndroid;
using Microsoft.Xna.Framework;
using MonoMod;

namespace Monocle
{
	/// <summary>
	/// Every rumble of the game ends in GamePadData.Rumble. Additive wrapper: the port's "Rumble" setting decides whether the controller
	/// (the original code, through FNA/SDL) gets it, the phone vibrates instead, or neither. Everest does not touch this method.
	/// </summary>
	[MonoModPatch("Monocle.MInput/GamePadData")]
	public class patch_GamePadData
	{
		// The original's field; the constructor is internal, so the patch class cannot derive from the original.
		[MonoModIgnore]
		public readonly PlayerIndex PlayerIndex;

		public extern void orig_Rumble(float strength, float time);

		public void Rumble(float strength, float time)
		{
			if (PortRumble.Route(PlayerIndex, strength, time))
			{
				orig_Rumble(strength, time);
			}
		}

		public extern void orig_StopRumble();

		public void StopRumble()
		{
			orig_StopRumble();
			PortRumble.Stop();
		}
	}
}
