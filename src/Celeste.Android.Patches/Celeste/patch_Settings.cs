using CelesteAndroid;
using Monocle;
using MonoMod;

namespace Celeste
{
	class patch_Settings : Settings
	{
		/// <summary>
		/// No Android o modo janela renderiza 320x180×escala e o sistema estica a imagem (distorce e borra):
		/// sempre tela cheia, na resolução nativa. No desktop continua como no original.
		/// </summary>
		[MonoModReplace]
		public new void ApplyScreen()
		{
			if (Fullscreen || HostConfig.IsAndroid)
			{
				Fullscreen = true;
				Engine.ViewPadding = ViewportPadding;
				Engine.SetFullscreen();
			}
			else
			{
				Engine.ViewPadding = 0;
				Engine.SetWindowed(320 * WindowScale, 180 * WindowScale);
			}
		}
	}
}
