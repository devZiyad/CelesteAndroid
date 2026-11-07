#pragma warning disable CS0626 // extern sem atributo: o MonoMod troca por orig_*
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

			// VSync do port (sobrepõe o do Settings do jogo no início; o menu de vídeo continua valendo depois).
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
