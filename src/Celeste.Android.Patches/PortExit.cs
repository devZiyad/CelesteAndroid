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
		private static TextMenu? lastMenu;
		private static TextMenu.Button? lastExit;
		private static int waitedTicks;

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
			lastMenu = menu;
			lastExit = exit;
			items.RemoveAt(items.Count - 1);
			items.Insert(items.IndexOf(saveItem) + 1, exit);
			menu.RecalculateSize();
			// Marker for scripted checks: the pause menu's contents and state.
			Console.WriteLine("[CelesteAndroid] menu pause: " + string.Join(", ", items.OfType<TextMenu.Button>().Select(b => b.Label))
				+ " | paused=" + level.Paused.ToString().ToLowerInvariant());
		}

		/// <summary>The menu is queued in the entity list (not yet in the scene) right after Pause returns.</summary>
		private static TextMenu? FindPauseMenu(Level level)
		{
			var toAdd = (IEnumerable?)typeof(EntityList).GetField("toAdd", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(level.Entities);
			return toAdd?.OfType<TextMenu>().LastOrDefault() ?? level.Entities.FindFirst<TextMenu>();
		}

		/// <summary>The pause menu of a paused level (null if there is none).</summary>
		public static TextMenu? PauseMenu(Level level) => level.Paused && lastMenu?.Scene == level ? lastMenu : null;

		/// <summary>
		/// Leaves for the launcher: in a level it opens the pause menu if needed and presses its "Exit to launcher" item (Save and Quit, then
		/// close); in any other scene there is nothing to save and the game closes at once. Returns false while it has to wait (the level cannot
		/// pause yet); call it again next update.
		/// </summary>
		public static bool RequestExit()
		{
			if (Engine.Scene is not Level level)
			{
				waitedTicks = 0;
				Console.WriteLine("[CelesteAndroid] exit to launcher: closing the game (" + (Engine.Scene?.GetType().Name ?? "no scene") + ")");
				Engine.Instance.Exit();
				return true;
			}
			if (!level.Paused)
			{
				PortLifecycle.PauseLevel();
			}
			if (level.Paused)
			{
				waitedTicks = 0;
				if (PauseMenu(level) != null && lastExit != null)
				{
					lastExit.OnPressed?.Invoke();
					return true;
				}
				Console.WriteLine("[CelesteAndroid] exit to launcher: no Save and Quit in this menu, closing the game");
				Engine.Instance.Exit();
				return true;
			}
			if (++waitedTicks > 300) // 5 s and the level still cannot pause
			{
				waitedTicks = 0;
				Console.WriteLine("[CelesteAndroid] exit to launcher: level cannot pause, closing the game");
				Engine.Instance.Exit();
				return true;
			}
			return false;
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
