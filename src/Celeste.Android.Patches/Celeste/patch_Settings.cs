#pragma warning disable CS0626 // extern sem atributo: o MonoMod troca por orig_*
using CelesteAndroid;

namespace Celeste
{
	class patch_Settings : Settings
	{
		public extern void orig_ApplyScreen();

		/// <summary>
		/// No Android o jogo roda sempre em tela cheia, na resolução nativa (o modo janela renderiza 320x180×escala e o
		/// sistema estica a imagem): força Fullscreen antes do ApplyScreen original, que então segue o ramo de tela cheia.
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
