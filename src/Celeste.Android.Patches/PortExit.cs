using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Celeste;
using Monocle;

namespace CelesteAndroid
{
	/// <summary>
	/// "Exit to launcher" in the pause menu. It is "Save and Quit" (the game's own button, so the save and the sound are the vanilla
	/// ones) followed by closing the game once the main menu is back; the activity then finishes and the launcher shows.
	/// </summary>
	public static class PortExit
	{
		private static bool exitPending;

		/// <summary>Called after the original Level.Pause built the menu: adds the item right below "Save and Quit".</summary>
		public static void AddExitItem(Level level)
		{
			TextMenu? menu = FindPauseMenu(level);
			if (menu == null)
			{
				return;
			}
			FieldInfo itemsField = typeof(TextMenu).GetField("items", BindingFlags.NonPublic | BindingFlags.Instance)!;
			var items = (IList)itemsField.GetValue(menu)!;
			string saveQuit = Dialog.Clean("menu_pause_savequit");
			TextMenu.Button? saveItem = items.OfType<TextMenu.Button>().FirstOrDefault(b => b.Label == saveQuit);
			if (saveItem == null)
			{
				return; // minimal/cutscene/event menus have no "Save and Quit": nothing to build on
			}

			var exit = new TextMenu.Button("Exit to launcher") { Disabled = saveItem.Disabled };
			exit.Pressed(() =>
			{
				exitPending = true;
				Console.WriteLine("[CelesteAndroid] exit to launcher: saving and quitting");
				saveItem.OnPressed?.Invoke();
			});
			menu.Add(exit);
			items.RemoveAt(items.Count - 1);
			items.Insert(items.IndexOf(saveItem) + 1, exit);
			menu.RecalculateSize();
		}

		/// <summary>The menu is queued in the entity list (not yet in the scene) right after Pause returns.</summary>
		private static TextMenu? FindPauseMenu(Level level)
		{
			var toAdd = (IEnumerable?)typeof(EntityList).GetField("toAdd", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(level.Entities);
			return toAdd?.OfType<TextMenu>().LastOrDefault() ?? level.Entities.FindFirst<TextMenu>();
		}

		/// <summary>Every update: once Save and Quit brought the main menu back, close the game.</summary>
		public static void Tick()
		{
			if (exitPending && Engine.Scene is OverworldLoader)
			{
				exitPending = false;
				Console.WriteLine("[CelesteAndroid] exit to launcher: closing the game");
				Engine.Instance.Exit();
			}
		}
	}
}
