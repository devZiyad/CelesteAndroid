using System;
using System.Reflection;
using Celeste;
using Monocle;

namespace CelesteAndroid
{
	/// <summary>
	/// Aplica uma opção do port assim que ela muda no menu PORT, sem reiniciar. Overlay, canto, limite de quadros e
	/// os modos de tela são lidos a cada quadro; o resto precisa de um empurrão.
	/// </summary>
	public static class PortLive
	{
		public static void Apply(PortSetting setting)
		{
			if (setting == PortSettingsCatalog.ScreenFit || setting == PortSettingsCatalog.IntegerScaling)
			{
				// Recalcula Viewport/ScreenMatrix com o novo encaixe (UpdateView é privado no Engine).
				typeof(Engine).GetMethod("UpdateView", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(Engine.Instance, null);
			}
			else if (setting == PortSettingsCatalog.VSync)
			{
				bool vsync = PortConfig.VSync;
				Settings.Instance.VSync = vsync;
				Engine.Graphics.SynchronizeWithVerticalRetrace = vsync;
				Engine.Graphics.ApplyChanges();
			}
			else if (setting == PortSettingsCatalog.ResolutionScale)
			{
				Settings.Instance.ApplyScreen();
			}
		}
	}
}
