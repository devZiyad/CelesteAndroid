#pragma warning disable CS0626 // extern without an attribute: MonoMod swaps it for orig_*
using System;
using System.Collections;
using System.Reflection;
using CelesteAndroid;
using FMOD.Studio;

namespace Celeste
{
	static class patch_MenuOptions
	{
		public static extern TextMenu orig_Create(bool inGame = false, EventInstance snapshot = null);

		/// <summary>
		/// Additive hook: the original options menu is built by orig_Create (without rewriting it) and only afterwards
		/// (1) hides "Fullscreen"/"Window Scale" on Android (the game always runs native fullscreen) and
		/// (2) appends the PORT section at the end. Coexists with Everest's hooks, which also call orig_Create.
		/// </summary>
		public static TextMenu Create(bool inGame = false, EventInstance snapshot = null)
		{
			TextMenu menu = orig_Create(inGame, snapshot);
			if (HostConfig.IsAndroid)
			{
				HideFullscreenOptions(menu);
			}
			AddPortSection(menu);
			return menu;
		}

		private static void HideFullscreenOptions(TextMenu menu)
		{
			string fullscreen = Dialog.Clean("options_fullscreen");
			string window = Dialog.Clean("options_window");
			var items = (IEnumerable)typeof(TextMenu).GetField("items", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(menu)!;
			foreach (object item in items)
			{
				string? label = item switch
				{
					TextMenu.Option<bool> o => o.Label,
					TextMenu.Option<int> o => o.Label,
					_ => null,
				};
				if (label == fullscreen || label == window)
				{
					((TextMenu.Item)item).Visible = false;
				}
			}
			menu.RecalculateSize();
		}

		// Menu order: what applies immediately first, what only applies on the next launch last.
		private static readonly PortSetting[] MenuOrder =
		{
			PortSettingsCatalog.Overlay, PortSettingsCatalog.OverlayCorner, PortSettingsCatalog.ScreenFit,
			PortSettingsCatalog.IntegerScaling, PortSettingsCatalog.FrameCap, PortSettingsCatalog.VSync,
			PortSettingsCatalog.ResolutionScale, PortSettingsCatalog.Renderer,
		};

		private static void AddPortSection(TextMenu menu)
		{
			menu.Add(new TextMenu.SubHeader("PORT"));
			foreach (PortSetting setting in MenuOrder)
			{
				menu.Add(CreateItem(setting));
			}
			// Same as the end of the original menu: with more items than fit on screen, start scrolled to the selection.
			if (menu.Height > menu.ScrollableMinSize)
			{
				menu.Position.Y = menu.ScrollTargetY;
			}
		}

		private static TextMenu.Item CreateItem(PortSetting setting)
		{
			string label = setting.Label + (setting.NextLaunch ? " (applies on next launch)" : "");
			int current = setting.IndexOf(PortConfig.Get(setting));
			void Apply(int index)
			{
				PortConfig.Set(setting, setting.Values[index]);
				PortLive.Apply(setting);
			}

			int on = Array.IndexOf(setting.Labels, "On"), off = Array.IndexOf(setting.Labels, "Off");
			if (setting.Labels.Length == 2 && on >= 0 && off >= 0)
			{
				return new TextMenu.OnOff(label, current == on).Change(isOn => Apply(isOn ? on : off));
			}
			return new TextMenu.Slider(label, i => setting.Labels[i], 0, setting.Labels.Length - 1, current).Change(Apply);
		}
	}
}
