using System;
using System.Reflection;
using Celeste;
using Monocle;

namespace CelesteAndroid
{
	/// <summary>
	/// Applies a port option as soon as it changes in the PORT menu, without restarting. Overlay, corner, frame cap and
	/// the screen modes are read every frame; the rest needs a nudge.
	/// </summary>
	public static class PortLive
	{
		public static void Apply(PortSetting setting)
		{
			if (setting == PortSettingsCatalog.ScreenFit || setting == PortSettingsCatalog.IntegerScaling)
			{
				// Recomputes Viewport/ScreenMatrix with the new fit (UpdateView is private in Engine).
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
