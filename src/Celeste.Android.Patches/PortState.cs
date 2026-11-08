using System;
using Celeste;
using Monocle;

namespace CelesteAndroid
{
	/// <summary>
	/// Logs where the game is (title screen, main menu, file select, in level, pause menu...) whenever it changes, so scripted checks
	/// can read it from logcat instead of taking screenshots: <c>[CelesteAndroid] state: title</c>.
	/// </summary>
	public static class PortState
	{
		private static string? last;

		/// <summary>Every update (cheap: a few type checks); prints only on change.</summary>
		public static void Tick()
		{
			string state = PortBack.Showing ? "return confirmation" : Describe(Engine.Scene);
			if (state == last)
			{
				return;
			}
			last = state;
			Console.WriteLine("[CelesteAndroid] state: " + state);
		}

		private static string Describe(Scene? scene)
		{
			switch (scene)
			{
				case null:
					return "none";
				case Level level:
					return level.Paused ? "pause menu" : "level";
				case Overworld overworld:
					return overworld.Current switch
					{
						OuiTitleScreen => "title",
						OuiMainMenu => "main menu",
						OuiFileSelect => "file select",
						OuiFileNaming => "file naming",
						null => "overworld",
						Oui oui => "overworld " + oui.GetType().Name.Replace("Oui", "").ToLowerInvariant(),
					};
				default:
					return scene.GetType().Name.ToLowerInvariant();
			}
		}
	}
}
