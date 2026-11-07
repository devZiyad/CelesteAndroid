#pragma warning disable CS0626 // extern without an attribute: MonoMod swaps it for orig_*
using CelesteAndroid;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;
using MonoMod;

namespace Celeste
{
	class patch_Celeste : Celeste
	{
		[MonoModIgnore]
		public patch_Celeste()
		{
		}

		protected extern void orig_Initialize();

		protected override void Initialize()
		{
			orig_Initialize();
			PortLifecycle.Install();

			// The port's VSync (overrides the game's Settings value at startup; the video menu still applies afterwards).
			bool vsync = PortConfig.VSync;
			Settings.Instance.VSync = vsync;
			if (Engine.Graphics.SynchronizeWithVerticalRetrace != vsync)
			{
				Engine.Graphics.SynchronizeWithVerticalRetrace = vsync;
				Engine.Graphics.ApplyChanges();
			}
			PresentationParameters pp = GraphicsDevice.PresentationParameters;
			PortConfig.LogApplied(pp.BackBufferWidth, pp.BackBufferHeight);
		}
	}
}
