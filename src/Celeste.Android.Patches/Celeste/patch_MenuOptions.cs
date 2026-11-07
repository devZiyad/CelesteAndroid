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

		/// <summary>No Android o jogo roda sempre em tela cheia nativa: esconde "Fullscreen" e "Window Scale" do menu Video.</summary>
		public static TextMenu Create(bool inGame = false, EventInstance snapshot = null)
		{
			TextMenu menu = orig_Create(inGame, snapshot);
			if (!HostConfig.IsAndroid)
			{
				return menu;
			}
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
			return menu;
		}
	}
}
